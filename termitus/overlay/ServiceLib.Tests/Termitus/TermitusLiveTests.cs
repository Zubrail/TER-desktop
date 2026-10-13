using ServiceLib.Services;
using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

public class TermitusLiveTests
{
    [Test]
    public async Task FormatSpeed_Units()
    {
        await Assert.That(TermitusLive.FormatSpeed(0)).IsEqualTo("0 Б/с");
        await Assert.That(TermitusLive.FormatSpeed(850)).IsEqualTo("850 Б/с");
        await Assert.That(TermitusLive.FormatSpeed(2_500)).IsEqualTo("2,5 КБ/с");
        await Assert.That(TermitusLive.FormatSpeed(250_000)).IsEqualTo("250 КБ/с");
        await Assert.That(TermitusLive.FormatSpeed(1_200_000)).IsEqualTo("1,2 МБ/с");
        await Assert.That(TermitusLive.FormatSpeed(1_200_000, english: true)).IsEqualTo("1.2 MB/s");
        await Assert.That(TermitusLive.FormatSpeed(-5)).IsEqualTo("0 Б/с");
    }

    [Test]
    public async Task History_KeepsLast60_NewestAtTheEnd()
    {
        var h = new TermitusSpeedHistory();
        for (var i = 1; i <= 70; i++)
        {
            h.Add(i, i * 2);
        }
        var (d, u) = h.Snapshot();
        await Assert.That(d.Length).IsEqualTo(60);
        await Assert.That(d[^1]).IsEqualTo(70);
        await Assert.That(d[0]).IsEqualTo(11);
        await Assert.That(u[^1]).IsEqualTo(140);
        h.Clear();
        await Assert.That(h.Snapshot().Down.All(x => x == 0)).IsTrue();
    }

    [Test]
    public async Task GraphPaths_IdleIsFlat_AndInsideTheBox()
    {
        var zeros = new long[60];
        var (area, line) = TermitusLive.GraphPaths(zeros, zeros);
        await Assert.That(area.StartsWith("M0,44 L0,42")).IsTrue();
        await Assert.That(area.EndsWith("L360,44 Z")).IsTrue();
        await Assert.That(line.StartsWith("M0,42")).IsTrue();
        var busy = Enumerable.Range(0, 60).Select(i => i * 100_000L).ToArray();
        var (a2, _) = TermitusLive.GraphPaths(busy, zeros);
        // the highest sample sits near the top (y = 4), never above the box
        await Assert.That(a2.Contains("L360,4 ")).IsTrue();
    }

    [Test]
    public async Task PingClass_Thresholds()
    {
        await Assert.That(TermitusLive.PingClass(-1)).IsEqualTo(string.Empty);
        await Assert.That(TermitusLive.PingClass(80)).IsEqualTo("good");
        await Assert.That(TermitusLive.PingClass(200)).IsEqualTo("mid");
        await Assert.That(TermitusLive.PingClass(900)).IsEqualTo("bad");
    }
}
