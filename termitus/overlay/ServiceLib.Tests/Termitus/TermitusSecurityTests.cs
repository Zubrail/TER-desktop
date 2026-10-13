using ServiceLib.Tests.CoreConfig;
using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>
/// SECURITY.md re-verification on REAL generated core configs (0.3):
/// loopback-only inbound, no access log, upstream self-update disabled,
/// sing-box TUN leak-safe (strict_route, IPv6 into the tunnel, DNS hijacked
/// and resolved through the proxy). Shares AppManager config with other
/// config tests, so it does not run in parallel with them.
/// </summary>
[NotInParallel("termitus-appmanager-config")]
public class TermitusSecurityTests
{
    /// <summary>A config the way TER leaves it: hardened + leak-safe defaults + TER DNS preset.</summary>
    private static Config TerConfig(ECoreType coreType)
    {
        var config = CoreConfigTestFactory.CreateConfig(coreType);
        config.Inbound[0].AllowLANConn = true; // must be tightened back
        config.CoreBasicItem.LogEnabled = true; // must be switched off
        config.TunModeItem.StrictRoute = false; // a 0.1/0.2 config could carry this
        config.Inbound[0].SniffingEnabled = false;
        TermitusSecurity.ApplyCoreDefaults(config);
        TermitusDefaults.ApplyLeakSafeDefaults(config);
        // As TermitusRussiaPreset sets it.
        config.SimpleDNSItem.DirectDNS = "77.88.8.8";
        config.SimpleDNSItem.RemoteDNS = "8.8.8.8,https://dns.google/dns-query,1.1.1.1";
        config.SimpleDNSItem.BootstrapDNS = "77.88.8.8";
        CoreConfigTestFactory.BindAppManagerConfig(config);
        return config;
    }

    [Test]
    public async Task Xray_SystemProxyMode_InboundIsLoopbackOnly_AndNoTrafficLog()
    {
        var config = TerConfig(ECoreType.Xray);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray);

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        await result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
        await Assert.That(cfg.inbounds.Count).IsGreaterThan(0);
        foreach (var inbound in cfg.inbounds)
        {
            await Assert.That(inbound.listen).IsEqualTo(Global.Loopback);
        }
        await Assert.That(cfg.log.access).IsNull();
        await Assert.That(cfg.log.error).IsNull();
    }

    [Test]
    public async Task Singbox_MixedInbound_IsLoopbackOnly_AndNoLogFile()
    {
        var config = TerConfig(ECoreType.sing_box);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box);

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();

        await result.Success.Should().BeTrue();
        var cfg = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
        foreach (var inbound in cfg.inbounds.Where(i => i.type == nameof(EInboundProtocol.mixed)))
        {
            await Assert.That(inbound.listen).IsEqualTo(Global.Loopback);
        }
        await Assert.That(cfg.log.output).IsNull();
    }

    /// <summary>The TUN pre-core exactly as upstream builds it on macOS (legacy protect):
    /// a sing-box SOCKS node pointing at the local Xray mixed port.</summary>
    private static (SingboxConfig Cfg, Config Config) TunPreCore(bool hostHasGlobalV6)
    {
        var config = TerConfig(ECoreType.sing_box);
        config.TunModeItem.EnableTun = true;
        config.TunModeItem.EnableIPv6Address = false; // upstream default
        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box);
        node.Address = Global.Loopback;
        node.Port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        node.Username = null;
        node.Password = null;
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box, hostHasGlobalV6) with
        {
            IsTunEnabled = true,
        };
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        if (result.Success != true)
        {
            throw new InvalidOperationException(result.Msg);
        }
        return (JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!, config);
    }

    [Test]
    public async Task Tun_StrictRouteAndAutoRoute_On()
    {
        var (cfg, _) = TunPreCore(true);
        var tun = cfg.inbounds.Single(i => i.type == "tun");
        await Assert.That(tun.auto_route).IsTrue();
        await Assert.That(tun.strict_route).IsTrue();
    }

    [Test]
    public async Task Tun_HostWithGlobalIPv6_RoutesIPv6IntoTunnel()
    {
        var (cfg, _) = TunPreCore(true);
        var tun = cfg.inbounds.Single(i => i.type == "tun");
        await Assert.That(tun.address!.Any(a => a.Contains(':'))).IsTrue()
            .Because("without a tun IPv6 address sing-box adds no ::/0 route and IPv6 bypasses the VPN");
    }

    [Test]
    public async Task Tun_HostWithoutIPv6_KeepsIPv4Only()
    {
        var (cfg, _) = TunPreCore(false);
        var tun = cfg.inbounds.Single(i => i.type == "tun");
        await Assert.That(tun.address!.All(a => !a.Contains(':'))).IsTrue();
    }

    [Test]
    public async Task Tun_DnsIsHijacked_AndForeignNamesResolveThroughTheProxy()
    {
        var (cfg, _) = TunPreCore(true);

        // Every :53 / DNS packet that enters the tun goes to sing-box's DNS module.
        await Assert.That(cfg.route.rules.Any(r => r.action == "hijack-dns")).IsTrue();

        // Remote resolvers are reached through the proxy, and they are the default.
        var remote = cfg.dns.servers.Where(s => s.tag?.StartsWith(Global.SingboxRemoteDNSTagPrefix) == true).ToList();
        await Assert.That(remote.Count).IsGreaterThan(0);
        foreach (var s in remote)
        {
            await Assert.That(s.detour).IsEqualTo(Global.ProxyTag);
        }
        await Assert.That(cfg.dns.final).IsEqualTo(Global.SingboxRemoteDNSTag);
    }

    [Test]
    public async Task LeakSafeMigration_TurnsStrictRouteAndSniffingBackOn()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.TunModeItem.StrictRoute = false;
        config.Inbound[0].SniffingEnabled = false;
        TermitusDefaults.ApplyLeakSafeDefaults(config);
        await Assert.That(config.TunModeItem.StrictRoute).IsTrue();
        await Assert.That(config.TunModeItem.AutoRoute).IsTrue();
        await Assert.That(config.Inbound[0].SniffingEnabled).IsTrue();
    }

    [Test]
    public async Task HardenedDefaults_LoopbackOnly_NoLog()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.Inbound[0].AllowLANConn = true;
        config.CoreBasicItem.LogEnabled = true;
        TermitusSecurity.ApplyCoreDefaults(config);
        await Assert.That(config.Inbound[0].AllowLANConn).IsFalse();
        await Assert.That(config.CoreBasicItem.LogEnabled).IsFalse();
    }

    [Test]
    public async Task UpstreamAppSelfUpdate_IsDisabled()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        var messages = new List<string>();
        var service = new UpdateService(config, (_, msg) =>
        {
            messages.Add(msg);
            return Task.CompletedTask;
        });

        await service.CheckUpdateGuiN(false, false);

        await Assert.That(messages.Count).IsEqualTo(1);
        await Assert.That(messages[0]).Contains("termitus.online");
    }

    [Test]
    public async Task DataDirPermissions_KeepHelperScriptsExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        // 0.3.0 regression: 0600 on binConfigs/proxy_set_osx_sh.sh made upstream
        // fail to start it ("ErrorStartingProcess") -> system proxy never set.
        var script = Path.Combine(Utils.GetBinConfigPath(), "ter-perm-probe.sh");
        File.WriteAllText(script, "#!/bin/sh\n");
        try
        {
            TermitusSecurity.HardenDataDirPermissions();
            await Assert.That(File.GetUnixFileMode(script))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Test]
    public async Task DataDirPermissions_AreUserOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var probe = Utils.GetConfigPath("ter-perm-probe.txt");
        File.WriteAllText(probe, "x");
        try
        {
            TermitusSecurity.HardenDataDirPermissions();
            var dirMode = File.GetUnixFileMode(Utils.GetConfigPath());
            var fileMode = File.GetUnixFileMode(probe);
            await Assert.That(dirMode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await Assert.That(fileMode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            File.Delete(probe);
        }
    }
}
