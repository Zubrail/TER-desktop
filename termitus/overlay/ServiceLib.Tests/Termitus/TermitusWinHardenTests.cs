using System.Security.Cryptography;
using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>0.6.5 security review D-1/D-2: the privileged side only runs what matches the pinned hash and a sanitised config (Windows-only ACL calls are not exercised here).</summary>
public class TermitusWinHardenTests
{
    private static string Hex(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();

    [Test]
    public async Task CopyVerified_KeepsOnlyACopyThatMatchesThePinnedHash()
    {
        var tmp = Directory.CreateTempSubdirectory("ter-harden").FullName;
        try
        {
            var src = Path.Combine(tmp, "user", "sing-box.exe");
            var dst = Path.Combine(tmp, "protected");
            Directory.CreateDirectory(Path.GetDirectoryName(src)!);
            Directory.CreateDirectory(dst);
            File.WriteAllText(src, "genuine core");
            var pin = Hex(File.ReadAllBytes(src));
            var placed = TermitusWinHarden.CopyVerified(src, dst, pin);
            await Assert.That(placed).IsEqualTo(Path.Combine(dst, "sing-box.exe"));
            // an ordinary process swaps the user's copy: refused, and the earlier good copy stays untouched
            File.WriteAllText(src, "evil core");
            await Assert.That(TermitusWinHarden.CopyVerified(src, dst, pin)).IsEqualTo(placed); // identical copy already there, nothing is re-read from the user folder
            File.Delete(placed!);
            await Assert.That(TermitusWinHarden.CopyVerified(src, dst, pin)).IsNull();
            await Assert.That(File.Exists(Path.Combine(dst, "sing-box.exe"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dst, "sing-box.exe.new"))).IsFalse();
            // no pin = refuse
            await Assert.That(TermitusWinHarden.CopyVerified(src, dst, null)).IsNull();
            await Assert.That(TermitusWinHarden.CopyVerified(src, dst, "abc")).IsNull();
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task FilesToProtect_CoreAndKnownSideDllsOnly()
    {
        var tmp = Directory.CreateTempSubdirectory("ter-harden2").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tmp, "sing-box.exe"), "x");
            File.WriteAllText(Path.Combine(tmp, "libcronet.dll"), "x");
            File.WriteAllText(Path.Combine(tmp, "evil.dll"), "x"); // anything else in the folder is never copied or loaded from the protected dir
            var l = TermitusWinHarden.FilesToProtect(Path.Combine(tmp, "sing-box.exe"));
            await Assert.That(l.SequenceEqual(new[] { "sing-box.exe", "libcronet.dll" })).IsTrue();
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Test]
    public async Task NotElevated_NothingChanges()
    {
        // the test process is not an elevated Windows TER: the hooks must be transparent
        await Assert.That(TermitusWinHarden.Active).IsFalse();
        await Assert.That(TermitusWinHarden.CoreFor("/x/sing-box")).IsEqualTo("/x/sing-box");
        await Assert.That(TermitusWinHarden.WorkDir()).IsEqualTo(Utils.GetBinConfigPath());
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(TermitusWinHarden.PinDllSearchPath()).IsFalse();
        }
    }

    [Test]
    public async Task Sanitizer_RefusesGarbageAndStripsWritePrimitives()
    {
        const string wd = "/protected/work";
        foreach (var bad in new[] { null, "", "not json", "[]", "{}", "{\"inbounds\":[],\"outbounds\":[{}]}", "{\"inbounds\":[{}]}", "{\"inbounds\":[{}],\"outbounds\":[{}]" })
        {
            await Assert.That(TermitusConfigSanitizer.Sanitize(bad, wd)).IsNull();
        }
        const string evil = "{\"log\":{\"output\":\"C:\\\\Windows\\\\evil.log\",\"access\":\"C:\\\\a\",\"error\":\"C:\\\\e\",\"level\":\"warn\"},"
            + "\"inbounds\":[{\"type\":\"tun\"}],\"outbounds\":[{\"type\":\"direct\"}],"
            + "\"experimental\":{\"cache_file\":{\"enabled\":true,\"path\":\"C:\\\\Users\\\\u\\\\cache.db\"},\"clash_api\":{\"external_controller\":\"127.0.0.1:1\",\"external_ui\":\"C:\\\\ui\"}}}";
        var o = TermitusConfigSanitizer.Sanitize(evil, wd)!;
        await Assert.That(o).IsNotNull();
        await Assert.That(o).DoesNotContain("evil.log");
        await Assert.That(o).DoesNotContain("C:\\\\a");
        await Assert.That(o).DoesNotContain("external_ui");
        await Assert.That(o).DoesNotContain("Users");
        await Assert.That(o).Contains("\"level\":\"warn\"");
        await Assert.That(o.Replace("\\\\", "/")).Contains("/protected/work");
        const string good = "{\"log\":{\"output\":\"\",\"access\":\"none\"},\"inbounds\":[{\"listen\":\"127.0.0.1\"}],\"outbounds\":[{\"type\":\"vless\"}]}";
        var g = TermitusConfigSanitizer.Sanitize(good, wd)!;
        await Assert.That(g).Contains("127.0.0.1");
        await Assert.That(g).Contains("\"access\":\"none\"");
    }
}
