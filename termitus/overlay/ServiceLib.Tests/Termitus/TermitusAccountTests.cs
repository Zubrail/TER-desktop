using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>
/// TER account page: Subscription-Userinfo header parsing, /info JSON parsing and the
/// header / JSON fallback rules that decide what the page, chip and banner show.
/// </summary>
public class TermitusAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static long Ts(DateTimeOffset d) => d.ToUnixTimeSeconds();

    // ---- header parser ----

    [Test]
    public async Task ParseUserInfo_StandardHeader()
    {
        var ui = TermitusAccount.ParseUserInfo("upload=1024; download=2048; total=0; expire=1794614400");
        await Assert.That(ui).IsNotNull();
        await Assert.That(ui!.Upload).IsEqualTo(1024L);
        await Assert.That(ui.Download).IsEqualTo(2048L);
        await Assert.That(ui.Total).IsEqualTo(0L);
        await Assert.That(ui.Expire).IsEqualTo(1794614400L);
    }

    [Test]
    public async Task ParseUserInfo_ToleratesSpacingCaseOrderDecimalsAndJunk()
    {
        var ui = TermitusAccount.ParseUserInfo(" Expire = 1794614400 ;DOWNLOAD=1.5e3;foo=bar;upload=abc; =5; total=10737418240 ");
        await Assert.That(ui).IsNotNull();
        await Assert.That(ui!.Download).IsEqualTo(1500L);
        await Assert.That(ui.Upload).IsEqualTo(0L);
        await Assert.That(ui.Total).IsEqualTo(10737418240L);
        await Assert.That(ui.Expire).IsEqualTo(1794614400L);
    }

    [Test]
    public async Task ParseUserInfo_ExpireZeroOrMissing_MeansNoEndDate()
    {
        await Assert.That(TermitusAccount.ParseUserInfo("upload=1; download=2; total=0; expire=0")!.Expire).IsNull();
        await Assert.That(TermitusAccount.ParseUserInfo("upload=1; download=2")!.Expire).IsNull();
    }

    [Test]
    public async Task ParseUserInfo_EmptyOrGarbage_IsNull()
    {
        await Assert.That(TermitusAccount.ParseUserInfo(null)).IsNull();
        await Assert.That(TermitusAccount.ParseUserInfo("   ")).IsNull();
        await Assert.That(TermitusAccount.ParseUserInfo("hello world")).IsNull();
        await Assert.That(TermitusAccount.ParseUserInfo("upload=-5; expire=x")).IsNull();
    }

    // ---- /info JSON ----

    [Test]
    public async Task ParseInfoJson_FullContract()
    {
        var dto = TermitusAccount.ParseInfoJson("""
            {"expire": 1794614400, "plan": "3 месяца", "devices_used": 2, "devices_allowed": 3,
             "devices": [{"name": "MacBook", "platform": "macOS", "is_active": true},
                         {"name": "Pixel", "platform": "Android", "is_active": false}],
             "renew_url": "https://t.me/TErpiUsis_bot?start=renew", "extra": {"ignored": 1}}
            """);
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Expire).IsEqualTo(1794614400L);
        await Assert.That(dto.Plan).IsEqualTo("3 месяца");
        await Assert.That(dto.DevicesUsed).IsEqualTo(2);
        await Assert.That(dto.DevicesAllowed).IsEqualTo(3);
        await Assert.That(dto.Devices!.Count).IsEqualTo(2);
        await Assert.That(dto.Devices[1].IsActive == false).IsTrue();
    }

    [Test]
    public async Task ParseInfoJson_NullExpireAndNumbersAsStrings()
    {
        var dto = TermitusAccount.ParseInfoJson("""{"expire": null, "devices_used": "1", "devices_allowed": "3"}""");
        await Assert.That(dto).IsNotNull();
        await Assert.That(dto!.Expire).IsNull();
        await Assert.That(dto.DevicesUsed).IsEqualTo(1);
    }

    [Test]
    public async Task ParseInfoJson_BrokenOrWrongShape_IsNull()
    {
        await Assert.That(TermitusAccount.ParseInfoJson(null)).IsNull();
        await Assert.That(TermitusAccount.ParseInfoJson("<html>404 Not Found</html>")).IsNull();
        await Assert.That(TermitusAccount.ParseInfoJson("{\"expire\": \"soon\"}")).IsNull();
        await Assert.That(TermitusAccount.ParseInfoJson("[1,2,3]")).IsNull();
        await Assert.That(TermitusAccount.ParseInfoJson("{\"devices_used\": -1}")).IsNull();
    }

    [Test]
    public async Task InfoUrl_AppendsInfoBeforeQuery()
    {
        await Assert.That(TermitusAccount.InfoUrl("https://termitus.online/sub/abc")).IsEqualTo("https://termitus.online/sub/abc/info");
        await Assert.That(TermitusAccount.InfoUrl("https://termitus.online/sub/abc/?f=1")).IsEqualTo("https://termitus.online/sub/abc/info?f=1");
        await Assert.That(TermitusAccount.InfoUrl("vless://x@y:1")).IsNull();
        await Assert.That(TermitusAccount.InfoUrl("")).IsNull();
    }

    [Test]
    public async Task RenewUrl_OnlyOurHttpsLinks()
    {
        await Assert.That(TermitusAccount.IsSafeRenewUrl("https://t.me/TErpiUsis_bot")).IsTrue();
        await Assert.That(TermitusAccount.IsSafeRenewUrl("https://termitus.online/pay")).IsTrue();
        await Assert.That(TermitusAccount.IsSafeRenewUrl("http://t.me/x")).IsFalse();
        await Assert.That(TermitusAccount.IsSafeRenewUrl("https://t.me.evil.com/x")).IsFalse();
        await Assert.That(TermitusAccount.IsSafeRenewUrl("file:///etc/passwd")).IsFalse();
        await Assert.That(TermitusAccount.IsSafeRenewUrl(null)).IsFalse();
    }

    [Test]
    public async Task DecodeTitle_Base64Prefix()
    {
        await Assert.That(TermitusAccount.DecodeTitle("base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("ТеРМиТуС")))).IsEqualTo("ТеРМиТуС");
        await Assert.That(TermitusAccount.DecodeTitle("TeRMiTuS")).IsEqualTo("TeRMiTuS");
        await Assert.That(TermitusAccount.DecodeTitle("base64:!!!")).IsNull();
    }

    // ---- header / JSON fallback ----

    private static TermitusAccountSnapshot HeaderOnly(string? header, DateTimeOffset at) =>
        TermitusAccount.WithHeaders(null, "sub1", header, null, null, at);

    [Test]
    public async Task Compute_NoData_IsUnknown()
    {
        await Assert.That(TermitusAccount.Compute(null, Now).State).IsEqualTo(TermitusAccountState.Unknown);
        await Assert.That(TermitusAccount.Compute(new TermitusAccountSnapshot { SubId = "sub1" }, Now).State).IsEqualTo(TermitusAccountState.Unknown);
    }

    [Test]
    public async Task Compute_HeaderActive_FarExpiry()
    {
        var s = HeaderOnly($"upload=100; download=900; total=0; expire={Ts(Now.AddDays(40))}", Now);
        var st = TermitusAccount.Compute(s, Now);
        await Assert.That(st.State).IsEqualTo(TermitusAccountState.Active);
        await Assert.That(st.TrafficUsed).IsEqualTo(1000L);
        await Assert.That(st.TrafficTotal).IsNull();
        await Assert.That(st.DaysLeft).IsEqualTo(40);
        await Assert.That(st.DevicesUsed).IsNull(); // header knows nothing about devices
    }

    [Test]
    public async Task Compute_ThreeDaysOrLess_IsExpiring()
    {
        var st = TermitusAccount.Compute(HeaderOnly($"expire={Ts(Now.AddDays(3).AddHours(5))}", Now), Now);
        await Assert.That(st.State).IsEqualTo(TermitusAccountState.Expiring);
        await Assert.That(st.DaysLeft).IsEqualTo(3);
        var st4 = TermitusAccount.Compute(HeaderOnly($"expire={Ts(Now.AddDays(4).AddHours(1))}", Now), Now);
        await Assert.That(st4.State).IsEqualTo(TermitusAccountState.Active);
        var stHours = TermitusAccount.Compute(HeaderOnly($"expire={Ts(Now.AddHours(5))}", Now), Now);
        await Assert.That(stHours.State).IsEqualTo(TermitusAccountState.Expiring);
        await Assert.That(stHours.DaysLeft).IsEqualTo(0);
    }

    [Test]
    public async Task Compute_PastExpiry_IsExpired()
    {
        var st = TermitusAccount.Compute(HeaderOnly($"expire={Ts(Now.AddDays(-1))}", Now), Now);
        await Assert.That(st.State).IsEqualTo(TermitusAccountState.Expired);
    }

    [Test]
    public async Task Compute_SuccessfulResponseWithoutHeader_IsInactive()
    {
        var st = TermitusAccount.Compute(HeaderOnly(null, Now), Now);
        await Assert.That(st.State).IsEqualTo(TermitusAccountState.Inactive);
        await Assert.That(st.TrafficUsed).IsNull();
    }

    [Test]
    public async Task Compute_HeaderWithoutExpiry_IsActiveWithNoEndDate()
    {
        var st = TermitusAccount.Compute(HeaderOnly("upload=0; download=5; total=0", Now), Now);
        await Assert.That(st.State).IsEqualTo(TermitusAccountState.Active);
        await Assert.That(st.Until).IsNull();
    }

    [Test]
    public async Task Compute_FreshInfo_AddsPlanDevicesAndOverridesExpiry()
    {
        var s = HeaderOnly($"upload=1; download=2; total=0; expire={Ts(Now.AddDays(2))}", Now.AddMinutes(-1));
        s.Info = TermitusAccount.ParseInfoJson($$"""
            {"expire": {{Ts(Now.AddDays(32))}}, "plan": "Месяц", "devices_used": 2, "devices_allowed": 3,
             "devices": [{"name": "Mac", "platform": "macOS", "is_active": true}, {"name": "", "is_active": false}],
             "renew_url": "https://t.me/TErpiUsis_bot"}
            """);
        s.InfoAt = Ts(Now);
        var st = TermitusAccount.Compute(s, Now);
        await Assert.That(st.State).IsEqualTo(TermitusAccountState.Active); // renewed: /info is newer
        await Assert.That(st.DaysLeft).IsEqualTo(32);
        await Assert.That(st.Plan).IsEqualTo("Месяц");
        await Assert.That(st.DevicesUsed).IsEqualTo(2);
        await Assert.That(st.DevicesAllowed).IsEqualTo(3);
        await Assert.That(st.Devices.Count).IsEqualTo(2);
        await Assert.That(st.Devices[1].Name).IsEqualTo("—");
        await Assert.That(st.RenewUrl).IsEqualTo("https://t.me/TErpiUsis_bot");
        await Assert.That(st.TrafficUsed).IsEqualTo(3L); // traffic still from the header
    }

    [Test]
    public async Task Compute_StaleInfo_HeaderWinsExpiry_ButExtrasStay()
    {
        var s = HeaderOnly($"expire={Ts(Now.AddDays(-2))}", Now);
        s.Info = new TermitusAccountInfoDto { Expire = Ts(Now.AddDays(20)), Plan = "Год", RenewUrl = "https://evil.example/pay" };
        s.InfoAt = Ts(Now.AddHours(-7));
        var st = TermitusAccount.Compute(s, Now);
        await Assert.That(st.State).IsEqualTo(TermitusAccountState.Expired);
        await Assert.That(st.Plan).IsEqualTo("Год");
        await Assert.That(st.RenewUrl).IsNull(); // not our link -> the app opens the bot
    }

    [Test]
    public async Task Compute_InfoWithNullExpire_FallsBackToHeader()
    {
        var s = HeaderOnly($"expire={Ts(Now.AddDays(10))}", Now.AddMinutes(-1));
        s.Info = TermitusAccount.ParseInfoJson("""{"expire": null, "devices_used": 1, "devices_allowed": 3}""");
        s.InfoAt = Ts(Now);
        var st = TermitusAccount.Compute(s, Now);
        await Assert.That(st.State).IsEqualTo(TermitusAccountState.Active);
        await Assert.That(st.DaysLeft).IsEqualTo(10);
        await Assert.That(st.DevicesUsed).IsEqualTo(1);
    }

    [Test]
    public async Task WithHeaders_KeepsInfoOfSameSub_DropsOtherSub()
    {
        var old = new TermitusAccountSnapshot { SubId = "sub1", Info = new TermitusAccountInfoDto { Plan = "Месяц" }, InfoAt = Ts(Now) };
        await Assert.That(TermitusAccount.WithHeaders(old, "sub1", "expire=0", null, null, Now).Info!.Plan).IsEqualTo("Месяц");
        await Assert.That(TermitusAccount.WithHeaders(old, "sub2", "expire=0", null, null, Now).Info).IsNull();
    }

    [Test]
    public async Task Snapshot_RoundTripsThroughJson_WithoutUrl()
    {
        var s = TermitusAccount.WithHeaders(null, "sub1", "upload=1; download=2; total=3; expire=1794614400", "TER", null, Now);
        var json = JsonSerializer.Serialize(s);
        await Assert.That(json.Contains("http", StringComparison.Ordinal)).IsFalse();
        var back = JsonSerializer.Deserialize<TermitusAccountSnapshot>(json);
        await Assert.That(back!.UserInfo!.Expire).IsEqualTo(1794614400L);
        await Assert.That(back.UserInfo.Total).IsEqualTo(3L);
    }

    // ---- formatting ----

    [Test]
    public async Task FormatDate_RussianGenitive_YearOnlyWhenDifferent()
    {
        var nov = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 14, 12, 0, 0)));
        await Assert.That(TermitusAccount.FormatDate(nov, Now, en: false)).IsEqualTo("14 ноября");
        await Assert.That(TermitusAccount.FormatDate(nov, Now, en: true)).IsEqualTo("November 14");
        await Assert.That(TermitusAccount.FormatDate(nov.AddYears(1), Now, en: false)).IsEqualTo("14 ноября 2027");
    }

    [Test]
    public async Task FormatBytes_HumanUnits()
    {
        await Assert.That(TermitusAccount.FormatBytes(512, en: true)).IsEqualTo("512 B");
        await Assert.That(TermitusAccount.FormatBytes(1536L * 1024 * 1024, en: true)).IsEqualTo("1.5 GB");
        await Assert.That(TermitusAccount.FormatBytes(1536L * 1024 * 1024, en: false)).IsEqualTo("1,5 ГБ");
        await Assert.That(TermitusAccount.FormatBytes(820L * 1024 * 1024, en: false)).IsEqualTo("820 МБ");
    }

    [Test]
    public async Task RuPlural_Days()
    {
        string P(int n) => TermitusAccount.RuPlural(n, "день", "дня", "дней");
        await Assert.That(P(1)).IsEqualTo("день");
        await Assert.That(P(2)).IsEqualTo("дня");
        await Assert.That(P(5)).IsEqualTo("дней");
        await Assert.That(P(11)).IsEqualTo("дней");
        await Assert.That(P(21)).IsEqualTo("день");
        await Assert.That(P(23)).IsEqualTo("дня");
    }
    // ---- 0.4: balance + top-up ----

    [Test]
    public async Task Info_BalanceAndTopup()
    {
        var s = HeaderOnly("upload=0; download=0; total=0; expire=" + Ts(Now.AddDays(30)), Now.AddMinutes(-1));
        s.Info = TermitusAccount.ParseInfoJson("""{"balance": 249.5, "topup_url": "https://t.me/TErpiUsis_bot?start=topup"}""");
        s.InfoAt = Ts(Now);
        var st = TermitusAccount.Compute(s, Now);
        await Assert.That(st.Balance).IsEqualTo(249.5m);
        await Assert.That(st.TopupUrl).IsEqualTo("https://t.me/TErpiUsis_bot?start=topup");
    }

    [Test]
    public async Task Info_BalanceAsString_UnsafeTopupDropped()
    {
        var s = HeaderOnly(null, Now.AddMinutes(-1));
        s.Info = TermitusAccount.ParseInfoJson("""{"balance": "0", "topup_url": "http://termitus.online/pay"}""");
        s.InfoAt = Ts(Now);
        var st = TermitusAccount.Compute(s, Now);
        await Assert.That(st.Balance).IsEqualTo(0m);
        await Assert.That(st.TopupUrl).IsNull();

        s.Info = TermitusAccount.ParseInfoJson("""{"topup_url": "https://pay.evil.example/termitus.online"}""");
        await Assert.That(TermitusAccount.Compute(s, Now).TopupUrl).IsNull();
        await Assert.That(TermitusAccount.Compute(s, Now).Balance).IsNull();
    }

    [Test]
    public async Task FormatMoney_RuAndEn()
    {
        await Assert.That(TermitusAccount.FormatMoney(1234m, en: false)).IsEqualTo("1\u00A0234\u00A0₽");
        await Assert.That(TermitusAccount.FormatMoney(99.5m, en: false)).IsEqualTo("99,50\u00A0₽");
        await Assert.That(TermitusAccount.FormatMoney(0m, en: false)).IsEqualTo("0\u00A0₽");
        await Assert.That(TermitusAccount.FormatMoney(1234.5m, en: true)).IsEqualTo("1,234.50 RUB");
    }
}
