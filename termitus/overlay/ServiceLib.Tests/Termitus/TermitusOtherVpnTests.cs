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
}
