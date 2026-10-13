namespace ServiceLib.Services;

/// <summary>Status + body of one call. Status null = no HTTP answer at all (offline, timeout, TLS).</summary>
public sealed record TermitusHttpResult(int? Status, string? Body, TimeSpan? RetryAfter = null)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>
/// Small HTTP transport for the TER endpoints (login, support). Rules:
///  - the URL is a secret (subscription token / login code inside): never logged,
///    exceptions are logged by type only;
///  - through the local SOCKS inbound when a port is given (we are connected),
///    falling back to direct, exactly like TermitusAccount;
///  - bodies capped at 256 KB; clients are reused (support polls every 10 s
///    while its page is open, a new TLS handshake each time would be wasteful).
/// </summary>
public static class TermitusHttp
{
    private const string _tag = "TermitusHttp";
    private const int MaxBody = 256 * 1024;
    private static readonly object _lock = new();
    private static HttpClient? _direct;
    private static (int Port, HttpClient Client)? _socks;

    public static string UserAgent => Utils.GetVersion(false);

    private static HttpClient NewClient(IWebProxy? proxy) => new(new SocketsHttpHandler
    {
        Proxy = proxy,
        UseProxy = proxy != null,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // per-request timeout via CancellationToken
    };

    /// <summary>The shared client for a route (null = direct); used by the updater's download.</summary>
    public static HttpClient ClientFor(int? socksPort) => Client(socksPort);

    private static HttpClient Client(int? socksPort)
    {
        lock (_lock)
        {
            if (socksPort is not > 0)
            {
                return _direct ??= NewClient(null);
            }
            if (_socks is { } s && s.Port == socksPort)
            {
                return s.Client;
            }
            var c = NewClient(new WebProxy($"socks5://{Global.Loopback}:{socksPort}"));
            _socks = (socksPort.Value, c); // the old one is left for the GC: in-flight requests may still use it
            return c;
        }
    }

    public static Task<TermitusHttpResult> GetAsync(string url, int? socksPort, TimeSpan timeout, CancellationToken ct)
        => SendAsync(HttpMethod.Get, url, null, socksPort, timeout, ct);

    public static Task<TermitusHttpResult> PostJsonAsync<T>(string url, T body, int? socksPort, TimeSpan timeout, CancellationToken ct)
        => SendAsync(HttpMethod.Post, url, JsonSerializer.Serialize(body), socksPort, timeout, ct);

    /// <param name="userAgent">null = TER/version; the updater passes the bare generic "TER" (no version, no identifiers).</param>
    public static async Task<TermitusHttpResult> SendAsync(HttpMethod method, string url, string? json, int? socksPort,
        TimeSpan timeout, CancellationToken ct, string? userAgent = null)
    {
        int?[] routes = socksPort is > 0 ? [socksPort, null] : [null];
        foreach (var port in routes)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                using var req = new HttpRequestMessage(method, url);
                req.Headers.UserAgent.TryParseAdd(userAgent ?? UserAgent);
                req.Headers.Accept.ParseAdd("application/json");
                if (json != null)
                {
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }
                using var resp = await Client(port).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                string? body = null;
                if (resp.Content.Headers.ContentLength is null or <= MaxBody)
                {
                    body = await resp.Content.ReadAsStringAsync(cts.Token);
                    if (body.Length > MaxBody)
                    {
                        body = null;
                    }
                }
                return new TermitusHttpResult((int)resp.StatusCode, body, RetryAfterOf(resp));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"{_tag}: request failed via {(port == null ? "direct" : "proxy")} ({ex.GetType().Name})");
            }
        }
        return new TermitusHttpResult(null, null);
    }

    /// <summary>Retry-After (seconds or HTTP date), capped at 10 min; null when absent.</summary>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        TimeSpan? t = ra?.Delta ?? (ra?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        return t is { } v ? TimeSpan.FromSeconds(Math.Clamp(v.TotalSeconds, 0, 600)) : null;
    }

    /// <summary>
    /// Opens an https link in the browser / Telegram. Unlike ProcUtils.ProcessStart it
    /// never logs the exception message (it could carry the URL with a login code).
    /// </summary>
    public static bool OpenUrl(string url)
    {
        if (TermitusTestHooks.NoOpen)
        {
            Logging.SaveLog($"{_tag}: open link suppressed (test instance, {(TermitusAccount.IsSafeRenewUrl(url) ? "our https link" : "other link")})");
            return true;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: open link failed ({ex.GetType().Name})");
            return false;
        }
    }
}
