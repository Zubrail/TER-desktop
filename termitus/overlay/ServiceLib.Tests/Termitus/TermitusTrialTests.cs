using System.Text.Json;

using TUnit.Core;

using ServiceLib.Services;

namespace ServiceLib.Tests.Termitus;

/// <summary>0.6.5 in-app trial: install id shape, request shape, response / refusal parsing, texts and links.</summary>
public class TermitusTrialTests
{
    [Test]
    public async Task InstallId_Is32UrlSafeChars_AndUnique()
    {
        var set = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            var id = TermitusTrial.NewInstallId();
            await Assert.That(id.Length).IsEqualTo(32);
            await Assert.That(id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')).IsTrue();
            await Assert.That(TermitusTrial.IsValidInstallId(id)).IsTrue();
            set.Add(id);
        }
        await Assert.That(set.Count).IsEqualTo(100);
        await Assert.That(TermitusTrial.IsValidInstallId("short")).IsFalse();
        await Assert.That(TermitusTrial.IsValidInstallId("has space has space has space!!")).IsFalse();
        await Assert.That(TermitusTrial.IsValidInstallId(null)).IsFalse();
    }

    [Test]
    public async Task Request_Shape_OmitsNullRefAndCampaign()
    {
        var id = TermitusTrial.NewInstallId();
        var json = JsonSerializer.Serialize(TermitusTrial.BuildRequest(id, "macos"));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("consent").GetBoolean()).IsTrue();
        await Assert.That(root.GetProperty("install_id").GetString()).IsEqualTo(id);
        await Assert.That(root.GetProperty("platform").GetString()).IsEqualTo("macos");
        await Assert.That(root.TryGetProperty("ref", out _)).IsFalse();
        await Assert.That(root.TryGetProperty("campaign", out _)).IsFalse();

        var withRef = JsonSerializer.Serialize(TermitusTrial.BuildRequest(id, "windows", "123", "camp1"));
        using var d2 = JsonDocument.Parse(withRef);
        await Assert.That(d2.RootElement.GetProperty("ref").GetString()).IsEqualTo("123");
        await Assert.That(d2.RootElement.GetProperty("campaign").GetString()).IsEqualTo("camp1");
        await Assert.That(d2.RootElement.GetProperty("platform").GetString()).IsEqualTo("windows");
    }

    private const string Ok = "{\"subscription_url\":\"https://termitus.online/subscription/abc123\",\"code\":\"GIFT-1234\",\"days\":2,\"expires_at\":\"2026-10-21T10:00:00Z\"}";

    [Test]
    public async Task Ok_Response_IsParsed()
    {
        var o = TermitusTrial.Interpret(200, Ok, null);
        await Assert.That(o.Kind).IsEqualTo(TermitusTrialKind.Ok);
        await Assert.That(o.SubscriptionUrl).IsEqualTo("https://termitus.online/subscription/abc123");
        await Assert.That(o.Code).IsEqualTo("GIFT-1234");
        await Assert.That(o.Days).IsEqualTo(2);
        await Assert.That(o.ExpiresAt!.Value.UtcDateTime).IsEqualTo(new DateTime(2026, 10, 21, 10, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task Ok_WithBadPieces_IsFailed()
    {
        await Assert.That(TermitusTrial.Interpret(200, "{}", null).Kind).IsEqualTo(TermitusTrialKind.Failed);
        await Assert.That(TermitusTrial.Interpret(200, "not json", null).Kind).IsEqualTo(TermitusTrialKind.Failed);
        await Assert.That(TermitusTrial.Interpret(200, Ok.Replace("https://", "http://"), null).Kind).IsEqualTo(TermitusTrialKind.Failed);       // not https
        await Assert.That(TermitusTrial.Interpret(200, Ok.Replace("GIFT-1234", "bad code!"), null).Kind).IsEqualTo(TermitusTrialKind.Failed);
        await Assert.That(TermitusTrial.Interpret(200, Ok.Replace("\"days\":2", "\"days\":0"), null).Kind).IsEqualTo(TermitusTrialKind.Failed);
        await Assert.That(TermitusTrial.Interpret(200, Ok.Replace("https://termitus", "https://u:p@termitus"), null).Kind).IsEqualTo(TermitusTrialKind.Failed);
    }

    [Test]
    [Arguments("busy")]
    [Arguments("ip")]
    [Arguments("subnet")]
    [Arguments("install")]
    public async Task Refusal_429_CarriesReasonAndDetail(string reason)
    {
        var o = TermitusTrial.Interpret(429, "{\"detail\":\"Текст отказа\"}", reason);
        await Assert.That(o.Kind).IsEqualTo(TermitusTrialKind.Refused);
        await Assert.That(o.Reason).IsEqualTo(reason);
        await Assert.That(o.Detail).IsEqualTo("Текст отказа");
    }

    [Test]
    public async Task Refusal_UnknownReason_BecomesIp_DetailIsCleaned()
    {
        var o = TermitusTrial.Interpret(429, "{\"detail\":\"a\\nb\\u0007c\"}", "weird");
        await Assert.That(o.Reason).IsEqualTo("ip");
        await Assert.That(o.Detail).IsEqualTo("abc");
        await Assert.That(TermitusTrial.Interpret(429, null, null).Detail).IsEqualTo(string.Empty);
        await Assert.That(TermitusTrial.CleanDetail(new string('x', 500)).Length).IsEqualTo(300);
        var loc = TermitusLocale.Current;
        await Assert.That(loc.TrialRefusalText("ip", "Своё")).IsEqualTo("Своё");
        await Assert.That(loc.TrialRefusalText("install", null)).IsEqualTo(loc.TrialRefusedInstall);
        await Assert.That(loc.TrialRefusalText("busy", "")).IsEqualTo(loc.TrialRefusedBusy);
    }

    [Test]
    public async Task OtherStatuses()
    {
        await Assert.That(TermitusTrial.Interpret(null, null, null).Kind).IsEqualTo(TermitusTrialKind.Offline);
        await Assert.That(TermitusTrial.Interpret(400, "{}", null).Kind).IsEqualTo(TermitusTrialKind.NoConsent);
        await Assert.That(TermitusTrial.Interpret(422, "{}", null).Kind).IsEqualTo(TermitusTrialKind.BadInstall);
        await Assert.That(TermitusTrial.Interpret(503, "{}", null).Kind).IsEqualTo(TermitusTrialKind.NotConfigured);
        await Assert.That(TermitusTrial.Interpret(502, null, null).Kind).IsEqualTo(TermitusTrialKind.Offline);
        await Assert.That(TermitusTrial.Interpret(404, null, null).Kind).IsEqualTo(TermitusTrialKind.Failed);
    }

    [Test]
    public async Task Links_BindAndLegal()
    {
        await Assert.That(TermitusTrial.BindUrl("GIFT-1234")).IsEqualTo("https://t.me/TErpiUsis_bot?start=redeem_GIFT-1234");
        await Assert.That(TermitusTrial.BindUrl("bad code")).IsNull();
        await Assert.That(TermitusTrial.BindUrl(null)).IsNull();
        await Assert.That(TermitusTrial.LegalUrl("terms", false)).IsEqualTo("https://termitus.online/static/terms.html");
        await Assert.That(TermitusTrial.LegalUrl("consent", true)).IsEqualTo("https://termitus.online/static/consent.html?lang=en");
        await Assert.That(TermitusTrial.LegalUrl("privacy", false)).IsEqualTo("https://termitus.online/static/privacy.html");
    }

    [Test]
    public async Task Info_RoundTrip_And_Line()
    {
        var until = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        var raw = TermitusTrial.EncodeInfo(2, until);
        var back = TermitusTrial.DecodeInfo(raw);
        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Value.Days).IsEqualTo(2);
        await Assert.That(back.Value.Until).IsEqualTo(until);
        await Assert.That(TermitusTrial.DecodeInfo("junk")).IsNull();
        await Assert.That(TermitusTrial.DecodeInfo("0|5")).IsNull();
        var now = until.AddDays(-2);
        var line = TermitusTrial.TrialLine(2, until, now, false);
        await Assert.That(line).IsEqualTo("Пробный доступ: осталось 2 дня, до 21 октября");
        await Assert.That(TermitusTrial.TrialLine(2, until, now, true).StartsWith("Trial access: 2 days left, until ")).IsTrue();
        await Assert.That(TermitusTrial.TrialLine(2, until, until.AddHours(1), false).StartsWith("Пробный доступ закончился ")).IsTrue();
    }

    [Test]
    public async Task TimeLeft_OneRuleForBannerChipAndTrialCard()
    {
        var until = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        // the owner's case: ~1.9 days left -> every text says 1 day, never 2, never a bare «до 22»
        var now = until.AddHours(-47);
        var left = TermitusAccount.Left(until, now)!.Value;
        await Assert.That(TermitusAccount.LeftPhrase(left, false)).IsEqualTo("осталось 1 день");
        var line = TermitusTrial.TrialLine(2, until, now, false);
        await Assert.That(line).Contains("осталось 1 день");
        await Assert.That(line).EndsWith("22 октября");
        await Assert.That(TermitusAccount.LeftUnit(left, false)).IsEqualTo("1 день");
        // hours, plurals, under an hour, passed
        await Assert.That(TermitusAccount.LeftPhrase(TermitusAccount.Left(until, until.AddHours(-5))!.Value, false)).IsEqualTo("осталось 5 часов");
        await Assert.That(TermitusAccount.LeftPhrase(TermitusAccount.Left(until, until.AddHours(-2))!.Value, false)).IsEqualTo("осталось 2 часа");
        await Assert.That(TermitusAccount.LeftPhrase(TermitusAccount.Left(until, until.AddHours(-21))!.Value, false)).IsEqualTo("осталось 21 час");
        await Assert.That(TermitusAccount.LeftPhrase(TermitusAccount.Left(until, until.AddMinutes(-20))!.Value, false)).IsEqualTo("осталось меньше часа");
        await Assert.That(TermitusAccount.LeftPhrase(TermitusAccount.Left(until, until.AddHours(-72))!.Value, false)).IsEqualTo("осталось 3 дня");
        await Assert.That(TermitusAccount.LeftPhrase(TermitusAccount.Left(until, until.AddHours(-5))!.Value, true)).IsEqualTo("5 hours left");
        await Assert.That(TermitusAccount.Left(until, until)).IsNull();
        // the date always names the month in the genitive case
        foreach (var month in Enumerable.Range(1, 12))
        {
            var d = new DateTimeOffset(2026, month, 22, 12, 0, 0, TimeSpan.Zero);
            await Assert.That(TermitusTrial.TrialLine(2, d, d.AddHours(-30), false)).DoesNotMatch(@"до \d+$");
        }
    }

    [Test]
    public async Task WindowTitle_SimpleModeIsJustTer()
    {
        await Assert.That(TermitusWinTitle.Compose(false, true, false, "TER", "TER 0.6.5", "admin", "Запущено без прав администратора")).IsEqualTo("TER");
        await Assert.That(TermitusWinTitle.Compose(false, true, true, "TER", "TER 0.6.5", "admin", "x")).IsEqualTo("TER");
        await Assert.That(TermitusWinTitle.Compose(true, true, false, "TER", "TER 0.6.5", "admin", "Запущено без прав администратора")).IsEqualTo("TER 0.6.5 - Запущено без прав администратора");
        await Assert.That(TermitusWinTitle.Compose(true, false, false, "TER", "TER 0.6.5", "admin", "x")).IsEqualTo("TER");
    }
}
