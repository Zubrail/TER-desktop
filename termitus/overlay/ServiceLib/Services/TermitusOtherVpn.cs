using System.Diagnostics;

namespace ServiceLib.Services;

/// <summary>
/// 0.5: spots another VPN on this Mac, purely locally (reads the routing table with
/// the system `netstat`; nothing is sent anywhere). Two VPNs fighting over the
/// default route is the commonest reason for "connected, but nothing opens" /
/// "server unreachable", so TER explains it calmly instead of failing silently.
///
/// A VPN shows up as a tunnel interface (utunN) that owns the *unscoped* default
/// route, either `default` itself or the 0/1 + 128.0/1 split pair. Scoped routes
/// (flag I) exist for every utun macOS makes for itself (iCloud Private Relay,
/// AirDrop...) and are ignored.
/// </summary>
public static class TermitusOtherVpn
{
    /// <summary>utun interfaces that currently carry the default route; empty = none.</summary>
    public static IReadOnlyList<string> ParseDefaultRouteTunnels(string? netstatInet)
    {
        var result = new List<string>();
        if (netstatInet.IsNullOrEmpty())
        {
            return result;
        }
        foreach (var raw in netstatInet.Split('\n'))
        {
            var cols = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Destination Gateway Flags Netif [Expire]
            if (cols.Length < 4)
            {
                continue;
            }
            var dest = cols[0];
            if (dest != "default" && dest != "0/1" && dest != "128.0/1" && dest != "0.0.0.0/1" && dest != "128.0.0.0/1")
            {
                continue;
            }
            var flags = cols[2];
            var netif = cols[3];
            if (flags.Contains('I') || !netif.StartsWith("utun", StringComparison.Ordinal))
            {
                continue;
            }
            if (!result.Contains(netif))
            {
                result.Add(netif);
            }
        }
        return result;
    }

    /// <summary>Other-VPN tunnels right now (call only while TER's own tunnel is down).</summary>
    public static async Task<IReadOnlyList<string>> DetectAsync()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return [];
        }
        try
        {
            var psi = new ProcessStartInfo("/usr/sbin/netstat")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-rn");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("inet");
            using var p = Process.Start(psi);
            if (p == null)
            {
                return [];
            }
            var outputTask = p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var text = await outputTask.WaitAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return ParseDefaultRouteTunnels(text);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TermitusOtherVpn", ex);
            return [];
        }
    }
}
