using TUnit.Core;

using ServiceLib.Services;

namespace ServiceLib.Tests.Termitus;

/// <summary>0.6.5: which bundled core files overwrite the copies in the data folder.</summary>
public class TermitusCoreSyncTests
{
    [Test]
    [Arguments("xray/xray.exe", true)]
    [Arguments("xray\\wintun.dll", true)]
    [Arguments("sing_box/sing-box.exe", true)]
    [Arguments("sing_box/libcronet.dll", true)]
    [Arguments("geoip.dat", false)]
    [Arguments("ter-geo.version", false)]
    [Arguments("other/x.exe", false)]
    public async Task CoreFiles(string rel, bool core)
    {
        await Assert.That(TermitusCoreSync.IsCoreFile(rel)).IsEqualTo(core);
    }

    [Test]
    public async Task Decision_SizeThenHash()
    {
        var hashed = 0;
        string H(string v) { hashed++; return v; }
        await Assert.That(TermitusCoreSync.NeedsRefresh(10, null, () => H("a"), () => H("a"))).IsTrue();
        await Assert.That(TermitusCoreSync.NeedsRefresh(10, 11, () => H("a"), () => H("a"))).IsTrue();
        await Assert.That(hashed).IsEqualTo(0);               // no hashing when size already differs
        await Assert.That(TermitusCoreSync.NeedsRefresh(10, 10, () => H("AB"), () => H("ab"))).IsFalse();
        await Assert.That(TermitusCoreSync.NeedsRefresh(10, 10, () => H("a"), () => H("b"))).IsTrue();
    }

    [Test]
    public async Task Refresh_OverwritesChangedCore_KeepsSameAndGeo()
    {
        var tmp = Directory.CreateTempSubdirectory("ter-core").FullName;
        try
        {
            var from = Path.Combine(tmp, "app", "bin");
            var to = Path.Combine(tmp, "data", "bin");
            Directory.CreateDirectory(Path.Combine(from, "xray"));
            Directory.CreateDirectory(Path.Combine(to, "xray"));
            File.WriteAllText(Path.Combine(from, "xray", "xray.exe"), "NEW-CORE");
            File.WriteAllText(Path.Combine(to, "xray", "xray.exe"), "OLD-CORE");
            File.WriteAllText(Path.Combine(from, "xray", "same.dll"), "SAME");
            File.WriteAllText(Path.Combine(to, "xray", "same.dll"), "SAME");
            File.WriteAllText(Path.Combine(from, "geoip.dat"), "geo-new");
            File.WriteAllText(Path.Combine(to, "geoip.dat"), "geo-old");
            TermitusCoreSync.Refresh(from, to);
            await Assert.That(File.ReadAllText(Path.Combine(to, "xray", "xray.exe"))).IsEqualTo("NEW-CORE");
            await Assert.That(File.ReadAllText(Path.Combine(to, "geoip.dat"))).IsEqualTo("geo-old");
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }
}
