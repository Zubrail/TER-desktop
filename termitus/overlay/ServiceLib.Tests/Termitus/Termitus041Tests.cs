using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>
/// TER 0.4.1: Telegram account line, location names without emoji (freeze fix),
/// what the «Поддержка» page shows offline/loading, test hooks off by default.
/// </summary>
public class Termitus041Tests
{
    private static TermitusTelegramDto Tg(string? user, string? name) => new() { Username = user, FirstName = name };

    [Test]
    public async Task FormatTelegram_NameAndUsername()
    {
        await Assert.That(TermitusAccount.FormatTelegram(Tg("ivan", "Иван"))).IsEqualTo("Иван · @ivan");
        await Assert.That(TermitusAccount.FormatTelegram(Tg("@ivan_99", null))).IsEqualTo("@ivan_99");
        await Assert.That(TermitusAccount.FormatTelegram(Tg(null, "  Иван  "))).IsEqualTo("Иван");
        await Assert.That(TermitusAccount.FormatTelegram(Tg(null, null))).IsNull();
        await Assert.That(TermitusAccount.FormatTelegram(Tg("", " "))).IsNull();
        await Assert.That(TermitusAccount.FormatTelegram(null)).IsNull();
    }

    [Test]
    public async Task FormatTelegram_RejectsJunk()
    {
        // Not a Telegram username (too short / spaces / a link): dropped, the name stays.
        await Assert.That(TermitusAccount.FormatTelegram(Tg("ab", "Иван"))).IsEqualTo("Иван");
        await Assert.That(TermitusAccount.FormatTelegram(Tg("evil name", null))).IsNull();
        await Assert.That(TermitusAccount.FormatTelegram(Tg("https://t.me/x", null))).IsNull();
        // Control characters removed, very long names cut.
        await Assert.That(TermitusAccount.FormatTelegram(Tg(null, "Ив\u0000а\nн"))).IsEqualTo("Иван");
        var longName = TermitusAccount.FormatTelegram(Tg(null, new string('я', 200)))!;
        await Assert.That(longName.Length).IsEqualTo(65);
        await Assert.That(longName.EndsWith('…')).IsTrue();
    }

    [Test]
    public async Task InfoJson_TelegramFieldOptional()
    {
        var with = TermitusAccount.ParseInfoJson("""{"expire": 1790000000, "telegram": {"username": "ter_user", "first_name": "Аня"}}""");
        await Assert.That(with!.Telegram!.Username).IsEqualTo("ter_user");
        await Assert.That(with.Telegram.FirstName).IsEqualTo("Аня");
        var snap = new TermitusAccountSnapshot { SubId = "s", Info = with, InfoAt = 1_780_000_000 };
        var st = TermitusAccount.Compute(snap, DateTimeOffset.FromUnixTimeSeconds(1_780_000_100));
        await Assert.That(st.Telegram).IsEqualTo("Аня · @ter_user");

        foreach (var json in new[] { """{"expire": 1790000000}""", """{"telegram": null}""", """{"telegram": {"username": null, "first_name": null}}""" })
        {
            var dto = TermitusAccount.ParseInfoJson(json);
            await Assert.That(dto).IsNotNull();
            var s = TermitusAccount.Compute(new TermitusAccountSnapshot { SubId = "s", Info = dto, InfoAt = 1 }, DateTimeOffset.UtcNow);
            await Assert.That(s.Telegram).IsNull();
        }
        // A wrong type for "telegram" must not take the whole /info down.
        foreach (var json in new[] { """{"plan": "Год", "telegram": "ivan"}""", """{"plan": "Год", "telegram": [1, 2]}""", """{"plan": "Год", "telegram": {"username": 5, "first_name": {"x": 1}}}""" })
        {
            var dto = TermitusAccount.ParseInfoJson(json);
            await Assert.That(dto).IsNotNull();
            await Assert.That(dto!.Plan).IsEqualTo("Год");
            await Assert.That(TermitusAccount.FormatTelegram(dto.Telegram)).IsNull();
        }
    }

    [Test]
    public async Task DisplayParts_FlagBecomesBadge()
    {
        await Assert.That(TermitusLocations.DisplayParts("🇳🇱 Нидерланды 1")).IsEqualTo(("NL", "Нидерланды 1"));
        await Assert.That(TermitusLocations.DisplayParts("🇷🇺 Россия")).IsEqualTo(("RU", "Россия"));
        await Assert.That(TermitusLocations.DisplayParts("Германия 🇩🇪")).IsEqualTo(("DE", "Германия"));
        // No flag: first letter; other emoji (incl. ZWJ / variation selectors) are dropped.
        await Assert.That(TermitusLocations.DisplayParts("Финляндия 2")).IsEqualTo(("Ф", "Финляндия 2"));
        await Assert.That(TermitusLocations.DisplayParts("⚡️ Быстрый  сервер 🚀")).IsEqualTo(("Б", "Быстрый сервер"));
        await Assert.That(TermitusLocations.DisplayParts("👨‍💻 dev")).IsEqualTo(("D", "dev"));
        // Only a flag / nothing at all.
        await Assert.That(TermitusLocations.DisplayParts("🇫🇷")).IsEqualTo(("FR", "FR"));
        await Assert.That(TermitusLocations.DisplayParts("")).IsEqualTo(("·", "—"));
        await Assert.That(TermitusLocations.DisplayParts(null)).IsEqualTo(("·", "—"));
    }

    [Test]
    public async Task DisplayParts_NoEmojiLeftInText()
    {
        foreach (var key in new[] { "🇳🇱 Нидерланды 1", "🇺🇸 США · H2", "⭐ Premium ✅", "🏳️‍🌈 x", "1️⃣ one" })
        {
            var (_, name) = TermitusLocations.DisplayParts(key);
            // Nothing outside the BMP and no emoji presentation selectors / ZWJ / keycaps.
            await Assert.That(name.Any(char.IsSurrogate)).IsFalse();
            await Assert.That(name.IndexOfAny(['️', '‍', '⃣'])).IsEqualTo(-1);
        }
    }

    [Test]
    public async Task SupportView_NeverEmpty()
    {
        // No subscription -> the bot button.
        var v = TermitusSupport.ViewOf(false, TermitusSupportAvailability.Unknown, false, 0);
        await Assert.That(v.BotVisible).IsTrue();
        await Assert.That(v.ChatVisible).IsFalse();

        // First answer pending -> chat card, loading, input disabled.
        v = TermitusSupport.ViewOf(true, TermitusSupportAvailability.Unknown, false, 0);
        await Assert.That((v.ChatVisible, v.Loading, v.CanType, v.Offline, v.BotVisible)).IsEqualTo((true, true, false, false, false));

        // Unreachable before anything loaded -> chat card + "no connection", input disabled, no spinner.
        v = TermitusSupport.ViewOf(true, TermitusSupportAvailability.Unknown, true, 0);
        await Assert.That((v.ChatVisible, v.Offline, v.CanType, v.Loading, v.Empty)).IsEqualTo((true, true, false, false, false));

        // Unreachable later -> the messages stay, input disabled.
        v = TermitusSupport.ViewOf(true, TermitusSupportAvailability.Available, true, 5);
        await Assert.That((v.ChatVisible, v.Offline, v.CanType)).IsEqualTo((true, true, false));

        // Normal.
        v = TermitusSupport.ViewOf(true, TermitusSupportAvailability.Available, false, 0);
        await Assert.That((v.ChatVisible, v.CanType, v.Empty, v.Loading)).IsEqualTo((true, true, true, false));

        // Known: no chat on the backend / guest -> bot button, never "offline".
        foreach (var a in new[] { TermitusSupportAvailability.Unavailable, TermitusSupportAvailability.Guest })
        {
            v = TermitusSupport.ViewOf(true, a, true, 0);
            await Assert.That((v.BotVisible, v.ChatVisible, v.Offline)).IsEqualTo((true, false, false));
        }
    }

    [Test]
    public async Task TestHooks_OffByDefault()
    {
        // The test process does not set TER_TEST_*: a normal launch is untouched.
        await Assert.That(TermitusTestHooks.IsTestInstance).IsFalse();
        await Assert.That(TermitusTestHooks.MutexName).IsEqualTo("v2rayN");
        await Assert.That(TermitusLogin.ApiBase).IsEqualTo("https://termitus.online");
        await Assert.That(TermitusTestHooks.NoOpen).IsFalse();
    }

    [Test]
    public async Task TestHooks_ApiBaseNeedsATestInstanceAndSafeScheme()
    {
        // D-6: a bare TER_TEST_API_BASE must never redirect login traffic
        await Assert.That(TermitusTestHooks.SafeApiBase(null, "https://evil.example")).IsNull();
        await Assert.That(TermitusTestHooks.SafeApiBase("t1", "http://evil.example")).IsNull();
        await Assert.That(TermitusTestHooks.SafeApiBase("t1", "ftp://x")).IsNull();
        await Assert.That(TermitusTestHooks.SafeApiBase("t1", "not a url")).IsNull();
        await Assert.That(TermitusTestHooks.SafeApiBase("t1", "https://stage.example")).IsEqualTo("https://stage.example");
        await Assert.That(TermitusTestHooks.SafeApiBase("t1", "http://127.0.0.1:9")).IsEqualTo("http://127.0.0.1:9");
    }

    [Test]
    public async Task TestHooks_CompiledOutOfRelease()
    {
#if DEBUG
        await Assert.That(TermitusTestHooks.Compiled).IsTrue();
#else
        await Assert.That(TermitusTestHooks.Compiled).IsFalse();
        await Assert.That(TermitusTestHooks.Env("PATH")).IsNull();
#endif
    }
}
