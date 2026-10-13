using ServiceLib.Services;
using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

public class TermitusLogPrivacyTests
{
    [Test]
    public async Task SubscriptionToken_And_ProxyLinks_And_Uuids_AreMasked()
    {
        var m = TermitusLogPrivacy.Mask(
            "GET https://termitus.online/subscription/AbCdEf123456XYZ?variant=mobile failed; "
            + "link vless://11111111-2222-3333-4444-555555555555@1.2.3.4:443?security=reality&pbk=ABCDEFGH#NL ; id 11111111-2222-3333-4444-555555555555")!;
        await Assert.That(m.Contains("AbCdEf123456XYZ")).IsFalse();
        await Assert.That(m.Contains("/subscription/***")).IsTrue();
        await Assert.That(m.Contains("vless://***")).IsTrue();
        await Assert.That(m.Contains("1.2.3.4")).IsFalse();
        await Assert.That(m.Contains("11111111-2222")).IsFalse();
        await Assert.That(m.Contains("****-uuid-****")).IsTrue();
    }

    [Test]
    public async Task SecretFields_AreMasked_PlainText_IsUntouched()
    {
        var m = TermitusLogPrivacy.Mask("""{"password":"hunter2hunter2","x":1} token=abcd1234 sid: 6a7b8c9d""")!;
        await Assert.That(m.Contains("hunter2hunter2")).IsFalse();
        await Assert.That(m.Contains("abcd1234")).IsFalse();
        await Assert.That(m.Contains("6a7b8c9d")).IsFalse();
        var plain = "TER perf: ui-blocked 133.4 ms | home=True status='Подключено'";
        await Assert.That(TermitusLogPrivacy.Mask(plain)).IsEqualTo(plain);
        await Assert.That(TermitusLogPrivacy.Mask(null)).IsNull();
        await Assert.That(TermitusLogPrivacy.Mask("")).IsEqualTo("");
    }

    [Test]
    public async Task PurgeOld_DeletesOnlyOldTxtFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ter-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var oldLog = Path.Combine(dir, "2026-01-01.txt");
            var newLog = Path.Combine(dir, "2026-10-12.txt");
            var shot = Path.Combine(dir, "ter-shot-x.png");
            foreach (var f in new[] { oldLog, newLog, shot })
            {
                File.WriteAllText(f, "x");
            }
            File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(shot, DateTime.UtcNow.AddDays(-10));
            var n = TermitusLogPrivacy.PurgeOld(dir, DateTime.UtcNow);
            await Assert.That(n).IsEqualTo(1);
            await Assert.That(File.Exists(oldLog)).IsFalse();
            await Assert.That(File.Exists(newLog)).IsTrue();
            await Assert.That(File.Exists(shot)).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
