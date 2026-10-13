using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>Second launch → "show" reaches the running instance over the local pipe.</summary>
public class TermitusInstanceChannelTests
{
    private static string UniqueName() => $"ter-t-{Guid.NewGuid():N}"[..20];

    [Test]
    public async Task Signal_ReachesListener()
    {
        var name = UniqueName();
        var shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = TermitusInstanceChannel.StartListener(() => shown.TrySetResult(true), name);

        var sent = false;
        for (var i = 0; i < 20 && !sent; i++) // the listener binds asynchronously
        {
            sent = TermitusInstanceChannel.TrySignalExisting(name, 250);
        }
        await Assert.That(sent).IsTrue();
        var done = await Task.WhenAny(shown.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(done == shown.Task).IsTrue();
    }

    [Test]
    public async Task Listener_KeepsServingAfterFirstSignal()
    {
        var name = UniqueName();
        var count = 0;
        using var listener = TermitusInstanceChannel.StartListener(() => Interlocked.Increment(ref count), name);
        for (var n = 0; n < 2; n++)
        {
            var sent = false;
            for (var i = 0; i < 20 && !sent; i++)
            {
                sent = TermitusInstanceChannel.TrySignalExisting(name, 250);
            }
            await Assert.That(sent).IsTrue();
            for (var i = 0; i < 50 && Volatile.Read(ref count) <= n; i++)
            {
                await Task.Delay(100);
            }
        }
        await Assert.That(Volatile.Read(ref count)).IsEqualTo(2);
    }

    [Test]
    public async Task NoInstance_SignalFailsFast()
    {
        var sw = Stopwatch.StartNew();
        await Assert.That(TermitusInstanceChannel.TrySignalExisting(UniqueName(), 300)).IsFalse();
        await Assert.That(sw.Elapsed < TimeSpan.FromSeconds(3)).IsTrue();
    }

    [Test]
    public async Task DefaultPipeName_IsShortEnoughForUnixSocketPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{TermitusInstanceChannel.DefaultPipeName}");
        await Assert.That(Encoding.UTF8.GetByteCount(path)).IsLessThan(104);
    }
}
