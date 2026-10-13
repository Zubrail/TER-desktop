using System.Globalization;
using System.Net.Http.Headers;

namespace ServiceLib.Services;

/// <summary>
/// "Аккаунт" page data: subscription status, expiry, traffic, devices.
///
/// Two sources, both from OUR backend, both optional:
///  1. The subscription response itself: header
///     <c>Subscription-Userinfo: upload=..; download=..; total=..; expire=..</c>
///     (present only while a subscription is active — no header on a successful
///     response = no active subscription), plus <c>Profile-Title</c>.
///     Captured (a) for free whenever upstream downloads the subscription
///     (import, "update subscription", upstream auto-update) through the
///     <see cref="OnDownloadedAsync"/> hook patched into DownloadService by
///     apply-changes.sh, and (b) by <see cref="RefreshAsync"/>, a header-only GET
///     the account page makes at start, every 6 h and on «Обновить».
///  2. <c>GET &lt;subscription-url&gt;/info</c> (JSON, <see cref="TermitusAccountInfoDto"/>).
///     Not deployed yet: any failure (404, timeout, bad JSON) is silent and the
///     page falls back to the header data.
///
/// The subscription URL is a secret: it is never logged and never written to
/// the cache (the cache stores the SubItem id only). Cache file:
/// guiConfigs/ter-account.json, 0600 on macOS/Linux.
/// </summary>
public static class TermitusAccount
{
    private const string _tag = "TermitusAccount";
    private const string CacheFile = "ter-account.json";
    public const string HeaderUserInfo = "Subscription-Userinfo";
    public const string HeaderProfileTitle = "Profile-Title";
    public const string HeaderSupportUrl = "Support-Url";
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    /// <summary>Raised (on a background thread) after the cache was updated.</summary>
    public static event Action? Changed;

    #region Pure logic (unit-tested)

    /// <summary>
    /// Parses <c>upload=1; download=2; total=0; expire=1700000000</c>. Unknown keys and
    /// junk are ignored; numbers may be written as decimals ("1.5e9"). Returns null
    /// when nothing usable is in the header. expire 0/missing = no expiry date.
    /// </summary>
    public static TermitusSubUserInfo? ParseUserInfo(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }
        long? up = null, down = null, total = null, expire = null;
        foreach (var part in header.Split([';', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = part[..eq].Trim().ToLowerInvariant();
            if (!TryNumber(part[(eq + 1)..].Trim(), out var n))
            {
                continue;
            }
            switch (key)
            {
                case "upload": up = n; break;
                case "download": down = n; break;
                case "total": total = n; break;
                case "expire": expire = n; break;
            }
        }
        if (up == null && down == null && total == null && expire == null)
        {
            return null;
        }
        return new TermitusSubUserInfo(up ?? 0, down ?? 0, total ?? 0, expire is > 0 ? expire : null);
    }

    private static bool TryNumber(string s, out long value)
    {
        value = 0;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value >= 0;
        }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d >= 0 && d < 9e18 && !double.IsNaN(d))
        {
            value = (long)d;
            return true;
        }
        return false;
    }

    /// <summary>Parses the /info JSON; null on anything unexpected (never throws).</summary>
    public static TermitusAccountInfoDto? ParseInfoJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            var dto = JsonSerializer.Deserialize<TermitusAccountInfoDto>(json, InfoJsonOptions);
            if (dto == null)
            {
                return null;
            }
            // Sanity: negative counters are junk, not data.
            if (dto.DevicesUsed is < 0 || dto.DevicesAllowed is < 0 || dto.Expire is < 0)
            {
                return null;
            }
            return dto;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static readonly JsonSerializerOptions InfoJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// <c>https://host/sub/TOKEN?x=1</c> -> <c>https://host/sub/TOKEN/info?x=1</c>. Null if not http(s).
    /// </summary>
    public static string? InfoUrl(string? subscriptionUrl)
    {
        if (subscriptionUrl.IsNullOrEmpty()
            || !Uri.TryCreate(subscriptionUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }
        var b = new UriBuilder(uri) { Fragment = string.Empty };
        b.Path = b.Path.TrimEnd('/') + "/info";
        return b.Uri.AbsoluteUri;
    }

    /// <summary>Only our own https links may be opened from data the server sent.</summary>
    public static bool IsSafeRenewUrl(string? url)
    {
        if (url.IsNullOrEmpty() || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }
        var host = u.Host.ToLowerInvariant();
        return host is "t.me" or "telegram.me" or "termitus.online" || host.EndsWith(".termitus.online", StringComparison.Ordinal);
    }

    /// <summary>"base64:0J/RgNC40LLQtdGC" -> decoded text; plain text as is.</summary>
    public static string? DecodeTitle(string? title)
    {
        if (title.IsNullOrEmpty())
        {
            return null;
        }
        title = title.Trim();
        if (title.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(title[7..].Trim()));
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return title;
    }

    /// <summary>
    /// Folds header data and /info data into what the page shows. Expiry comes from
    /// whichever source was fetched more recently (/info wins a tie); a fresh header-less
    /// subscription response means "no active subscription".
    /// </summary>
    public static TermitusAccountStatus Compute(TermitusAccountSnapshot? s, DateTimeOffset now)
    {
        if (s == null || (s.HeaderAt == null && s.Info == null))
        {
            return new TermitusAccountStatus(TermitusAccountState.Unknown, null, null, null, null, null, null, null, null, []);
        }

        var info = s.Info;
        var infoFresh = info != null && s.InfoAt != null && (s.HeaderAt == null || s.InfoAt >= s.HeaderAt);
        long? expire;
        bool active;
        if (infoFresh && info!.Expire != null)
        {
            expire = info.Expire;
            active = true;
        }
        else if (s.HeaderAt != null)
        {
            active = s.UserInfo != null;
            expire = s.UserInfo?.Expire;
        }
        else
        {
            // /info only, no expiry in it: we know nothing about the term.
            expire = null;
            active = true;
        }

        TermitusAccountState state;
        int? daysLeft = null;
        DateTimeOffset? until = expire is > 0 ? DateTimeOffset.FromUnixTimeSeconds(expire.Value) : null;
        if (!active)
        {
            state = TermitusAccountState.Inactive;
        }
        else if (until == null)
        {
            state = s.HeaderAt == null && !infoFresh ? TermitusAccountState.Unknown : TermitusAccountState.Active;
        }
        else if (until <= now)
        {
            state = TermitusAccountState.Expired;
        }
        else
        {
            daysLeft = (int)Math.Floor((until.Value - now).TotalDays);
            state = daysLeft <= TermitusAccountStatus.WarnDays ? TermitusAccountState.Expiring : TermitusAccountState.Active;
        }

        var ui = s.UserInfo;
        long? used = ui == null ? null : ui.Upload + ui.Download;
        long? total = ui is { Total: > 0 } ? ui.Total : null;
        var renew = info != null && IsSafeRenewUrl(info.RenewUrl) ? info.RenewUrl : null;
        var topup = info != null && IsSafeRenewUrl(info.TopupUrl) ? info.TopupUrl : null;
        var devices = info?.Devices?
            .Where(d => d != null)
            .Select(d => new TermitusDeviceRow(
                string.IsNullOrWhiteSpace(d.Name) ? "—" : d.Name.Trim(),
                d.Platform?.Trim() ?? string.Empty,
                d.IsActive ?? true))
            .ToList() ?? [];

        return new TermitusAccountStatus(
            state,
            until,
            daysLeft,
            used,
            total,
            string.IsNullOrWhiteSpace(info?.Plan) ? null : info!.Plan!.Trim(),
            info?.DevicesUsed ?? (info?.Devices != null ? devices.Count : null),
            info?.DevicesAllowed,
            renew,
            devices)
        {
            Balance = info?.Balance,
            TopupUrl = topup,
            Telegram = FormatTelegram(info?.Telegram),
        };
    }

    private static readonly Regex TelegramUsername = new("^[A-Za-z0-9_]{3,64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The Telegram account from /info as one line: "Иван · @ivan", "@ivan", "Иван";
    /// null when nothing usable. The username must look like a Telegram username (a leading
    /// "@" is tolerated); the name is trimmed, control characters dropped, max 64 chars.
    /// </summary>
    public static string? FormatTelegram(TermitusTelegramDto? tg)
    {
        if (tg == null)
        {
            return null;
        }
        var user = tg.Username?.Trim().TrimStart('@');
        if (user != null && !TelegramUsername.IsMatch(user))
        {
            user = null;
        }
        var name = tg.FirstName == null ? null : new string(tg.FirstName.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name is { Length: > 64 })
        {
            name = name[..64].TrimEnd() + "…";
        }
        if (string.IsNullOrEmpty(name))
        {
            name = null;
        }
        return (name, user) switch
        {
            (not null, not null) => $"{name} · @{user}",
            (null, not null) => $"@{user}",
            (not null, null) => name,
            _ => null,
        };
    }

    #endregion Pure logic (unit-tested)

    #region Formatting (RU first, EN second)

    private static readonly string[] RuMonthsGen =
        ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];

    private static readonly string[] EnMonths =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>"14 ноября" (or "14 ноября 2027" when not this year); EN: "November 14".</summary>
    public static string FormatDate(DateTimeOffset when, DateTimeOffset now, bool en)
    {
        var d = when.ToLocalTime();
        var year = d.Year != now.ToLocalTime().Year;
        return en
            ? $"{EnMonths[d.Month - 1]} {d.Day}{(year ? $", {d.Year}" : string.Empty)}"
            : $"{d.Day} {RuMonthsGen[d.Month - 1]}{(year ? $" {d.Year}" : string.Empty)}";
    }

    /// <summary>Human units, 1024-based: "820 МБ", "1,4 ГБ" / "1.4 GB".</summary>
    public static string FormatBytes(long bytes, bool en)
    {
        string[] ru = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        string[] enU = ["B", "KB", "MB", "GB", "TB"];
        var units = en ? enU : ru;
        double v = Math.Max(0, bytes);
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        var culture = en ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("ru-RU");
        var num = i == 0 || v >= 100 ? v.ToString("0", culture) : v.ToString("0.#", culture);
        return $"{num} {units[i]}";
    }

    /// <summary>"1 234 ₽", "99,50 ₽" (RU) / "1,234 RUB", "99.50 RUB" (EN).</summary>
    public static string FormatMoney(decimal rub, bool en)
    {
        var whole = decimal.Truncate(rub) == rub;
        if (en)
        {
            return $"{rub.ToString(whole ? "#,0" : "#,0.00", CultureInfo.InvariantCulture)} RUB";
        }
        var ru = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        ru.NumberGroupSeparator = "\u00A0";
        ru.NumberDecimalSeparator = ",";
        return $"{rub.ToString(whole ? "#,0" : "#,0.00", ru)}\u00A0₽";
    }

    /// <summary>Russian plural: 1 день, 2 дня, 5 дней.</summary>
    public static string RuPlural(int n, string one, string few, string many)
    {
        var a = Math.Abs(n) % 100;
        var b = a % 10;
        if (a is > 10 and < 20)
        {
            return many;
        }
        return b == 1 ? one : b is >= 2 and <= 4 ? few : many;
    }

    #endregion Formatting (RU first, EN second)

    #region Cache

    // In-memory copy of the cache file: the account render runs on every home refresh,
    // re-reading + re-parsing the JSON each time was pure waste (0.4 smoothness pass).
    // We are the only writer, so after the first read memory is the truth.
    private static volatile TermitusAccountSnapshot? _memory;
    private static volatile bool _memoryLoaded;

    public static TermitusAccountSnapshot? LoadCache()
    {
        if (_memoryLoaded)
        {
            return _memory;
        }
        var snap = ReadCacheFile();
        _memory = snap;
        _memoryLoaded = true;
        return snap;
    }

    private static TermitusAccountSnapshot? ReadCacheFile()
    {
        try
        {
            var path = Utils.GetConfigPath(CacheFile);
            if (!File.Exists(path))
            {
                return null;
            }
            return JsonSerializer.Deserialize<TermitusAccountSnapshot>(File.ReadAllText(path), InfoJsonOptions);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: cache read failed ({ex.GetType().Name})");
            return null;
        }
    }

    private static void SaveCache(TermitusAccountSnapshot snapshot)
    {
        _memory = snapshot;
        _memoryLoaded = true;
        try
        {
            var path = Utils.GetConfigPath(CacheFile);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>«Выйти»: forget the cached account (file + memory).</summary>
    public static async Task ClearCacheAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _memory = null;
            _memoryLoaded = true;
            var path = Utils.GetConfigPath(CacheFile);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: cache delete failed ({ex.GetType().Name})");
        }
        finally
        {
            _lock.Release();
        }
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private static async Task UpdateCacheAsync(Func<TermitusAccountSnapshot?, TermitusAccountSnapshot> update)
    {
        await _lock.WaitAsync();
        try
        {
            SaveCache(update(ReadCacheFile())); // fresh copy: callers may mutate it
        }
        finally
        {
            _lock.Release();
        }
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>Applies the header part of a subscription response to a snapshot (pure).</summary>
    public static TermitusAccountSnapshot WithHeaders(TermitusAccountSnapshot? old, string subId,
        string? userInfoHeader, string? profileTitle, string? supportUrl, DateTimeOffset now)
    {
        var keep = old != null && old.SubId == subId;
        return new TermitusAccountSnapshot
        {
            SubId = subId,
            HeaderAt = now.ToUnixTimeSeconds(),
            UserInfo = ParseUserInfo(userInfoHeader),
            ProfileTitle = DecodeTitle(profileTitle),
            SupportUrl = IsSafeRenewUrl(supportUrl) ? supportUrl : null,
            Info = keep ? old!.Info : null,
            InfoAt = keep ? old!.InfoAt : null,
        };
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var v)
            || (response.Content?.Headers.TryGetValues(name, out v) ?? false))
        {
            return string.Join(";", v);
        }
        return null;
    }

    #endregion Cache

    #region Network

    /// <summary>
    /// Hook (apply-changes.sh -> DownloadService.DownloadStringAsync): called after every
    /// successful upstream string download. Records the headers only when the URL is one
    /// of the user's subscriptions (main URL, no converter). Never throws, never logs the URL.
    /// </summary>
    public static async Task OnDownloadedAsync(string url, HttpResponseMessage response)
    {
        try
        {
            var subs = await AppManager.Instance.SubItems() ?? [];
            var sub = subs.FirstOrDefault(s => s.ConvertTarget.IsNullOrEmpty()
                && s.Url.IsNotEmpty()
                && Utils.GetPunycode(s.Url.TrimEx()) == url);
            if (sub == null)
            {
                return;
            }
            var ui = Header(response, HeaderUserInfo);
            var title = Header(response, HeaderProfileTitle);
            var support = Header(response, HeaderSupportUrl);
            await UpdateCacheAsync(old => WithHeaders(old, sub.Id, ui, title, support, DateTimeOffset.UtcNow));
            Logging.SaveLog($"{_tag}: subscription headers captured (userinfo={(ui != null ? "yes" : "no")})");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: header hook failed ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Re-reads account data for <paramref name="sub"/>: one header-only GET of the
    /// subscription URL (same User-Agent/headers as upstream; the body is not read) and
    /// one GET of /info. Through the local proxy when <paramref name="socksPort"/> is set,
    /// falling back to direct (as upstream does). Returns true if anything was updated.
    /// </summary>
    public static async Task<TermitusAccountRefresh> RefreshAsync(SubItem sub, int? socksPort, CancellationToken ct = default)
    {
        if (sub.Url.IsNullOrEmpty())
        {
            return new(false, true);
        }
        var url = Utils.GetPunycode(sub.Url.TrimEx());
        var ua = sub.UserAgent.IsNullOrEmpty() ? Utils.GetVersion(false) : sub.UserAgent;
        HttpRequestHeadersHelper.TryParse(sub.RequestHeaders, out var extra);
        var updated = false;

        // 1) subscription headers
        using (var resp = await SendAsync(url, ua, extra, "*/*", socksPort, ct))
        {
            if (resp is { IsSuccessStatusCode: true })
            {
                var ui = Header(resp, HeaderUserInfo);
                var title = Header(resp, HeaderProfileTitle);
                var support = Header(resp, HeaderSupportUrl);
                await UpdateCacheAsync(old => WithHeaders(old, sub.Id, ui, title, support, DateTimeOffset.UtcNow));
                updated = true;
            }
            else
            {
                Logging.SaveLog($"{_tag}: subscription check failed ({(resp == null ? "no response" : ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture))})");
            }
            if (resp == null)
            {
                // No HTTP answer at all: /info is on the same server, do not wait for a
                // second timeout — the page says "no connection" right away.
                return new(false, false);
            }
        }

        // 2) /info (optional; 404 until the backend ships it)
        var infoUrl = InfoUrl(url);
        if (infoUrl != null)
        {
            using var resp = await SendAsync(infoUrl, ua, extra, "application/json", socksPort, ct);
            TermitusAccountInfoDto? dto = null;
            if (resp is { IsSuccessStatusCode: true })
            {
                try
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    dto = body.Length <= 256 * 1024 ? ParseInfoJson(body) : null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    dto = null;
                }
            }
            if (dto != null)
            {
                await UpdateCacheAsync(old =>
                {
                    var s = old != null && old.SubId == sub.Id ? old : new TermitusAccountSnapshot { SubId = sub.Id };
                    s.Info = dto;
                    s.InfoAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return s;
                });
                updated = true;
            }
        }
        return new(updated, true);
    }

    private static async Task<HttpResponseMessage?> SendAsync(string url, string ua, Dictionary<string, string> extra,
        string accept, int? socksPort, CancellationToken ct)
    {
        IWebProxy?[] routes = socksPort is > 0
            ? [new WebProxy($"socks5://{Global.Loopback}:{socksPort}"), null]
            : [null];
        foreach (var proxy in routes)
        {
            try
            {
                var handler = new SocketsHttpHandler
                {
                    Proxy = proxy,
                    UseProxy = proxy != null,
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(10),
                };
                using var client = new HttpClient(handler) { Timeout = RequestTimeout };
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.TryParseAdd(ua);
                req.Headers.Accept.ParseAdd(accept);
                foreach (var (k, v) in extra)
                {
                    req.Headers.TryAddWithoutValidation(k, v);
                }
                // Headers only; the content stays unread (dispose closes the stream).
                var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (accept == "application/json" && resp.IsSuccessStatusCode)
                {
                    await resp.Content.LoadIntoBufferAsync(ct);
                }
                return resp;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Exception text can carry the host; type only, the URL is a secret.
                Logging.SaveLog($"{_tag}: request failed via {(proxy == null ? "direct" : "proxy")} ({ex.GetType().Name})");
            }
        }
        return null;
    }

    #endregion Network
}

/// <summary>Parsed <c>Subscription-Userinfo</c>. Expire null = no end date.</summary>
public sealed record TermitusSubUserInfo(long Upload, long Download, long Total, long? Expire);

/// <summary>
/// JSON contract of <c>GET &lt;subscription-url&gt;/info</c> (backend in progress).
/// Adjust names here only. Every field is optional.
/// </summary>
public sealed class TermitusAccountInfoDto
{
    [JsonPropertyName("expire")] public long? Expire { get; set; }
    [JsonPropertyName("plan")] public string? Plan { get; set; }
    [JsonPropertyName("devices_used")] public int? DevicesUsed { get; set; }
    [JsonPropertyName("devices_allowed")] public int? DevicesAllowed { get; set; }
    [JsonPropertyName("devices")] public List<TermitusAccountDeviceDto>? Devices { get; set; }
    [JsonPropertyName("renew_url")] public string? RenewUrl { get; set; }
    /// <summary>Account balance in roubles (TER 0.4).</summary>
    [JsonPropertyName("balance")] public decimal? Balance { get; set; }
    /// <summary>Where «Пополнить» leads; only https t.me / termitus.online is opened.</summary>
    [JsonPropertyName("topup_url")] public string? TopupUrl { get; set; }
    /// <summary>The Telegram account the subscription belongs to (TER 0.4.1; optional).</summary>
    [JsonPropertyName("telegram"), JsonConverter(typeof(TermitusLenientTelegramConverter))]
    public TermitusTelegramDto? Telegram { get; set; }
}

/// <summary>"telegram" that is not an object (a string, a number...) = absent, not a broken /info.</summary>
public sealed class TermitusLenientTelegramConverter : JsonConverter<TermitusTelegramDto?>
{
    public override TermitusTelegramDto? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }
        var dto = new TermitusTelegramDto();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            var name = reader.GetString();
            reader.Read();
            var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
            if (string.Equals(name, "username", StringComparison.OrdinalIgnoreCase))
            {
                dto.Username = value;
            }
            else if (string.Equals(name, "first_name", StringComparison.OrdinalIgnoreCase))
            {
                dto.FirstName = value;
            }
        }
        return dto;
    }

    public override void Write(Utf8JsonWriter writer, TermitusTelegramDto? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("username", value.Username);
        writer.WriteString("first_name", value.FirstName);
        writer.WriteEndObject();
    }
}

/// <summary><c>"telegram": {"username": "ivan" | null, "first_name": "Иван" | null}</c></summary>
public sealed class TermitusTelegramDto
{
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("first_name")] public string? FirstName { get; set; }
}

/// <summary>
/// Outcome of <see cref="TermitusAccount.RefreshAsync"/>. Reached = the server answered
/// anything over HTTP; false = no answer at all (offline, timeout, another VPN app in the way).
/// </summary>
public sealed record TermitusAccountRefresh(bool Updated, bool Reached);

public sealed class TermitusAccountDeviceDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("is_active")] public bool? IsActive { get; set; }
}

/// <summary>What is cached in guiConfigs/ter-account.json (no URL in it).</summary>
public sealed class TermitusAccountSnapshot
{
    public string SubId { get; set; } = string.Empty;
    /// <summary>Unix time of the last successful subscription response (header or not).</summary>
    public long? HeaderAt { get; set; }
    /// <summary>Null after a successful response without the header = no active subscription.</summary>
    public TermitusSubUserInfo? UserInfo { get; set; }
    public string? ProfileTitle { get; set; }
    public string? SupportUrl { get; set; }
    public TermitusAccountInfoDto? Info { get; set; }
    public long? InfoAt { get; set; }

    [JsonIgnore]
    public long? LastFetchedAt => InfoAt > HeaderAt || HeaderAt == null ? InfoAt : HeaderAt;
}

public enum TermitusAccountState
{
    Unknown,
    Active,
    Expiring,
    Expired,
    Inactive,
}

public sealed record TermitusDeviceRow(string Name, string Platform, bool IsActive)
{
    public bool HasPlatform => Platform.Length > 0;
}

public sealed record TermitusAccountStatus(
    TermitusAccountState State,
    DateTimeOffset? Until,
    int? DaysLeft,
    long? TrafficUsed,
    long? TrafficTotal,
    string? Plan,
    int? DevicesUsed,
    int? DevicesAllowed,
    string? RenewUrl,
    IReadOnlyList<TermitusDeviceRow> Devices)
{
    public decimal? Balance { get; init; }
    public string? TopupUrl { get; init; }
    /// <summary>"Иван · @ivan" (from /info "telegram"), null when the backend does not send it.</summary>
    public string? Telegram { get; init; }

    /// <summary>Warn (banner + amber chip) when this many days or fewer are left.</summary>
    public const int WarnDays = 3;
}
