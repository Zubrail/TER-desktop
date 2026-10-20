using System.Runtime.InteropServices;

namespace ServiceLib.Services;

/// <summary>
/// 0.6.4 (Mac): runs the TUN core as root through the macOS authorization dialog (Touch ID or
/// password -- the OS asks, TER never sees a password). See <see cref="TermitusMacShell"/> for the
/// command. ONE authorization per TER run: the root supervisor stays, a reload / reconnect / country switch only
/// drops a flag file next to it. Hooked in from upstream's CoreAdminManager (patch in apply-changes.sh); Linux keeps
/// upstream's sudo path.
/// </summary>
public static class TermitusMacCore
{
    private const string Tag = "TermitusMacCore";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static volatile bool _cancelled;
    private static string? _supSignature; // command line of the supervisor we started in this run

    public static string PidFile => Utils.GetTempPath("ter-core.pid");
    public static string SupFile => Utils.GetTempPath("ter-sup.pid");
    public static string StopFile => Utils.GetTempPath("ter-core.stop");
    public static string HaltFile => Utils.GetTempPath("ter-core.halt");
    public static string ReloadFile => Utils.GetTempPath("ter-core.reload");
    /// <summary>Per TER run (by pid): root creates it exclusively and keeps it open, so an old file never blocks it.</summary>
    public static string LogFile => Utils.GetLogPath($"ter-core-{Environment.ProcessId}.log");

    /// <summary>True once if the user closed the system dialog since the last call (then TER falls back to «только браузеры»).</summary>
    public static bool ConsumeCancelled()
    {
        var c = _cancelled;
        _cancelled = false;
        return c;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int LibcKill(int pid, int sig);

    /// <summary>kill(pid, 0): alive (also when it belongs to root -> EPERM) or gone.</summary>
    public static bool PidAlive(int pid)
    {
        if (pid <= 1 || !Utils.IsNonWindows())
        {
            return false;
        }
        try
        {
            if (LibcKill(pid, 0) == 0)
            {
                return true;
            }
            return Marshal.GetLastWin32Error() == 1; // EPERM = exists, not ours
        }
        catch
        {
            return false;
        }
    }

    public static int ReadPid()
    {
        try
        {
            return int.TryParse(File.ReadAllText(PidFile).Trim(), out var p) ? p : -1;
        }
        catch
        {
            return -1;
        }
    }

    public static int ReadSupPid()
    {
        try
        {
            return int.TryParse(File.ReadAllText(SupFile).Trim(), out var p) ? p : -1;
        }
        catch
        {
            return -1;
        }
    }

    public static bool SupervisorAlive()
    {
        var p = ReadSupPid();
        return p > 1 && PidAlive(p);
    }

    public static bool CoreRunning()
    {
        var pid = ReadPid();
        return pid > 1 && PidAlive(pid);
    }

    /// <summary>Startup: an orphan from an older version may still be around. Only log -- the supervisor prevents new ones.</summary>
    public static void LogStaleOnStartup()
    {
        try
        {
            foreach (var f in Directory.GetFiles(Utils.GetLogPath(), "ter-core-*.log"))
            {
                if (f != LogFile)
                {
                    TryDelete(f);
                }
            }
            if (Utils.IsMacOS() && File.Exists(SupFile) && !SupervisorAlive())
            {
                File.Delete(SupFile);
            }
            if (Utils.IsMacOS() && File.Exists(PidFile))
            {
                var pid = ReadPid();
                Logging.SaveLog(pid > 1 && PidAlive(pid)
                    ? $"{Tag}: an earlier core (pid {pid}) is still running"
                    : $"{Tag}: stale pid file removed");
                if (!(pid > 1 && PidAlive(pid)))
                {
                    File.Delete(PidFile);
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }

    public static async Task<ProcessService?> StartAsync(string coreExe, CoreInfo coreInfo, string configPath, Func<bool, string, Task>? updateFunc)
    {
        await Gate.WaitAsync();
        try
        {
            _cancelled = false;
            // D-1: the pin is the hash embedded in the app; a core in the user's data folder that differs is refused before any dialog.
            var pin = TermitusCoreHashes.ExpectedFor(coreExe);
            if (!TermitusCoreHashes.Verify(coreExe, pin))
            {
                Logging.SaveLog($"{Tag}: the core on disk does not match the checksum built into TER; not starting it as administrator");
                throw new Exception(ResUI.FailedToRunCore);
            }
            var supervisor = TermitusMacShell.BuildSupervisor(
                Environment.ProcessId, Utils.GetBinConfigPath(), coreExe, coreInfo.Arguments, Utils.GetBinConfigPath(configPath),
                coreInfo.Environment, LogFile, PidFile, SupFile, StopFile, HaltFile, ReloadFile, OtherCoreExes(), pin);
            var script = supervisor == null ? null : TermitusMacShell.BuildAppleScript(supervisor, TermitusLocale.Current.TunMacPrompt);
            if (supervisor == null || script == null)
            {
                Logging.SaveLog($"{Tag}: refused an unsafe path or argument");
                throw new Exception(ResUI.FailedToRunCore);
            }

            long logStart = 0;
            try
            {
                // reused supervisor: its log already exists, the tail starts after what is there; first start: the file is created by root
                logStart = File.Exists(LogFile) ? new FileInfo(LogFile).Length : 0;
            }
            catch (Exception ex)
            {
                Logging.SaveLog(Tag, ex);
            }

            var alive = SupervisorAlive();
            var mode = TermitusMacShell.ChooseStart(alive, _supSignature, supervisor);
            if (mode == TermitusMacShell.StartMode.Reuse)
            {
                // same supervisor, same command line: no dialog, just ask it to (re)start the core
                var oldPid = ReadPid();
                TryDelete(HaltFile);
                File.WriteAllText(ReloadFile, "reload");
                var wait = DateTime.UtcNow.AddSeconds(12);
                while (DateTime.UtcNow < wait && !TermitusMacShell.NewCoreReady(oldPid, ReadPid(), CoreRunning()))
                {
                    await Task.Delay(100);
                }
                if (!CoreRunning() || ReadPid() == oldPid)
                {
                    Logging.SaveLog($"{Tag}: the supervisor did not restart the core");
                    throw new Exception(ResUI.FailedToRunCore);
                }
            }
            else
            {
                if (alive)
                {
                    await ShutdownCoreAsync(); // a supervisor with another command line: end it, authorize anew
                }
                _supSignature = null;
                TryDelete(StopFile);
                TryDelete(HaltFile);
                TryDelete(ReloadFile);
                TryDelete(PidFile);
                TryDelete(LogFile);
                logStart = 0;
                var verdict = await RunOsascriptAsync(script);
                if (verdict == TermitusMacShell.AuthResult.Cancelled)
                {
                    _cancelled = true;
                    Logging.SaveLog($"{Tag}: the user closed the authorization dialog");
                    throw new OperationCanceledException(ResUI.FailedToRunCore);
                }
                if (verdict != TermitusMacShell.AuthResult.Ok)
                {
                    throw new Exception(ResUI.FailedToRunCore);
                }
                _supSignature = supervisor;
                var until = DateTime.UtcNow.AddSeconds(15); // leftover cores are cleaned first (~1-2 s)
                while (DateTime.UtcNow < until && !CoreRunning())
                {
                    await Task.Delay(100);
                }
                if (!CoreRunning())
                {
                    Logging.SaveLog($"{Tag}: the core did not start (no pid file)");
                    throw new Exception(ResUI.FailedToRunCore);
                }
            }
            await Task.Delay(300); // a config error makes the core exit right away
            if (!CoreRunning())
            {
                throw new Exception(ResUI.FailedToRunCore);
            }
            return new TermitusMacProcess(ReadPid(), logStart, updateFunc);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<TermitusMacShell.AuthResult> RunOsascriptAsync(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi);
            if (p == null)
            {
                return TermitusMacShell.AuthResult.Failed;
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)); // the user may take a while
            var errTask = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch { /* already gone */ }
                return TermitusMacShell.AuthResult.Failed;
            }
            var err = await errTask;
            var r = TermitusMacShell.Classify(p.ExitCode, err);
            if (r != TermitusMacShell.AuthResult.Ok && r != TermitusMacShell.AuthResult.Cancelled)
            {
                Logging.SaveLog($"{Tag}: osascript exit {p.ExitCode}"); // not stderr: it may echo the command
            }
            return r;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
            return TermitusMacShell.AuthResult.Failed;
        }
    }

    /// <summary>Other cores of this app's bin dir; a leftover one (older version) is stopped by the supervisor, by full path.</summary>
    private static List<string> OtherCoreExes()
    {
        var list = new List<string>();
        try
        {
            foreach (var t in new[] { ECoreType.sing_box, ECoreType.Xray })
            {
                var f = CoreInfoManager.Instance.GetCoreExecFile(CoreInfoManager.Instance.GetCoreInfo(t), out _);
                if (!f.IsNullOrEmpty() && File.Exists(f))
                {
                    list.Add(f);
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
        return list;
    }

    /// <summary>Disconnect / before a restart: the core stops, the supervisor stays idle (no new dialog on reconnect). Waits up to 5 s.</summary>
    public static async Task StopAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (!SupervisorAlive() || !File.Exists(PidFile))
            {
                return; // nothing running to stop (a repeated stop after CoreStop lands here)
            }
            var pid = ReadPid();
            File.WriteAllText(HaltFile, "halt");
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && (File.Exists(PidFile) || (pid > 1 && PidAlive(pid))))
            {
                await Task.Delay(100);
            }
            if (pid > 1 && PidAlive(pid))
            {
                Logging.SaveLog($"{Tag}: core (pid {pid}) still alive 5 s after the stop request");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
        finally
        {
            TryDelete(HaltFile);
            Gate.Release();
        }
    }

    /// <summary>Before the updater swaps the app (and any time TER wants no root process left): core AND supervisor end.</summary>
    public static async Task ShutdownAsync()
    {
        await Gate.WaitAsync();
        try
        {
            await ShutdownCoreAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task ShutdownCoreAsync()
    {
        try
        {
            if (!SupervisorAlive() && !File.Exists(PidFile))
            {
                return;
            }
            File.WriteAllText(StopFile, "stop");
            var pid = ReadPid();
            var sup = ReadSupPid();
            var until = DateTime.UtcNow.AddSeconds(7);
            while (DateTime.UtcNow < until
                   && ((sup > 1 && PidAlive(sup)) || File.Exists(PidFile) || (pid > 1 && PidAlive(pid))))
            {
                await Task.Delay(100);
            }
            if ((pid > 1 && PidAlive(pid)) || (sup > 1 && PidAlive(sup)))
            {
                Logging.SaveLog($"{Tag}: core or supervisor still alive 7 s after the shutdown request");
            }
            else
            {
                TryDelete(PidFile);
                TryDelete(SupFile);
            }
            _supSignature = null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
        finally
        {
            TryDelete(StopFile);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}

/// <summary>
/// What CoreManager holds instead of a child process: HasExited / Id follow the root core's pid, the
/// core's log file is tailed into the same log window as every other core.
/// </summary>
public sealed class TermitusMacProcess : ProcessService
{
    private readonly int _pid;
    private readonly CancellationTokenSource _cts = new();

    public TermitusMacProcess(int pid, long logOffset, Func<bool, string, Task>? updateFunc)
    {
        _pid = pid;
        _ = Task.Run(() => TailAsync(logOffset, updateFunc, _cts.Token));
    }

    public override int Id => _pid;
    public override bool HasExited => !File.Exists(TermitusMacCore.PidFile) || !TermitusMacCore.PidAlive(_pid);

    public override async Task StopAsync()
    {
        await TermitusMacCore.StopAsync();
        _cts.Cancel();
    }

    public override void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task TailAsync(long offset, Func<bool, string, Task>? updateFunc, CancellationToken ct)
    {
        try
        {
            var carry = string.Empty;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var fs = new FileStream(TermitusMacCore.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length < offset)
                    {
                        offset = 0;
                    }
                    if (fs.Length > offset)
                    {
                        fs.Seek(offset, SeekOrigin.Begin);
                        var buf = new byte[(int)Math.Min(fs.Length - offset, 64 * 1024)];
                        var n = await fs.ReadAsync(buf, ct);
                        offset += n;
                        carry += Encoding.UTF8.GetString(buf, 0, n);
                        int nl;
                        while ((nl = carry.IndexOf('\n')) >= 0)
                        {
                            var line = carry[..nl].TrimEnd('\r');
                            carry = carry[(nl + 1)..];
                            if (line.Length > 0 && updateFunc != null)
                            {
                                await updateFunc(false, line + Environment.NewLine);
                            }
                        }
                        continue;
                    }
                }
                catch (IOException)
                {
                    // log not there yet
                }
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TermitusMacProcess", ex);
        }
    }
}
