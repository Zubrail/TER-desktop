namespace ServiceLib.Services;

/// <summary>
/// TER-only settings that have no upstream equivalent (upstream Config is not
/// ours to extend without patching its model). Stored as plain "key=value"
/// lines in guiConfigs/ter-settings.txt, next to ter-location.txt.
///
///  - KillSwitch (default OFF): "if the VPN drops while connected, keep the
///    internet blocked instead of silently going direct". See SECURITY.md #2
///    and TermitusConnectionGuard for exactly what this can and cannot do.
/// </summary>
public static class TermitusSettings
{
    private const string FileName = "ter-settings.txt";
    private const string KillSwitchKey = "kill_switch";
    private static readonly object _lock = new();

    public static bool KillSwitch
    {
        get => Read().TryGetValue(KillSwitchKey, out var v) && v == "1";
        set => Write(KillSwitchKey, value ? "1" : "0");
    }

    /// <summary>"system" (default, follows macOS live), "light" or "dark".</summary>
    public static string Theme
    {
        get => Read().TryGetValue("theme", out var v) && v is "light" or "dark" ? v : "system";
        set => Write("theme", value is "light" or "dark" ? value : "system");
    }

    /// <summary>"auto" (default), "tcp" (VLESS family) or "udp" (Hysteria2): which kind of transport Connect prefers.</summary>
    public static string Protocol
    {
        get => Read().TryGetValue("protocol", out var v) && v is "tcp" or "udp" ? v : "auto";
        set => Write("protocol", value is "tcp" or "udp" ? value : "auto");
    }

    /// <summary>«Проверять обновления автоматически» (0.6, default ON). Off = no automatic request at all; the manual button still works.</summary>
    public static bool AutoUpdate
    {
        get => !(Read().TryGetValue("auto_update", out var v) && v == "0");
        set => Write("auto_update", value ? "1" : "0");
    }

    /// <summary>"version|unix seconds" of the last «Позже» (the banner stays hidden for 24 h for that version).</summary>
    public static string UpdateSnooze
    {
        get => Read().TryGetValue("update_later", out var v) ? v : string.Empty;
        set => Write("update_later", value);
    }

    /// <summary>Parses "key=value" lines; ignores blanks, comments and junk.</summary>
    public static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return map;
    }

    private static Dictionary<string, string> Read()
    {
        lock (_lock)
        {
            try
            {
                var path = Utils.GetConfigPath(FileName);
                return File.Exists(path) ? Parse(File.ReadAllLines(path)) : [];
            }
            catch (Exception ex)
            {
                Logging.SaveLog("TermitusSettings", ex);
                return [];
            }
        }
    }

    private static void Write(string key, string value)
    {
        lock (_lock)
        {
            try
            {
                var map = Read();
                map[key] = value;
                File.WriteAllLines(Utils.GetConfigPath(FileName), map.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            }
            catch (Exception ex)
            {
                Logging.SaveLog("TermitusSettings", ex);
            }
        }
    }
}
