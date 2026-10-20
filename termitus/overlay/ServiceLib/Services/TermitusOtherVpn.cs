using System.Diagnostics;
using System.Net.NetworkInformation;

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
    /// <summary>Windows: one network adapter as the OS reports it (pure data, so the rules are testable).</summary>
    public readonly record struct AdapterInfo(string Name, string Description, NetworkInterfaceType Type, bool Up, bool HasDefaultRoute);

    // Names of the TUN adapters our own cores create (upstream: singbox_tun / xray_tun).
    private static readonly string[] OwnAdapterNames = ["singbox_tun", "xray_tun"];

    // Substrings (case-insensitive) in name/description that mark a VPN adapter.
    private static readonly string[] VpnMarkers =
    [
        "TAP-Windows", "TAP-", "WireGuard", "Wintun", "OpenVPN", "AnyConnect", "Cisco", "Fortinet", "Forti",
        "GlobalProtect", "PANGP", "Pulse", "ZeroTier", "Tailscale", "Hamachi", "NordLynx", "ProtonVPN",
        "Outline", "Amnezia", "sing-box", "Happ", "v2ray",
    ];

    // Windows' own IPv6-transition tunnels are type Tunnel but are not a VPN.
    private static readonly string[] NotVpnMarkers = ["Teredo", "ISATAP", "6to4", "IP-HTTPS", "Loopback"];

    private static bool Has(string? text, string[] markers) =>
        !string.IsNullOrEmpty(text) && markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the adapter is one of TER's own TUN adapters.</summary>
    public static bool IsOwnAdapter(string? name) =>
        !string.IsNullOrEmpty(name) && OwnAdapterNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Names of other VPN adapters that are up and carry the default route (Windows rules).</summary>
    public static IReadOnlyList<string> ClassifyWindowsAdapters(IEnumerable<AdapterInfo>? adapters)
    {
        var result = new List<string>();
        if (adapters == null)
        {
            return result;
        }
        foreach (var a in adapters)
        {
            if (!a.Up || !a.HasDefaultRoute || IsOwnAdapter(a.Name))
            {
                continue;
            }
            if (Has(a.Name, NotVpnMarkers) || Has(a.Description, NotVpnMarkers))
            {
                continue;
            }
            // Ppp alone is NOT a VPN: USB 3G/4G modems and PPPoE are often the only internet link. It counts with a marker.
            var looksLikeVpn = a.Type == NetworkInterfaceType.Tunnel
                || Has(a.Name, VpnMarkers) || Has(a.Description, VpnMarkers);
            // A physical Ethernet/Wi-Fi card is never "another VPN", whatever the vendor string says.
            if (a.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 && !Has(a.Description, VpnMarkers) && !Has(a.Name, VpnMarkers))
            {
                continue;
            }
            if (looksLikeVpn && !result.Contains(a.Name))
            {
                result.Add(a.Name);
            }
        }
        return result;
    }

    /// <summary>
    /// Windows `route print -4` -> local interface addresses that own a default route (0.0.0.0/0 or the
    /// 0/1 + 128/1 split pair). Rows are "Destination Netmask Gateway Interface Metric"; numbers only,
    /// so it does not depend on the system language.
    /// </summary>
    public static IReadOnlySet<string> ParseWindowsDefaultRouteInterfaces(string? routePrint)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (routePrint.IsNullOrEmpty())
        {
            return result;
        }
        foreach (var raw in routePrint.Split('\n'))
        {
            var c = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (c.Length < 5 || !System.Net.IPAddress.TryParse(c[0], out _) || !System.Net.IPAddress.TryParse(c[3], out _))
            {
                continue;
            }
            var isDefault = (c[0] == "0.0.0.0" && (c[1] == "0.0.0.0" || c[1] == "128.0.0.0"))
                || (c[0] == "128.0.0.0" && c[1] == "128.0.0.0");
            if (isDefault)
            {
                result.Add(c[3]);
            }
        }
        return result;
    }

    private static async Task<IReadOnlyList<string>> DetectWindowsAsync()
    {
        string? routes = null;
        try
        {
            var psi = new ProcessStartInfo("route.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("print");
            psi.ArgumentList.Add("-4");
            using var p = Process.Start(psi);
            if (p != null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                routes = await p.StandardOutput.ReadToEndAsync(cts.Token);
                await p.WaitForExitAsync(cts.Token);
            }
        }
        catch
        {
            // best effort: the gateway check below still works without the route table
        }
        var routeIps = ParseWindowsDefaultRouteInterfaces(routes);
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var props = ni.GetIPProperties();
                var hasGw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !g.Address.Equals(System.Net.IPAddress.Any));
                var hasRoute = hasGw || props.UnicastAddresses.Any(u => routeIps.Contains(u.Address.ToString()));
                list.Add(new AdapterInfo(ni.Name, ni.Description, ni.NetworkInterfaceType, ni.OperationalStatus == OperationalStatus.Up, hasRoute));
            }
            catch
            {
                // an adapter that cannot be read is skipped
            }
        }
        return ClassifyWindowsAdapters(list);
    }

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
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return await DetectWindowsAsync();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("TermitusOtherVpn", ex);
                return [];
            }
        }
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
