using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace ServiceLib.Services;

/// <summary>
/// 0.5 live stats for the connect screen and the tray popover. Everything stays in memory:
/// the last 60 speed samples live in a ring buffer, nothing is written to disk or sent
/// anywhere. Speeds come from the local core's own counters (upstream statistics).
/// </summary>
public sealed class TermitusSpeedHistory
{
    public const int Capacity = 60;
    private readonly long[] _down = new long[Capacity];
    private readonly long[] _up = new long[Capacity];
    private int _count;
    private readonly object _lock = new();

    public void Add(long down, long up)
    {
        lock (_lock)
        {
            Array.Copy(_down, 1, _down, 0, Capacity - 1);
            Array.Copy(_up, 1, _up, 0, Capacity - 1);
            _down[^1] = Math.Max(0, down);
            _up[^1] = Math.Max(0, up);
            _count = Math.Min(Capacity, _count + 1);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_down);
            Array.Clear(_up);
            _count = 0;
        }
    }

    public (long[] Down, long[] Up) Snapshot()
    {
        lock (_lock)
        {
            return ((long[])_down.Clone(), (long[])_up.Clone());
        }
    }
}

public static class TermitusLive
{
    public const double GraphWidth = 360;
    public const double GraphHeight = 44;

    /// <summary>Bytes per second as "0 Б/с", "850 КБ/с", "1,2 МБ/с".</summary>
    public static string FormatSpeed(long bytesPerSecond, bool english = false)
    {
        var ci = CultureInfo.GetCultureInfo(english ? "en-US" : "ru-RU");
        var b = Math.Max(0, bytesPerSecond);
        var (unitB, unitK, unitM, unitG) = english ? ("B/s", "KB/s", "MB/s", "GB/s") : ("Б/с", "КБ/с", "МБ/с", "ГБ/с");
        if (b < 1000)
        {
            return $"{b.ToString(ci)} {unitB}";
        }
        if (b < 1_000_000)
        {
            return $"{(b / 1000.0).ToString(b < 10_000 ? "0.0" : "0", ci)} {unitK}";
        }
        if (b < 1_000_000_000)
        {
            return $"{(b / 1_000_000.0).ToString(b < 100_000_000 ? "0.0" : "0", ci)} {unitM}";
        }
        return $"{(b / 1_000_000_000.0).ToString("0.0", ci)} {unitG}";
    }

    /// <summary>
    /// SVG path data for the graph. Down = filled area, up = a line. Both share one scale so the
    /// shapes are comparable; the scale never goes below 50 KB/s, so an idle line stays flat.
    /// </summary>
    public static (string DownArea, string UpLine) GraphPaths(long[] down, long[] up,
        double width = GraphWidth, double height = GraphHeight)
    {
        var n = down.Length;
        var max = Math.Max(50_000L, Math.Max(down.Max(), up.Max()));
        var inv = CultureInfo.InvariantCulture;
        double X(int i) => n <= 1 ? 0 : width * i / (n - 1);
        double Y(long v) => height - 2 - (height - 6) * Math.Min(1.0, (double)v / max);

        // 0.6.2: both paths start with a zero-length anchor at the top-left corner. The view
        // draws them with Stretch="Fill", which scales the geometry's OWN bounds to the box:
        // without the anchor an idle graph (all samples ~0, a 2 px sliver at the bottom) was
        // blown up into a solid orange rectangle (owner's screenshot, 0.6.0). With it the
        // bounds always span the full 0..height, so Fill only scales the width.
        const string anchor = "M0,0 L0,0 ";
        var area = new System.Text.StringBuilder(anchor);
        area.Append(CultureInfo.InvariantCulture, $"M0,{height.ToString("0.#", inv)} ");
        for (var i = 0; i < n; i++)
        {
            area.Append(CultureInfo.InvariantCulture, $"L{X(i).ToString("0.#", inv)},{Y(down[i]).ToString("0.#", inv)} ");
        }
        area.Append(CultureInfo.InvariantCulture, $"L{width.ToString("0.#", inv)},{height.ToString("0.#", inv)} Z");

        var line = new System.Text.StringBuilder(anchor);
        for (var i = 0; i < n; i++)
        {
            line.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? "M" : "L")}{X(i).ToString("0.#", inv)},{Y(up[i]).ToString("0.#", inv)} ");
        }
        return (area.ToString(), line.ToString().TrimEnd());
    }

    /// <summary>"good" under 150 ms, "mid" under 400, else "bad"; empty when unknown.</summary>
    public static string PingClass(int ms) => ms <= 0 ? string.Empty : ms < 150 ? "good" : ms < 400 ? "mid" : "bad";

    /// <summary>
    /// One round trip through our local SOCKS inbound to the same URL upstream's "real delay"
    /// test uses (so it measures the whole tunnel, not just the first hop). Returns ms, or -1.
    /// </summary>
    public static async Task<int> PingThroughTunnelAsync(int socksPort, string? url, CancellationToken ct)
    {
        if (socksPort <= 0 || url.IsNullOrEmpty())
        {
            return -1;
        }
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://{Global.Loopback}:{socksPort}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            var sw = Stopwatch.StartNew();
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            return resp.IsSuccessStatusCode ? Math.Max(1, (int)sw.ElapsedMilliseconds) : -1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"TermitusLive: ping failed ({ex.GetType().Name})");
            return -1;
        }
    }
}
