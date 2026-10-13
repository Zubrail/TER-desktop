using ServiceLib.Tests.CoreConfig;
using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>
/// TER (fork) defaults: Russian routing preset instead of upstream's Chinese
/// built-ins, Russian UI language, "TER x.y.z" title. Share the test DB in the
/// test bin dir, so they run sequentially.
/// </summary>
[NotInParallel("termitus-routing-db")]
public class TermitusDefaultsTests
{
    private static async Task<Config> FreshRoutingDbAsync()
    {
        var config = CoreConfigTestFactory.CreateConfig();
        config.ConstItem = new ConstItem();
        config.SimpleDNSItem = ConfigHandler.InitBuiltinSimpleDNS();
        CoreConfigTestFactory.BindAppManagerConfig(config);
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        await SQLiteHelper.Instance.ExecuteAsync("delete from RoutingItem");
        return config;
    }

    [Test]
    public async Task FreshInstall_BuiltinRouting_IsRussiaPreset_NotChinese()
    {
        var config = await FreshRoutingDbAsync();

        await ConfigHandler.InitBuiltinRouting(config);

        var items = await AppManager.Instance.RoutingItems() ?? [];
        await items.Count.Should().BeEqualTo(3);
        await items.Any(t => t.Remarks.StartsWith("V4-")).Should().BeFalse();
        var active = items.Single(t => t.IsActive);
        await active.Remarks.Should().BeEqualTo("RUv1-Всё, кроме РФ");
        await active.RuleSet.Should().Contain("geosite:ru-available-only-inside");
        await active.RuleSet.Should().Contain("geoip:ru");
        await active.DomainStrategy.Should().BeEqualTo("IPOnDemand");
        await config.SimpleDNSItem.DirectDNS.Should().BeEqualTo("77.88.8.8");
        await config.ConstItem.GeoSourceUrl.Should().BeEqualTo(Global.GeoFilesSources[1]);
    }

    [Test]
    public async Task UpgradeFromChineseDefaults_AddsRussiaAndMakesItActive()
    {
        var config = await FreshRoutingDbAsync();
        // what TER 0.1.0 left behind: upstream's Chinese built-ins only
        await ConfigHandler.InitBuiltinRouting(config, true);
        var before = await AppManager.Instance.RoutingItems() ?? [];
        await before.Any(t => t.Remarks.StartsWith("V4-")).Should().BeTrue();

        await ConfigHandler.InitBuiltinRouting(config);

        var active = (await AppManager.Instance.RoutingItems() ?? []).Single(t => t.IsActive);
        await active.Remarks.Should().BeEqualTo("RUv1-Всё, кроме РФ");
    }

    [Test]
    public async Task UserChoiceOfRouting_IsNotOverriddenOnNextStart()
    {
        var config = await FreshRoutingDbAsync();
        await ConfigHandler.InitBuiltinRouting(config);
        var all = (await AppManager.Instance.RoutingItems() ?? []).Single(t => t.Remarks == "RUv1-Всё");
        await ConfigHandler.SetDefaultRouting(config, all);

        await ConfigHandler.InitBuiltinRouting(config); // next app start

        var items = await AppManager.Instance.RoutingItems() ?? [];
        await items.Count.Should().BeEqualTo(3);
        await items.Single(t => t.IsActive).Remarks.Should().BeEqualTo("RUv1-Всё");
    }

    [Test]
    public async Task Language_MigratesUpstreamEnglishOnce_ThenRespectsUserChoice()
    {
        File.Delete(Utils.GetConfigPath("ter-migrations.txt"));
        var config = CoreConfigTestFactory.CreateConfig();
        config.UiItem = new UIItem { CurrentLanguage = "en" };

        TermitusDefaults.OnConfigLoaded(config);
        await config.UiItem.CurrentLanguage.Should().BeEqualTo("ru");

        config.UiItem.CurrentLanguage = "en"; // user picks English in Settings
        TermitusDefaults.OnConfigLoaded(config);
        await config.UiItem.CurrentLanguage.Should().BeEqualTo("en");
        File.Delete(Utils.GetConfigPath("ter-migrations.txt"));
    }

    [Test]
    public async Task WindowTitle_IsAppNameAndVersion()
    {
        await Global.AppName.Should().BeEqualTo("TER");
        await Regex.IsMatch(Utils.GetVersion(), @"^TER \d+\.\d+\.\d+$").Should().BeTrue();
        await Utils.GetVersion(false).Should().StartWith("TER/");
    }
}
