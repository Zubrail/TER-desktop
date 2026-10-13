namespace ServiceLib.Services;

/// <summary>Why the guard wants to act.</summary>
public enum TermitusTrigger
{
    None,

    /// <summary>The core process exited while the user is "connected".</summary>
    CoreDied,

    /// <summary>The physical network changed (Wi-Fi ↔ other) and has settled.</summary>
    NetworkChanged,

    /// <summary>After a network change the core is alive but nothing passes through it.</summary>
    TunnelDead,
}

public enum TermitusGuardAction
{
    None,

    /// <summary>Restart the core (and, when Repick, re-test and re-pick the server first).</summary>
    Recover,

    /// <summary>Fast retries are used up while the core is dead: go to the loud "interrupted" state.</summary>
    GiveUp,
}

public readonly record struct TermitusGuardDecision(TermitusGuardAction Action, TermitusTrigger Trigger, int Attempt, bool Repick)
{
    public static readonly TermitusGuardDecision None = new(TermitusGuardAction.None, TermitusTrigger.None, 0, false);
}

/// <summary>
/// Pure decision logic of the TER connection guard (auto-reconnect + kill switch).
/// No I/O, no clock of its own: the caller feeds observations every tick and
/// gets back what to do, so the anti-loop rules are unit-tested.
///
/// Rules:
///  - core death is confirmed only on <see cref="DeadTicksToConfirm"/> consecutive
///    ticks (house debounce pattern; also hides the gap inside an upstream Reload);
///  - a network change acts only after the fingerprint has been stable for
///    <see cref="NetworkSettle"/> and the machine is online (Wi-Fi hand-over
///    produces several changes in a row; offline there is nothing to fix);
///  - attempts back off 5s/15s/30s/60s; the 2nd and later attempts (and a dead
///    tunnel) re-test the servers and re-pick via TermitusLocations;
///  - after <see cref="MaxAttempts"/> fast attempts with the core still dead:
///    one GiveUp, then a slow retry every <see cref="SlowRetry"/> (never a loop);
///  - with the core alive, network/probe-triggered restarts stop at MaxAttempts
///    (no restart storms on a flapping Wi-Fi) and never cause a GiveUp;
///  - the counter resets after <see cref="HealthyReset"/> of a live core.
/// </summary>
public sealed class TermitusReconnectPolicy
{
    public const int DeadTicksToConfirm = 2;
    public const int MaxAttempts = 5;
    public static readonly TimeSpan NetworkSettle = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan HealthyReset = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SlowRetry = TimeSpan.FromMinutes(2);

    /// <summary>Pause after attempt n (1-based): index n-1, last value repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private string? _fingerprint;
    private DateTime? _networkChangedAt;
    private bool _probeFailed;
    private int _deadTicks;
    private int _attempts;
    private DateTime _nextAllowed = DateTime.MinValue;
    private DateTime _lastAttemptAt = DateTime.MinValue;

    public int Attempts => _attempts;
    public bool GaveUp { get; private set; }

    /// <summary>New session (user pressed Connect/Disconnect): forget history, keep the network baseline.</summary>
    public void Reset()
    {
        _networkChangedAt = null;
        _probeFailed = false;
        _deadTicks = 0;
        _attempts = 0;
        _nextAllowed = DateTime.MinValue;
        _lastAttemptAt = DateTime.MinValue;
        GaveUp = false;
    }

    public void ObserveNetwork(string fingerprint, DateTime now)
    {
        if (_fingerprint == null)
        {
            _fingerprint = fingerprint;
            return;
        }
        if (!string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
        {
            _fingerprint = fingerprint;
            _networkChangedAt = now; // restart the settle window on every change
        }
    }

    /// <summary>Result of the connectivity probe run after a network-change restart.</summary>
    public void ReportProbe(bool ok)
    {
        if (!ok)
        {
            _probeFailed = true;
        }
    }

    public TermitusGuardDecision Next(bool coreAlive, bool networkUp, DateTime now)
    {
        _deadTicks = coreAlive ? 0 : _deadTicks + 1;
        var coreDead = _deadTicks >= DeadTicksToConfirm;
        var networkSettled = _networkChangedAt is { } changedAt && now - changedAt >= NetworkSettle;

        if (!coreDead && !networkSettled && !_probeFailed)
        {
            if (_attempts > 0 && coreAlive && now - _lastAttemptAt >= HealthyReset)
            {
                _attempts = 0;
                GaveUp = false;
            }
            return TermitusGuardDecision.None;
        }

        if (!networkUp || now < _nextAllowed)
        {
            return TermitusGuardDecision.None; // keep the pending reason for later
        }

        var trigger = coreDead ? TermitusTrigger.CoreDied
            : _probeFailed ? TermitusTrigger.TunnelDead
            : TermitusTrigger.NetworkChanged;

        if (_attempts >= MaxAttempts)
        {
            if (!coreDead)
            {
                // Core is alive: stop restarting it for network reasons.
                _networkChangedAt = null;
                _probeFailed = false;
                return TermitusGuardDecision.None;
            }
            _nextAllowed = now + SlowRetry;
            if (!GaveUp)
            {
                GaveUp = true;
                return new(TermitusGuardAction.GiveUp, trigger, _attempts, false);
            }
            MarkAttempt(now);
            return new(TermitusGuardAction.Recover, trigger, _attempts + 1, true);
        }

        _attempts++;
        _nextAllowed = now + Backoff[Math.Min(_attempts - 1, Backoff.Count - 1)];
        MarkAttempt(now);
        return new(TermitusGuardAction.Recover, trigger, _attempts, _attempts >= 2 || trigger == TermitusTrigger.TunnelDead);
    }

    private void MarkAttempt(DateTime now)
    {
        _lastAttemptAt = now;
        _networkChangedAt = null;
        _probeFailed = false;
        _deadTicks = 0; // the restarted core gets the full debounce again
    }
}

/// <summary>Physical-network fingerprint used to notice Wi-Fi ↔ other switches.</summary>
public static class TermitusNetwork
{
    // Tunnels (ours and the system's), loopback and Apple peer-to-peer links that
    // flap on their own (AirDrop/awdl, llw, anpi, Internet Sharing bridges).
    private static readonly string[] _ignoredPrefixes =
        ["utun", "tun", "lo", "singbox", "xray", "awdl", "llw", "anpi", "bridge", "gif", "stf"];

    /// <summary>
    /// "en0=192.168.1.5;en7=10.0.0.3|v6": sorted usable IPv4 addresses per interface,
    /// plus a flag for a global IPv6 address (the v6 address itself is not used:
    /// privacy addresses rotate daily and would look like a network change).
    /// </summary>
    public static string Fingerprint(IEnumerable<(string Name, IPAddress Address)> addresses)
    {
        var v4 = new SortedSet<string>(StringComparer.Ordinal);
        var v6 = false;
        foreach (var (name, addr) in addresses)
        {
            if (name.IsNullOrEmpty() || _ignoredPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (IPAddress.IsLoopback(addr))
            {
                continue;
            }
            if (addr.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = addr.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254)
                {
                    continue; // link-local (no DHCP yet)
                }
                v4.Add($"{name}={addr}");
            }
            else if (addr.AddressFamily == AddressFamily.InterNetworkV6
                     && !addr.IsIPv6LinkLocal && !addr.IsIPv6SiteLocal && !addr.IsIPv6UniqueLocal)
            {
                v6 = true;
            }
        }
        return string.Join(";", v4) + (v6 ? "|v6" : string.Empty);
    }

    public static bool IsUp(string fingerprint) => fingerprint.Length > 0;

    public static string Current()
    {
        try
        {
            var list = new List<(string, IPAddress)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    list.Add((nic.Name, ua.Address));
                }
            }
            return Fingerprint(list);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TermitusNetwork", ex);
            return "?"; // unknown: treat as "up, unchanged"
        }
    }

    /// <summary>
    /// True when an HTTP request through the local core (SOCKS5 on loopback)
    /// gets an answer. Used only after a network-change restart.
    /// </summary>
    public static async Task<bool> ProbeThroughCoreAsync(int localPort, string url, TimeSpan timeout)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://{Global.Loopback}:{localPort}"),
                UseProxy = true,
                AllowAutoRedirect = false,
            };
            using var client = new HttpClient(handler) { Timeout = timeout };
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return (int)resp.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }
}
