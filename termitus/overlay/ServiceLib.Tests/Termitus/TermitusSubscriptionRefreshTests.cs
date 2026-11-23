using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

public class TermitusSubscriptionRefreshTests
{
    [Test]
    public async Task EnablesOnlyOwnedHttpsSubscriptionFeeds()
    {
        await TermitusSubscriptionRefresh.DefaultInterval("https://termitus.online/subscription/synthetic/mobile").Should().BeEqualTo(120);
        await TermitusSubscriptionRefresh.DefaultInterval("https://sub.termitus.online/subscription/synthetic").Should().BeEqualTo(120);
        foreach (var url in new[] { "https://termitus.online.evil.test/subscription/x", "https://evil.test/subscription/x", "http://termitus.online/subscription/x", "https://termitus.online/app/x", "https://user@termitus.online/subscription/x", "https://termitus.online:8443/subscription/x", "not a url" })
            await TermitusSubscriptionRefresh.DefaultInterval(url).Should().BeEqualTo(0);
    }

    [Test]
    public async Task MigrationPreservesCustomIntervalsAndDisabledOrThirdPartySubscriptions()
    {
        var item = new SubItem { Url = "https://termitus.online/subscription/synthetic", Enabled = true };
        await TermitusSubscriptionRefresh.ConfigureMissingInterval(item).Should().BeTrue();
        await item.AutoUpdateInterval.Should().BeEqualTo(120);
        item.AutoUpdateInterval = 17;
        await TermitusSubscriptionRefresh.ConfigureMissingInterval(item).Should().BeFalse();
        await item.AutoUpdateInterval.Should().BeEqualTo(17);
        item.Enabled = false; item.AutoUpdateInterval = 0;
        await TermitusSubscriptionRefresh.ConfigureMissingInterval(item).Should().BeFalse();
        item.Enabled = true; item.Url = "https://other.test/subscription/x";
        await TermitusSubscriptionRefresh.ConfigureMissingInterval(item).Should().BeFalse();
        await item.AutoUpdateInterval.Should().BeEqualTo(0);
    }
    [Test]
    public async Task ScheduledRefreshKeepsSameConnectionButDetectsCredentialAndSelectionChanges()
    {
        var profile = new ProfileItem { IndexId = "selected", ConfigType = EConfigType.VLESS,
            Address = "203.0.113.5", Port = 443, Password = "synthetic", Network = "xhttp" };
        await TermitusSubscriptionRefresh.KeepScheduledConnection("selected", profile).Should().BeFalse();
        using (TermitusSubscriptionRefresh.BeginScheduled(profile, "selected"))
        {
            profile.Remarks = "new label";
            await TermitusSubscriptionRefresh.KeepScheduledConnection("selected", profile).Should().BeTrue();
            await TermitusSubscriptionRefresh.KeepScheduledConnection("other", profile).Should().BeFalse();
            profile.Password = "rotated-synthetic";
            await TermitusSubscriptionRefresh.KeepScheduledConnection("selected", profile).Should().BeFalse();
            await TermitusSubscriptionRefresh.KeepScheduledConnection("selected", null).Should().BeFalse();
        }
        await TermitusSubscriptionRefresh.KeepScheduledConnection("selected", profile).Should().BeFalse();
    }
}

[NotInParallel("termitus-routing-db")]
public class TermitusSubscriptionReplacementTests
{
    [Test]
    public async Task MalformedReplacementPreservesExistingUriProfileAndSelection()
    {
        var config = ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.CreateConfig();
        ServiceLib.Tests.CoreConfig.CoreConfigTestFactory.BindAppManagerConfig(config);
        SQLiteHelper.Instance.CreateTable<ProfileItem>();
        SQLiteHelper.Instance.CreateTable<SubItem>();
        var sub = "refresh-invalid-" + Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid().ToString("N");
        var old = new ProfileItem { IndexId = id, Subid = sub, IsSub = true, ConfigType = EConfigType.VLESS,
            Address = "203.0.113.5", Port = 443, Password = "synthetic", Network = "grpc", Remarks = "Fixture France" };
        await SQLiteHelper.Instance.InsertAsync(old);
        config.IndexId = id;
        try
        {
            var ret = await ConfigHandler.AddBatchServers(config, "<html>Temporary upstream error</html>", sub, true);
            await Assert.That(ret <= 0).IsTrue();
            var profiles = await AppManager.Instance.ProfileItems(sub);
            await Assert.That(profiles.Count).IsEqualTo(1);
            await Assert.That(profiles[0].IndexId).IsEqualTo(id);
            await Assert.That(profiles[0].Password).IsEqualTo("synthetic");
            await Assert.That(config.IndexId).IsEqualTo(id);
        }
        finally { await SQLiteHelper.Instance.DeleteAsync(old); }
    }

    [Test]
    public async Task SuccessMetadataIsUnknownUntilParsedAndDoesNotStoreFeedUrl()
    {
        var sub = "refresh-metadata-" + Guid.NewGuid().ToString("N");
        await Assert.That(TermitusSubscriptionRefresh.LastSuccess([sub])).IsNull();
        TermitusSubscriptionRefresh.RecordSuccess(sub);
        var stamp = TermitusSubscriptionRefresh.LastSuccess([sub]);
        await Assert.That(stamp.HasValue).IsTrue();
        await Assert.That(stamp!.Value > DateTimeOffset.UtcNow.AddMinutes(-1)).IsTrue();
        await Assert.That(TermitusSubscriptionRefresh.LastSuccess([sub, "never-refreshed-fixture"])).IsNull();
        var cache = File.ReadAllText(Utils.GetConfigPath("ter-server-refresh.json"));
        await Assert.That(cache.Contains("https://")).IsFalse();
    }
}
