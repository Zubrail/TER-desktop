using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>0.6.5: consent-based crash report (mirrors Android TerCrash) and the antivirus «core file gone» check.</summary>
public class TermitusCrashTests
{
    [Test]
    public async Task Redact_RemovesLinksIpsKeysAndUserNames()
    {
        var raw = "at X.Y() in C:\\Users\\Ivan Petrov\\src\\a.cs:12\n"
            + "https://termitus.online/sub/ABCDEFGH12345 vless://u@1.2.3.4:443?x=1 server 203.0.113.7 and 2001:db8::1\n"
            + "uuid 11111111-2222-3333-4444-555555555555 key AbCdEf0123456789AbCdEf0123456789xyz /Users/ivan/Library/x";
        var r = TermitusCrash.Redact(raw);
        await Assert.That(r).DoesNotContain("Ivan");
        await Assert.That(r).DoesNotContain("ABCDEFGH12345");
        await Assert.That(r).DoesNotContain("203.0.113.7");
        await Assert.That(r).DoesNotContain("2001:db8");
        await Assert.That(r).DoesNotContain("11111111-2222");
        await Assert.That(r).DoesNotContain("AbCdEf0123456789");
        await Assert.That(r).DoesNotContain("/Users/ivan");
        await Assert.That(r).Contains("C:\\Users\\<user>");
        await Assert.That(r).Contains("at X.Y()"); // stack frames stay readable
        await Assert.That(r).Contains("a.cs:12");
    }

    [Test]
    public async Task Build_HasHeaderCapsLengthAndFrames()
    {
        Exception ex;
        try
        {
            throw new InvalidOperationException("boom https://secret.example/x?token=abc123456");
        }
        catch (Exception e)
        {
            ex = e;
        }
        var t = TermitusCrash.Build("TER 0.6.5", "Windows 11 x64", new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero), ex);
        await Assert.That(t).StartsWith("[Отчёт об ошибке] TER Desktop TER 0.6.5");
        await Assert.That(t).Contains("Система: Windows 11 x64");
        await Assert.That(t).Contains("InvalidOperationException");
        await Assert.That(t).DoesNotContain("secret.example");
        await Assert.That(t).DoesNotContain("abc123456");
        var big = new Exception(new string('x', 20000));
        await Assert.That(TermitusCrash.Build("v", "s", DateTimeOffset.UtcNow, big).Length).IsLessThanOrEqualTo(TermitusCrash.MaxChars);
    }

    [Test]
    public async Task CoreFiles_ReportsMissingOrEmptyCores()
    {
        long? Present(string p) => 1000;
        await Assert.That(TermitusCoreFiles.FindMissing("/b", true, Present).Count).IsEqualTo(0);
        long? NoSing(string p) => p.EndsWith("sing-box.exe") ? null : 1000;
        await Assert.That(TermitusCoreFiles.FindMissing("/b", true, NoSing).SequenceEqual(new[] { "sing-box.exe" })).IsTrue();
        await Assert.That(TermitusCoreFiles.FindMissing("/b", false, NoSing).Count).IsEqualTo(0); // proxy mode does not need sing-box
        long? Empty(string p) => 0; // quarantine can leave a 0-byte file
        await Assert.That(TermitusCoreFiles.FindMissing("/b", true, Empty).SequenceEqual(new[] { "xray.exe", "sing-box.exe" })).IsTrue();
        await Assert.That(TermitusCoreFiles.MissingOnWindows(true).Count).IsEqualTo(OperatingSystem.IsWindows() ? TermitusCoreFiles.MissingOnWindows(true).Count : 0);
    }
}
