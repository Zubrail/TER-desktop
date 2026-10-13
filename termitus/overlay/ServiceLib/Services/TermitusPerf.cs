namespace ServiceLib.Services;

/// <summary>
/// Opt-in timings for the TER simple UI. Off by default; turn on with the environment
/// variable <c>TER_PERF=1</c> (<c>open -a TER</c> does NOT pass env on macOS; run
/// <c>TER_PERF=1 /Applications/TER.app/Contents/MacOS/v2rayN</c>).
/// Lines go to the normal log as <c>TER perf: &lt;name&gt; 3.2 ms</c>; names only, never data.
///
/// 0.4.1: besides scopes there are breadcrumbs (<see cref="Mark"/>): what the UI thread
/// was doing recently. The UI watchdog (v2rayN.Desktop/Common/TermitusUiWatchdog.cs) pings
/// the UI thread every 50 ms; when a ping is late by more than 100 ms it logs the delay
/// with the breadcrumbs of that window and how much of it was JIT compilation and GC
/// (<see cref="Snapshot"/>), which is what tells "our code" from "first-time code" apart.
/// </summary>
public static class TermitusPerf
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("TER_PERF") == "1";

    private const int MaxMarks = 64;
    private static readonly object _marksLock = new();
    private static readonly Queue<(long Ts, string What)> _marks = new();

    /// <summary>using var _ = TermitusPerf.Measure("render-account");</summary>
    public static Scope Measure(string name) => new(Enabled ? name : null);

    public static void Report(string name, double ms)
    {
        if (Enabled)
        {
            Logging.SaveLog($"TER perf: {name} {Fmt(ms)} ms");
        }
    }

    /// <summary>Breadcrumb: something happened (a navigation, a long UI post, a layout pass).</summary>
    public static void Mark(string what)
    {
        if (!Enabled)
        {
            return;
        }
        lock (_marksLock)
        {
            _marks.Enqueue((Stopwatch.GetTimestamp(), what));
            while (_marks.Count > MaxMarks)
            {
                _marks.Dequeue();
            }
        }
    }

    /// <summary>Breadcrumbs newer than <paramref name="sinceTs"/>, "+12ms nav:support" relative to it.</summary>
    public static string MarksSince(long sinceTs)
    {
        lock (_marksLock)
        {
            var parts = _marks
                .Where(m => m.Ts >= sinceTs)
                .Select(m => $"+{Fmt(Stopwatch.GetElapsedTime(sinceTs, m.Ts).TotalMilliseconds)}ms {m.What}")
                .ToList();
            return parts.Count == 0 ? "-" : string.Join(", ", parts);
        }
    }

    /// <summary>Process-wide JIT/GC counters; the difference of two snapshots explains a stall.</summary>
    public readonly record struct Snapshot(TimeSpan Jit, long JitMethods, TimeSpan GcPause, int Gen0, int Gen1, int Gen2, long Allocated)
    {
        public static Snapshot Take() => new(
            System.Runtime.JitInfo.GetCompilationTime(),
            System.Runtime.JitInfo.GetCompiledMethodCount(),
            GC.GetTotalPauseDuration(),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalAllocatedBytes());

        /// <summary>"jit 412ms/1830 methods, gc 3ms (g0+1 g1+0 g2+0), alloc 12MB"</summary>
        public string Diff(Snapshot before)
            => $"jit {Fmt((Jit - before.Jit).TotalMilliseconds)}ms/{JitMethods - before.JitMethods} methods, "
               + $"gc {Fmt((GcPause - before.GcPause).TotalMilliseconds)}ms (g0+{Gen0 - before.Gen0} g1+{Gen1 - before.Gen1} g2+{Gen2 - before.Gen2}), "
               + $"alloc {Fmt((Allocated - before.Allocated) / 1048576.0)}MB";
    }

    public static string Fmt(double v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly long _start;

        internal Scope(string? name)
        {
            _name = name;
            _start = name == null ? 0 : Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_name != null)
            {
                Report(_name, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
            }
        }
    }
}
