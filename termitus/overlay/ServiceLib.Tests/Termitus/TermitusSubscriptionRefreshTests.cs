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
