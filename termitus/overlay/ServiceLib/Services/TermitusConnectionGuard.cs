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

    /// <summary>The Mac woke from sleep (the clock jumped): connections made before are stale.</summary>
    Woke,
}

public enum TermitusGuardAction
{
    None,

    /// <summary>Restart the core (and, when Repick, re-test and re-pick the server first).</summary>
    Recover,

    /// <summary>Fast retries are used up while the core is dead: go to the loud "interrupted" state.</summary>
    GiveUp,

    /// <summary>Wake / network change settled: quietly probe the tunnel first; restart only if the probe fails.</summary>
    CheckHealth,

    /// <summary>The core is alive but 3 automatic fixes within 5 min did not help: say so, wait for the user's click.</summary>
    NoConnection,
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
/// Core DEAD:
///  - death is confirmed only on <see cref="DeadTicksToConfirm"/> consecutive ticks
///    (house debounce pattern; also hides the gap inside an upstream Reload);
///  - attempts back off 5s/15s/30s/60s; the 2nd and later attempts re-test the servers and re-pick;
///  - after <see cref="MaxAttempts"/> fast attempts: one GiveUp, then a slow retry every
///    <see cref="SlowRetry"/> (never a loop); the counter resets after <see cref="HealthyReset"/> of a live core.
///
/// Core ALIVE but the network changed or the Mac woke (lesson 44 follow-up):
///  - the event acts only after it has been stable for <see cref="NetworkSettle"/> / <see cref="WakeSettle"/>
///    and the machine is online (offline there is nothing to fix);
///  - it only asks for a quiet health probe (<see cref="TermitusGuardAction.CheckHealth"/>); the caller reports the
///    result with <see cref="ReportProbe"/>; a good tunnel is never restarted;
///  - a failed probe restarts the core: fix 1 keeps the server, fix 2 and 3 re-pick it ("after 2 failures switch");
///  - at most <see cref="MaxAliveAttempts"/> automatic fixes per <see cref="AliveWindow"/>; then ONE
///    <see cref="TermitusGuardAction.NoConnection"/> and silence until the user clicks (Reset) or a later probe passes.
/// </summary>
public sealed class TermitusReconnectPolicy
{
    public const int DeadTicksToConfirm = 2;
    public const int MaxAttempts = 5;
    public const int MaxAliveAttempts = 3;
    public static readonly TimeSpan NetworkSettle = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan WakeSettle = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CheckGap = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan AliveWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan HealthyReset = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SlowRetry = TimeSpan.FromMinutes(2);

    /// <summary>Pause after attempt n (1-based): index n-1, last value repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private string? _fingerprint;
    private DateTime? _networkChangedAt;
    private DateTime? _wakeAt;
    private bool _probeFailed;
    private int _deadTicks;
    private int _attempts;
    private DateTime _nextAllowed = DateTime.MinValue;
    private DateTime _nextCheck = DateTime.MinValue;
    private DateTime _lastAttemptAt = DateTime.MinValue;
    private readonly List<DateTime> _aliveFixes = [];

    public int Attempts => _attempts;
    public bool GaveUp { get; private set; }

    /// <summary>The core is alive but the automatic fixes are used up; waiting for the user.</summary>
    public bool NoConnection { get; private set; }

    /// <summary>New session (user pressed Connect/Disconnect/«переподключить»): forget history, keep the network baseline.</summary>
    public void Reset()
    {
        _networkChangedAt = null;
        _wakeAt = null;
        _probeFailed = false;
        _deadTicks = 0;
        _attempts = 0;
        _nextAllowed = DateTime.MinValue;
        _nextCheck = DateTime.MinValue;
        _lastAttemptAt = DateTime.MinValue;
        _aliveFixes.Clear();
        GaveUp = false;
        NoConnection = false;
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

    /// <summary>The Mac woke from sleep (see <see cref="TermitusWakeDetector"/>).</summary>
    public void ObserveWake(DateTime now) => _wakeAt = now;

    /// <summary>Result of a connectivity probe (after CheckHealth or after a restart).</summary>
    public void ReportProbe(bool ok)
    {
        if (ok)
        {
            _probeFailed = false;
            NoConnection = false;
        }
        else
        {
            _probeFailed = true;
        }
    }

    public TermitusGuardDecision Next(bool coreAlive, bool networkUp, DateTime now)
    {
        _deadTicks = coreAlive ? 0 : _deadTicks + 1;
        var coreDead = _deadTicks >= DeadTicksToConfirm;
        var networkSettled = _networkChangedAt is { } changedAt && now - changedAt >= NetworkSettle;
        var wokeSettled = _wakeAt is { } wokeAt && now - wokeAt >= WakeSettle;

        if (!coreDead && !networkSettled && !wokeSettled && !_probeFailed)
        {
            if (_attempts > 0 && coreAlive && now - _lastAttemptAt >= HealthyReset)
            {
                _attempts = 0;
                GaveUp = false;
            }
            return TermitusGuardDecision.None;
        }

        if (!coreDead)
        {
            return NextWithLiveCore(networkUp, networkSettled, wokeSettled, now);
        }

        if (!networkUp || now < _nextAllowed)
        {
            return TermitusGuardDecision.None; // keep the pending reason for later
        }

        const TermitusTrigger trigger = TermitusTrigger.CoreDied;
        if (_attempts >= MaxAttempts)
        {
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
        return new(TermitusGuardAction.Recover, trigger, _attempts, _attempts >= 2);
    }

    private TermitusGuardDecision NextWithLiveCore(bool networkUp, bool networkSettled, bool wokeSettled, DateTime now)
    {
        if (!networkUp)
        {
            return TermitusGuardDecision.None; // keep the pending reason for later
        }

        if (!_probeFailed)
        {
            // An event settled, nothing known to be wrong yet: ask for a quiet probe (rate-limited).
            if (now < _nextCheck)
            {
                return TermitusGuardDecision.None;
            }
            _nextCheck = now + CheckGap;
            var trigger = wokeSettled ? TermitusTrigger.Woke : TermitusTrigger.NetworkChanged;
            _networkChangedAt = null;
            _wakeAt = null;
            return new(TermitusGuardAction.CheckHealth, trigger, 0, false);
        }

        // The probe failed: restart (fix 1), switch server (fix 2, 3), or give the decision to the user.
        _aliveFixes.RemoveAll(t => now - t >= AliveWindow);
        if (_aliveFixes.Count >= MaxAliveAttempts)
        {
            _probeFailed = false;
            _networkChangedAt = null;
            _wakeAt = null;
            if (NoConnection)
            {
                return TermitusGuardDecision.None;
            }
            NoConnection = true;
            return new(TermitusGuardAction.NoConnection, TermitusTrigger.TunnelDead, _aliveFixes.Count, false);
        }
        if (now < _nextAllowed)
        {
            return TermitusGuardDecision.None;
        }
        _aliveFixes.Add(now);
        var fix = _aliveFixes.Count;
        _nextAllowed = now + Backoff[Math.Min(fix - 1, Backoff.Count - 1)];
        _lastAttemptAt = now;
        _networkChangedAt = null;
        _wakeAt = null;
        _probeFailed = false; // the caller probes again after the restart and reports
        return new(TermitusGuardAction.Recover, TermitusTrigger.TunnelDead, fix, fix >= 2);
    }

    private void MarkAttempt(DateTime now)
    {
        _lastAttemptAt = now;
        _networkChangedAt = null;
        _wakeAt = null;
        _probeFailed = false;
        _deadTicks = 0; // the restarted core gets the full debounce again
    }
}

/// <summary>
/// Notices that the machine slept: the guard timer ticks every ~2 s, so a gap of more than
/// <see cref="Threshold"/> between two observations means the process was suspended (lid closed,
/// idle sleep). Wall clock on purpose: it advances during sleep, the monotonic one may not.
/// A manual clock change looks the same, which is harmless (one quiet probe).
/// </summary>
public sealed class TermitusWakeDetector
{
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(10);

    private DateTime? _last;

    /// <summary>Call on every timer tick; true when the gap since the previous call is a sleep.</summary>
    public bool Observe(DateTime now)
    {
        var woke = _last is { } last && now - last > Threshold;
        _last = now;
        return woke;
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
                Proxy = TermitusLocalGuard.Proxy(localPort),
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
