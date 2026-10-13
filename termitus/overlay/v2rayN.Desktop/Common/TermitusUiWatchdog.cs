using System.Diagnostics;
using ServiceLib.Services;

namespace v2rayN.Desktop.Common;

/// <summary>
/// TER_PERF=1 only: "is the UI thread blocked, and by what?" (0.4.1 freeze hunt).
/// A background thread posts a no-op to the UI dispatcher every 50 ms (Send priority:
/// it runs as soon as the current UI work item ends) and waits for it. A ping that comes
/// back more than 100 ms late is logged as
///   <c>TER perf: ui-blocked 640.2 ms | jit 410.3ms/2210 methods, gc 2.1ms (...) | marks: +3ms nav:support, ...</c>
/// i.e. how long the window could not react, how much of that the JIT and the GC took
/// (process-wide during the stall), and our breadcrumbs (TermitusPerf.Mark) inside it.
/// </summary>
internal static class TermitusUiWatchdog
{
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(50);
    private const double ReportOverMs = 100;
    private static int _started;

    public static void StartOnce()
    {
        if (!TermitusPerf.Enabled || Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }
        var t = new Thread(Loop) { IsBackground = true, Name = "TER UI watchdog", Priority = ThreadPriority.AboveNormal };
        t.Start();
        Logging.SaveLog("TER perf: ui watchdog on (ping every 50 ms, report > 100 ms)");
    }

    private static void Loop()
    {
        using var done = new ManualResetEventSlim(false);
        while (true)
        {
            Thread.Sleep(Every);
            done.Reset();
            var sent = Stopwatch.GetTimestamp();
            var before = TermitusPerf.Snapshot.Take();
            try
            {
                Dispatcher.UIThread.Post(() => done.Set(), DispatcherPriority.Send);
            }
            catch
            {
                return; // dispatcher gone: the app is closing
            }
            if (done.Wait(TimeSpan.FromMilliseconds(ReportOverMs)))
            {
                continue;
            }
            // Late: wait for it (up to 30 s) and report the whole stall.
            done.Wait(TimeSpan.FromSeconds(30));
            var ms = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
            var diff = TermitusPerf.Snapshot.Take().Diff(before);
            Logging.SaveLog($"TER perf: ui-blocked {TermitusPerf.Fmt(ms)} ms | {diff} | marks: {TermitusPerf.MarksSince(sent - Stopwatch.Frequency / 2)}");
        }
    }
}
