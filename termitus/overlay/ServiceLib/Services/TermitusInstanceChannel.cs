using System.IO.Pipes;

namespace ServiceLib.Services;

/// <summary>
/// "Second launch shows the running window" for macOS/Linux.
///
/// Upstream (non-Windows) holds a named Mutex and a second launch simply exits,
/// so a stale hidden instance (e.g. one started from the mounted dmg) made TER
/// "not open at all". On Windows upstream already signals the first instance
/// through a named EventWaitHandle; named events do not exist on Unix in .NET,
/// so here the first instance listens on a local named pipe (a Unix domain
/// socket in the per-user temp dir) and the second one sends "show".
///
/// The pipe is created with PipeOptions.CurrentUserOnly: only processes of the
/// same OS user can connect, and the only accepted command is "show", so it is
/// not a remote-control surface.
/// </summary>
public static class TermitusInstanceChannel
{
    public const string ShowCommand = "show";
    private const int MaxCommandLength = 32;

    /// <summary>Short (Unix socket paths are limited to ~104 bytes) and per user.</summary>
    public static string DefaultPipeName
    {
        get
        {
            var user = Environment.UserName ?? string.Empty;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..8].ToLowerInvariant();
            return TermitusTestHooks.Instance == null ? $"ter-ui-{hash}" : $"ter-ui-{hash}-t{TermitusTestHooks.Instance}";
        }
    }

    /// <summary>Asks a running instance to show its window. False when nobody answered.</summary>
    public static bool TrySignalExisting(string? pipeName = null, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName, PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(ShowCommand);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts listening in the background; <paramref name="onShow"/> runs on a
    /// thread-pool thread for every "show" received. Dispose to stop.
    /// Must be called only by the instance that owns the single-instance mutex.
    /// </summary>
    public static IDisposable StartListener(Action onShow, string? pipeName = null)
    {
        var name = pipeName ?? DefaultPipeName;
        var cts = new CancellationTokenSource();
        RemoveStaleSocket(name);
        _ = Task.Run(() => ListenLoop(name, onShow, cts.Token));
        return cts;
    }

    private static async Task ListenLoop(string name, Action onShow, CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                failures = 0;

                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(TimeSpan.FromSeconds(2));
                var buf = new byte[MaxCommandLength];
                var n = await server.ReadAsync(buf, readCts.Token);
                var cmd = Encoding.UTF8.GetString(buf, 0, Math.Max(0, n)).Trim();
                if (cmd == ShowCommand)
                {
                    onShow();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logging.SaveLog("TermitusInstanceChannel", ex);
                // Never spin: back off, and give up after repeated failures.
                if (++failures >= 5)
                {
                    return;
                }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(failures), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// A crashed previous instance can leave its socket file behind, which would
    /// make bind() fail. Only the mutex owner calls this, so the file cannot
    /// belong to a live instance.
    /// </summary>
    private static void RemoveStaleSocket(string name)
    {
        if (Utils.IsWindows())
        {
            return;
        }
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{name}");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
