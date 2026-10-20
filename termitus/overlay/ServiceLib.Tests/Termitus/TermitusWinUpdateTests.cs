using System.IO.Compression;

using TUnit.Core;

using ServiceLib.Services;

namespace ServiceLib.Tests.Termitus;

/// <summary>0.6.5 Windows auto-update: zip-slip guard, argument parsing, rename/copy/rollback sequencing (fake file system).</summary>
public class TermitusWinUpdateTests
{
    [Test]
    [Arguments("TER/TER.exe", true)]
    [Arguments("TER/bin/xray/xray.exe", true)]
    [Arguments("TER\\bin\\x.dll", true)]
    [Arguments("TER/Папка с пробелом/файл.txt", true)]
    [Arguments("TER/", true)]
    [Arguments("../evil.exe", false)]
    [Arguments("TER/../../evil.exe", false)]
    [Arguments("TER\\..\\..\\evil.exe", false)]
    [Arguments("/etc/passwd", false)]
    [Arguments("\\Windows\\System32\\x.dll", false)]
    [Arguments("C:\\Windows\\x.dll", false)]
    [Arguments("C:/x", false)]
    [Arguments("TER/file.txt:stream", false)]
    [Arguments("TER/.. /x", false)]
    [Arguments("TER/.../x", false)]
    [Arguments("", false)]
    [Arguments("a\0b", false)]
    public async Task EntryNames_ZipSlipGuard(string name, bool safe)
    {
        await Assert.That(TermitusWinUpdate.IsSafeEntryName(name)).IsEqualTo(safe);
    }

    [Test]
    public async Task SafeTarget_StaysUnderRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ter-root");
        var ok = TermitusWinUpdate.SafeTarget(root, "TER/bin/a.dll");
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.StartsWith(Path.GetFullPath(root))).IsTrue();
        await Assert.That(TermitusWinUpdate.SafeTarget(root, "TER/../../x")).IsNull();
    }

    [Test]
    public async Task AppRoot_AtRootOrSingleTopFolder()
    {
        await Assert.That(TermitusWinUpdate.FindAppRootPrefix(["TER.exe", "bin/x"])).IsEqualTo("");
        await Assert.That(TermitusWinUpdate.FindAppRootPrefix(["TER/", "TER/TER.exe", "TER/bin/a"])).IsEqualTo("TER/");
        await Assert.That(TermitusWinUpdate.FindAppRootPrefix(["TER/bin/TER.exe"])).IsNull();
        await Assert.That(TermitusWinUpdate.FindAppRootPrefix(["readme.txt"])).IsNull();
    }

    [Test]
    public async Task ApplyArgs_Parse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Папка с пробелом", "TER");
        var a = TermitusWinUpdate.ParseApplyArgs(["--ter-apply-update", dir + Path.DirectorySeparatorChar, "1234"]);
        await Assert.That(a).IsNotNull();
        await Assert.That(a!.OldPid).IsEqualTo(1234);
        await Assert.That(a.AppDir).IsEqualTo(dir);
        await Assert.That(TermitusWinUpdate.IsApplyMode(["--ter-apply-update"])).IsTrue();
        await Assert.That(TermitusWinUpdate.IsApplyMode([])).IsFalse();
        await Assert.That(TermitusWinUpdate.IsApplyMode(null)).IsFalse();
        await Assert.That(TermitusWinUpdate.ParseApplyArgs(["--ter-apply-update", dir])).IsNull();
        await Assert.That(TermitusWinUpdate.ParseApplyArgs(["--ter-apply-update", dir, "0"])).IsNull();
        await Assert.That(TermitusWinUpdate.ParseApplyArgs(["--ter-apply-update", dir, "-5"])).IsNull();
        await Assert.That(TermitusWinUpdate.ParseApplyArgs(["--ter-apply-update", dir, "abc"])).IsNull();
        await Assert.That(TermitusWinUpdate.ParseApplyArgs(["--ter-apply-update", "relative/dir", "5"])).IsNull();
        await Assert.That(TermitusWinUpdate.ParseApplyArgs(["--x", dir, "5"])).IsNull();
        await Assert.That(TermitusWinUpdate.ParseApplyArgs(["--ter-apply-update", dir, "5", "extra"])).IsNull();
    }

    [Test]
    public async Task Allowed_OnlyFromUpdateFolder_ToAnotherFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "L", "TeRMiTuS", "update");
        var own = Path.Combine(root, "0.6.5", "TER");
        var app = new TermitusApplyArgs(Path.Combine(Path.GetTempPath(), "Apps", "TER"), 5);
        await Assert.That(TermitusWinUpdate.IsAllowedApply(app, own, root)).IsTrue();
        await Assert.That(TermitusWinUpdate.IsAllowedApply(app, app.AppDir, root)).IsFalse();            // not from the install itself
        await Assert.That(TermitusWinUpdate.IsAllowedApply(app with { AppDir = own }, own, root)).IsFalse(); // target must not be the source
        await Assert.That(TermitusWinUpdate.IsAllowedApply(app, Path.Combine(Path.GetTempPath(), "Other"), root)).IsFalse();
    }

    [Test]
    public async Task Plan_NamesOldDir_AvoidsExisting()
    {
        var app = Path.Combine(Path.GetTempPath(), "TER");
        var p = TermitusWinUpdate.BuildPlan(app, "/src", "0.6.4", _ => false);
        await Assert.That(p.OldDir).IsEqualTo(app + ".old-0.6.4");
        await Assert.That(p.Forward.Select(s => s.Kind).ToArray()).IsEquivalentTo(new[] { TermitusApplyStepKind.Rename, TermitusApplyStepKind.Copy, TermitusApplyStepKind.Start });
        await Assert.That(p.Rollback.Select(s => s.Kind).ToArray()).IsEquivalentTo(new[] { TermitusApplyStepKind.Delete, TermitusApplyStepKind.Rename, TermitusApplyStepKind.Start });
        var taken = TermitusWinUpdate.BuildPlan(app, "/src", "0.6.4", x => x == app + ".old-0.6.4");
        await Assert.That(taken.OldDir).IsEqualTo(app + ".old-0.6.4-2");
        await Assert.That(TermitusWinUpdate.BuildPlan(app, "/src", "..\\evil/x", _ => false).OldDir).IsEqualTo(app + ".old-..evilx");
        await Assert.That(TermitusWinUpdate.SanitizeVersion("")).IsEqualTo("prev");
        await Assert.That(TermitusWinUpdate.SanitizeVersion("0.6.5+d9d008b4db6aa0c1f2e3d4c5b6a79881726354")).IsEqualTo("0.6.5");
        await Assert.That(TermitusWinUpdate.SanitizeVersion("0.6.5 beta")).IsEqualTo("0.6.5");
        await Assert.That(TermitusWinUpdate.SanitizeVersion("+abc")).IsEqualTo("prev");
    }

    [Test]
    public async Task OldDirNames()
    {
        await Assert.That(TermitusWinUpdate.IsOldDirName("TER", "TER.old-0.6.4")).IsTrue();
        await Assert.That(TermitusWinUpdate.IsOldDirName("TER", "TER.old-0.6.4-2")).IsTrue();
        await Assert.That(TermitusWinUpdate.IsOldDirName("TER", "TER")).IsFalse();
        await Assert.That(TermitusWinUpdate.IsOldDirName("TER", "Other.old-1")).IsFalse();
    }

    // ---- executor over a fake file system ----

    private sealed class FakeFs : ITermitusApplyFs
    {
        public HashSet<string> Present = [];
        public List<string> Log = [];
        public bool OldExits = true;
        public int RenameFailuresLeft;       // fails the first N renames (locked)
        public bool RenameAlwaysFails;
        public bool CopyFails;
        public bool BackRenameFails;
        public bool NewStartFails;
        public bool CopyCreatesExe = true;
        public bool CopyDropsOne;
        public bool CopyThrowsOther;

        public bool WaitForExit(int pid, TimeSpan timeout) { Log.Add("wait"); return OldExits; }
        public bool Exists(string path) => Present.Contains(path);
        public int CountFiles(string dir) => Present.Count(x => x.StartsWith(dir + "/"));
        public void Rename(string from, string to)
        {
            Log.Add($"rename {from} -> {to}");
            if (RenameAlwaysFails || (RenameFailuresLeft > 0 && RenameFailuresLeft-- > 0) || (BackRenameFails && Present.Contains(to) == false && from.Contains(".old-")))
            {
                throw new IOException("locked");
            }
            foreach (var p in Present.Where(x => x == from || x.StartsWith(from + "/")).ToList())
            {
                Present.Remove(p);
                Present.Add(to + p[from.Length..]);
            }
        }
        public void CopyTree(string from, string to)
        {
            Log.Add($"copy {from} -> {to}");
            if (CopyThrowsOther)
            {
                Present.Add(to);
                throw new NotSupportedException("weird");
            }
            if (CopyFails)
            {
                Present.Add(to);
                throw new IOException("disk full");
            }
            Present.Add(to);
            var kids = Present.Where(x => x.StartsWith(from + "/")).ToList();
            if (CopyDropsOne && kids.Count > 1)
            {
                kids.RemoveAt(kids.Count - 1);
            }
            foreach (var k in kids)
            {
                Present.Add(to + k[from.Length..]);
            }
            if (!CopyCreatesExe)
            {
                Present.Remove(to + "/TER.exe");
            }
        }
        public void DeleteTree(string path)
        {
            Log.Add($"delete {path}");
            foreach (var p in Present.Where(x => x == path || x.StartsWith(path + "/")).ToList())
            {
                Present.Remove(p);
            }
        }
        public void Start(string exe)
        {
            Log.Add($"start {exe}");
            if (NewStartFails && exe.StartsWith("/apps/TER") && Present.Contains("/apps/TER/NEWMARK"))
            {
                throw new InvalidOperationException("cannot start");
            }
        }
        public void Sleep(TimeSpan t) => Log.Add("sleep");
    }

    private static FakeFs NewFs()
    {
        var fs = new FakeFs();
        fs.Present.UnionWith(["/apps/TER", "/apps/TER/TER.exe", "/upd/0.6.5/TER", "/upd/0.6.5/TER/TER.exe"]);
        return fs;
    }

    private static readonly TermitusApplyArgs Args = new("/apps/TER", 42);

    [Test]
    public async Task Run_Success_RenamesCopiesStartsNew()
    {
        var fs = NewFs();
        var r = TermitusWinUpdate.Run(fs, Args, "/upd/0.6.5/TER", "0.6.4");
        await Assert.That(r).IsEqualTo(TermitusApplyResult.Ok);
        await Assert.That(fs.Log.ToArray()).IsEquivalentTo(new[]
        {
            "wait", "rename /apps/TER -> /apps/TER.old-0.6.4", "copy /upd/0.6.5/TER -> /apps/TER", "start /apps/TER/TER.exe",
        });
        await Assert.That(fs.Present.Contains("/apps/TER.old-0.6.4/TER.exe")).IsTrue();
    }

    [Test]
    public async Task Run_RetriesWhenLocked()
    {
        var fs = NewFs();
        fs.RenameFailuresLeft = 3;
        var r = TermitusWinUpdate.Run(fs, Args, "/upd/0.6.5/TER", "0.6.4");
        await Assert.That(r).IsEqualTo(TermitusApplyResult.Ok);
        await Assert.That(fs.Log.Count(x => x == "sleep")).IsEqualTo(3);
    }

    [Test]
    public async Task Run_RenameNeverWorks_StartsOldUntouched()
    {
        var fs = NewFs();
        fs.RenameAlwaysFails = true;
        var r = TermitusWinUpdate.Run(fs, Args, "/upd/0.6.5/TER", "0.6.4");
        await Assert.That(r).IsEqualTo(TermitusApplyResult.RenameFailed);
        await Assert.That(fs.Log.Count(x => x.StartsWith("rename"))).IsEqualTo(TermitusWinUpdate.Retries);
        await Assert.That(fs.Log.Last()).IsEqualTo("start /apps/TER/TER.exe");
        await Assert.That(fs.Log.Any(x => x.StartsWith("copy"))).IsFalse();
    }

    [Test]
    public async Task Run_CopyFails_RollsBackAndStartsOld()
    {
        var fs = NewFs();
        fs.CopyFails = true;
        var r = TermitusWinUpdate.Run(fs, Args, "/upd/0.6.5/TER", "0.6.4");
        await Assert.That(r).IsEqualTo(TermitusApplyResult.RolledBack);
        await Assert.That(fs.Log.Skip(3).ToArray()).IsEquivalentTo(new[]
        {
            "delete /apps/TER", "rename /apps/TER.old-0.6.4 -> /apps/TER", "start /apps/TER/TER.exe",
        });
        await Assert.That(fs.Present.Contains("/apps/TER/TER.exe")).IsTrue();   // the old install is back
        await Assert.That(fs.Present.Contains("/apps/TER.old-0.6.4")).IsFalse();
    }

    [Test]
    public async Task Run_IncompleteCopy_RollsBack()
    {
        var fs = NewFs();
        fs.Present.Add("/upd/0.6.5/TER/bin/a.dll");
        fs.CopyDropsOne = true;
        await Assert.That(TermitusWinUpdate.Run(fs, Args, "/upd/0.6.5/TER", "0.6.4")).IsEqualTo(TermitusApplyResult.RolledBack);
        await Assert.That(fs.Present.Contains("/apps/TER/TER.exe")).IsTrue();
    }

    [Test]
    public async Task Run_AnyExceptionAfterRename_RollsBack()
    {
        var fs = NewFs();
        fs.CopyThrowsOther = true;
        await Assert.That(TermitusWinUpdate.Run(fs, Args, "/upd/0.6.5/TER", "0.6.4")).IsEqualTo(TermitusApplyResult.RolledBack);
    }

    [Test]
    public async Task StaleUpdateDir_Only_After_10_Minutes()
    {
        var now = DateTime.UtcNow;
        await Assert.That(TermitusWinUpdate.IsStaleUpdateDir(now.AddMinutes(-9), now)).IsFalse();
        await Assert.That(TermitusWinUpdate.IsStaleUpdateDir(now.AddMinutes(-11), now)).IsTrue();
    }

    [Test]
    public async Task Run_NewExeMissingAfterCopy_RollsBack()
    {
        var fs = NewFs();
        fs.CopyCreatesExe = false;
        await Assert.That(TermitusWinUpdate.Run(fs, Args, "/upd/0.6.5/TER", "0.6.4")).IsEqualTo(TermitusApplyResult.RolledBack);
    }

    [Test]
    public async Task Run_RollbackImpossible_Reported()
    {
        var fs = NewFs();
        fs.CopyFails = true;
        fs.BackRenameFails = true;
        await Assert.That(TermitusWinUpdate.Run(fs, Args, "/upd/0.6.5/TER", "0.6.4")).IsEqualTo(TermitusApplyResult.RollbackFailed);
    }

    [Test]
    public async Task Run_OldStillRunning_TouchesNothing_AndStartsOld()
    {
        var fs = NewFs();
        fs.OldExits = false;
        await Assert.That(TermitusWinUpdate.Run(fs, Args, "/upd/0.6.5/TER", "0.6.4")).IsEqualTo(TermitusApplyResult.OldStillRunning);
        await Assert.That(fs.Log.Any(x => x.StartsWith("rename") || x.StartsWith("copy") || x.StartsWith("delete"))).IsFalse();
        await Assert.That(fs.Log.Any(x => x.StartsWith("start") && x.Contains("/apps/TER"))).IsTrue();
    }

    [Test]
    public async Task Run_NotATerInstall_Refused()
    {
        var fs = NewFs();
        fs.Present.Remove("/apps/TER/TER.exe");
        await Assert.That(TermitusWinUpdate.Run(fs, Args, "/upd/0.6.5/TER", "0.6.4")).IsEqualTo(TermitusApplyResult.BadSource);
        await Assert.That(fs.Log.Any(x => x.StartsWith("rename"))).IsFalse();
    }

    // ---- real zip on a temp dir ----

    private static string MakeZip(string dir, params (string Name, string Body)[] entries)
    {
        var zip = Path.Combine(dir, "u.zip");
        using var fs = File.Create(zip);
        using var z = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (n, b) in entries)
        {
            var e = z.CreateEntry(n);
            using var w = new StreamWriter(e.Open());
            w.Write(b);
        }
        return zip;
    }

    [Test]
    public async Task Stage_ExtractsTopFolder_AndRejectsSlip()
    {
        var tmp = Directory.CreateTempSubdirectory("ter-win-upd").FullName;
        try
        {
            var good = MakeZip(tmp, ("TER/TER.exe", "x"), ("TER/bin/a.txt", "y"), ("TER/Папка с пробелом/файл.txt", "z"));
            var root = Path.Combine(tmp, "update");
            var dir = TermitusWinUpdate.StageFromZip(good, "0.6.5", root);
            await Assert.That(dir).IsNotNull();
            await Assert.That(File.Exists(Path.Combine(dir!, "TER.exe"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir!, "Папка с пробелом", "файл.txt"))).IsTrue();

            File.Delete(good);
            var bad = MakeZip(tmp, ("TER/TER.exe", "x"), ("TER/../../escaped.txt", "evil"));
            await Assert.That(TermitusWinUpdate.StageFromZip(bad, "0.6.5", root)).IsNull();
            await Assert.That(File.Exists(Path.Combine(tmp, "escaped.txt"))).IsFalse();

            File.Delete(bad);
            var noExe = MakeZip(tmp, ("TER/readme.txt", "x"));
            await Assert.That(TermitusWinUpdate.StageFromZip(noExe, "0.6.5", root)).IsNull();
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task Cleanup_RemovesOldDirsAndUpdateFolder_ButNotTheApp()
    {
        var tmp = Directory.CreateTempSubdirectory("ter-win-clean").FullName;
        try
        {
            var app = Directory.CreateDirectory(Path.Combine(tmp, "TER")).FullName;
            var old = Directory.CreateDirectory(Path.Combine(tmp, "TER.old-0.6.4")).FullName;
            var other = Directory.CreateDirectory(Path.Combine(tmp, "OTHER.old-1")).FullName;
            var upd = Directory.CreateDirectory(Path.Combine(tmp, "update", "0.6.5")).FullName;
            // a fresh update\\<ver> folder must survive; an old one goes
            TermitusWinUpdate.CleanupLeftovers(app, Path.Combine(tmp, "update"), Path.Combine(app, "TER.exe"), DateTime.UtcNow, updateInProgress: false);
            await Assert.That(Directory.Exists(upd)).IsTrue();
            TermitusWinUpdate.CleanupLeftovers(app, Path.Combine(tmp, "update"), Path.Combine(app, "TER.exe"), DateTime.UtcNow.AddMinutes(30), updateInProgress: true);
            await Assert.That(Directory.Exists(upd)).IsTrue();
            TermitusWinUpdate.CleanupLeftovers(app, Path.Combine(tmp, "update"), Path.Combine(app, "TER.exe"), DateTime.UtcNow.AddMinutes(30), updateInProgress: false);
            await Assert.That(Directory.Exists(app)).IsTrue();
            await Assert.That(Directory.Exists(old)).IsFalse();
            await Assert.That(Directory.Exists(other)).IsTrue();
            await Assert.That(Directory.Exists(upd)).IsFalse();
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
