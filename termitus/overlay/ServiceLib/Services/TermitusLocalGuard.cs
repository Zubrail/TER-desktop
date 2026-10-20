using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ServiceLib.Services;

/// <summary>
/// 0.6.5 stealth: while TER is connected in «Весь трафик» (TUN) mode nothing on localhost may be usable by another app
/// (some apps scan localhost for proxy / API ports to detect a VPN and learn its exit). In that mode:
///   - the xray "mixed" inbound that the sing-box TUN core chains to requires a username + password that are generated
///     fresh at every core (re)start, live only in memory and in the two generated config files, and are never logged;
///   - that inbound sits on a random ephemeral port instead of the fixed default;
///   - the xray "metrics" listener is not created (in TUN mode the speed comes from sing-box);
///   - the sing-box clash_api (traffic graph) has a random secret that our own client sends as a bearer token.
/// In proxy-only mode («только браузеры») the browser has to reach the inbound without a password (the macOS / Windows
/// system proxy cannot carry credentials), so it stays 127.0.0.1-only and unauthenticated, BUT (0.6.5 security review D-5) on
/// a RANDOM port chosen once per TER run instead of the fixed, well-known 10808: the system proxy is set programmatically
/// with that port, so nothing breaks, while a local app can no longer find the proxy by probing 10808. A port the user set
/// by hand in the advanced settings (anything but the default 10808) is respected. Residual risk, documented in SECURITY.md:
/// a same-user process that scans loopback ports (or reads the system proxy setting) can still use / detect the proxy in
/// this mode; only the TUN mode closes that completely.
/// Pure helpers are unit-tested; the few upstream hooks are patched in by apply-changes.sh.
/// </summary>
public static class TermitusLocalGuard
{
    public const int MinCredentialLength = 16;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    private sealed record State(int Port, string User, string Pass);

    private static volatile State? _armed;
    private static volatile string _clashSecret = NewToken(32);

    public static bool IsArmed => _armed != null;

    public static int ChainPort => _armed?.Port ?? 0;

    public static string? User => _armed?.User;

    public static string? Pass => _armed?.Pass;

    /// <summary>Random token of <paramref name="length"/> characters (letters/digits without look-alikes), cryptographically random.</summary>
    public static string NewToken(int length)
        => RandomNumberGenerator.GetString(Alphabet, Math.Max(length, MinCredentialLength));

    /// <summary>Shape a generated credential must have (used by tests and as a sanity check).</summary>
    public static bool IsStrongCredential(string? s)
        => s is { Length: >= MinCredentialLength } && s.All(c => Alphabet.Contains(c));

    /// <summary>TUN chain starts: new credentials and a new random port for this core (re)start.</summary>
    public static void Arm()
    {
        var p = EphemeralPort(AppManager.Instance.StatePort, AppManager.Instance.StatePort2);
        Arm(p is >= 49152 and <= 65000 ? p : 49152 + RandomNumberGenerator.GetInt32(15000)); // room for the +offset ports (speedtest = +21)
    }

    public static void Arm(int port)
    {
        _armed = new State(port, NewToken(16), NewToken(32));
        RotateClashSecret();
    }

    /// <summary>Proxy-only mode (or no chain): back to the configured port, no password.</summary>
    public static void Disarm() => _armed = null;

    /// <summary>The main inbound's local port: the random one while armed, else the configured one.</summary>
    public static int EffectivePort(int configuredPort) => PickPort(_armed?.Port, configuredPort, () => RunPort);

    /// <summary>Pure rule: armed chain port, else the per-run random port for the default, else the port the user chose.</summary>
    public static int PickPort(int? armedPort, int configuredPort, Func<int> runPort)
        => armedPort ?? (configuredPort == DefaultLocalPort ? runPort() : configuredPort);

    /// <summary>The upstream default (ConfigHandler) that used to be the fixed local port.</summary>
    public const int DefaultLocalPort = 10808;

    private static readonly Lazy<int> _runPort = new(() =>
    {
        var p = EphemeralPort(DefaultLocalPort);
        return p is >= 49152 and <= 65000 ? p : 49152 + RandomNumberGenerator.GetInt32(15000);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>One random loopback port per TER run for the proxy-only inbound (kept stable for the whole run so the system proxy and the core agree; thread-safe).</summary>
    public static int RunPort => _runPort.Value;

    /// <summary>Puts password auth on the xray inbound settings when armed. Returns true if auth was applied.</summary>
    public static bool ApplyInboundAuth(Inboundsettings4Ray? settings)
    {
        var s = _armed;
        if (s == null || settings == null)
        {
            return false;
        }
        settings.auth = "password";
        settings.accounts = [new() { user = s.User, pass = s.Pass }];
        return true;
    }

    /// <summary>Fresh clash secret for a (re)started sing-box; our own client picks it up from here.</summary>
    public static string ClashSecret => _clashSecret;

    public static void RotateClashSecret() => _clashSecret = NewToken(32);

    /// <summary>"?token=…" for the sing-box traffic websocket (browser-style auth the clash API accepts).</summary>
    public static string ClashWsQuery => "?token=" + _clashSecret;

    /// <summary>A free ephemeral loopback port picked by the OS (49152+ range), not a well-known default.</summary>
    public static int EphemeralPort(params int[] avoid)
    {
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                var port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                if (!avoid.Contains(port))
                {
                    return port;
                }
            }
            return 49152 + RandomNumberGenerator.GetInt32(16000);
        }
        catch (SocketException)
        {
            return 49152 + RandomNumberGenerator.GetInt32(16000);
        }
    }

    /// <summary>The SOCKS5 proxy for our own HTTP clients: with credentials when talking to the armed chain port.</summary>
    public static WebProxy Proxy(int port)
    {
        var p = new WebProxy($"socks5://{Global.Loopback}:{port}");
        var s = _armed;
        if (s != null && s.Port == port)
        {
            p.Credentials = new NetworkCredential(s.User, s.Pass);
        }
        return p;
    }
}

/// <summary>Adds the clash API bearer token to requests for our own sing-box controller (loopback + its port) only.</summary>
public sealed class TermitusClashAuthHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    /// <summary>Pure rule: only loopback, only the controller port, only when no Authorization header is set.</summary>
    public static bool ShouldAuthorize(Uri? uri, int controllerPort, bool hasAuthHeader)
        => uri != null && uri.IsLoopback && uri.Port == controllerPort && !hasAuthHeader;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (ShouldAuthorize(request.RequestUri, AppManager.Instance.StatePort2, request.Headers.Authorization != null))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TermitusLocalGuard.ClashSecret);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
