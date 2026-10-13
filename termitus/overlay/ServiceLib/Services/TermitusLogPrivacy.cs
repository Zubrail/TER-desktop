using System.Text.RegularExpressions;

namespace ServiceLib.Services;

/// <summary>
/// 0.5 local-log hygiene (TER keeps no usage logs and sends none anywhere; what is written to
/// guiLogs is for support only): every line is masked before it reaches the file, and day files
/// older than <see cref="RetentionDays"/> are deleted at start.
///
/// Masked: subscription tokens in URLs, whole proxy links (vless://, hysteria2://, ...), UUIDs,
/// and values of password/token/secret/key-like fields. Pure function, unit-tested.
/// </summary>
public static partial class TermitusLogPrivacy
{
    public const int RetentionDays = 3;

    [GeneratedRegex(@"(?i)(/(?:subscription|sub)/)[A-Za-z0-9_\-\.~%]{6,}")]
    private static partial Regex SubTokenRx();

    [GeneratedRegex(@"(?i)\b(vless|vmess|trojan|ssr?|hysteria2?|hy2|tuic|wireguard|socks5?)://[^\s""'<>]+")]
    private static partial Regex ProxyLinkRx();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex UuidRx();

    [GeneratedRegex(@"(?i)\b(password|passwd|pass|token|secret|pbk|publickey|privatekey|shortid|sid|auth|obfs-password|poll_secret|code)([""']?\s*[:=]\s*[""']?)([^\s""',&}\]]{4,})")]
    private static partial Regex SecretFieldRx();

    public static string? Mask(string? text)
    {
        if (text.IsNullOrEmpty())
        {
            return text;
        }
        var s = SubTokenRx().Replace(text, "$1***");
        s = ProxyLinkRx().Replace(s, "$1://***");
        s = UuidRx().Replace(s, "****-uuid-****");
        s = SecretFieldRx().Replace(s, "$1$2***");
        return s;
    }

    /// <summary>Deletes *.txt day logs older than <paramref name="days"/> days (other files in the folder are left alone).</summary>
    public static int PurgeOld(string folder, DateTime nowUtc, int days = RetentionDays)
    {
        var deleted = 0;
        try
        {
            if (!Directory.Exists(folder))
            {
                return 0;
            }
            foreach (var f in Directory.EnumerateFiles(folder, "*.txt"))
            {
                try
                {
                    if (nowUtc - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(days))
                    {
                        File.Delete(f);
                        deleted++;
                    }
                }
                catch
                {
                    // locked or already gone: next start
                }
            }
        }
        catch
        {
            // never let log housekeeping break start-up
        }
        return deleted;
    }

    /// <summary>Called once from Logging.Setup (patched by apply-changes.sh).</summary>
    public static void PurgeOldLogs() => PurgeOld(Utils.GetLogPath(), DateTime.UtcNow);
}
