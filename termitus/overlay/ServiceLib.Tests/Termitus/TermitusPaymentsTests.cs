using ServiceLib.Services;
using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>0.5 in-app payment contract: plans/pay/status parsing and the 3 s / 15 min poll loop.</summary>
public class TermitusPaymentsTests
{
    private const string PlansJson = """
        {"plans":[{"id":"m1","title":"1 месяц","price_rub":199,"days":30},{"id":"y1","title":"Год","price_rub":"1590","days":365},{"id":"bad","title":"","price_rub":1}],
         "methods":[{"id":"card","title":"Карта"},{"id":"usdt","title":"USDT"},{"id":"","title":"x"}],
         "offer_url":"https://termitus.online/offer"}
        """;

    [Test]
    public async Task Endpoint_KeepsQuery_AndAppendsSegments()
    {
        await Assert.That(TermitusPayments.Endpoint("https://termitus.online/subscription/TOK?variant=mobile", "plans"))
            .IsEqualTo("https://termitus.online/subscription/TOK/plans?variant=mobile");
        await Assert.That(TermitusPayments.Endpoint("https://termitus.online/subscription/TOK/", "pay", "p 1"))
            .IsEqualTo("https://termitus.online/subscription/TOK/pay/p%201");
        await Assert.That(TermitusPayments.Endpoint("ftp://x/y", "plans")).IsNull();
        await Assert.That(TermitusPayments.Endpoint(null, "plans")).IsNull();
    }

    [Test]
    public async Task ParsePlans_SkipsBadEntries_AcceptsNumericStrings()
    {
        var p = TermitusPayments.ParsePlans(PlansJson)!;
        await Assert.That(p.Plans.Count).IsEqualTo(2);
        await Assert.That(p.Plans[1].PriceRub).IsEqualTo(1590);
        await Assert.That(p.Methods.Count).IsEqualTo(2);
        await Assert.That(p.OfferUrl).IsEqualTo("https://termitus.online/offer");
    }

    [Test]
    public async Task ParsePlans_GarbageOrEmpty_IsNull_AndNonHttpsOfferDropped()
    {
        await Assert.That(TermitusPayments.ParsePlans("<html>")).IsNull();
        await Assert.That(TermitusPayments.ParsePlans("{\"plans\":[],\"methods\":[]}")).IsNull();
        await Assert.That(TermitusPayments.ParsePlans("[1]")).IsNull();
        var p = TermitusPayments.ParsePlans("""{"plans":[{"id":"a","title":"A","price_rub":1,"days":1}],"methods":[{"id":"m","title":"M"}],"offer_url":"javascript:alert(1)"}""")!;
        await Assert.That(p.OfferUrl).IsNull();
    }

    [Test]
    public async Task InterpretStart_Cases()
    {
        await Assert.That(TermitusPayments.InterpretStart(null, null).Kind).IsEqualTo(TermitusPaymentsKind.Offline);
        await Assert.That(TermitusPayments.InterpretStart(404, "").Kind).IsEqualTo(TermitusPaymentsKind.Unavailable);
        await Assert.That(TermitusPayments.InterpretStart(500, "").Kind).IsEqualTo(TermitusPaymentsKind.Error);
        await Assert.That(TermitusPayments.InterpretStart(200, "nope").Kind).IsEqualTo(TermitusPaymentsKind.Error);
        await Assert.That(TermitusPayments.InterpretStart(200, """{"payment_id":"p1","pay_url":"http://evil/x"}""").Kind).IsEqualTo(TermitusPaymentsKind.Error);
        var ok = TermitusPayments.InterpretStart(201, """{"payment_id":"p1","pay_url":"https://pay.example/x"}""");
        await Assert.That(ok.Kind).IsEqualTo(TermitusPaymentsKind.Ok);
        await Assert.That(ok.PaymentId).IsEqualTo("p1");
    }

    [Test]
    public async Task ParseStatus_Words()
    {
        await Assert.That(TermitusPayments.ParseStatus(200, """{"status":"paid"}""")).IsEqualTo(TermitusPayStatus.Paid);
        await Assert.That(TermitusPayments.ParseStatus(200, """{"status":"PENDING"}""")).IsEqualTo(TermitusPayStatus.Pending);
        await Assert.That(TermitusPayments.ParseStatus(200, """{"status":"weird"}""")).IsNull();
        await Assert.That(TermitusPayments.ParseStatus(500, """{"status":"paid"}""")).IsNull();
        await Assert.That(TermitusPayments.ParseStatus(200, "x")).IsNull();
    }

    [Test]
    public async Task Wait_StopsOnPaid_KeepsGoingOnPendingAndNoAnswer()
    {
        var answers = new Queue<TermitusPayStatus?>([TermitusPayStatus.Pending, null, TermitusPayStatus.Pending, TermitusPayStatus.Paid]);
        var delays = 0;
        var r = await TermitusPayments.WaitAsync(_ => Task.FromResult(answers.Dequeue()), TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(15),
            (_, _) => { delays++; return Task.CompletedTask; }, CancellationToken.None);
        await Assert.That(r).IsEqualTo(TermitusWaitOutcome.Paid);
        await Assert.That(delays).IsEqualTo(3);
    }

    [Test]
    public async Task Wait_TimesOutAfter15Minutes_AtThreeSecondSteps()
    {
        var calls = 0;
        var r = await TermitusPayments.WaitAsync(_ => { calls++; return Task.FromResult<TermitusPayStatus?>(TermitusPayStatus.Pending); },
            TermitusPayments.PollInterval, TermitusPayments.MaxWait, (_, _) => Task.CompletedTask, CancellationToken.None);
        await Assert.That(r).IsEqualTo(TermitusWaitOutcome.TimedOut);
        await Assert.That(calls).IsEqualTo(301); // t = 0, 3, ... 900 s
    }

    [Test]
    public async Task Wait_FailedExpiredAndCancel()
    {
        Task<TermitusPayStatus?> F(TermitusPayStatus s) => Task.FromResult<TermitusPayStatus?>(s);
        await Assert.That(await TermitusPayments.WaitAsync(_ => F(TermitusPayStatus.Failed), TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1), (_, _) => Task.CompletedTask, CancellationToken.None)).IsEqualTo(TermitusWaitOutcome.Failed);
        await Assert.That(await TermitusPayments.WaitAsync(_ => F(TermitusPayStatus.Expired), TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1), (_, _) => Task.CompletedTask, CancellationToken.None)).IsEqualTo(TermitusWaitOutcome.Expired);
        using var cts = new CancellationTokenSource();
        var r = await TermitusPayments.WaitAsync(_ => F(TermitusPayStatus.Pending), TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(1),
            (_, _) => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cts.Token);
        await Assert.That(r).IsEqualTo(TermitusWaitOutcome.Cancelled);
    }
}
