using System.Text.RegularExpressions;

namespace ServiceLib.Services;

/// <summary>
/// 0.6 «Сообщить о проблеме»: what the diagnostics block may contain and how it is built.
/// STRICT RULE: nothing here is sent by itself. The text is built locally, shown to the user in full
/// (the preview IS this string) and goes to the support endpoint only when the user presses «Отправить».
/// Everything automatically collected is masked: no subscription tokens, UUIDs, links, IPs, domain names,
/// long secrets, user names in paths (<see cref="MaskStrict"/>).
/// </summary>
public static partial class TermitusDiagnostics
{
    [GeneratedRegex(@"(?i)\b[a-z][a-z0-9+.\-]{1,15}://[^\s""'<>]+")]
    private static partial Regex UrlRx();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}(?::\d{1,5})?\b")]
    private static partial Regex Ipv4Rx();

    [GeneratedRegex(@"(?i)(?<![\w:.])(?=[0-9a-f:]*[0-9a-f])(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}(?![\w:])")]
    private static partial Regex Ipv6Rx();

    [GeneratedRegex(@"(?i)\b(?:[a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}\b")]
    private static partial Regex DomainRx();

    [GeneratedRegex(@"\b[A-Za-z0-9+/_\-]{24,}={0,2}")]
    private static partial Regex LongSecretRx();

    [GeneratedRegex(@"(/Users/|/home/|C:\\Users\\)[^/\\\s]+")]
    private static partial Regex UserPathRx();

    /// <summary>Masks everything that could identify an account, a server or a visited site.</summary>
    public static string MaskStrict(string? text)
    {
        if (text.IsNullOrEmpty())
        {
            return string.Empty;
        }
        var s = TermitusLogPrivacy.Mask(text) ?? string.Empty;
        s = UrlRx().Replace(s, "<ссылка>");
        s = UserPathRx().Replace(s, "$1<user>");
        s = Ipv4Rx().Replace(s, "<ip>");
        s = Ipv6Rx().Replace(s, "<ip>");
        s = DomainRx().Replace(s, "<домен>");
        s = LongSecretRx().Replace(s, "<секрет>");
        return s;
    }
}

/// <summary>One recorded connection error: when (UTC) and a masked, human text. Kept in memory only.</summary>
public sealed record TermitusErrorEntry(DateTime AtUtc, string Text);

/// <summary>
/// The last connection errors for the report. In memory only (gone when TER quits), never written to disk and never
/// sent anywhere on its own. Texts come from our own fixed phrases plus an exception TYPE name; they are masked anyway.
/// </summary>
public static class TermitusErrorLog
{
    public const int Capacity = 20;
    private static readonly object _lock = new();
    private static readonly List<TermitusErrorEntry> _items = [];

    public static void Add(string text, DateTime? atUtc = null)
    {
        var entry = new TermitusErrorEntry(atUtc ?? DateTime.UtcNow, TermitusDiagnostics.MaskStrict(text).Trim());
        lock (_lock)
        {
            _items.Add(entry);
            if (_items.Count > Capacity)
            {
                _items.RemoveAt(0);
            }
        }
    }

    public static List<TermitusErrorEntry> Last(int n)
    {
        lock (_lock)
        {
            return _items.TakeLast(n).ToList();
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }
}

/// <summary>Facts about this run that go into the diagnostics block.</summary>
public sealed record TermitusDiagInput(
    string AppVersion,
    string Os,
    string Mode,
    string Server,
    string State,
    string Route,
    string ProtocolPref,
    IReadOnlyList<TermitusErrorEntry> Errors,
    IReadOnlyList<string>? CheckLines,
    DateTime? CheckAtLocal,
    string? CheckNote);

public sealed record TermitusReportChip(string Id, string Title, bool IsSelected);

/// <summary>Builds the message that is previewed and sent. Pure; unit-tested.</summary>
public static class TermitusReport
{
    public const int MaxUserText = 1500;
    public const int MaxErrors = 5;
    private const string DiagHeader = "--- Диагностика (приложена пользователем кнопкой) ---";

    public static readonly string[] ChipIds = ["connect", "slow", "sites", "drops", "other"];

    public static string ChipTitle(string id, TermitusLocale loc) => id switch
    {
        "connect" => loc.ChipConnect,
        "slow" => loc.ChipSlow,
        "sites" => loc.ChipSites,
        "drops" => loc.ChipDrops,
        _ => loc.ChipOther,
    };

    /// <summary>The diagnostics block, already masked, without any trailing newline.</summary>
    public static string BuildDiagnostics(TermitusDiagInput d)
    {
        var sb = new StringBuilder();
        sb.AppendLine(DiagHeader);
        sb.AppendLine($"Приложение: {TermitusDiagnostics.MaskStrict(d.AppVersion)}");
        sb.AppendLine($"Система: {TermitusDiagnostics.MaskStrict(d.Os)}");
        sb.AppendLine($"Режим: {d.Mode}");
        sb.AppendLine($"Сервер: {TermitusDiagnostics.MaskStrict(d.Server)}");
        sb.AppendLine($"Состояние: {d.State}");
        sb.AppendLine($"Маршрут: {d.Route}; протокол: {d.ProtocolPref}");
        var errors = d.Errors.TakeLast(MaxErrors).ToList();
        if (errors.Count == 0)
        {
            sb.AppendLine("Ошибки подключения: не было с запуска приложения");
        }
        else
        {
            sb.AppendLine("Последние ошибки подключения:");
            foreach (var e in errors)
            {
                sb.AppendLine($" - {e.AtUtc.ToLocalTime():HH:mm} {e.Text}");
            }
        }
        if (d.CheckLines is { Count: > 0 })
        {
            sb.AppendLine($"Проверка подключения ({d.CheckAtLocal:HH:mm}):");
            foreach (var l in d.CheckLines)
            {
                sb.AppendLine($" - {TermitusDiagnostics.MaskStrict(l)}");
            }
        }
        else
        {
            sb.AppendLine($"Проверка подключения: {d.CheckNote ?? "не запускалась"}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The full message. User text first (masked only for tokens/links/UUIDs: it is theirs and they see it),
    /// then the diagnostics block when attached. Always &lt;= <paramref name="maxLength"/> (backend limit 4000):
    /// diagnostics lines are cut from the end.
    /// </summary>
    public static string Compose(string chipTitle, string? userText, string? diagnostics, bool attach, int maxLength = 3900)
    {
        var text = (userText ?? string.Empty).Trim();
        text = (TermitusLogPrivacy.Mask(text) ?? string.Empty);
        if (text.Length > MaxUserText)
        {
            text = text[..MaxUserText];
        }
        var head = $"[Сообщение из приложения TER] {chipTitle}" + (text.Length > 0 ? "\n" + text : string.Empty);
        if (!attach || diagnostics.IsNullOrEmpty())
        {
            return head;
        }
        var full = head + "\n\n" + diagnostics;
        if (full.Length <= maxLength)
        {
            return full;
        }
        var room = maxLength - head.Length - 2 - 20;
        var lines = diagnostics.Split('\n');
        var kept = new StringBuilder();
        foreach (var l in lines)
        {
            if (kept.Length + l.Length + 1 > room)
            {
                break;
            }
            kept.Append(l).Append('\n');
        }
        return head + "\n\n" + kept.ToString().TrimEnd() + "\n…(обрезано)";
    }
}

/// <summary>Unsent report kept on this computer (guiConfigs/ter-report-draft.json): chip, text, checkbox. No diagnostics.</summary>
public sealed class TermitusReportDraft
{
    private const string FileName = "ter-report-draft.json";

    [JsonPropertyName("chip")] public string Chip { get; set; } = "other";
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("attach")] public bool Attach { get; set; } = true;
    [JsonPropertyName("pending")] public bool Pending { get; set; }

    public static TermitusReportDraft? Load()
    {
        try
        {
            var path = Utils.GetConfigPath(FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<TermitusReportDraft>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save()
    {
        try
        {
            var path = Utils.GetConfigPath(FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.SaveLog($"TermitusReport: draft not saved ({ex.GetType().Name})");
        }
    }

    public static void Delete()
    {
        try
        {
            var path = Utils.GetConfigPath(FileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // next time
        }
    }
}

/// <summary>OS description for the report: "macOS 15.2 (arm64)". One short local `sw_vers` call.</summary>
public static class TermitusSystemInfo
{
    private static string? _cached;

    public static string Describe()
    {
        if (_cached != null)
        {
            return _cached;
        }
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string os;
        if (OperatingSystem.IsMacOS())
        {
            os = "macOS " + (SwVers() ?? Environment.OSVersion.Version.ToString(2));
        }
        else
        {
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        }
        return _cached = $"{os} ({arch})";
    }

    private static string? SwVers()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/sw_vers", "-productVersion") { RedirectStandardOutput = true, UseShellExecute = false });
            if (p == null)
            {
                return null;
            }
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return o.Length is > 0 and < 20 ? o : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
