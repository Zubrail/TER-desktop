namespace ServiceLib.Services;

/// <summary>What one poll answer means for the login flow.</summary>
public enum TermitusPollKind
{
    /// <summary>Not confirmed yet: poll again.</summary>
    Pending,
    /// <summary>Confirmed: <see cref="TermitusPollOutcome.SubscriptionUrl"/> is set.</summary>
    Ok,
    Expired,
    Declined,
    Used,
    /// <summary>Transient (offline, timeout, 429, 5xx): keep polling until the deadline.</summary>
    Retry,
    /// <summary>The server answered something we cannot use: stop, offer the link field.</summary>
    Failed,
}

/// <param name="TrialAttached">0.6.5: the trial code sent at /start was redeemed for this Telegram account (trial = 7 days total).</param>
/// <param name="TrialNote">0.6.5: "code_unavailable" | "cooldown" | "error": the code could not be attached (the login itself still succeeded).</param>
public sealed record TermitusPollOutcome(TermitusPollKind Kind, string? SubscriptionUrl = null, TimeSpan? RetryAfter = null,
    bool TrialAttached = false, string? TrialNote = null)
{
    public bool IsFinal => Kind is not (TermitusPollKind.Pending or TermitusPollKind.Retry);
}

/// <summary>A started login: what to open, how long to wait, how often to ask.</summary>
public sealed record TermitusLoginSession(string Code, string BotUrl, DateTimeOffset Deadline, TimeSpan PollInterval)
{
    public int SecondsLeft(DateTimeOffset now) => Math.Max(0, (int)Math.Ceiling((Deadline - now).TotalSeconds));

    public bool IsExpired(DateTimeOffset now) => now >= Deadline;
}

public enum TermitusLoginStartKind
{
    Ok,
    /// <summary>Endpoint missing (404/405/501) or answer unusable: hide the feature, show the link field.</summary>
    Unavailable,
    /// <summary>No answer (offline/timeout/5xx): say so, the user can retry.</summary>
    Offline,
}

public sealed record TermitusLoginStart(TermitusLoginStartKind Kind, TermitusLoginSession? Session = null);

/// <summary>
/// «Войти через Telegram» (TER 0.4): the app asks our backend for a one-time code, opens
/// the bot with it, the user confirms in Telegram, the app polls until the backend hands
/// out the subscription URL (once). Codes and URLs are secrets: never logged.
/// The decision logic is pure and unit-tested; <see cref="StartAsync"/>/<see cref="PollAsync"/>
/// are thin network wrappers.
/// </summary>
public static class TermitusLogin
{
    private const string _tag = "TermitusLogin";
    public const string DefaultApiBase = "https://termitus.online";
    /// <summary>termitus.online, or TER_TEST_API_BASE for a test instance (TermitusTestHooks).</summary>
    public static string ApiBase => TermitusTestHooks.ApiBase is { Length: > 0 } b ? b : DefaultApiBase;
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(3);
    /// <summary>Pauses before re-reading a still-empty subscription after login (keys are provisioned in the background).</summary>
    public static readonly TimeSpan[] EmptySubscriptionRetries = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly Regex CodePattern = new("^[A-Za-z0-9_-]{4,128}$", RegexOptions.CultureInvariant);

    #region Pure logic (unit-tested)

    /// <summary>"macos" | "windows" | "linux" | "other".</summary>
    public static string PlatformName()
        => OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : "other";

    /// <summary>Machine name, trimmed to something a bot message can show.</summary>
    public static string DeviceName(string? machineName)
    {
        var n = (machineName ?? string.Empty).Trim();
        if (n.Length == 0)
        {
            return "Computer";
        }
        return n.Length > 64 ? n[..64] : n;
    }

    /// <summary>Validates /app/login/start. Null = unusable (missing/odd code, bot link not ours).</summary>
    public static TermitusLoginSession? ToSession(TermitusLoginStartResponse? r, DateTimeOffset now)
    {
        if (r == null || r.Code == null || !CodePattern.IsMatch(r.Code) || !IsSafeBotUrl(r.BotUrl))
        {
            return null;
        }
        var life = r.ExpiresIn is > 0 and <= 3600 ? TimeSpan.FromSeconds(r.ExpiresIn.Value) : DefaultLifetime;
        var every = r.PollInterval is > 0 ? TimeSpan.FromSeconds(Math.Clamp(r.PollInterval.Value, 1, 15)) : DefaultPollInterval;
        return new TermitusLoginSession(r.Code, r.BotUrl!, now + life, every);
    }

    /// <summary>The bot link must be an https link to Telegram or our site (same rule as renew_url).</summary>
    public static bool IsSafeBotUrl(string? url) => TermitusAccount.IsSafeRenewUrl(url);

    /// <summary>The URL we will import: absolute https, no credentials in it, sane length.</summary>
    public static bool IsAcceptableSubscriptionUrl(string? url)
    {
        return url != null
            && url.Length <= 2048
            && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)
            // test instance only: the fake backend on this very computer speaks plain http
            && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && TermitusTestHooks.IsTestInstance && u.IsLoopback))
            && u.UserInfo.Length == 0
            && u.Host.Length > 0;
    }

    /// <summary>The note values the backend may send; anything else is treated as "error".</summary>
    public static string? NormalizeTrialNote(string? note)
    {
        var n = (note ?? string.Empty).Trim().ToLowerInvariant();
        return n.Length == 0 ? null : n is "code_unavailable" or "cooldown" ? n : "error";
    }

    /// <summary>Maps one poll answer (HTTP status null = no answer) to what the flow does next.</summary>
    public static TermitusPollOutcome InterpretPoll(int? status, string? body, TimeSpan? retryAfter = null)
    {
        if (status == 429)
        {
            return new(TermitusPollKind.Retry, null, retryAfter); // rate limited: back off as told
        }
        switch (status)
        {
            case null:
            case 408:
            case 425:
            case 429:
            case >= 500:
                return new(TermitusPollKind.Retry);

            case 202:
                return new(TermitusPollKind.Pending);

            case 404:
                return new(TermitusPollKind.Expired); // unknown code = it is gone

            case 410:
                return (Parse(body)?.Status?.Trim().ToLowerInvariant()) switch
                {
                    "declined" => new(TermitusPollKind.Declined),
                    "used" => new(TermitusPollKind.Used),
                    _ => new(TermitusPollKind.Expired),
                };

            case 200:
                var p = Parse(body);
                var st = p?.Status?.Trim().ToLowerInvariant();
                if (st == "pending")
                {
                    return new(TermitusPollKind.Pending);
                }
                if (st == "ok" && IsAcceptableSubscriptionUrl(p!.SubscriptionUrl))
                {
                    return new(TermitusPollKind.Ok, p.SubscriptionUrl!.Trim(), null, p.TrialAttached == true, NormalizeTrialNote(p.TrialNote));
                }
                return new(TermitusPollKind.Failed);

            default:
                return new(TermitusPollKind.Failed);
        }
    }

    private static TermitusLoginPollResponse? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<TermitusLoginPollResponse>(body, TermitusAccount.InfoJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"4:59".</summary>
    public static string FormatCountdown(int seconds)
    {
        seconds = Math.Max(0, seconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>
    /// The waiting loop: wait one interval (never past the deadline), poll, repeat until a
    /// final answer or the deadline (= Expired). Transient errors just mean "poll again".
    /// Clock, delay and poll are injected so the state machine is unit-tested without
    /// network or real time. Cancellation propagates (OperationCanceledException).
    /// </summary>
    public static async Task<TermitusPollOutcome> WaitForConfirmationAsync(
        TermitusLoginSession session,
        Func<CancellationToken, Task<TermitusPollOutcome>> poll,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<DateTimeOffset> now,
        CancellationToken ct)
    {
        TimeSpan? backoff = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var left = session.Deadline - now();
            if (left <= TimeSpan.Zero)
            {
                return new(TermitusPollKind.Expired);
            }
            var wait = backoff is { } b && b > session.PollInterval ? b : session.PollInterval;
            await delay(left < wait ? left : wait, ct);
            var o = await poll(ct);
            if (o.IsFinal)
            {
                return o;
            }
            // 429 Retry-After: wait that long (max 60 s) before the next poll, once.
            backoff = o.RetryAfter is { } ra ? TimeSpan.FromSeconds(Math.Min(ra.TotalSeconds, 60)) : null;
        }
    }

    #endregion Pure logic (unit-tested)

    #region Network

    /// <summary>POST /app/login/start. Direct connection (first run: nothing is connected).</summary>
    public static async Task<TermitusLoginStart> StartAsync(CancellationToken ct, string? trialCode = null)
    {
        // trialCode (0.6.5): the stored trial's gift code; the bot redeems it for the Telegram account that confirms.
        var req = new TermitusLoginStartRequest(DeviceName(Environment.MachineName), PlatformName(), trialCode.IsNullOrEmpty() ? null : trialCode);
        var res = await TermitusHttp.PostJsonAsync($"{ApiBase}/app/login/start", req, null, RequestTimeout, ct);
        Logging.SaveLog($"{_tag}: start -> {res.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no answer"}");
        if (res.Status == null || res.Status >= 500 || res.Status is 408 or 429)
        {
            return new(TermitusLoginStartKind.Offline);
        }
        if (!res.IsSuccess)
        {
            return new(TermitusLoginStartKind.Unavailable);
        }
        TermitusLoginStartResponse? dto = null;
        try
        {
            dto = res.Body == null ? null : JsonSerializer.Deserialize<TermitusLoginStartResponse>(res.Body, TermitusAccount.InfoJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            dto = null;
        }
        var session = ToSession(dto, DateTimeOffset.UtcNow);
        return session == null ? new(TermitusLoginStartKind.Unavailable) : new(TermitusLoginStartKind.Ok, session);
    }

    /// <summary>GET /app/login/poll/{code}.</summary>
    public static async Task<TermitusPollOutcome> PollAsync(TermitusLoginSession s, CancellationToken ct)
    {
        var res = await TermitusHttp.GetAsync($"{ApiBase}/app/login/poll/{Uri.EscapeDataString(s.Code)}", null, RequestTimeout, ct);
        var outcome = InterpretPoll(res.Status, res.Body, res.RetryAfter);
        if (outcome.Kind != TermitusPollKind.Pending)
        {
            Logging.SaveLog($"{_tag}: poll -> {outcome.Kind}");
        }
        return outcome;
    }

    #endregion Network
}
