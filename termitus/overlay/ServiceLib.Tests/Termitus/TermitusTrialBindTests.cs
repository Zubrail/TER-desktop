using System.Text.Json;

using TUnit.Core;

using ServiceLib.Services;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.Termitus;

/// <summary>0.6.5 «Привязать к Telegram»: normal login with trial_code; poll answer parsing and what happens to the stored trial.</summary>
public class TermitusTrialBindTests
{
    private const string Url = "\"subscription_url\":\"https://termitus.online/subscription/newdevice\"";

    [Test]
    public async Task StartRequest_CarriesTrialCode_OnlyWhenGiven()
    {
        var plain = JsonSerializer.Serialize(new TermitusLoginStartRequest("Mac", "macos"));
        using var d1 = JsonDocument.Parse(plain);
        await Assert.That(d1.RootElement.TryGetProperty("trial_code", out _)).IsFalse();

        var with = JsonSerializer.Serialize(new TermitusLoginStartRequest("Mac", "macos", "GIFT-1234"));
        using var d2 = JsonDocument.Parse(with);
        await Assert.That(d2.RootElement.GetProperty("trial_code").GetString()).IsEqualTo("GIFT-1234");
        await Assert.That(d2.RootElement.GetProperty("device_name").GetString()).IsEqualTo("Mac");
    }

    [Test]
    public async Task Poll_TrialAttached_IsParsed()
    {
        var o = TermitusLogin.InterpretPoll(200, "{\"status\":\"ok\"," + Url + ",\"trial_attached\":true}");
        await Assert.That(o.Kind).IsEqualTo(TermitusPollKind.Ok);
        await Assert.That(o.SubscriptionUrl).IsEqualTo("https://termitus.online/subscription/newdevice");
        await Assert.That(o.TrialAttached).IsTrue();
        await Assert.That(o.TrialNote).IsNull();
    }

    [Test]
    [Arguments("code_unavailable", "code_unavailable")]
    [Arguments("cooldown", "cooldown")]
    [Arguments("error", "error")]
    [Arguments("something_new", "error")]
    [Arguments("  COOLDOWN ", "cooldown")]
    public async Task Poll_TrialNote_IsNormalized_LoginStillOk(string raw, string expected)
    {
        var o = TermitusLogin.InterpretPoll(200, "{\"status\":\"ok\"," + Url + ",\"trial_note\":\"" + raw + "\"}");
        await Assert.That(o.Kind).IsEqualTo(TermitusPollKind.Ok);
        await Assert.That(o.TrialAttached).IsFalse();
        await Assert.That(o.TrialNote).IsEqualTo(expected);
    }

    [Test]
    public async Task Poll_WithoutTrialFields_IsPlainLogin()
    {
        var o = TermitusLogin.InterpretPoll(200, "{\"status\":\"ok\"," + Url + "}");
        await Assert.That(o.Kind).IsEqualTo(TermitusPollKind.Ok);
        await Assert.That(o.TrialAttached).IsFalse();
        await Assert.That(o.TrialNote).IsNull();
        await Assert.That(TermitusLogin.NormalizeTrialNote(null)).IsNull();
        await Assert.That(TermitusLogin.NormalizeTrialNote("")).IsNull();
    }

    [Test]
    public async Task State_WhenTheStoredTrialIsCleared()
    {
        await Assert.That(TermitusHomeViewModel.ShouldClearTrial(true, null)).IsTrue();
        await Assert.That(TermitusHomeViewModel.ShouldClearTrial(false, "code_unavailable")).IsTrue();
        await Assert.That(TermitusHomeViewModel.ShouldClearTrial(false, "cooldown")).IsTrue();
        await Assert.That(TermitusHomeViewModel.ShouldClearTrial(false, "error")).IsFalse();   // kept: another try is possible
        await Assert.That(TermitusHomeViewModel.ShouldClearTrial(false, null)).IsFalse();
    }

    [Test]
    public async Task ResultLine_MatchesTheOutcome()
    {
        var loc = TermitusLocale.Current;
        await Assert.That(TermitusHomeViewModel.BindResultLine(loc, true, null)).IsEqualTo(loc.TrialBindDone);
        await Assert.That(TermitusHomeViewModel.BindResultLine(loc, false, "code_unavailable")).IsEqualTo(loc.TrialNoteUnavailable);
        await Assert.That(TermitusHomeViewModel.BindResultLine(loc, false, "cooldown")).IsEqualTo(loc.TrialNoteCooldown);
        await Assert.That(TermitusHomeViewModel.BindResultLine(loc, false, "error")).IsEqualTo(loc.TrialNoteError);
        await Assert.That(TermitusHomeViewModel.BindResultLine(loc, false, null)).IsEqualTo(loc.TrialBindLoggedIn);
    }
}
