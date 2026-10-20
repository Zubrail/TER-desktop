using System.Net.NetworkInformation;

using TUnit.Core;

using ServiceLib.Services;

namespace ServiceLib.Tests.Termitus;

/// <summary>0.5 other-VPN detection: parse `netstat -rn -f inet` (sample taken from a Mac running Happ TUN).</summary>
public class TermitusOtherVpnTests
{
    private const string Header = "Routing tables\n\nInternet:\nDestination        Gateway            Flags               Netif Expire\n";

    [Test]
    public async Task UnscopedDefaultOnUtun_IsAnotherVpn()
    {
        var t = Header +
            "default            link#26            UCSg                utun8       \n" +
            "default            192.168.0.1        UGScIg                en0       \n" +
            "1.1.1.1            link#26            UHWIig              utun8       \n" +
            "10                 192.168.0.1        UGSc                  en0       \n";
        await Assert.That(TermitusOtherVpn.ParseDefaultRouteTunnels(t).Single()).IsEqualTo("utun8");
    }

    [Test]
    public async Task SplitDefault_0_1_And_128_Is_Detected_Once()
    {
        var t = Header +
            "default            192.168.0.1        UGScg                 en0       \n" +
            "0/1                10.8.0.1           UGSc                utun4       \n" +
            "128.0/1            10.8.0.1           UGSc                utun4       \n";
        await Assert.That(TermitusOtherVpn.ParseDefaultRouteTunnels(t).Single()).IsEqualTo("utun4");
    }

    [Test]
    public async Task PlainWifi_And_ScopedUtun_AreNotAVpn()
    {
        var t = Header +
            "default            192.168.0.1        UGScg                 en0       \n" +
            "default            link#14            UCSIg               utun2       \n" +
            "17.248.176.5       link#26            UHWIig              utun8       \n";
        await Assert.That(TermitusOtherVpn.ParseDefaultRouteTunnels(t)).IsEmpty();
        await Assert.That(TermitusOtherVpn.ParseDefaultRouteTunnels(null)).IsEmpty();
        await Assert.That(TermitusOtherVpn.ParseDefaultRouteTunnels("")).IsEmpty();
    }

    private static TermitusOtherVpn.AdapterInfo A(string name, string desc, NetworkInterfaceType t, bool up = true, bool gw = true) => new(name, desc, t, up, gw);

    [Test]
    public async Task Windows_TapAdapterWithDefaultRoute_IsAnotherVpn()
    {
        var r = TermitusOtherVpn.ClassifyWindowsAdapters([
            A("Wi-Fi", "Intel(R) Wi-Fi 6 AX201", NetworkInterfaceType.Wireless80211),
            A("OpenVPN", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet)]);
        await Assert.That(r.Single()).IsEqualTo("OpenVPN");
    }

    [Test]
    public async Task Windows_OurOwnTun_NeverCounts()
    {
        var r = TermitusOtherVpn.ClassifyWindowsAdapters([
            A("singbox_tun", "Wintun Userspace Tunnel", NetworkInterfaceType.Ethernet),
            A("xray_tun", "Wintun Userspace Tunnel", NetworkInterfaceType.Tunnel)]);
        await Assert.That(r).IsEmpty();
    }

    [Test]
    public async Task Windows_DownOrNoRoute_OrTransitionTunnels_AreIgnored()
    {
        var r = TermitusOtherVpn.ClassifyWindowsAdapters([
            A("WireGuard", "WireGuard Tunnel", NetworkInterfaceType.Ethernet, up: false),
            A("Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Ethernet, gw: false),
            A("Teredo Tunneling Pseudo-Interface", "Teredo Tunneling Pseudo-Interface", NetworkInterfaceType.Tunnel),
            A("Ethernet", "Realtek PCIe GbE", NetworkInterfaceType.Ethernet)]);
        await Assert.That(r).IsEmpty();
        await Assert.That(TermitusOtherVpn.ClassifyWindowsAdapters(null)).IsEmpty();
    }

    [Test]
    public async Task Windows_PppAndTunnelTypes_AreVpns_VendorNamesToo()
    {
        var r = TermitusOtherVpn.ClassifyWindowsAdapters([
            A("Ethernet 3", "Fortinet Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet),
            A("Ethernet 4", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter", NetworkInterfaceType.Ethernet)]);
        await Assert.That(r.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Windows_RoutePrint_FindsDefaultAndSplitRoutes()
    {
        const string t = "IPv4 Route Table\n=====\nActive Routes:\nNetwork Destination        Netmask          Gateway       Interface  Metric\n" +
            "          0.0.0.0          0.0.0.0      192.168.0.1    192.168.0.50     35\n" +
            "          0.0.0.0        128.0.0.0         10.8.0.1        10.8.0.2      5\n" +
            "        128.0.0.0        128.0.0.0         10.8.0.1        10.8.0.2      5\n" +
            "         10.0.0.0        255.0.0.0         On-link        10.9.0.2    281\n";
        var r = TermitusOtherVpn.ParseWindowsDefaultRouteInterfaces(t);
        await Assert.That(r.Count).IsEqualTo(2);
        await Assert.That(r.Contains("10.8.0.2")).IsTrue();
        await Assert.That(r.Contains("10.9.0.2")).IsFalse();
        await Assert.That(TermitusOtherVpn.ParseWindowsDefaultRouteInterfaces(null)).IsEmpty();
    }

    [Test]
    public async Task Windows_PlainPpp_IsNotAVpn_ButMarkedPppIs()
    {
        var modem = TermitusOtherVpn.ClassifyWindowsAdapters([A("Mobile", "Huawei USB 4G Modem", NetworkInterfaceType.Ppp)]);
        await Assert.That(modem).IsEmpty();
        var vpn = TermitusOtherVpn.ClassifyWindowsAdapters([A("OpenVPN PPP", "OpenVPN dial-up", NetworkInterfaceType.Ppp)]);
        await Assert.That(vpn.Single()).IsEqualTo("OpenVPN PPP");
    }
}
