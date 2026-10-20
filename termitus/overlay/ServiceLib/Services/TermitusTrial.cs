using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace ServiceLib.Services;

/// <summary>Body of POST /app/trial (null ref / campaign are not sent).</summary>
public sealed record TermitusTrialRequest(
    [property: JsonPropertyName("consent")] bool Consent,
    [property: JsonPropertyName("install_id")] string InstallId,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ref = null,
    [property: JsonPropertyName("campaign"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Campaign = null);

public sealed record TermitusTrialResponse(
    [property: JsonPropertyName("subscription_url")] string? SubscriptionUrl,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("days")] int? Days,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);

public enum TermitusTrialKind
{
    Ok,
    /// <summary>429: busy | ip | subnet | install (see <see cref="TermitusTrialOutcome.Reason"/>).</summary>
    Refused,
    NoConsent,
    BadInstall,
    NotConfigured,
    Offline,
    Failed,
}

public sealed record TermitusTrialOutcome(TermitusTrialKind Kind, string? SubscriptionUrl = null, string? Code = null,
    int Days = 0, DateTimeOffset? ExpiresAt = null, string? Reason = null, string? Detail = null);

/// <summary>
/// 0.6.5 «Попробовать бесплатно — 2 дня»: POST /app/trial {consent, install_id, platform, ref?, campaign?}.
/// install_id = 32 random URL-safe characters made once per install, kept in TER's settings file, never logged and sent
/// only to /app/trial. The subscription link and the gift code are secrets: never logged. A refusal is never retried
/// automatically. Everything but <see cref="StartAsync"/> is pure and unit-tested.
/// </summary>
public static class TermitusTrial
{
    private const string _tag = "TermitusTrial";
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    public const int InstallIdLength = 32;
    public const string BotName = "TErpiUsis_bot";
    private static readonly Regex InstallIdPattern = new("^[A-Za-z0-9_-]{16,128}$", RegexOptions.CultureInvariant);
    private static readonly Regex CodePattern = new("^[A-Za-z0-9_-]{4,128}$", RegexOptions.CultureInvariant);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    #region Pure logic

    /// <summary>32 cryptographically random URL-safe characters.</summary>
    public static string NewInstallId() => RandomNumberGenerator.GetString(Alphabet, InstallIdLength);

    public static bool IsValidInstallId(string? s) => s != null && InstallIdPattern.IsMatch(s);

    /// <summary>"macos" | "windows"; null where the backend has no trial platform (Linux, other) = the button is hidden.</summary>
    public static string? TrialPlatform() => OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : null;

    /// <summary>The stored id, or a fresh one that is stored right away (once per install).</summary>
    public static string InstallId()
    {
        var cur = TermitusSettings.InstallIdRaw;
        if (IsValidInstallId(cur))
        {
            return cur;
        }
        var id = NewInstallId();
        TermitusSettings.InstallIdRaw = id;
        return id;
    }

    public static TermitusTrialRequest BuildRequest(string installId, string platform, string? refCode = null, string? campaign = null)
        => new(true, installId, platform, refCode.IsNullOrEmpty() ? null : refCode, campaign.IsNullOrEmpty() ? null : campaign);

    /// <summary>Terms / consent / privacy page on the site (same pages the site's consent line links to).</summary>
    public static string LegalUrl(string kind, bool en)
    {
        var page = kind switch { "consent" => "consent.html", "privacy" => "privacy.html", _ => "terms.html" };
        return $"https://termitus.online/static/{page}{(en ? "?lang=en" : string.Empty)}";
    }

    /// <summary>Opens the bot with the trial's gift code: the code is redeemed there and the trial becomes a real account.</summary>
    public static string? BindUrl(string? code)
        => code != null && CodePattern.IsMatch(code) ? $"https://t.me/{BotName}?start=redeem_{Uri.EscapeDataString(code)}" : null;

    public static string BotUrl => $"https://t.me/{BotName}";

    /// <summary>Server text for the user: single plain line, no control characters, at most 300 chars.</summary>
    public static string CleanDetail(string? s)
    {
        var t = new string((s ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > 300 ? t[..300] : t;
    }

    /// <summary>Maps one answer (status null = no HTTP answer) to what the UI does. Never throws.</summary>
    public static TermitusTrialOutcome Interpret(int? status, string? body, string? trialReason)
    {
        switch (status)
        {
            case null:
                return new(TermitusTrialKind.Offline);
            case 200:
                TermitusTrialResponse? r = null;
                try
                {
                    r = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<TermitusTrialResponse>(body, TermitusAccount.InfoJsonOptions);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
                {
                    r = null;
                }
                if (r != null && IsAcceptableUrl(r.SubscriptionUrl) && r.Code != null && CodePattern.IsMatch(r.Code) && r.Days is > 0 and <= 366)
                {
                    return new(TermitusTrialKind.Ok, r.SubscriptionUrl!.Trim(), r.Code, r.Days.Value, r.ExpiresAt);
                }
                return new(TermitusTrialKind.Failed);
            case 429:
                var reason = (trialReason ?? string.Empty).Trim().ToLowerInvariant();
                if (reason is not ("busy" or "ip" or "subnet" or "install"))
                {
                    reason = "ip";
                }
                return new(TermitusTrialKind.Refused, Reason: reason, Detail: DetailOf(body));
            case 400:
                return new(TermitusTrialKind.NoConsent);
            case 422:
                return new(TermitusTrialKind.BadInstall);
            case 503:
                return new(TermitusTrialKind.NotConfigured);
            case 408:
            case >= 500:
                return new(TermitusTrialKind.Offline);
            default:
                return new(TermitusTrialKind.Failed);
        }
    }

    private static string DetailOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                ? CleanDetail(d.GetString())
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>https without credentials (same rule as the Telegram login); a test instance also takes its fake backend on loopback http.</summary>
    public static bool IsAcceptableUrl(string? url) => TermitusLogin.IsAcceptableSubscriptionUrl(url);

    /// <summary>"days|unix seconds" for the settings file.</summary>
    public static string EncodeInfo(int days, DateTimeOffset until) => $"{days}|{until.ToUnixTimeSeconds()}";

    public static (int Days, DateTimeOffset Until)? DecodeInfo(string? raw)
    {
        var p = (raw ?? string.Empty).Split('|');
        return p.Length == 2
            && int.TryParse(p[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0
            && long.TryParse(p[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var u)
                ? (d, DateTimeOffset.FromUnixTimeSeconds(u))
                : null;
    }

    /// <summary>
    /// «Пробный доступ: осталось 1 день, до 21 октября» (same «time left» rule and wording as the banner and the sidebar chip);
    /// after the end «Пробный доступ закончился 21 октября». The date always carries the month.
    /// </summary>
    public static string TrialLine(int days, DateTimeOffset until, DateTimeOffset now, bool en)
    {
        var date = TermitusAccount.FormatDate(until, now, en);
        if (until <= now)
        {
            return en ? $"Trial access ended on {date}" : $"Пробный доступ закончился {date}";
        }
        var left = TermitusAccount.LeftPhrase(TermitusAccount.Left(until, now)!.Value, en);
        return en ? $"Trial access: {left}, until {date}" : $"Пробный доступ: {left}, до {date}";
    }

    #endregion Pure logic

    #region Network

    /// <summary>POST /app/trial, direct (nothing is connected on first run). One attempt, no automatic retry.</summary>
    public static async Task<TermitusTrialOutcome> StartAsync(CancellationToken ct)
    {
        var platform = TrialPlatform();
        if (platform == null)
        {
            return new(TermitusTrialKind.Failed);
        }
        var req = BuildRequest(InstallId(), platform);
        var res = await TermitusHttp.PostJsonAsync($"{TermitusLogin.ApiBase}/app/trial", req, null, RequestTimeout, ct);
        var o = Interpret(res.Status, res.Body, res.TrialReason);
        Logging.SaveLog($"{_tag}: trial -> {o.Kind}{(o.Reason == null ? string.Empty : " (" + o.Reason + ")")}");
        return o;
    }

    #endregion Network
}
