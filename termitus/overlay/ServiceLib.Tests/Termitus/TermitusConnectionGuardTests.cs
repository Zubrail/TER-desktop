using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>
/// TER connection guard decisions: debounce, backoff, re-pick, give-up, no loops.
/// The clock is passed in, so every rule is checked deterministically.
/// </summary>
public class TermitusConnectionGuardTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private const string WiFi = "en0=192.168.1.5";
    private const string Hotspot = "en0=172.20.10.3";

    private static TermitusReconnectPolicy Policy()
    {
        var p = new TermitusReconnectPolicy();
        p.ObserveNetwork(WiFi, T0); // baseline
        return p;
    }

    [Test]
    public async Task HealthyCore_NeverActs()
    {
        var p = Policy();
        for (var i = 0; i < 100; i++)
        {
            var d = p.Next(true, true, T0.AddSeconds(2 * i));
            await Assert.That(d.Action).IsEqualTo(TermitusGuardAction.None);
        }
    }

    [Test]
    public async Task CoreDeath_IsConfirmedOnTwoTicks_ThenRestartsSameServer()
    {
        var p = Policy();
        await Assert.That(p.Next(false, true, T0).Action).IsEqualTo(TermitusGuardAction.None);
        var d = p.Next(false, true, T0.AddSeconds(2));
        await Assert.That(d.Action).IsEqualTo(TermitusGuardAction.Recover);
        await Assert.That(d.Trigger).IsEqualTo(TermitusTrigger.CoreDied);
        await Assert.That(d.Attempt).IsEqualTo(1);
        await Assert.That(d.Repick).IsFalse(); // first try: just restart
    }

    [Test]
    public async Task OneDeadTickInsideAReload_IsIgnored()
    {
        var p = Policy();
        await Assert.That(p.Next(false, true, T0).Action).IsEqualTo(TermitusGuardAction.None);
        await Assert.That(p.Next(true, true, T0.AddSeconds(2)).Action).IsEqualTo(TermitusGuardAction.None);
        await Assert.That(p.Next(false, true, T0.AddSeconds(4)).Action).IsEqualTo(TermitusGuardAction.None);
    }

    [Test]
    public async Task RepeatedDeath_BacksOff_RepicksFromSecondAttempt_ThenGivesUpOnce_ThenSlowRetry()
    {
        var p = Policy();
        var now = T0;
        var decisions = new List<(DateTime At, TermitusGuardDecision D)>();
        // Core stays dead for 10 minutes, ticks every 2 s.
        for (var i = 0; i < 300; i++)
        {
            var d = p.Next(false, true, now);
            if (d.Action != TermitusGuardAction.None)
            {
                decisions.Add((now, d));
            }
            now = now.AddSeconds(2);
        }

        var recovers = decisions.Where(x => x.D.Action == TermitusGuardAction.Recover).ToList();
        var giveUps = decisions.Where(x => x.D.Action == TermitusGuardAction.GiveUp).ToList();

        // 5 fast attempts, exactly one GiveUp, then slow retries every 2 min — never a loop.
        await Assert.That(giveUps.Count).IsEqualTo(1);
        var fast = recovers.Where(x => x.At < giveUps[0].At).ToList();
        await Assert.That(fast.Count).IsEqualTo(TermitusReconnectPolicy.MaxAttempts);
        await Assert.That(fast[0].D.Repick).IsFalse();
        await Assert.That(fast.Skip(1).All(x => x.D.Repick)).IsTrue();
        for (var i = 1; i < fast.Count; i++)
        {
            var gap = fast[i].At - fast[i - 1].At;
            var expected = TermitusReconnectPolicy.Backoff[Math.Min(i - 1, TermitusReconnectPolicy.Backoff.Count - 1)];
            await Assert.That(gap >= expected).IsTrue();
        }
        var slow = recovers.Where(x => x.At > giveUps[0].At).ToList();
        await Assert.That(slow.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(slow.Count).IsLessThanOrEqualTo(5); // ≤ one per 2 min over the rest of 10 min
        await Assert.That(p.GaveUp).IsTrue();
    }

    [Test]
    public async Task Offline_DoesNotBurnAttempts()
    {
        var p = Policy();
        for (var i = 0; i < 60; i++)
        {
            await Assert.That(p.Next(false, false, T0.AddSeconds(2 * i)).Action).IsEqualTo(TermitusGuardAction.None);
        }
        await Assert.That(p.Attempts).IsEqualTo(0);
        // Network is back: act right away.
        await Assert.That(p.Next(false, true, T0.AddSeconds(122)).Action).IsEqualTo(TermitusGuardAction.Recover);
    }

    [Test]
    public async Task NetworkChange_WaitsUntilStable_ThenRestartsOnce()
    {
        var p = Policy();
        // Wi-Fi off → nothing → hotspot, changes 1 s apart (a typical hand-over).
        p.ObserveNetwork("", T0.AddSeconds(1));
        await Assert.That(p.Next(true, false, T0.AddSeconds(1)).Action).IsEqualTo(TermitusGuardAction.None);
        p.ObserveNetwork(Hotspot, T0.AddSeconds(2));
        await Assert.That(p.Next(true, true, T0.AddSeconds(3)).Action).IsEqualTo(TermitusGuardAction.None); // not settled
        var d = p.Next(true, true, T0.AddSeconds(2) + TermitusReconnectPolicy.NetworkSettle);
        await Assert.That(d.Action).IsEqualTo(TermitusGuardAction.Recover);
        await Assert.That(d.Trigger).IsEqualTo(TermitusTrigger.NetworkChanged);
        await Assert.That(d.Repick).IsFalse();
        // No second restart for the same change.
        for (var i = 1; i < 30; i++)
        {
            await Assert.That(p.Next(true, true, T0.AddSeconds(10 + 2 * i)).Action).IsEqualTo(TermitusGuardAction.None);
        }
    }

    [Test]
    public async Task DeadTunnelAfterNetworkChange_Repicks()
    {
        var p = Policy();
        p.ObserveNetwork(Hotspot, T0);
        var first = p.Next(true, true, T0 + TermitusReconnectPolicy.NetworkSettle);
        await Assert.That(first.Trigger).IsEqualTo(TermitusTrigger.NetworkChanged);
        p.ReportProbe(false);
        var d = p.Next(true, true, T0 + TermitusReconnectPolicy.NetworkSettle + TermitusReconnectPolicy.Backoff[0]);
        await Assert.That(d.Action).IsEqualTo(TermitusGuardAction.Recover);
        await Assert.That(d.Trigger).IsEqualTo(TermitusTrigger.TunnelDead);
        await Assert.That(d.Repick).IsTrue();
    }

    [Test]
    public async Task FlappingWifi_WithLiveCore_StopsRestartingAndNeverGivesUp()
    {
        var p = Policy();
        var now = T0;
        var recovers = 0;
        for (var i = 0; i < 200; i++)
        {
            p.ObserveNetwork(i % 2 == 0 ? Hotspot : WiFi, now);
            now = now.AddSeconds(10); // a change every 10 s, each one settles
            var d = p.Next(true, true, now);
            await Assert.That(d.Action).IsNotEqualTo(TermitusGuardAction.GiveUp);
            if (d.Action == TermitusGuardAction.Recover)
            {
                recovers++;
            }
        }
        await Assert.That(recovers).IsEqualTo(TermitusReconnectPolicy.MaxAttempts);
        await Assert.That(p.GaveUp).IsFalse();
    }

    [Test]
    public async Task HealthyForAWhile_ResetsTheCounter()
    {
        var p = Policy();
        p.Next(false, true, T0);
        await Assert.That(p.Next(false, true, T0.AddSeconds(2)).Action).IsEqualTo(TermitusGuardAction.Recover);
        await Assert.That(p.Attempts).IsEqualTo(1);
        p.Next(true, true, T0.AddSeconds(4));
        p.Next(true, true, T0.AddSeconds(2) + TermitusReconnectPolicy.HealthyReset);
        await Assert.That(p.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task Reset_ForgetsGiveUp()
    {
        var p = Policy();
        var now = T0;
        for (var i = 0; i < 200 && !p.GaveUp; i++, now = now.AddSeconds(2))
        {
            p.Next(false, true, now);
        }
        await Assert.That(p.GaveUp).IsTrue();
        p.Reset();
        await Assert.That(p.GaveUp).IsFalse();
        await Assert.That(p.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task Fingerprint_IgnoresTunnelsLoopbackLinkLocalAndRotatingV6()
    {
        var a = TermitusNetwork.Fingerprint(
        [
            ("en0", IPAddress.Parse("192.168.1.5")),
            ("en0", IPAddress.Parse("2a02:6b8::1234")),
            ("lo0", IPAddress.Loopback),
            ("utun4", IPAddress.Parse("172.18.0.1")),
            ("awdl0", IPAddress.Parse("fe80::1")),
            ("en5", IPAddress.Parse("169.254.10.10")),
        ]);
        var b = TermitusNetwork.Fingerprint(
        [
            ("utun7", IPAddress.Parse("172.18.0.1")),
            ("en0", IPAddress.Parse("2a02:6b8::abcd")), // privacy address rotated
            ("en0", IPAddress.Parse("192.168.1.5")),
        ]);
        await Assert.That(a).IsEqualTo("en0=192.168.1.5|v6");
        await Assert.That(b).IsEqualTo(a);
        await Assert.That(TermitusNetwork.IsUp(a)).IsTrue();
        await Assert.That(TermitusNetwork.Fingerprint([("utun3", IPAddress.Parse("10.0.0.1"))])).IsEqualTo(string.Empty);
        await Assert.That(TermitusNetwork.IsUp(string.Empty)).IsFalse();
    }

    [Test]
    public async Task Settings_ParseIgnoresJunk()
    {
        var map = TermitusSettings.Parse(["# comment", "", "kill_switch=1", "=x", "junk", " a = b "]);
        await Assert.That(map["kill_switch"]).IsEqualTo("1");
        await Assert.That(map["a"]).IsEqualTo("b");
        await Assert.That(map.Count).IsEqualTo(2);
    }
}
