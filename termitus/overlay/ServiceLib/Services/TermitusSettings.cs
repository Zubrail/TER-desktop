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

    /// <summary>«Защищать весь трафик компьютера» (0.6.1, default ON): Connect brings TUN up,
    /// asking the admin password once per app run (upstream keeps it in memory only and
    /// resets EnableTun on every launch -- this is the user's lasting choice instead).</summary>
    public static bool AllTraffic
    {
        get => !(Read().TryGetValue("all_traffic", out var v) && v == "0");
        set => Write("all_traffic", value ? "1" : "0");
    }

    /// <summary>One-shot (0.6.4, Windows): «Продолжить» in the administrator sheet sets it just before TER restarts elevated;
    /// the elevated start clears it and connects by itself.</summary>
    public static bool ConnectAfterElevation
    {
        get => Read().TryGetValue("connect_after_elevation", out var v) && v == "1";
        set => Write("connect_after_elevation", value ? "1" : "0");
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

    /// <summary>0.6.5 trial: random id made ONCE per install (see TermitusTrial.InstallId). Never logged, never sent anywhere but /app/trial.</summary>
    public static string InstallIdRaw
    {
        get => Read().TryGetValue("install_id", out var v) ? v : string.Empty;
        set => Write("install_id", value);
    }

    /// <summary>The gift code of the in-app trial (redeemed in the bot to turn the trial into a real account); empty = no trial.</summary>
    public static string TrialCode
    {
        get => Read().TryGetValue("trial_code", out var v) ? v : string.Empty;
        set => Write("trial_code", value);
    }

    /// <summary>"days|unix seconds of the end" of the trial, or empty.</summary>
    public static string TrialInfo
    {
        get => Read().TryGetValue("trial_info", out var v) ? v : string.Empty;
        set => Write("trial_info", value);
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
