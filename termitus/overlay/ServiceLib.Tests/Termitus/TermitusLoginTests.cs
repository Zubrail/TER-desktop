using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>
/// «Войти через Telegram»: start-response validation, poll answer interpretation and the
/// waiting loop (fake clock: no network, no real time).
/// </summary>
public class TermitusLoginTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static TermitusLoginStartResponse Start(string? code = "AbC123", string? bot = "https://t.me/TErpiUsis_bot?start=login_AbC123",
        double? expires = 300, double? poll = 2) => new(code, bot, expires, poll);

    // ---- start response ----

    [Test]
    public async Task ToSession_ValidContract()
    {
        var s = TermitusLogin.ToSession(Start(), Now);
        await Assert.That(s).IsNotNull();
        await Assert.That(s!.Code).IsEqualTo("AbC123");
        await Assert.That(s.Deadline).IsEqualTo(Now.AddSeconds(300));
        await Assert.That(s.PollInterval).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task ToSession_DefaultsAndClamps()
    {
        var s = TermitusLogin.ToSession(Start(expires: null, poll: null), Now)!;
        await Assert.That(s.Deadline).IsEqualTo(Now + TermitusLogin.DefaultLifetime);
        await Assert.That(s.PollInterval).IsEqualTo(TermitusLogin.DefaultPollInterval);

        var fast = TermitusLogin.ToSession(Start(poll: 0.1), Now)!;
        await Assert.That(fast.PollInterval).IsEqualTo(TimeSpan.FromSeconds(1)); // never hammer the backend
        var slow = TermitusLogin.ToSession(Start(poll: 600, expires: 999999), Now)!;
        await Assert.That(slow.PollInterval).IsEqualTo(TimeSpan.FromSeconds(15));
        await Assert.That(slow.Deadline).IsEqualTo(Now + TermitusLogin.DefaultLifetime); // absurd lifetime ignored
    }

    [Test]
    public async Task ToSession_RejectsBadCodeOrForeignBotLink()
    {
        await Assert.That(TermitusLogin.ToSession(null, Now)).IsNull();
        await Assert.That(TermitusLogin.ToSession(Start(code: null), Now)).IsNull();
        await Assert.That(TermitusLogin.ToSession(Start(code: "a/../b"), Now)).IsNull();
        await Assert.That(TermitusLogin.ToSession(Start(code: "ab"), Now)).IsNull();
        await Assert.That(TermitusLogin.ToSession(Start(bot: "http://t.me/x"), Now)).IsNull();
        await Assert.That(TermitusLogin.ToSession(Start(bot: "https://t.me.evil.com/x"), Now)).IsNull();
        await Assert.That(TermitusLogin.ToSession(Start(bot: "tg://resolve?domain=x"), Now)).IsNull();
        await Assert.That(TermitusLogin.ToSession(Start(bot: null), Now)).IsNull();
        await Assert.That(TermitusLogin.ToSession(Start(bot: "https://termitus.online/app/go"), Now)).IsNotNull();
    }

    [Test]
    public async Task StartResponse_ParsesFromJson()
    {
        var dto = JsonSerializer.Deserialize<TermitusLoginStartResponse>(
            """{"code":"Q1w2E3","bot_url":"https://t.me/b?start=x","expires_in":"300","poll_interval":2.5,"extra":1}""",
            TermitusAccount.InfoJsonOptions);
        var s = TermitusLogin.ToSession(dto, Now)!;
        await Assert.That(s.Code).IsEqualTo("Q1w2E3");
        await Assert.That(s.PollInterval).IsEqualTo(TimeSpan.FromSeconds(2.5));
    }

    // ---- poll answers ----

    [Test]
    public async Task InterpretPoll_Matrix()
    {
        await Assert.That(TermitusLogin.InterpretPoll(202, """{"status":"pending"}""").Kind).IsEqualTo(TermitusPollKind.Pending);
        await Assert.That(TermitusLogin.InterpretPoll(202, null).Kind).IsEqualTo(TermitusPollKind.Pending);
        await Assert.That(TermitusLogin.InterpretPoll(200, """{"status":"pending"}""").Kind).IsEqualTo(TermitusPollKind.Pending);
        await Assert.That(TermitusLogin.InterpretPoll(410, """{"status":"expired"}""").Kind).IsEqualTo(TermitusPollKind.Expired);
        await Assert.That(TermitusLogin.InterpretPoll(410, """{"status":"DECLINED"}""").Kind).IsEqualTo(TermitusPollKind.Declined);
        await Assert.That(TermitusLogin.InterpretPoll(410, """{"status":"used"}""").Kind).IsEqualTo(TermitusPollKind.Used);
        await Assert.That(TermitusLogin.InterpretPoll(410, "garbage").Kind).IsEqualTo(TermitusPollKind.Expired);
        await Assert.That(TermitusLogin.InterpretPoll(404, null).Kind).IsEqualTo(TermitusPollKind.Expired);
        await Assert.That(TermitusLogin.InterpretPoll(null, null).Kind).IsEqualTo(TermitusPollKind.Retry);
        await Assert.That(TermitusLogin.InterpretPoll(502, "<html>").Kind).IsEqualTo(TermitusPollKind.Retry);
        await Assert.That(TermitusLogin.InterpretPoll(429, null).Kind).IsEqualTo(TermitusPollKind.Retry);
        await Assert.That(TermitusLogin.InterpretPoll(400, null).Kind).IsEqualTo(TermitusPollKind.Failed);
    }

    [Test]
    public async Task InterpretPoll_OkNeedsAnHttpsSubscriptionUrl()
    {
        var ok = TermitusLogin.InterpretPoll(200, """{"status":"ok","subscription_url":" https://sub.example.org/s/TOKEN "}""");
        await Assert.That(ok.Kind).IsEqualTo(TermitusPollKind.Ok);
        await Assert.That(ok.SubscriptionUrl).IsEqualTo("https://sub.example.org/s/TOKEN");

        await Assert.That(TermitusLogin.InterpretPoll(200, """{"status":"ok"}""").Kind).IsEqualTo(TermitusPollKind.Failed);
        await Assert.That(TermitusLogin.InterpretPoll(200, """{"status":"ok","subscription_url":"http://x.org/s"}""").Kind).IsEqualTo(TermitusPollKind.Failed);
        await Assert.That(TermitusLogin.InterpretPoll(200, """{"status":"ok","subscription_url":"https://u:p@x.org/s"}""").Kind).IsEqualTo(TermitusPollKind.Failed);
        await Assert.That(TermitusLogin.InterpretPoll(200, """{"status":"ok","subscription_url":"vless://abc@x:443"}""").Kind).IsEqualTo(TermitusPollKind.Failed);
        await Assert.That(TermitusLogin.InterpretPoll(200, "not json").Kind).IsEqualTo(TermitusPollKind.Failed);
    }

    // ---- the waiting loop ----

    private sealed class FakeClock
    {
        public DateTimeOffset T = Now;
        public readonly List<TimeSpan> Waits = [];

        public Task Delay(TimeSpan d, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Waits.Add(d);
            T += d;
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task Wait_PendingPendingThenOk()
    {
        var clock = new FakeClock();
        var s = TermitusLogin.ToSession(Start(), Now)!;
        var answers = new Queue<TermitusPollOutcome>([new(TermitusPollKind.Pending), new(TermitusPollKind.Retry), new(TermitusPollKind.Ok, "https://x/s")]);
        var o = await TermitusLogin.WaitForConfirmationAsync(s, _ => Task.FromResult(answers.Dequeue()), clock.Delay, () => clock.T, CancellationToken.None);
        await Assert.That(o.Kind).IsEqualTo(TermitusPollKind.Ok);
        await Assert.That(clock.Waits.Count).IsEqualTo(3);
        await Assert.That(clock.Waits.All(w => w == TimeSpan.FromSeconds(2))).IsTrue();
    }

    [Test]
    public async Task Wait_ExpiresAtDeadline_LastWaitShortened()
    {
        var clock = new FakeClock();
        var s = TermitusLogin.ToSession(Start(expires: 5, poll: 2), Now)!;
        var polls = 0;
        var o = await TermitusLogin.WaitForConfirmationAsync(s, _ =>
        {
            polls++;
            return Task.FromResult(new TermitusPollOutcome(TermitusPollKind.Pending));
        }, clock.Delay, () => clock.T, CancellationToken.None);
        await Assert.That(o.Kind).IsEqualTo(TermitusPollKind.Expired);
        await Assert.That(polls).IsEqualTo(3); // at 2 s, 4 s, 5 s
        await Assert.That(clock.Waits[^1]).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(clock.T).IsEqualTo(Now.AddSeconds(5));
    }

    [Test]
    public async Task Wait_StopsOnDeclined()
    {
        var clock = new FakeClock();
        var s = TermitusLogin.ToSession(Start(), Now)!;
        var o = await TermitusLogin.WaitForConfirmationAsync(s, _ => Task.FromResult(new TermitusPollOutcome(TermitusPollKind.Declined)),
            clock.Delay, () => clock.T, CancellationToken.None);
        await Assert.That(o.Kind).IsEqualTo(TermitusPollKind.Declined);
        await Assert.That(clock.Waits.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Wait_CancelPropagates()
    {
        var clock = new FakeClock();
        var s = TermitusLogin.ToSession(Start(), Now)!;
        using var cts = new CancellationTokenSource();
        var polls = 0;
        var threw = false;
        try
        {
            await TermitusLogin.WaitForConfirmationAsync(s, _ =>
            {
                if (++polls == 2)
                {
                    cts.Cancel(); // user pressed «Отмена»
                }
                return Task.FromResult(new TermitusPollOutcome(TermitusPollKind.Pending));
            }, clock.Delay, () => clock.T, cts.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
        await Assert.That(polls).IsEqualTo(2);
    }

    [Test]
    public async Task RateLimited_HonoursRetryAfterOnce()
    {
        var o = TermitusLogin.InterpretPoll(429, null, TimeSpan.FromSeconds(20));
        await Assert.That(o.Kind).IsEqualTo(TermitusPollKind.Retry);
        await Assert.That(o.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(20));

        var clock = new FakeClock();
        var s = TermitusLogin.ToSession(Start(poll: 3), Now)!;
        var answers = new Queue<TermitusPollOutcome>([
            new(TermitusPollKind.Retry, null, TimeSpan.FromSeconds(20)),
            new(TermitusPollKind.Pending),
            new(TermitusPollKind.Retry, null, TimeSpan.FromMinutes(10)),
            new(TermitusPollKind.Ok, "https://x/s")]);
        var r = await TermitusLogin.WaitForConfirmationAsync(s, _ => Task.FromResult(answers.Dequeue()), clock.Delay, () => clock.T, CancellationToken.None);
        await Assert.That(r.Kind).IsEqualTo(TermitusPollKind.Ok);
        await Assert.That(clock.Waits.Select(w => w.TotalSeconds).ToArray()).IsEquivalentTo(new double[] { 3, 20, 3, 60 });
    }

    // ---- small helpers ----

    [Test]
    public async Task Countdown_DeviceName_Platform()
    {
        await Assert.That(TermitusLogin.FormatCountdown(300)).IsEqualTo("5:00");
        await Assert.That(TermitusLogin.FormatCountdown(59)).IsEqualTo("0:59");
        await Assert.That(TermitusLogin.FormatCountdown(-3)).IsEqualTo("0:00");
        var s = TermitusLogin.ToSession(Start(expires: 300), Now)!;
        await Assert.That(s.SecondsLeft(Now.AddSeconds(0.4))).IsEqualTo(300);
        await Assert.That(s.IsExpired(Now.AddSeconds(300))).IsTrue();
        await Assert.That(TermitusLogin.DeviceName("  Ivans-MacBook-Pro ")).IsEqualTo("Ivans-MacBook-Pro");
        await Assert.That(TermitusLogin.DeviceName("")).IsEqualTo("Computer");
        await Assert.That(TermitusLogin.DeviceName(new string('x', 100)).Length).IsEqualTo(64);
        await Assert.That(TermitusLogin.PlatformName()).IsEqualTo(OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux");
    }

    [Test]
    public async Task StartRequest_SerializesSnakeCase()
    {
        var json = JsonSerializer.Serialize(new TermitusLoginStartRequest("Mac", "macos"));
        await Assert.That(json).IsEqualTo("""{"device_name":"Mac","platform":"macos"}""");
    }
}
