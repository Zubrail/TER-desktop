using ServiceLib.Services;
using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>Account 2.0 (0.6.3): JSON contracts of /account/*, status mapping and the formatting helpers of the new cards.</summary>
public class TermitusAccountApiTests
{
    private static DateTimeOffset Local(int d, int h, int m = 0)
    {
        var dt = new DateTime(2026, 10, d, h, m, 0);
        return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
    }

    // ---- endpoint + status mapping ----

    [Test]
    public async Task Endpoint_AddsAccountSegment_KeepsQuery()
    {
        await Assert.That(TermitusAccountApi.Endpoint("https://termitus.online/subscription/TOK?x=1", "devices"))
            .IsEqualTo("https://termitus.online/subscription/TOK/account/devices?x=1");
        await Assert.That(TermitusAccountApi.Endpoint("https://termitus.online/subscription/TOK", "devices", "7", "manage"))
            .IsEqualTo("https://termitus.online/subscription/TOK/account/devices/7/manage");
        await Assert.That(TermitusAccountApi.Endpoint("ftp://x/y", "devices")).IsNull();
        await Assert.That(TermitusAccountApi.Endpoint(null, "devices")).IsNull();
    }

    [Test]
    public async Task Interpret_MapsStatuses_AndPicksServerReason()
    {
        await Assert.That(TermitusAccountApi.Interpret(null, null).Kind).IsEqualTo(TermitusAcctKind.Offline);
        await Assert.That(TermitusAccountApi.Interpret(200, "{}").Kind).IsEqualTo(TermitusAcctKind.Ok);
        var guest = TermitusAccountApi.Interpret(403, """{"detail":"Управлять подпиской может только её владелец"}""");
        await Assert.That(guest.Kind).IsEqualTo(TermitusAcctKind.NotManager);
        await Assert.That(TermitusAccountApi.Interpret(402, """{"price_rub":50}""").Kind).IsEqualTo(TermitusAcctKind.PaymentRequired);
        await Assert.That(TermitusAccountApi.Interpret(429, "{}").Kind).IsEqualTo(TermitusAcctKind.TooMany);
        var refused = TermitusAccountApi.Interpret(409, """{"detail":"Нельзя забрать управление"}""");
        await Assert.That(refused.Kind).IsEqualTo(TermitusAcctKind.Refused);
        await Assert.That(refused.Detail).IsEqualTo("Нельзя забрать управление");
        await Assert.That(TermitusAccountApi.Interpret(409, "<html>").Detail).IsNull();
        await Assert.That(TermitusAccountApi.Interpret(404, "{}").Kind).IsEqualTo(TermitusAcctKind.Error);
        await Assert.That(TermitusAccountApi.Interpret(500, "{}").Kind).IsEqualTo(TermitusAcctKind.Error);
    }

    // ---- parsers ----

    [Test]
    public async Task ParseDevices_ReadsAllFields_SkipsRowsWithoutId_NaiveTimeIsUtc()
    {
        var list = TermitusAccountApi.ParseDevices("""
            {"devices":[
              {"id":1,"name":"MacBook","platform":"macos","last_seen":"2026-10-13T10:00:00","is_this_device":true,"can_manage":true,"paid_slot":false,"is_active":true},
              {"id":2,"name":"","platform":"android","last_seen":null,"is_this_device":false,"can_manage":false,"paid_slot":true,"is_active":false},
              {"name":"no id"}
            ]}
            """)!;
        await Assert.That(list.Count).IsEqualTo(2);
        await Assert.That(list[0].IsThis).IsTrue();
        await Assert.That(list[0].CanManage).IsTrue();
        await Assert.That(list[0].LastSeen).IsEqualTo(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));
        await Assert.That(list[1].Name).IsEqualTo("Android"); // empty name -> platform word
        await Assert.That(list[1].PaidSlot).IsTrue();
        await Assert.That(list[1].IsActive).IsFalse();
        await Assert.That(list[1].LastSeen).IsNull();
    }

    [Test]
    public async Task ParseDevices_Garbage_IsNull()
    {
        await Assert.That(TermitusAccountApi.ParseDevices("<html>")).IsNull();
        await Assert.That(TermitusAccountApi.ParseDevices("{}")).IsNull();
        await Assert.That(TermitusAccountApi.ParseDevices(null)).IsNull();
        await Assert.That(TermitusAccountApi.ParseDevices("""{"devices":[]}""")!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParseReferrals_ReadsCounters_AndKeepsOnlyHttpsLinks()
    {
        var r = TermitusAccountApi.ParseReferrals("""
            {"link":"https://t.me/bot?start=ref_1","site_link":"javascript:x","invited_count":4,"bonus_rub":238.5,"bonus_days":8,
             "reward":"days","bonus_percent":15,"bonus_days_per_payment":4}
            """)!;
        await Assert.That(r.Link).IsEqualTo("https://t.me/bot?start=ref_1");
        await Assert.That(r.SiteLink).IsNull();
        await Assert.That(r.Invited).IsEqualTo(4);
        await Assert.That(r.BonusRub).IsEqualTo(238.5m);
        await Assert.That(r.BonusDays).IsEqualTo(8);
        await Assert.That(r.RewardIsDays).IsTrue();
        await Assert.That(r.Percent).IsEqualTo(15m);
        await Assert.That(r.DaysPerPayment).IsEqualTo(4);
        var none = TermitusAccountApi.ParseReferrals("""{"link":null,"reward":"weird"}""")!;
        await Assert.That(none.Link).IsNull();
        await Assert.That(none.Reward).IsEqualTo("money");
        await Assert.That(TermitusAccountApi.ParseReferrals("[]")).IsNull();
    }

    [Test]
    public async Task ParsePayments_ReadsRows()
    {
        var p = TermitusAccountApi.ParsePayments("""
            {"payments":[{"date":"2026-10-11T09:30:00","amount":199.0,"currency":"RUB","provider":"Карта / СБП","kind":"purchase","plan_days":30,"status":"paid"},
                         {"date":null,"amount":"50","currency":"RUB","provider":"С баланса","kind":"device","plan_days":null,"status":"failed"}, 5]}
            """)!;
        await Assert.That(p.Count).IsEqualTo(2);
        await Assert.That(p[0].Amount).IsEqualTo(199m);
        await Assert.That(p[0].PlanDays).IsEqualTo(30);
        await Assert.That(p[1].Amount).IsEqualTo(50m);
        await Assert.That(p[1].Date).IsNull();
        await Assert.That(p[1].PlanDays).IsEqualTo(0);
        await Assert.That(TermitusAccountApi.ParsePayments("{}")).IsNull();
    }

    [Test]
    public async Task ParseInvite_PaymentStep_Link_AndRejectsForeignLinks()
    {
        var pay = TermitusAccountApi.ParseInvite("""{"needs_payment":true,"price_rub":50,"free_limit":3,"devices":3,"balance_rub":150.0}""")!;
        await Assert.That(pay.NeedsPayment).IsTrue();
        await Assert.That(pay.PriceRub).IsEqualTo(50);
        await Assert.That(pay.BalanceRub).IsEqualTo(150m);
        var ok = TermitusAccountApi.ParseInvite("""{"join_url":"https://termitus.online/app/join/ABC","expires_at":"2026-10-13T10:10:00","price_rub":0}""")!;
        await Assert.That(ok.NeedsPayment).IsFalse();
        await Assert.That(ok.JoinUrl).IsEqualTo("https://termitus.online/app/join/ABC");
        await Assert.That(ok.ExpiresAt).IsEqualTo(new DateTimeOffset(2026, 10, 13, 10, 10, 0, TimeSpan.Zero));
        await Assert.That(TermitusAccountApi.ParseInvite("""{"join_url":"https://evil.example/x"}""")).IsNull();
        await Assert.That(TermitusAccountApi.ParseInvite("""{"join_url":"http://termitus.online/x"}""")).IsNull();
        await Assert.That(TermitusAccountApi.ParseInvite("nope")).IsNull();
    }

    [Test]
    public async Task ParseNoFunds_And_ParseRotated()
    {
        var nf = TermitusAccountApi.ParseNoFunds("""{"detail":"x","price_rub":50,"balance_rub":20}""")!;
        await Assert.That(nf.PriceRub).IsEqualTo(50);
        await Assert.That(nf.BalanceRub).IsEqualTo(20m);
        await Assert.That(TermitusAccountApi.ParseNoFunds("junk")).IsNull();
        await Assert.That(TermitusAccountApi.ParseRotated("""{"status":"rotated","subscription_url":"https://termitus.online/subscription/NEW"}"""))
            .IsEqualTo("https://termitus.online/subscription/NEW");
        await Assert.That(TermitusAccountApi.ParseRotated("""{"subscription_url":"https://evil.example/s"}""")).IsNull();
    }

    // ---- formatting ----

    [Test]
    public async Task PlatformLabel_KnownAndUnknown()
    {
        await Assert.That(TermitusAccountApi.PlatformLabel("macos", false)).IsEqualTo("macOS");
        await Assert.That(TermitusAccountApi.PlatformLabel("IOS", false)).IsEqualTo("iOS");
        await Assert.That(TermitusAccountApi.PlatformLabel("router", false)).IsEqualTo("Роутер");
        await Assert.That(TermitusAccountApi.PlatformLabel("router", true)).IsEqualTo("Router");
        await Assert.That(TermitusAccountApi.PlatformLabel("", false)).IsEqualTo("Устройство");
        await Assert.That(TermitusAccountApi.PlatformLabel("harmony", false)).IsEqualTo("Harmony");
    }

    [Test]
    public async Task RelativeTime_RussianWords()
    {
        var now = Local(13, 12);
        await Assert.That(TermitusAccountApi.RelativeTime(null, now, false)).IsEqualTo("ещё не подключалось");
        await Assert.That(TermitusAccountApi.RelativeTime(now.AddSeconds(-20), now, false)).IsEqualTo("только что");
        await Assert.That(TermitusAccountApi.RelativeTime(now.AddMinutes(-5), now, false)).IsEqualTo("5 мин назад");
        await Assert.That(TermitusAccountApi.RelativeTime(now.AddHours(-3), now, false)).IsEqualTo("3 ч назад");
        await Assert.That(TermitusAccountApi.RelativeTime(Local(12, 8), now, false)).IsEqualTo("вчера"); // 28 h, previous calendar day
        await Assert.That(TermitusAccountApi.RelativeTime(Local(10, 8), now, false)).IsEqualTo("3 дня назад");
        await Assert.That(TermitusAccountApi.RelativeTime(Local(8, 8), now, false)).IsEqualTo("5 дней назад");
        await Assert.That(TermitusAccountApi.RelativeTime(Local(1, 8), now, false)).IsEqualTo("1 октября");
    }

    [Test]
    public async Task RelativeTime_English()
    {
        var now = Local(13, 12);
        await Assert.That(TermitusAccountApi.RelativeTime(now.AddMinutes(-5), now, true)).IsEqualTo("5 min ago");
        await Assert.That(TermitusAccountApi.RelativeTime(Local(12, 8), now, true)).IsEqualTo("yesterday");
        await Assert.That(TermitusAccountApi.RelativeTime(Local(10, 8), now, true)).IsEqualTo("3 days ago");
        await Assert.That(TermitusAccountApi.RelativeTime(null, now, true)).IsEqualTo("never connected");
    }

    [Test]
    public async Task PaymentWhat_Kinds()
    {
        await Assert.That(TermitusAccountApi.PaymentWhat("purchase", 30, "Карта", false)).IsEqualTo("Подписка 30 дн.");
        await Assert.That(TermitusAccountApi.PaymentWhat("purchase", 365, "Карта", true)).IsEqualTo("Subscription 365 d.");
        await Assert.That(TermitusAccountApi.PaymentWhat("purchase", 0, "Карта", false)).IsEqualTo("Пополнение");
        await Assert.That(TermitusAccountApi.PaymentWhat("topup", 0, "CryptoBot", false)).IsEqualTo("Пополнение");
        await Assert.That(TermitusAccountApi.PaymentWhat("device", 0, "С баланса", false)).IsEqualTo("Устройство");
        await Assert.That(TermitusAccountApi.PaymentWhat("gift", 30, "Карта", false)).IsEqualTo("Подарок");
    }

    [Test]
    public async Task PaymentAmount_AndCaption()
    {
        await Assert.That(TermitusAccountApi.PaymentAmount(1590m, "RUB", false)).IsEqualTo("1 590 ₽");
        await Assert.That(TermitusAccountApi.PaymentAmount(12.5m, "USDT", true)).IsEqualTo("12.5 USDT");
        var now = Local(13, 12);
        var p = new TermitusPaymentItem(Local(11, 9), 199m, "RUB", "Карта / СБП", "purchase", 30, "paid");
        await Assert.That(TermitusAccountApi.PaymentCaption(p, now, false)).IsEqualTo("11 октября · Карта / СБП");
        await Assert.That(TermitusAccountApi.PaymentCaption(p with { Status = "failed" }, now, false)).IsEqualTo("11 октября · Карта / СБП · не прошла");
        await Assert.That(TermitusAccountApi.PaymentCaption(p with { Date = null, Provider = "", Status = "pending" }, now, true)).IsEqualTo("waiting for payment");
        await Assert.That(TermitusAccountApi.PaymentStatusWord("paid", false)).IsNull();
    }

    [Test]
    public async Task ReferralRule_Text_Plurals()
    {
        await Assert.That(TermitusAccountApi.ReferralRule(15m, 4, false)).IsEqualTo("15% или 4 дня с каждой оплаты и пополнения друга");
        await Assert.That(TermitusAccountApi.ReferralRule(10.0m, 1, false)).IsEqualTo("10% или 1 день с каждой оплаты и пополнения друга");
        await Assert.That(TermitusAccountApi.ReferralRule(12.5m, 7, false)).IsEqualTo("12.5% или 7 дней с каждой оплаты и пополнения друга");
        await Assert.That(TermitusAccountApi.ReferralRule(15m, 4, true)).IsEqualTo("15% or 4 days from every payment and top-up of your friend");
    }
}
