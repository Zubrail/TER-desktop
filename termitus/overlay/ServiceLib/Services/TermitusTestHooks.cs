namespace ServiceLib.Services;

/// <summary>
/// Opt-in hooks for running a SECOND, throw-away TER next to the user's real one
/// (live checks before a release, TER 0.4.1). All off unless the env var is set;
/// a normal launch never sees any of this.
///
///  TER_TEST_INSTANCE=name  own single-instance mutex + "show window" channel (the real
///                          TER is neither blocked nor brought to the front), and the
///                          test instance never touches the system proxy or TUN
///                          (SysProxyType forced to Unchanged, EnableTun off) and does
///                          not start a core at launch — the real TER's connection is safe.
///  TER_TEST_API_BASE=url   base of /app/login/* instead of https://termitus.online
///                          (e.g. an unroutable address to see the offline states).
///  TER_TEST_NO_OPEN=1      never open links (Telegram/browser) — they are logged by kind only.
///  TER_TEST_TOUR=...       self-driving UI tour (v2rayN.Desktop/Common/TermitusSelfTest.cs).
/// Use with a portable build in its own folder (the build output is portable: data lives
/// next to the binary), never with /Applications/TER.app.
/// </summary>
public static class TermitusTestHooks
{
    /// <summary>
    /// 0.6.5 security review D-6: the TER_TEST_* variables exist only in Debug builds. A Release build (the one users get)
    /// never reads them, so a process or LaunchAgent that sets one cannot redirect login/update traffic or swap the update key.
    /// </summary>
    public static string? Env(string name)
    {
#if DEBUG
        return Environment.GetEnvironmentVariable(name);
#else
        return null;
#endif
    }

    /// <summary>True when the hooks are compiled in (Debug builds only).</summary>
    public static bool Compiled =>
#if DEBUG
        true;
#else
        false;
#endif

    public static readonly string? Instance = Clean(Env("TER_TEST_INSTANCE"));
    /// <summary>Only honoured on a test instance, and only https:// or a loopback http:// (the login answer carries the subscription URL).</summary>
    public static readonly string? ApiBase = SafeApiBase(Instance, Env("TER_TEST_API_BASE")?.Trim().TrimEnd('/'));
    public static readonly bool NoOpen = Env("TER_TEST_NO_OPEN") == "1";
    public static readonly string? Tour = Env("TER_TEST_TOUR");
    // 0.6, test instance only (each is ignored on a normal launch, see TermitusUpdateKey/TermitusUpdater/TermitusConnCheck):
    /// <summary>TER_TEST_UPDATE_URL: manifest URL instead of https://termitus.online/app/latest.json.</summary>
    public static readonly string? UpdateUrl = Env("TER_TEST_UPDATE_URL")?.Trim();
    /// <summary>TER_TEST_UPDATE_PUBKEY: base64 Ed25519 key used instead of the embedded one.</summary>
    public static readonly string? UpdatePubKey = Env("TER_TEST_UPDATE_PUBKEY")?.Trim();
    /// <summary>TER_TEST_UPDATE_TARGET: a throw-away .app folder the update swaps instead of the running bundle.</summary>
    public static readonly string? UpdateTarget = Env("TER_TEST_UPDATE_TARGET")?.Trim();
    /// <summary>TER_TEST_PROBE: simulated connection-check results, e.g. "udp=-1,tcp=120,Германия=-1" (substring of server name or tcp/udp = ms; -1 = fail).</summary>
    public static readonly string? ProbeSpec = Env("TER_TEST_PROBE")?.Trim();

    public static string? SafeApiBase(string? instance, string? url)
    {
        if (instance == null || string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return null;
        }
        return u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback) ? url : null;
    }

    public static bool IsTestInstance => Instance != null;

    /// <summary>Upstream's mutex name ("v2rayN"), or a private one for a test instance.</summary>
    public static string MutexName => Instance == null ? "v2rayN" : $"v2rayN-ter-test-{Instance}";

    /// <summary>Applied right after the config is loaded (TermitusDefaults.OnConfigLoaded).</summary>
    public static void ApplyToConfig(Config config)
    {
        if (!IsTestInstance)
        {
            return;
        }
        config.SystemProxyItem ??= new();
        config.SystemProxyItem.SysProxyType = ESysProxyType.Unchanged;
        config.TunModeItem ??= new();
        config.TunModeItem.EnableTun = false;
        config.GuiItem ??= new();
        config.GuiItem.AutoRun = false;
        // Own local ports (socks/http/speedtest/...): far from the real TER's 10808+.
        if (config.Inbound is { Count: > 0 })
        {
            config.Inbound.First().LocalPort = 31808;
        }
        Logging.SaveLog($"TER test instance '{Instance}': system proxy/TUN untouched");
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }
        var t = new string(s.Trim().Where(char.IsAsciiLetterOrDigit).Take(16).ToArray());
        return t.Length == 0 ? null : t;
    }
}
