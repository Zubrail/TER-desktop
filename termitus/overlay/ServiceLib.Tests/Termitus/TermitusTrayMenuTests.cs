using ServiceLib.Services;
using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

public class TermitusTrayMenuTests
{
    private static TermitusTrayState State(bool connected = false, bool sub = true, bool auto = true) => new(
        connected, false, sub, auto,
        [("🇳🇱 Нидерланды 1", "Нидерланды 1", false), ("🇩🇪 Германия 1", "Германия 1", true)],
        true, false, false);

    [Test]
    public async Task TopLevel_IsShort_AndTechnicalThingsLiveUnderAdvanced()
    {
        var items = TermitusTrayMenu.Build(State(), TermitusLocale.Current);
        var top = items.Where(i => i.Kind != TermitusTrayKind.Separator).Select(i => i.Id ?? i.Title).ToList();
        await Assert.That(top.Count).IsEqualTo(6); // Connect, Country, Open, Report, Advanced, Quit
        await Assert.That(top[0]).IsEqualTo(TermitusTrayMenu.Connect);
        await Assert.That(top[2]).IsEqualTo(TermitusTrayMenu.Open);
        await Assert.That(top[3]).IsEqualTo(TermitusTrayMenu.Report);
        await Assert.That(top[5]).IsEqualTo(TermitusTrayMenu.Quit);
        var adv = items.Single(i => i.Title == TermitusLocale.Current.TrayAdvanced);
        var advIds = adv.Children!.Where(c => c.Id != null).Select(c => c.Id).ToList();
        await Assert.That(advIds.Contains(TermitusTrayMenu.ProxySet)).IsTrue();
        await Assert.That(advIds.Contains(TermitusTrayMenu.CopyProxy)).IsTrue();
        await Assert.That(advIds.Contains(TermitusTrayMenu.UpdateSub)).IsTrue();
        await Assert.That(items.Any(i => i.Id is TermitusTrayMenu.ProxySet or TermitusTrayMenu.CopyProxy)).IsFalse();
    }

    [Test]
    public async Task Connect_TogglesTitle_AndIsDisabledWithoutSubscription()
    {
        var off = TermitusTrayMenu.Build(State(), TermitusLocale.Current)[0];
        var on = TermitusTrayMenu.Build(State(connected: true), TermitusLocale.Current)[0];
        await Assert.That(off.Title).IsEqualTo(TermitusLocale.Current.TrayConnect);
        await Assert.That(on.Title).IsEqualTo(TermitusLocale.Current.TrayDisconnect);
        await Assert.That(TermitusTrayMenu.Build(State(sub: false), TermitusLocale.Current)[0].Enabled).IsFalse();
    }

    [Test]
    public async Task Countries_AutoFirst_CheckmarkFollowsChoice()
    {
        var c = TermitusTrayMenu.Build(State(auto: true), TermitusLocale.Current)[1].Children!;
        await Assert.That(c[0].Id).IsEqualTo(TermitusTrayMenu.CountryAuto);
        await Assert.That(c[0].Checked).IsTrue();
        await Assert.That(c.Count).IsEqualTo(3);
        await Assert.That(c.Skip(1).Any(x => x.Checked)).IsFalse(); // manual choice not active while Auto
        var manual = TermitusTrayMenu.Build(State(auto: false), TermitusLocale.Current)[1].Children!;
        await Assert.That(manual[0].Checked).IsFalse();
        await Assert.That(manual.Single(x => x.Checked).Id).IsEqualTo("country:🇩🇪 Германия 1");
    }

    [Test]
    public async Task Describe_IsReadable()
    {
        var d = TermitusTrayMenu.Describe(TermitusTrayMenu.Build(State(), TermitusLocale.Current));
        var loc = TermitusLocale.Current;
        await Assert.That(d.StartsWith($"{loc.TrayConnect} | {loc.TrayCountry} ›[{loc.AutoTitle}✓ | ")).IsTrue();
        await Assert.That(d.Contains(loc.TrayOpen)).IsTrue();
        await Assert.That(d.EndsWith(loc.TrayQuit)).IsTrue();
    }

    [Test]
    public async Task IconState_Priority_BusyThenWarnThenOn()
    {
        await Assert.That(TermitusTrayMenu.IconState(true, true, true)).IsEqualTo("busy");
        await Assert.That(TermitusTrayMenu.IconState(false, true, true)).IsEqualTo("warn");
        await Assert.That(TermitusTrayMenu.IconState(false, false, true)).IsEqualTo("on");
        await Assert.That(TermitusTrayMenu.IconState(false, false, false)).IsEqualTo("off");
    }

    [Test]
    public async Task Tooltip_FollowsState()
    {
        var loc = TermitusLocale.Current;
        await Assert.That(TermitusTrayMenu.Tooltip("on", loc)).IsEqualTo(loc.TrayTooltipOn);
        await Assert.That(TermitusTrayMenu.Tooltip("off", loc)).IsEqualTo(loc.TrayTooltipOff);
        await Assert.That(TermitusTrayMenu.Tooltip("busy", loc)).IsEqualTo(loc.TrayTooltipBusy);
        await Assert.That(TermitusTrayMenu.Tooltip("warn", loc)).IsEqualTo(loc.TrayTooltipWarn);
        await Assert.That(TermitusTrayMenu.Tooltip("???", loc)).IsEqualTo(loc.TrayTooltipOff);
    }
}
