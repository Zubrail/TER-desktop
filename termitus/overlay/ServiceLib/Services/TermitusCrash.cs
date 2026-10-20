using System.Text;
using System.Text.RegularExpressions;

namespace ServiceLib.Services;

/// <summary>
/// 0.6.5 crash capture, consent based (mirrors the Android 0.1.3 flow, TerCrash.kt): an unhandled exception is written to ONE small
/// local file (version, system, time, stack trace) with everything that looks like a link, UUID, IP address, key or the Windows user
/// name removed. Nothing is ever sent by itself: on the next launch the user is asked in the «Сообщить о проблеме» sheet, sees exactly the text,
/// and only «Отправить» posts it through the support chat. Closing the sheet or sending deletes the file.
/// </summary>
public static partial class TermitusCrash
{
    public const string Prefix = "[Отчёт об ошибке]";
    public const int MaxFrames = 60;
    public const int MaxChars = 3800;
    private const string FileName = "ter-crash.txt";

    [GeneratedRegex(@"(?i)\b[a-z][a-z0-9+.\-]{1,15}://[^\s""'<>)]+")]
    private static partial Regex AnyUrlRx();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Rx();

    [GeneratedRegex(@"(?i)(?<![0-9a-f:])(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}(?![0-9a-f:])")]
    private static partial Regex Ipv6Rx();

    // long token-like strings that contain a digit (class names and method names do not)
    [GeneratedRegex(@"(?=[A-Za-z+/_\-]*\d)[A-Za-z0-9+/_\-]{24,}={0,2}")]
    private static partial Regex DigitSecretRx();

    [GeneratedRegex(@"(?i)([A-Z]:[\\/]Users[\\/])[^\\/\s:""']+")]
    private static partial Regex WinUserRx();

    [GeneratedRegex(@"(?i)(/Users/|/home/)[^/\s:""']+")]
    private static partial Regex UnixUserRx();

    /// <summary>Strips links, UUIDs, IPs, key-like strings and the user name in profile paths. Stack frames stay readable.</summary>
    public static string Redact(string? text)
    {
        var s = TermitusLogPrivacy.Mask(text) ?? string.Empty;
        s = AnyUrlRx().Replace(s, "<ссылка>");
        s = Ipv4Rx().Replace(s, "<ip>");
        s = Ipv6Rx().Replace(s, "<ip>");
        s = WinUserRx().Replace(s, "$1<user>");
        s = UnixUserRx().Replace(s, "$1<user>");
        s = DigitSecretRx().Replace(s, "<секрет>");
        return s;
    }

    /// <summary>Pure: the report text.</summary>
    public static string Build(string version, string system, DateTimeOffset at, Exception e)
    {
        var trace = string.Join('\n', e.ToString().Split('\n').Take(MaxFrames).Select(l => l.TrimEnd('\r')));
        var sb = new StringBuilder();
        sb.Append(Prefix).Append(" TER Desktop ").Append(version).Append('\n');
        sb.Append("Система: ").Append(Redact(system)).Append('\n');
        sb.Append("Время: ").Append(at.ToUniversalTime().ToString("u", System.Globalization.CultureInfo.InvariantCulture)).Append("\n\n");
        sb.Append(Redact(trace));
        var all = sb.ToString();
        return all.Length > MaxChars ? all[..MaxChars] : all;
    }

    public static string FilePath() => Utils.GetConfigPath(FileName);

    /// <summary>Called from the unhandled-exception hooks. Must never make a crash worse: every failure is swallowed.</summary>
    public static void Write(Exception? e)
    {
        if (e == null)
        {
            return;
        }
        try
        {
            var text = Build(Utils.GetVersionInfo(), TermitusSystemInfo.Describe(), DateTimeOffset.UtcNow, e);
            var path = FilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception)
        {
            // reporting must never make a crash worse
        }
    }

    /// <summary>The waiting report, or null.</summary>
    public static string? Pending()
    {
        try
        {
            var path = FilePath();
            var fi = new FileInfo(path);
            return fi.Exists && fi.Length > 0 && fi.Length < 64 * 1024 ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            File.Delete(FilePath());
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
