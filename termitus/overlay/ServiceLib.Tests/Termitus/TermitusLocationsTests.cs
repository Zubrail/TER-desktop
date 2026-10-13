using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>TER server picker: locations from subscription names, Auto never prefers Russia.</summary>
public class TermitusLocationsTests
{
    private static readonly (string, string?)[] Sub =
    [
        ("ru", "🇷🇺 Россия"),
        ("fr1", "🇫🇷 Франция 1"),
        ("lv", "🇱🇻 Латвия"),
        ("us3h", "🇺🇸 США 3 · H2"),
        ("fr1h", "🇫🇷 Франция 1 · H2"),
    ];

    [Test]
    public async Task Group_MergesTransportsOfOneNode()
    {
        var locs = TermitusLocations.Group(Sub);
        await Assert.That(locs.Select(l => l.Key).ToArray())
            .IsEquivalentTo(new[] { "🇷🇺 Россия", "🇫🇷 Франция 1", "🇱🇻 Латвия", "🇺🇸 США 3" });
        await Assert.That(locs.Single(l => l.Key == "🇫🇷 Франция 1").IndexIds.Count).IsEqualTo(2);
        await Assert.That(locs.Single(l => l.Key == "🇷🇺 Россия").IsRussia).IsTrue();
    }

    [Test]
    public async Task Auto_SkipsRussia_EvenWhenFastest()
    {
        var locs = TermitusLocations.Group(Sub);
        var delays = new Dictionary<string, int> { ["ru"] = 86, ["fr1"] = -1, ["fr1h"] = 104, ["us3h"] = 408 };
        await Assert.That(TermitusLocations.Pick(TermitusLocations.AutoKey, locs, delays)).IsEqualTo("fr1h");
    }

    [Test]
    public async Task Auto_NeverPicksRussia_EvenWhenNothingElseAnswers()
    {
        var locs = TermitusLocations.Group(Sub);
        var delays = new Dictionary<string, int> { ["ru"] = 86, ["fr1"] = -1, ["fr1h"] = -1 };
        await Assert.That(TermitusLocations.Pick(TermitusLocations.AutoKey, locs, delays)).IsNull();
        // ...but the user can still choose Russia by hand (whitelist mode).
        await Assert.That(TermitusLocations.Pick("🇷🇺 Россия", locs, delays)).IsEqualTo("ru");
    }

    [Test]
    public async Task ChosenLocation_UsesItsFastestWorkingTransport()
    {
        var locs = TermitusLocations.Group(Sub);
        var delays = new Dictionary<string, int> { ["fr1"] = -1, ["fr1h"] = 104 };
        await Assert.That(TermitusLocations.Pick("🇫🇷 Франция 1", locs, delays)).IsEqualTo("fr1h");
        await Assert.That(TermitusLocations.Pick("🇱🇻 Латвия", locs, delays)).IsNull();
    }

    [Test]
    public async Task ProtocolPreference_UsedWhenItAnswers_ElseIgnored()
    {
        var locs = TermitusLocations.Group(Sub);
        var delays = new Dictionary<string, int> { ["fr1"] = 90, ["fr1h"] = 104, ["us3h"] = 408 };
        // prefer the "h" (Hysteria2) transports even though TCP is faster
        await Assert.That(TermitusLocations.Pick("🇫🇷 Франция 1", locs, delays, id => id.EndsWith('h'))).IsEqualTo("fr1h");
        await Assert.That(TermitusLocations.Pick(TermitusLocations.AutoKey, locs, delays, id => id.EndsWith('h'))).IsEqualTo("fr1h");
        // preferred kind did not answer anywhere in that location -> normal pick
        var tcpOnly = new Dictionary<string, int> { ["fr1"] = 90 };
        await Assert.That(TermitusLocations.Pick("🇫🇷 Франция 1", locs, tcpOnly, id => id.EndsWith('h'))).IsEqualTo("fr1");
        await Assert.That(TermitusLocations.Pick("🇫🇷 Франция 1", locs, delays, null)).IsEqualTo("fr1");
    }
}
