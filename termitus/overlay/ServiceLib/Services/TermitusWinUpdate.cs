using System.Diagnostics;
using System.IO.Compression;

namespace ServiceLib.Services;

/// <summary>Arguments of the update helper: <c>--ter-apply-update "&lt;appDir&gt;" &lt;oldPid&gt;</c>.</summary>
public sealed record TermitusApplyArgs(string AppDir, int OldPid);

public enum TermitusApplyResult
{
    Ok,
    /// <summary>The old TER did not exit in time; nothing was touched.</summary>
    OldStillRunning,
    /// <summary>The app folder could not be renamed (locked); nothing changed, the old TER was started again.</summary>
    RenameFailed,
    /// <summary>Copying the new files failed; the old folder was put back and started.</summary>
    RolledBack,
    /// <summary>Copy failed AND the old folder could not be put back (needs a manual fix).</summary>
    RollbackFailed,
    BadSource,
    BadArguments,
    /// <summary>Unexpected error before anything was changed.</summary>
    Failed,
}

public enum TermitusApplyStepKind { Rename, Copy, Delete, Start }

public sealed record TermitusApplyStep(TermitusApplyStepKind Kind, string From, string? To = null);

/// <summary>What the helper will do, as plain data (so it is unit-tested without touching a disk).</summary>
public sealed record TermitusApplyPlan(string OldDir, IReadOnlyList<TermitusApplyStep> Forward, IReadOnlyList<TermitusApplyStep> Rollback, TermitusApplyStep StartOld);

/// <summary>The few file/process operations the helper needs; the real one is <see cref="TermitusWinApplyFs"/>.</summary>
public interface ITermitusApplyFs
{
    /// <summary>True when the process is gone (or never existed) within the timeout.</summary>
    bool WaitForExit(int pid, TimeSpan timeout);

    bool Exists(string path);

    /// <summary>Number of files under a folder (to check that a copy is complete).</summary>
    int CountFiles(string dir);

    void Rename(string from, string to);

    void CopyTree(string from, string to);

    void DeleteTree(string path);

    void Start(string exe);

    void Sleep(TimeSpan t);
}

/// <summary>
/// 0.6.5 Windows auto-update (mirrors the macOS flow). A running .exe cannot be replaced, so after the user presses
/// «Обновить» TER: verifies the zip against the SIGNED sha256 (TermitusUpdater), unpacks it to
/// %LOCALAPPDATA%\TeRMiTuS\update\&lt;version&gt;\ (zip-slip safe), stops the core, clears the system proxy and starts the NEW
/// TER.exe from there with <c>--ter-apply-update "appDir" oldPid</c>. That helper mode (Program.Main, before the mutex
/// and any UI) waits for the old process, renames appDir to appDir.old-&lt;version&gt;, copies its own folder into appDir,
/// starts appDir\TER.exe; on any failure it puts the old folder back and starts it. Leftovers are removed at a later normal start.
/// Pure helpers + <see cref="Run"/> over <see cref="ITermitusApplyFs"/> are unit-tested; <see cref="TermitusWinApplyFs"/> is the thin glue.
/// </summary>
public static class TermitusWinUpdate
{
    private const string _tag = "TermitusWinUpdate";
    public const string ApplyFlag = "--ter-apply-update";
    public const string ExeName = "TER.exe";
    public const int MaxEntries = 20000;
    public const long MaxTotalBytes = 1536L * 1024 * 1024;
    public static readonly TimeSpan WaitOldExit = TimeSpan.FromSeconds(60);
    public const int Retries = 10;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    #region Pure helpers

    /// <summary>Zip-slip guard on the entry NAME: no absolute path, no "..", no drive letter / stream colon, no NUL.</summary>
    public static bool IsSafeEntryName(string? name)
    {
        if (name.IsNullOrEmpty() || name.Contains('\0') || name.Contains(':'))
        {
            return false;
        }
        var n = name.Replace('\\', '/');
        if (n.StartsWith('/'))
        {
            return false;
        }
        foreach (var seg in n.Split('/'))
        {
            // Windows drops trailing dots/spaces, so ".. " or "..." would mean "..".
            if (seg != "." && seg.Length > 0 && seg.Trim(' ', '.').Length == 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Full target path of an entry under <paramref name="root"/>, or null if the name is unsafe / escapes the root.</summary>
    public static string? SafeTarget(string root, string entryName)
    {
        if (!IsSafeEntryName(entryName))
        {
            return null;
        }
        var rootFull = Path.GetFullPath(root).TrimEnd('/', '\\');
        var full = Path.GetFullPath(Path.Combine(rootFull, entryName.Replace('\\', '/')));
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>"" when TER.exe is at the zip root, "TER/" when it sits in the single top folder, null when it is not there.</summary>
    public static string? FindAppRootPrefix(IEnumerable<string> entryNames)
    {
        string? found = null;
        foreach (var raw in entryNames)
        {
            var n = raw.Replace('\\', '/');
            var parts = n.Split('/');
            if (!string.Equals(parts[^1], ExeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (parts.Length == 1)
            {
                return string.Empty;
            }
            if (parts.Length == 2)
            {
                found ??= parts[0] + "/";
            }
        }
        return found;
    }

    /// <summary>Digits/letters/dots/dashes only, so a version can name a folder.</summary>
    public static string SanitizeVersion(string? v)
    {
        // "0.6.5+d9d008b..." (informational version with a commit hash) -> "0.6.5"; also cut at whitespace.
        v = (v ?? string.Empty).Trim();
        var cut = v.IndexOfAny(['+', ' ', '\t']);
        if (cut >= 0)
        {
            v = v[..cut];
        }
        var s = new string(v.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-').ToArray());
        return s.Length is > 0 and <= 32 ? s : "prev";
    }

    public static bool IsInside(string root, string path)
    {
        var r = Path.GetFullPath(root).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsApplyMode(string[]? args) => args is { Length: > 0 } && args[0] == ApplyFlag;

    /// <summary><c>--ter-apply-update "appDir" oldPid</c>; null if malformed (relative path, bad pid, wrong count).</summary>
    public static TermitusApplyArgs? ParseApplyArgs(string[]? args)
    {
        if (args is not { Length: 3 } || args[0] != ApplyFlag || args[1].IsNullOrEmpty() || args[1].Contains('\0'))
        {
            return null;
        }
        if (!Path.IsPathRooted(args[1]))
        {
            return null;
        }
        if (!int.TryParse(args[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return null;
        }
        return new TermitusApplyArgs(Path.GetFullPath(args[1]).TrimEnd('/', '\\'), pid);
    }

    /// <summary>
    /// The helper may only run from inside the update folder and only touch a different folder that really is a TER install
    /// (checked by the executor); this keeps "TER.exe --ter-apply-update X" from being a way to rename arbitrary folders.
    /// </summary>
    public static bool IsAllowedApply(TermitusApplyArgs a, string ownDir, string updateRoot)
        => IsInside(updateRoot, ownDir)
           && !IsInside(updateRoot, a.AppDir)
           && !string.Equals(Path.GetFullPath(ownDir).TrimEnd('/', '\\'), a.AppDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>The plan: rename appDir -> appDir.old-&lt;ver&gt;, copy the new folder in, start it; and how to undo it.</summary>
    public static TermitusApplyPlan BuildPlan(string appDir, string sourceDir, string oldVersion, Func<string, bool> exists)
    {
        var baseName = appDir + ".old-" + SanitizeVersion(oldVersion);
        var old = baseName;
        for (var i = 2; exists(old) && i < 50; i++)
        {
            old = baseName + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var newExe = Path.Combine(appDir, ExeName);
        var oldExe = Path.Combine(appDir, ExeName);
        return new TermitusApplyPlan(
            old,
            [
                new(TermitusApplyStepKind.Rename, appDir, old),
                new(TermitusApplyStepKind.Copy, sourceDir, appDir),
                new(TermitusApplyStepKind.Start, newExe),
            ],
            [
                new(TermitusApplyStepKind.Delete, appDir),
                new(TermitusApplyStepKind.Rename, old, appDir),
                new(TermitusApplyStepKind.Start, oldExe),
            ],
            new TermitusApplyStep(TermitusApplyStepKind.Start, oldExe));
    }

    /// <summary>True for "&lt;appDirName&gt;.old-*" siblings (leftovers of earlier updates).</summary>
    public static bool IsOldDirName(string appDirName, string candidate)
        => candidate.StartsWith(appDirName + ".old-", StringComparison.OrdinalIgnoreCase);

    #endregion Pure helpers

    #region Executor (pure sequencing over ITermitusApplyFs)

    private static bool Retry(ITermitusApplyFs fs, Action action)
    {
        for (var i = 0; i < Retries; i++)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception)
            {
                fs.Sleep(RetryDelay);
            }
        }
        return false;
    }

    private static void TryStart(ITermitusApplyFs fs, TermitusApplyStep step)
    {
        try
        {
            fs.Start(step.From);
        }
        catch (Exception)
        {
            // nothing more can be done
        }
    }

    private static TermitusApplyResult Rollback(ITermitusApplyFs fs, TermitusApplyPlan plan)
    {
        var del = plan.Rollback[0];
        var ren = plan.Rollback[1];
        var ok = (!fs.Exists(del.From) || Retry(fs, () => fs.DeleteTree(del.From))) && Retry(fs, () => fs.Rename(ren.From, ren.To!));
        if (!ok)
        {
            return TermitusApplyResult.RollbackFailed;
        }
        TryStart(fs, plan.StartOld);
        return TermitusApplyResult.RolledBack;
    }

    /// <summary>The whole helper job. <paramref name="sourceDir"/> = the folder that holds the new TER.exe (our own folder).</summary>
    public static TermitusApplyResult Run(ITermitusApplyFs fs, TermitusApplyArgs a, string sourceDir, string oldVersion)
    {
        if (!fs.WaitForExit(a.OldPid, WaitOldExit))
        {
            // Nothing touched. The old TER is still there (or hung) -- never leave the user without any TER:
            // start it; the single-instance channel just brings the running one forward.
            TryStart(fs, new TermitusApplyStep(TermitusApplyStepKind.Start, Path.Combine(a.AppDir, ExeName)));
            return TermitusApplyResult.OldStillRunning;
        }
        if (!fs.Exists(Path.Combine(sourceDir, ExeName)) || !fs.Exists(Path.Combine(a.AppDir, ExeName)))
        {
            return TermitusApplyResult.BadSource;
        }
        var plan = BuildPlan(a.AppDir, sourceDir, oldVersion, fs.Exists);
        var rename = plan.Forward[0];
        var copy = plan.Forward[1];
        var start = plan.Forward[2];

        if (!Retry(fs, () => fs.Rename(rename.From, rename.To!)))
        {
            TryStart(fs, plan.StartOld); // nothing changed: the old TER simply starts again
            return TermitusApplyResult.RenameFailed;
        }
        try
        {
            fs.CopyTree(copy.From, copy.To!);
            if (!fs.Exists(start.From))
            {
                throw new IOException("new exe missing after copy");
            }
            if (fs.CountFiles(copy.To!) != fs.CountFiles(copy.From))
            {
                throw new IOException("copy incomplete");
            }
            fs.Start(start.From);
        }
        catch (Exception)
        {
            // ANY failure after the rename must put the old folder back.
            return Rollback(fs, plan);
        }
        return TermitusApplyResult.Ok;
    }

    #endregion Executor

    #region Windows glue

    private static int _updateInProgress;

    /// <summary>Set from the moment staging starts; once the helper is launched it is never cleared in this process.</summary>
    public static bool UpdateInProgress => Volatile.Read(ref _updateInProgress) == 1;

    public static void MarkUpdateStarted() => Volatile.Write(ref _updateInProgress, 1);

    /// <summary>Staging or helper start failed: this process may clean up again.</summary>
    public static void MarkUpdateAborted() => Volatile.Write(ref _updateInProgress, 0);

    public static readonly TimeSpan StaleUpdateDirAge = TimeSpan.FromMinutes(10);

    /// <summary>An update\&lt;ver&gt; folder may be deleted only when it has not been touched for 10 minutes.</summary>
    public static bool IsStaleUpdateDir(DateTime lastWriteUtc, DateTime nowUtc) => nowUtc - lastWriteUtc > StaleUpdateDirAge;

    public static string UpdateRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeRMiTuS", "update");

    private static string ResultFile() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeRMiTuS", "update-result.txt");

    /// <summary>The folder of the running TER.exe, or null when this is not an installed TER.exe (dev build, other name).</summary>
    public static string? CurrentAppDir()
    {
        var exe = Environment.ProcessPath;
        if (exe.IsNullOrEmpty() || !string.Equals(Path.GetFileName(exe), ExeName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return Path.GetDirectoryName(exe);
    }

    /// <summary>
    /// Unpacks the (already sha256-verified) zip to update\&lt;version&gt;\ and returns the folder holding TER.exe, or null.
    /// Every entry goes through the zip-slip guard; entry count and unpacked size are capped.
    /// </summary>
    public static string? StageFromZip(string zipPath, string version, string updateRoot, string? expectedSha256 = null)
    {
        MarkUpdateStarted();
        try
        {
            var dest = Path.Combine(updateRoot, SanitizeVersion(version));
            if (Directory.Exists(dest))
            {
                Directory.Delete(dest, true);
            }
            TermitusUpdater.PrivateDir(updateRoot);
            Directory.CreateDirectory(dest);
            // D-7: verify the file we actually unpack: ONE handle (FileShare.Read: nobody can write it meanwhile), hashed first,
            // then the archive is read from that very handle.
            using var zipStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (expectedSha256 != null)
            {
                var got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zipStream));
                if (!string.Equals(got, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Logging.SaveLog($"{_tag}: the update archive changed after it was verified; refusing to unpack it");
                    return null;
                }
                zipStream.Position = 0;
            }
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries)
            {
                return null;
            }
            var prefix = FindAppRootPrefix(zip.Entries.Select(e => e.FullName));
            if (prefix == null)
            {
                Logging.SaveLog($"{_tag}: no {ExeName} in the update archive");
                return null;
            }
            long total = 0;
            foreach (var e in zip.Entries)
            {
                var target = SafeTarget(dest, e.FullName);
                if (target == null)
                {
                    Logging.SaveLog($"{_tag}: unsafe path in the update archive, aborted");
                    return null;
                }
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var src = e.Open();
                using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                var buf = new byte[81920];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > MaxTotalBytes)
                    {
                        Logging.SaveLog($"{_tag}: update archive too big when unpacked");
                        return null;
                    }
                    dst.Write(buf, 0, n);
                }
            }
            var appRoot = prefix.Length == 0 ? dest : Path.Combine(dest, prefix.TrimEnd('/'));
            return File.Exists(Path.Combine(appRoot, ExeName)) ? appRoot : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Logging.SaveLog($"{_tag}: unpack failed ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>Starts the NEW TER.exe in helper mode. The caller must quit TER right after.</summary>
    public static bool StartApplyHelper(string newAppDir, string appDir)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(newAppDir, ExeName)) { UseShellExecute = false, WorkingDirectory = newAppDir };
            psi.ArgumentList.Add(ApplyFlag);
            psi.ArgumentList.Add(appDir);
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Logging.SaveLog($"{_tag}: helper start failed ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>
    /// Program.Main, first thing: runs the helper job when started with <see cref="ApplyFlag"/> and returns true
    /// (the caller then just returns). No UI, no mutex, no logging infrastructure; only a one-word result file.
    /// </summary>
    public static bool TryRunFromArgs(string[] args)
    {
        if (!OperatingSystem.IsWindows() || !IsApplyMode(args))
        {
            return false;
        }
        var result = TermitusApplyResult.BadArguments;
        try
        {
            result = TermitusApplyResult.Failed; // flips to the real result below when the arguments are fine
            var parsed = ParseApplyArgs(args);
            var own = Path.GetDirectoryName(Environment.ProcessPath);
            if (parsed == null || own == null || !IsAllowedApply(parsed, own, UpdateRoot()))
            {
                result = TermitusApplyResult.BadArguments;
            }
            else
            {
                string oldVersion;
                try
                {
                    oldVersion = FileVersionInfo.GetVersionInfo(Path.Combine(parsed.AppDir, ExeName)).ProductVersion ?? "prev";
                }
                catch (Exception ex) when (ex is IOException or ArgumentException)
                {
                    oldVersion = "prev";
                }
                result = Run(new TermitusWinApplyFs(), parsed, own, oldVersion);
            }
        }
        catch (Exception)
        {
            result = TermitusApplyResult.Failed;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResultFile())!);
            File.WriteAllText(ResultFile(), result.ToString());
        }
        catch (Exception)
        {
            // best effort
        }
        return true;
    }

    /// <summary>Normal start: logs the helper's one-word result (once) and removes leftovers. Never throws, never blocks.</summary>
    public static void AfterStartHousekeeping()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (File.Exists(ResultFile()))
                {
                    Logging.SaveLog($"{_tag}: last update helper result = {File.ReadAllText(ResultFile()).Trim()}");
                    File.Delete(ResultFile());
                }
                await Task.Delay(TimeSpan.FromSeconds(30)); // the helper may still be exiting
                var appDir = CurrentAppDir();
                CleanupLeftovers(appDir, UpdateRoot(), Environment.ProcessPath);
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"{_tag}: housekeeping failed ({ex.GetType().Name})");
            }
        });
    }

    public static void CleanupLeftovers(string? appDir, string updateRoot, string? runningExe)
        => CleanupLeftovers(appDir, updateRoot, runningExe, DateTime.UtcNow, UpdateInProgress);

    public static void CleanupLeftovers(string? appDir, string updateRoot, string? runningExe, DateTime nowUtc, bool updateInProgress)
    {
        if (!appDir.IsNullOrEmpty())
        {
            var full = Path.GetFullPath(appDir).TrimEnd('/', '\\');
            var parent = Path.GetDirectoryName(full);
            var name = Path.GetFileName(full);
            if (parent != null && Directory.Exists(parent))
            {
                foreach (var d in Directory.EnumerateDirectories(parent).Where(d => IsOldDirName(name, Path.GetFileName(d))))
                {
                    TryDeleteTree(d);
                }
            }
        }
        if (updateInProgress || !Directory.Exists(updateRoot) || (!runningExe.IsNullOrEmpty() && IsInside(updateRoot, runningExe)))
        {
            return;
        }
        foreach (var d in Directory.EnumerateDirectories(updateRoot))
        {
            if (IsStaleUpdateDir(Directory.GetLastWriteTimeUtc(d), nowUtc))
            {
                TryDeleteTree(d);
            }
        }
        if (!Directory.EnumerateFileSystemEntries(updateRoot).Any())
        {
            TryDeleteTree(updateRoot);
        }
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // locked or in use: next start
        }
    }

    #endregion Windows glue
}

/// <summary>The real file system / process side of the helper.</summary>
public sealed class TermitusWinApplyFs : ITermitusApplyFs
{
    public bool WaitForExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // The old TER may already be gone and its pid reused by something else (review 19.10):
            // only a process that is really TER is waited for.
            if (!string.Equals(p.ProcessName, Path.GetFileNameWithoutExtension(TermitusWinUpdate.ExeName),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return p.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true; // no such process
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public int CountFiles(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();

    public void Rename(string from, string to) => Directory.Move(from, to);

    public void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), true);
        }
    }

    public void DeleteTree(string path) => Directory.Delete(path, true);

    public void Start(string exe)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        using var p = Process.Start(psi);
    }

    public void Sleep(TimeSpan t) => Thread.Sleep(t);
}
