using System.Security.Cryptography;
using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>0.6.5 security review D-7: what is installed is what was verified.</summary>
public class TermitusUpdateStagingTests
{
    private static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Test]
    public async Task SealFile_CopiesVerifiedBytesAndRefusesTamperedOnes()
    {
        var tmp = Directory.CreateTempSubdirectory("ter-seal").FullName;
        try
        {
            var src = Path.Combine(tmp, "a.dmg");
            var data = RandomNumberGenerator.GetBytes(300_000);
            File.WriteAllBytes(src, data);
            var sealedPath = TermitusUpdater.SealFile(src, Hex(data), Path.Combine(tmp, "sealed"));
            await Assert.That(sealedPath).IsNotNull();
            await Assert.That(File.ReadAllBytes(sealedPath!).SequenceEqual(data)).IsTrue();
            // the sealed copy is what is used: replacing the original later changes nothing
            File.WriteAllText(src, "evil");
            await Assert.That(File.ReadAllBytes(sealedPath!).SequenceEqual(data)).IsTrue();
            // a file that no longer matches the signed hash is refused and leaves nothing behind
            var before = Directory.GetFiles(Path.Combine(tmp, "sealed")).Length;
            await Assert.That(TermitusUpdater.SealFile(src, Hex(data), Path.Combine(tmp, "sealed"))).IsNull();
            await Assert.That(Directory.GetFiles(Path.Combine(tmp, "sealed")).Length).IsEqualTo(before);
            await Assert.That(TermitusUpdater.SealFile(Path.Combine(tmp, "missing"), Hex(data), Path.Combine(tmp, "sealed"))).IsNull();
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task PrivateDir_IsOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var tmp = Directory.CreateTempSubdirectory("ter-priv").FullName;
        try
        {
            var d = Path.Combine(tmp, "updates");
            Directory.CreateDirectory(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            TermitusUpdater.PrivateDir(d);
            await Assert.That(File.GetUnixFileMode(d)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task TreeHash_DetectsAnyChangeInAStagedBundle()
    {
        var tmp = Directory.CreateTempSubdirectory("ter-tree").FullName;
        try
        {
            var app = Path.Combine(tmp, "TER.app", "Contents", "MacOS");
            Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(app, "TER"), "binary");
            File.WriteAllText(Path.Combine(app, "x.txt"), "data");
            var root = Path.Combine(tmp, "TER.app");
            var h1 = TermitusUpdater.TreeHash(root);
            await Assert.That(h1).IsNotNull();
            await Assert.That(TermitusUpdater.TreeHash(root)).IsEqualTo(h1);
            File.WriteAllText(Path.Combine(app, "TER"), "binary+evil"); // swapped after staging
            await Assert.That(TermitusUpdater.TreeHash(root)).IsNotEqualTo(h1);
            File.WriteAllText(Path.Combine(app, "TER"), "binary");
            await Assert.That(TermitusUpdater.TreeHash(root)).IsEqualTo(h1);
            File.WriteAllText(Path.Combine(app, "added.sh"), "x"); // planted file
            await Assert.That(TermitusUpdater.TreeHash(root)).IsNotEqualTo(h1);
            File.Delete(Path.Combine(app, "added.sh"));
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(app, "lnk"), "/etc/hosts"); // not followed, target recorded
                var h2 = TermitusUpdater.TreeHash(root);
                File.Delete(Path.Combine(app, "lnk"));
                File.CreateSymbolicLink(Path.Combine(app, "lnk"), "/etc/passwd");
                await Assert.That(TermitusUpdater.TreeHash(root)).IsNotEqualTo(h2);
            }
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task StageFromZip_RefusesAnArchiveThatChangedAfterVerification()
    {
        var tmp = Directory.CreateTempSubdirectory("ter-zipv").FullName;
        try
        {
            var zip = Path.Combine(tmp, "a.zip");
            using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                var e = z.CreateEntry("TER/TER.exe");
                using var w = new StreamWriter(e.Open());
                w.Write("exe");
            }
            var good = Hex(File.ReadAllBytes(zip));
            var root = Path.Combine(tmp, "update");
            await Assert.That(TermitusWinUpdate.StageFromZip(zip, "0.6.5", root, good)).IsNotNull();
            // same file, but the signed hash is of something else (file swapped between verify and use)
            await Assert.That(TermitusWinUpdate.StageFromZip(zip, "0.6.6", root, new string('0', 64))).IsNull();
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
