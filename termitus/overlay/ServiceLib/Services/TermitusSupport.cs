namespace ServiceLib.Services;

/// <summary>One chat message as the app keeps it. Local = sent by us, no server id yet.</summary>
public sealed record TermitusChatMessage(string Id, bool FromOperator, string Text, long? At, bool Local = false);

public enum TermitusSupportAvailability
{
    Unknown,
    Available,
    /// <summary>404 (or an unusable answer): show the Telegram bot button instead.</summary>
    Unavailable,
    /// <summary>403: web-trial guest without Telegram — chat needs a Telegram account; show the bot.</summary>
    Guest,
    /// <summary>
    /// No HTTP answer at all (offline, timeout, another VPN app blocking the route; 0.4.1).
    /// Not a statement about the chat itself: what we knew before stays, the page says
    /// "no connection" and offers «Повторить».
    /// </summary>
    Offline,
}

public enum TermitusSendKind
{
    Ok,
    /// <summary>422: empty or too long.</summary>
    Invalid,
    /// <summary>429: too often; RetryAfter says how long.</summary>
    RateLimited,
    /// <summary>403: guest without Telegram.</summary>
    Guest,
    /// <summary>404/405/501: no chat on this backend.</summary>
    Unavailable,
    /// <summary>No answer / 5xx / other: try again.</summary>
    Failed,
}

/// <summary>Outcome of one POST. Message = the server copy (201 body), when parseable.</summary>
public sealed record TermitusSendResult(TermitusSendKind Kind, TermitusSupportMessageDto? Message = null, TimeSpan? RetryAfter = null);

/// <summary>Visibility of the parts of the «Поддержка» page (see TermitusSupport.ViewOf).</summary>
public sealed record TermitusSupportView(bool ChatVisible, bool BotVisible, bool Offline, bool CanType, bool Loading, bool Empty);

/// <summary>Result of one GET: Messages null = no usable answer.</summary>
public sealed record TermitusSupportFetch(TermitusSupportAvailability Availability, IReadOnlyList<TermitusSupportMessageDto>? Messages);

/// <summary>Seen/known ids per subscription (guiConfigs/ter-support.json, 0600). No URL, no text.</summary>
public sealed class TermitusSupportState
{
    public string SubId { get; set; } = string.Empty;
    public string? LastSeenOperatorId { get; set; }
    public string? LastKnownId { get; set; }
    /// <summary>Newest operator reply ever fetched (also by the background poll).</summary>
    public string? LastOperatorIdSeenInFetch { get; set; }
}

/// <summary>
/// «Поддержка» chat (TER 0.4) over <c>&lt;subscription_url&gt;/support</c>.
/// Pure helpers (merge, ordering, unread) are unit-tested; network calls are thin.
/// The subscription URL is a secret and message text is private: neither is logged.
/// </summary>
public static class TermitusSupport
{
    private const string _tag = "TermitusSupport";
    private const string StateFile = "ter-support.json";
    public const int MaxTextLength = 4000;
    /// <summary>The backend returns at most this many messages per GET (oldest first).</summary>
    public const int PageSize = 100;
    private const int MaxPages = 10;
    /// <summary>Only the newest messages are kept in memory / shown.</summary>
    public const int MaxMessages = 300;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly object _stateLock = new();

    #region Pure logic (unit-tested)

    /// <summary><c>https://h/sub/T?x=1</c> -> <c>https://h/sub/T/support?x=1&amp;after=5</c>. Null if not http(s).</summary>
    public static string? SupportUrl(string? subscriptionUrl, string? afterId = null)
    {
        if (subscriptionUrl.IsNullOrEmpty()
            || !Uri.TryCreate(subscriptionUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }
        var b = new UriBuilder(uri) { Fragment = string.Empty };
        b.Path = b.Path.TrimEnd('/') + "/support";
        if (afterId.IsNotEmpty())
        {
            var q = b.Query.TrimStart('?');
            b.Query = (q.Length > 0 ? q + "&" : string.Empty) + "after=" + Uri.EscapeDataString(afterId);
        }
        return b.Uri.AbsoluteUri;
    }

    /// <summary>Numeric ids compare as numbers, otherwise ordinal. Null is the smallest.</summary>
    public static int CompareIds(string? a, string? b)
    {
        if (a == null || b == null)
        {
            return a == null ? (b == null ? 0 : -1) : 1;
        }
        if (long.TryParse(a, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var x)
            && long.TryParse(b, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var y))
        {
            return x.CompareTo(y);
        }
        return string.CompareOrdinal(a, b);
    }

    /// <summary>Text to send: trimmed, null when empty or too long.</summary>
    public static string? NormalizeOutgoing(string? text)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) || t.Length > MaxTextLength ? null : t;
    }

    /// <summary>Parses the GET answer: a JSON array (or {"messages": [...]}). Null on junk.</summary>
    public static List<TermitusSupportMessageDto>? ParseMessages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("messages", out var inner))
            {
                root = inner;
            }
            if (root.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var list = new List<TermitusSupportMessageDto>();
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var m = el.Deserialize<TermitusSupportMessageDto>(TermitusAccount.InfoJsonOptions);
                if (m != null)
                {
                    list.Add(m);
                }
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Maps the POST answer (201 {id,from,text,at}) to what the chat does.</summary>
    public static TermitusSendResult InterpretSend(int? status, string? body, TimeSpan? retryAfter)
    {
        switch (status)
        {
            case >= 200 and < 300:
                TermitusSupportMessageDto? m = null;
                try
                {
                    m = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<TermitusSupportMessageDto>(body, TermitusAccount.InfoJsonOptions);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
                {
                    m = null;
                }
                return new(TermitusSendKind.Ok, m?.Id.IsNotEmpty() == true ? m : null);

            case 422:
                return new(TermitusSendKind.Invalid);

            case 429:
                return new(TermitusSendKind.RateLimited, null, retryAfter);

            case 403:
                return new(TermitusSendKind.Guest);

            case 404 or 405 or 501:
                return new(TermitusSendKind.Unavailable);

            default:
                return new(TermitusSendKind.Failed);
        }
    }

    /// <summary>A full page means there may be more after its last id.</summary>
    public static bool HasMorePages(int pageCount) => pageCount >= PageSize;

    /// <summary>"id" from the POST answer, or null.</summary>
    public static string? ParsePostedId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<TermitusSupportPostResponse>(json, TermitusAccount.InfoJsonOptions)?.Id;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Folds server messages into what we have: dedupe by id (server copy wins over a
    /// local one with the same id), drop junk (no id / empty text), order by id, local
    /// (unsent/unconfirmed) messages last, keep the newest <see cref="MaxMessages"/>.
    /// </summary>
    public static List<TermitusChatMessage> Merge(IReadOnlyList<TermitusChatMessage> existing, IEnumerable<TermitusSupportMessageDto>? incoming)
    {
        var byId = new Dictionary<string, TermitusChatMessage>(StringComparer.Ordinal);
        var local = new List<TermitusChatMessage>();
        foreach (var m in existing)
        {
            if (m.Local)
            {
                local.Add(m);
            }
            else
            {
                byId[m.Id] = m;
            }
        }
        foreach (var d in incoming ?? [])
        {
            if (d.Id.IsNullOrEmpty() || string.IsNullOrWhiteSpace(d.Text))
            {
                continue;
            }
            var op = string.Equals(d.From?.Trim(), "operator", StringComparison.OrdinalIgnoreCase);
            byId[d.Id] = new TermitusChatMessage(d.Id, op, d.Text.Trim(), d.At);
        }
        // A local message whose server copy arrived is replaced by it.
        local.RemoveAll(l => !l.Id.StartsWith(LocalPrefix, StringComparison.Ordinal) && byId.ContainsKey(l.Id));

        var merged = byId.Values.OrderBy(m => m.Id, Comparer<string>.Create(CompareIds)).ToList();
        merged.AddRange(local);
        if (merged.Count > MaxMessages)
        {
            merged.RemoveRange(0, merged.Count - MaxMessages);
        }
        return merged;
    }

    public const string LocalPrefix = "local:";

    /// <summary>Highest server id we hold (for ?after=), null if none.</summary>
    public static string? LastServerId(IEnumerable<TermitusChatMessage> messages)
    {
        string? max = null;
        foreach (var m in messages)
        {
            if (!m.Local && CompareIds(m.Id, max) > 0)
            {
                max = m.Id;
            }
        }
        return max;
    }

    /// <summary>Highest operator id, null if the operator never wrote.</summary>
    public static string? LastOperatorId(IEnumerable<TermitusChatMessage> messages)
        => LastServerId(messages.Where(m => m.FromOperator));

    /// <summary>Highest id in a raw server answer.</summary>
    public static string? LastServerId(IEnumerable<TermitusSupportMessageDto>? messages)
    {
        string? max = null;
        foreach (var d in messages ?? [])
        {
            if (d.Id.IsNotEmpty() && CompareIds(d.Id, max) > 0)
            {
                max = d.Id;
            }
        }
        return max;
    }

    /// <summary>Highest operator id in a raw server answer.</summary>
    public static string? LastOperatorId(IEnumerable<TermitusSupportMessageDto>? messages)
    {
        string? max = null;
        foreach (var d in messages ?? [])
        {
            if (d.Id.IsNotEmpty() && string.Equals(d.From?.Trim(), "operator", StringComparison.OrdinalIgnoreCase)
                && CompareIds(d.Id, max) > 0)
            {
                max = d.Id;
            }
        }
        return max;
    }

    /// <summary>Unread = the newest operator reply is newer than the last one the user saw.</summary>
    public static bool HasUnread(string? lastOperatorId, string? lastSeenOperatorId)
        => lastOperatorId != null && CompareIds(lastOperatorId, lastSeenOperatorId) > 0;

    /// <summary>Max of two ids (null-safe).</summary>
    public static string? MaxId(string? a, string? b) => CompareIds(a, b) >= 0 ? a : b;

    #endregion Pure logic (unit-tested)

    #region State file

    public static TermitusSupportState LoadState(string subId)
    {
        lock (_stateLock)
        {
            try
            {
                var path = Utils.GetConfigPath(StateFile);
                if (File.Exists(path))
                {
                    var s = JsonSerializer.Deserialize<TermitusSupportState>(File.ReadAllText(path), TermitusAccount.InfoJsonOptions);
                    if (s != null && s.SubId == subId)
                    {
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"{_tag}: state read failed ({ex.GetType().Name})");
            }
            return new TermitusSupportState { SubId = subId };
        }
    }

    /// <summary>«Выйти»: forget the read/known ids.</summary>
    public static void ClearState()
    {
        lock (_stateLock)
        {
            try
            {
                var path = Utils.GetConfigPath(StateFile);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"{_tag}: state delete failed ({ex.GetType().Name})");
            }
        }
    }

    /// <summary>
    /// What the «Поддержка» page shows (pure, unit-tested, 0.4.1). Never an empty page:
    /// while the first answer is pending or the server is unreachable the chat card stays
    /// with a disabled input; the bot button replaces it only when the chat is known to be
    /// missing (404) or not for this user (403), or there is no subscription.
    /// </summary>
    public static TermitusSupportView ViewOf(bool hasSubscription, TermitusSupportAvailability availability, bool offline, int messageCount)
    {
        var chat = hasSubscription && availability is TermitusSupportAvailability.Available or TermitusSupportAvailability.Unknown or TermitusSupportAvailability.Offline;
        var off = chat && offline;
        return new TermitusSupportView(
            ChatVisible: chat,
            BotVisible: !chat,
            Offline: off,
            CanType: chat && !off && availability == TermitusSupportAvailability.Available,
            Loading: chat && !off && availability != TermitusSupportAvailability.Available && messageCount == 0,
            Empty: chat && !off && availability == TermitusSupportAvailability.Available && messageCount == 0);
    }

    public static void SaveState(TermitusSupportState state)
    {
        lock (_stateLock)
        {
            try
            {
                var path = Utils.GetConfigPath(StateFile);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state));
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                File.Move(tmp, path, true);
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"{_tag}: state write failed ({ex.GetType().Name})");
            }
        }
    }

    #endregion State file

    #region Network

    private static string SubUrl(SubItem sub) => Utils.GetPunycode(sub.Url.TrimEx());

    /// <summary>
    /// GET support messages after <paramref name="afterId"/> (all when null). The backend
    /// answers oldest-first, max <see cref="PageSize"/> per call: full pages are followed with
    /// ?after=&lt;last id&gt; (up to <see cref="MaxPages"/>).
    /// </summary>
    public static async Task<TermitusSupportFetch> FetchAsync(SubItem sub, string? afterId, int? socksPort, CancellationToken ct)
    {
        var all = new List<TermitusSupportMessageDto>();
        var after = afterId;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = SupportUrl(SubUrl(sub), after);
            if (url == null)
            {
                return new(TermitusSupportAvailability.Unavailable, null);
            }
            var res = await TermitusHttp.GetAsync(url, socksPort, RequestTimeout, ct);
            if (res.Status is 404 or 405 or 501)
            {
                return new(TermitusSupportAvailability.Unavailable, null);
            }
            if (res.Status == 403)
            {
                return new(TermitusSupportAvailability.Guest, null);
            }
            if (res.Status == null && all.Count == 0)
            {
                return new(TermitusSupportAvailability.Offline, null);
            }
            if (!res.IsSuccess)
            {
                if (res.Status != null)
                {
                    Logging.SaveLog($"{_tag}: fetch -> {res.Status}");
                }
                // Transient: keep the last known state (pages fetched so far are still good).
                return all.Count > 0 ? new(TermitusSupportAvailability.Available, all) : new(TermitusSupportAvailability.Unknown, null);
            }
            var list = ParseMessages(res.Body);
            if (list == null)
            {
                return all.Count > 0 ? new(TermitusSupportAvailability.Available, all) : new(TermitusSupportAvailability.Unavailable, null);
            }
            all.AddRange(list);
            var last = LastServerId(list);
            if (!HasMorePages(list.Count) || last == null || CompareIds(last, after) <= 0)
            {
                break;
            }
            after = last;
        }
        return new(TermitusSupportAvailability.Available, all);
    }

    /// <summary>POST a message (201 {id,from,text,at} | 422 | 429 | 403).</summary>
    public static async Task<TermitusSendResult> SendAsync(SubItem sub, string text, int? socksPort, CancellationToken ct)
    {
        var url = SupportUrl(SubUrl(sub));
        if (url == null)
        {
            return new(TermitusSendKind.Unavailable);
        }
        var res = await TermitusHttp.PostJsonAsync(url, new TermitusSupportPostRequest(text), socksPort, RequestTimeout, ct);
        Logging.SaveLog($"{_tag}: send -> {res.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no answer"}");
        return InterpretSend(res.Status, res.Body, res.RetryAfter);
    }

    #endregion Network
}
