using System.Net;

using TUnit.Core;

using ServiceLib.Services;

namespace ServiceLib.Tests.Termitus;

/// <summary>0.6.5 local-port lockdown: credential shape, auth only in TUN mode, clash secret, bearer rule.</summary>
[NotInParallel]
public class TermitusLocalGuardTests
{
    [Test]
    public async Task Tokens_AreLongRandomAndUnique()
    {
        var set = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            var t = TermitusLocalGuard.NewToken(32);
            await Assert.That(t.Length).IsEqualTo(32);
            await Assert.That(TermitusLocalGuard.IsStrongCredential(t)).IsTrue();
            set.Add(t);
        }
        await Assert.That(set.Count).IsEqualTo(50);
        await Assert.That(TermitusLocalGuard.NewToken(3).Length).IsEqualTo(TermitusLocalGuard.MinCredentialLength); // never shorter than the minimum
        await Assert.That(TermitusLocalGuard.IsStrongCredential("short")).IsFalse();
        await Assert.That(TermitusLocalGuard.IsStrongCredential("has space in it 1234567890")).IsFalse();
    }

    [Test]
    public async Task TunMode_InboundGetsPasswordAuth_FreshAtEveryArm()
    {
        try
        {
            TermitusLocalGuard.Arm(41234);
            var s1 = new Inboundsettings4Ray { auth = "noauth" };
            await Assert.That(TermitusLocalGuard.ApplyInboundAuth(s1)).IsTrue();
            await Assert.That(s1.auth).IsEqualTo("password");
            var acc = s1.accounts!.Single();
            await Assert.That(TermitusLocalGuard.IsStrongCredential(acc.user)).IsTrue();
            await Assert.That(TermitusLocalGuard.IsStrongCredential(acc.pass)).IsTrue();
            await Assert.That(acc.user).IsEqualTo(TermitusLocalGuard.User);
            await Assert.That(acc.pass).IsEqualTo(TermitusLocalGuard.Pass);
            await Assert.That(TermitusLocalGuard.ChainPort).IsEqualTo(41234);
            await Assert.That(TermitusLocalGuard.EffectivePort(31808)).IsEqualTo(41234);

            var firstPass = TermitusLocalGuard.Pass;
            var firstSecret = TermitusLocalGuard.ClashSecret;
            TermitusLocalGuard.Arm(41235);
            await Assert.That(TermitusLocalGuard.Pass).IsNotEqualTo(firstPass);
            await Assert.That(TermitusLocalGuard.ClashSecret).IsNotEqualTo(firstSecret);
        }
        finally
        {
            TermitusLocalGuard.Disarm();
        }
    }

    [Test]
    public async Task ProxyMode_NoAuth_ConfiguredPortKept()
    {
        TermitusLocalGuard.Disarm();
        var s = new Inboundsettings4Ray { auth = "noauth" };
        await Assert.That(TermitusLocalGuard.ApplyInboundAuth(s)).IsFalse();
        await Assert.That(s.auth).IsEqualTo("noauth");
        await Assert.That(s.accounts).IsNull();
        await Assert.That(TermitusLocalGuard.IsArmed).IsFalse();
        await Assert.That(TermitusLocalGuard.EffectivePort(31808)).IsEqualTo(31808);
        await Assert.That(TermitusLocalGuard.ApplyInboundAuth(null)).IsFalse();
    }

    [Test]
    public async Task ProxyMode_DefaultPortBecomesARandomPerRunPort()
    {
        // pure rule (no shared state: other tests arm / disarm the guard in parallel)
        await Assert.That(TermitusLocalGuard.PickPort(null, 10808, () => 51234)).IsEqualTo(51234);
        await Assert.That(TermitusLocalGuard.PickPort(null, 20000, () => 51234)).IsEqualTo(20000); // chosen on purpose
        await Assert.That(TermitusLocalGuard.PickPort(41234, 10808, () => 51234)).IsEqualTo(41234); // armed TUN chain wins
        var p = TermitusLocalGuard.RunPort;
        await Assert.That(p).IsNotEqualTo(10808);
        await Assert.That(p).IsGreaterThanOrEqualTo(49152);
        await Assert.That(p).IsLessThanOrEqualTo(65000);
        await Assert.That(TermitusLocalGuard.RunPort).IsEqualTo(p); // stable for the whole run: system proxy and core agree
    }

    [Test]
    public async Task Serialized_Inbound_Has_Auth_Only_When_Armed()
    {
        try
        {
            var plain = JsonUtils.Serialize(new Inboundsettings4Ray { auth = "noauth" });
            await Assert.That(plain.Contains("password")).IsFalse();
            TermitusLocalGuard.Arm(41236);
            var s = new Inboundsettings4Ray { auth = "noauth" };
            TermitusLocalGuard.ApplyInboundAuth(s);
            var json = JsonUtils.Serialize(s);
            await Assert.That(json.Contains("\"auth\": \"password\"") || json.Contains("\"auth\":\"password\"")).IsTrue();
            await Assert.That(json.Contains(TermitusLocalGuard.Pass!)).IsTrue();
        }
        finally
        {
            TermitusLocalGuard.Disarm();
        }
    }

    [Test]
    public async Task Proxy_CarriesCredentials_OnlyForTheChainPort()
    {
        try
        {
            TermitusLocalGuard.Disarm();
            await Assert.That(TermitusLocalGuard.Proxy(31808).Credentials).IsNull();
            TermitusLocalGuard.Arm(41237);
            var cred = TermitusLocalGuard.Proxy(41237).Credentials as NetworkCredential;
            await Assert.That(cred).IsNotNull();
            await Assert.That(cred!.UserName).IsEqualTo(TermitusLocalGuard.User);
            await Assert.That(cred.Password).IsEqualTo(TermitusLocalGuard.Pass);
            await Assert.That(TermitusLocalGuard.Proxy(5555).Credentials).IsNull();
        }
        finally
        {
            TermitusLocalGuard.Disarm();
        }
    }

    [Test]
    public async Task ClashBearer_Rule()
    {
        var u = new Uri("http://127.0.0.1:41500/proxies");
        await Assert.That(TermitusClashAuthHandler.ShouldAuthorize(u, 41500, false)).IsTrue();
        await Assert.That(TermitusClashAuthHandler.ShouldAuthorize(u, 41501, false)).IsFalse();      // other port
        await Assert.That(TermitusClashAuthHandler.ShouldAuthorize(u, 41500, true)).IsFalse();       // already has a header
        await Assert.That(TermitusClashAuthHandler.ShouldAuthorize(new Uri("https://example.com:41500/x"), 41500, false)).IsFalse(); // never leak the secret off-box
        await Assert.That(TermitusClashAuthHandler.ShouldAuthorize(null, 41500, false)).IsFalse();
        await Assert.That(TermitusLocalGuard.ClashWsQuery.StartsWith("?token=")).IsTrue();
        await Assert.That(TermitusLocalGuard.IsStrongCredential(TermitusLocalGuard.ClashSecret)).IsTrue();
    }

    [Test]
    public async Task EphemeralPort_IsHighAndAvoidsGiven()
    {
        var p = TermitusLocalGuard.EphemeralPort();
        await Assert.That(p).IsGreaterThan(1024);
        var q = TermitusLocalGuard.EphemeralPort(p);
        await Assert.That(q).IsNotEqualTo(p);
    }
}

/// <summary>Generated core configs: the guard's effect on the real xray / sing-box config builders.</summary>
[NotInParallel]
public class TermitusLocalGuardConfigTests
{
    private static V2rayConfig GenXray(bool statistics)
    {
        var config = ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        config.GuiItem.EnableStatistics = statistics;
        ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        var context = ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);
        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        return JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
    }

    [Test]
    public async Task Xray_TunChain_InboundHasPassword_OnRandomPort_NoMetrics()
    {
        try
        {
            TermitusLocalGuard.Arm(43210);
            var cfg = GenXray(statistics: true);
            var inb = cfg.inbounds.First(i => i.protocol == nameof(EInboundProtocol.mixed));
            await Assert.That(inb.settings.auth).IsEqualTo("password");
            await Assert.That(inb.settings.accounts!.Single().pass).IsEqualTo(TermitusLocalGuard.Pass);
            await Assert.That(inb.port).IsEqualTo(43210);
            await Assert.That(inb.listen).IsEqualTo(Global.Loopback);
            await Assert.That(cfg.metrics).IsNull();
        }
        finally
        {
            TermitusLocalGuard.Disarm();
        }
    }

    [Test]
    public async Task Xray_ProxyMode_NoAuth_ConfiguredPort_MetricsOnLoopback()
    {
        TermitusLocalGuard.Disarm();
        var cfg = GenXray(statistics: true);
        var inb = cfg.inbounds.First(i => i.protocol == nameof(EInboundProtocol.mixed));
        await Assert.That(inb.settings.auth).IsEqualTo("noauth");
        await Assert.That(inb.settings.accounts).IsNull();
        await Assert.That(inb.listen).IsEqualTo(Global.Loopback);
        await Assert.That(cfg.metrics!.listen!.StartsWith(Global.Loopback + ":")).IsTrue();
    }

    [Test]
    public async Task Singbox_ClashApi_HasSecret_OnLoopback()
    {
        var config = ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.BindAppManagerConfig(config);
        var node = ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);
        var context = ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        var api = cfg.experimental!.clash_api!;
        await Assert.That(api.external_controller!.StartsWith(Global.Loopback + ":")).IsTrue();
        await Assert.That(api.secret).IsEqualTo(TermitusLocalGuard.ClashSecret);
        await Assert.That(TermitusLocalGuard.IsStrongCredential(api.secret)).IsTrue();
    }

    [Test]
    public async Task Singbox_SocksOutbound_ToChain_CarriesTheSameCredentials()
    {
        try
        {
            TermitusLocalGuard.Arm(43211);
            var config = ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
            ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.BindAppManagerConfig(config);
            var node = ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box);
            node.Address = Global.Loopback;
            node.Port = TermitusLocalGuard.ChainPort;
            node.Username = TermitusLocalGuard.User!;
            node.Password = TermitusLocalGuard.Pass!;
            var context = ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with { IsTunEnabled = true };
            var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
            var json = result.Data!.ToString()!;
            await Assert.That(json.Contains(TermitusLocalGuard.User!)).IsTrue();
            await Assert.That(json.Contains(TermitusLocalGuard.Pass!)).IsTrue();
        }
        finally
        {
            TermitusLocalGuard.Disarm();
        }
    }
}
