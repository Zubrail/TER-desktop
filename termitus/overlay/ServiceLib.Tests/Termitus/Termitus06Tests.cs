using System.Diagnostics;
using TUnit.Core;

using ServiceLib.Services;

namespace ServiceLib.Tests.Termitus;

/// <summary>0.6: updater (Ed25519 + manifest), problem report (masking, composition), connection check logic.</summary>
public class Termitus06Tests
{
    private const string TestPub = "A6EHv/POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg="; // seed 00 01 02 .. 1f (sign_release.py)
    private const string Notes = "Что нового:\n— быстрее\n— исправления";
    private const string Sha = "abababababababababababababababababababababababababababababababab";
    private const string Url = "https://termitus.online/app/TER-0.6.1-osx-arm64.dmg";
    // signatures made by termitus/tools/sign_release.py (pure-python Ed25519), see the generator in the repo history
    private const string Sig_Good = "cc8gmM4zpwX2QuaaArNIcrgWBtshCU1dwdGaZBNj70lWt0ESWomtktve5jG3CRUIsqUQuUT5mVt3R8pyFWcsCQ==";
    private const string Sig_HttpUrl = "qOc1C0J3Iv7Ogdh2XWK9QP+XHVAvPdN22/UBaGrQMJFY7ggaB1iAM+k0ir+JhvZeVy7TJH8htwHaoiWQDLm4DA==";
    private const string Sig_NoHash = "vY7C59aPiiWW2g0SbzKvShqIB4aiFiLg+0bzlAKPLhJvA3VWwVVZpgxV/2MZY5Dhn2XGrzLo99lINDp4xSIrDQ==";
    private const string Sig_Old = "zjioS0wgkEpZvf8TQHa6TuoWst2oMv6L+nZ4oYFy0j3wdIi9u6HYpnrS2t/Je8PR4dJNwQyPOueil0RTCAI5Dw==";
    private const string Sig_Same = "kx7SMK2CWP+USBiCbah+4g34ToXUFQ3qVC3Rb2J0dHs3UabfaMJlKhUpmKANMRvGE75tl6RadqIdkhf0UeBHAA==";
    private const string Sig_WinOnly = "2psvIvVftIG2vM2wl6M3zl6mXLDQ8jeKfPgfhLf6bqM2HIkdOETzyTEP6yKHgopRSG1nmvFA8S3q7h+F+9VTCg==";

    private static byte[] Pub => Convert.FromBase64String(TestPub);

    private static string Manifest(string version, string sig, string? url = Url, string? sha = Sha, string notes = Notes, string plat = "mac_arm64")
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = version,
            ["notes_ru"] = notes,
            ["url_" + plat] = url,
            ["sha256"] = sha == null ? new Dictionary<string, string>() : new Dictionary<string, string> { [plat] = sha },
            ["signature"] = sig,
        });

    #region Ed25519

    [Test]
    public async Task Ed25519_Rfc8032_Vectors()
    {
        // RFC 8032 section 7.1, test 1 (empty message) and test 2 (one byte 0x72)
        var pk1 = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        var sig1 = Convert.FromHexString("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        await Assert.That(TermitusEd25519.Verify(pk1, [], sig1)).IsTrue();
        await Assert.That(TermitusEd25519.Verify(pk1, [0x00], sig1)).IsFalse();

        var pk2 = Convert.FromHexString("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
        var sig2 = Convert.FromHexString("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
        await Assert.That(TermitusEd25519.Verify(pk2, [0x72], sig2)).IsTrue();
        await Assert.That(TermitusEd25519.Verify(pk1, [0x72], sig2)).IsFalse(); // another key
    }

    [Test]
    public async Task Ed25519_RejectsMalformedInput()
    {
        var pk = Convert.FromHexString("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
        var sig = Convert.FromHexString("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");
        await Assert.That(TermitusEd25519.Verify(pk, [0x72], sig[..63])).IsFalse();
        await Assert.That(TermitusEd25519.Verify(pk[..31], [0x72], sig)).IsFalse();
        var flipped = (byte[])sig.Clone();
        flipped[10] ^= 1;
        await Assert.That(TermitusEd25519.Verify(pk, [0x72], flipped)).IsFalse();
        // S >= L (malleability): add L to S
        var big = (byte[])sig.Clone();
        for (var i = 32; i < 64; i++)
        {
            big[i] = 0xFF;
        }
        await Assert.That(TermitusEd25519.Verify(pk, [0x72], big)).IsFalse();
        await Assert.That(TermitusEd25519.Verify(new byte[32], [0x72], new byte[64])).IsFalse();
    }

    #endregion Ed25519

    #region Manifest

    [Test]
    public async Task Manifest_Signed_NewerVersion_IsOffered()
    {
        var r = TermitusUpdateManifest.Evaluate(Manifest("0.6.1", Sig_Good), Pub, "0.6.0", "mac_arm64");
        await Assert.That(r.Status).IsEqualTo(TermitusUpdateStatus.Available);
        await Assert.That(r.Info!.Version).IsEqualTo("0.6.1");
        await Assert.That(r.Info.Url).IsEqualTo(Url);
        await Assert.That(r.Info.Sha256).IsEqualTo(Sha);
        await Assert.That(r.Info.NotesRu).IsEqualTo(Notes);
    }

    [Test]
    public async Task Manifest_AnyTamperedField_IsRejected()
    {
        var pub = Pub;
        // notes, version, url, sha256 changed after signing
        await Assert.That(TermitusUpdateManifest.Evaluate(Manifest("0.6.1", Sig_Good, notes: Notes + "!"), pub, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.Invalid);
        await Assert.That(TermitusUpdateManifest.Evaluate(Manifest("0.9.9", Sig_Good), pub, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.Invalid);
        await Assert.That(TermitusUpdateManifest.Evaluate(Manifest("0.6.1", Sig_Good, url: "https://evil.example/TER.dmg"), pub, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.Invalid);
        await Assert.That(TermitusUpdateManifest.Evaluate(Manifest("0.6.1", Sig_Good, sha: new string('0', 64)), pub, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.Invalid);
        // the signature of the mac_arm64 file cannot be moved to the x64 slot
        await Assert.That(TermitusUpdateManifest.Evaluate(Manifest("0.6.1", Sig_Good, plat: "mac_x64"), pub, "0.6.0", "mac_x64").Status).IsEqualTo(TermitusUpdateStatus.Invalid);
    }

    [Test]
    public async Task Manifest_Unsigned_WrongKey_Garbage_AreInvalid_NoKeyIsOff()
    {
        var pub = Pub;
        var unsigned = "{\"version\":\"0.6.1\",\"notes_ru\":\"x\",\"url_mac_arm64\":\"" + Url + "\",\"sha256\":{\"mac_arm64\":\"" + Sha + "\"}}";
        await Assert.That(TermitusUpdateManifest.Evaluate(unsigned, pub, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.Invalid);
        await Assert.That(TermitusUpdateManifest.Evaluate(Manifest("0.6.1", "AAAA"), pub, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.Invalid);
        var otherKey = Convert.FromHexString("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
        await Assert.That(TermitusUpdateManifest.Evaluate(Manifest("0.6.1", Sig_Good), otherKey, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.Invalid);
        foreach (var junk in new[] { "", "not json", "[]", "{", "null" })
        {
            await Assert.That(TermitusUpdateManifest.Evaluate(junk, pub, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.Invalid);
        }
        await Assert.That(TermitusUpdateManifest.Evaluate(Manifest("0.6.1", Sig_Good), null, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.NoKey);
    }

    [Test]
    public async Task Manifest_OldSameVersion_PlatformGaps_And_UnsafeLinks()
    {
        var pub = Pub;
        await Assert.That(TermitusUpdateManifest.Evaluate(Manifest("0.5.0", Sig_Old), pub, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.UpToDate); // no downgrade
        await Assert.That(TermitusUpdateManifest.Evaluate(Manifest("0.6.0", Sig_Same), pub, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.UpToDate);
        // signed, newer, but only a Windows file: nothing for a Mac
        var win = Manifest("0.6.1", Sig_WinOnly, url: "https://termitus.online/app/TER-win.zip", sha: string.Concat(Enumerable.Repeat("cd", 32)), plat: "win_x64");
        await Assert.That(TermitusUpdateManifest.Evaluate(win, pub, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.UpToDate);
        // signed but http:// / without a sha256 -> refused even though the signature is fine
        await Assert.That(TermitusUpdateManifest.Evaluate(Manifest("0.6.1", Sig_HttpUrl, url: "http://termitus.online/app/TER.dmg"), pub, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.Invalid);
        await Assert.That(TermitusUpdateManifest.Evaluate(Manifest("0.6.1", Sig_NoHash, sha: null), pub, "0.6.0", "mac_arm64").Status).IsEqualTo(TermitusUpdateStatus.Invalid);
    }

    [Test]
    public async Task CanonicalMessage_Matches_The_Python_Signer()
    {
        var m = TermitusUpdateManifest.CanonicalMessage("0.6.1", "а\nб",
            new Dictionary<string, string> { ["mac_arm64"] = "https://x/a.dmg" },
            new Dictionary<string, string> { ["mac_arm64"] = string.Concat(Enumerable.Repeat("ab", 32)) });
        var text = Encoding.UTF8.GetString(m);
        await Assert.That(text.StartsWith("TER-UPDATE-1\nversion:5:0.6.1\nnotes_ru:5:а\nб\nurl_mac_arm64:15:https://x/a.dmg\nurl_mac_x64:0:\nurl_win_x64:0:\nsha256_mac_arm64:64:")).IsTrue();
        await Assert.That(text.EndsWith("sha256_mac_x64:0:\nsha256_win_x64:0:\n")).IsTrue();
    }

    [Test]
    public async Task Versions_Compare_As_Numbers()
    {
        await Assert.That(TermitusUpdateManifest.IsNewer("0.6.10", "0.6.9")).IsTrue();
        await Assert.That(TermitusUpdateManifest.IsNewer("0.10.0", "0.9.9")).IsTrue();
        await Assert.That(TermitusUpdateManifest.IsNewer("v1.0", "0.9.9")).IsTrue();
        await Assert.That(TermitusUpdateManifest.IsNewer("0.6.0", "0.6.0")).IsFalse();
        await Assert.That(TermitusUpdateManifest.IsNewer("0.5.9", "0.6.0")).IsFalse();
        await Assert.That(TermitusUpdateManifest.IsNewer("abc", "0.6.0")).IsFalse();
        await Assert.That(TermitusUpdateManifest.IsNewer("0.6.1-beta", "0.6.0")).IsTrue();
    }

    [Test]
    public async Task Plist_Parse_And_SwapScript_Really_Swaps()
    {
        var (id, ver) = TermitusUpdater.ReadPlist("<dict><key>CFBundleIdentifier</key><string>online.termitus.desktop</string>\n<key>CFBundleShortVersionString</key>\n<string>0.6.1</string></dict>");
        await Assert.That(id).IsEqualTo("online.termitus.desktop");
        await Assert.That(ver).IsEqualTo("0.6.1");

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "ter-swap-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "new", "TER.app", "Contents"));
            Directory.CreateDirectory(Path.Combine(root, "inst", "TER.app", "Contents"));
            File.WriteAllText(Path.Combine(root, "new", "TER.app", "Contents", "v.txt"), "new");
            File.WriteAllText(Path.Combine(root, "inst", "TER.app", "Contents", "v.txt"), "old");
            var script = Path.Combine(root, "swap.sh");
            File.WriteAllText(script, TermitusUpdater.SwapScript);
            // a pid that is already gone: the helper must not wait
            var dead = Process.Start("/bin/sh", ["-c", "exit 0"])!;
            dead.WaitForExit();
            var target = Path.Combine(root, "inst", "TER.app");
            var p = Process.Start(new ProcessStartInfo("/bin/bash") { ArgumentList = { script, dead.Id.ToString(), Path.Combine(root, "new", "TER.app"), target, "0" } })!;
            p.WaitForExit(20000);
            await Assert.That(p.ExitCode).IsEqualTo(0);
            await Assert.That(File.ReadAllText(Path.Combine(target, "Contents", "v.txt"))).IsEqualTo("new");
            await Assert.That(Directory.Exists(target + ".old-ter-update")).IsFalse();

            // failure: the new bundle is missing -> the old one is put back untouched
            var p2 = Process.Start(new ProcessStartInfo("/bin/bash") { ArgumentList = { script, dead.Id.ToString(), Path.Combine(root, "nope", "TER.app"), target, "0" } })!;
            p2.WaitForExit(20000);
            await Assert.That(p2.ExitCode).IsNotEqualTo(0);
            await Assert.That(File.ReadAllText(Path.Combine(target, "Contents", "v.txt"))).IsEqualTo("new");
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // temp
            }
        }
    }

    #endregion Manifest

    #region Report

    [Test]
    public async Task MaskStrict_Removes_Everything_That_Identifies()
    {
        var raw = "sub https://termitus.online/subscription/AbCdEf0123456789Zz uuid 11111111-2222-3333-4444-555555555555 "
            + "ip 203.0.113.7:8443 v6 2001:db8::1 host edge-nl1.termitus.online link vless://11111111-2222-3333-4444-555555555555@1.2.3.4:443?x=1#n "
            + "path /Users/ivan/Library/x token=SECRETSECRET1234 blob QWxhZGRpbjpvcGVuIHNlc2FtZVFXRVJUWVVJ";
        var m = TermitusDiagnostics.MaskStrict(raw);
        foreach (var leak in new[] { "AbCdEf0123", "11111111-2222", "203.0.113", "2001:db8", "termitus.online", "edge-nl1", "1.2.3.4", "ivan", "SECRETSECRET", "QWxhZGRpbjpvcGVuIHNlc2FtZQ" })
        {
            await Assert.That(m.Contains(leak)).IsFalse();
        }
        await Assert.That(TermitusDiagnostics.MaskStrict("Нидерланды 1 · TCP (VLESS/xhttp): ok 180 мс")).IsEqualTo("Нидерланды 1 · TCP (VLESS/xhttp): ok 180 мс");
        await Assert.That(TermitusDiagnostics.MaskStrict("TER 0.6.0")).IsEqualTo("TER 0.6.0");
        await Assert.That(TermitusDiagnostics.MaskStrict("macOS 26.0.1 (arm64)")).IsEqualTo("macOS 26.0.1 (arm64)");
    }

    private static TermitusDiagInput Diag(IReadOnlyList<TermitusErrorEntry>? errors = null, IReadOnlyList<string>? check = null)
        => new("TER 0.6.0", "macOS 26.0.1 (arm64)", "системный прокси", "Нидерланды 1 · TCP (VLESS/xhttp)", "отключено",
            "Все, кроме РФ", "Авто", errors ?? [], check, check == null ? null : new DateTime(2026, 10, 12, 14, 35, 0), "не запускалась");

    [Test]
    public async Task Diagnostics_Has_The_Promised_Fields_And_Nothing_Else()
    {
        var errs = new[] { new TermitusErrorEntry(DateTime.UtcNow, "ни один сервер не ответил на проверку") };
        var d = TermitusReport.BuildDiagnostics(Diag(errs, ["Нидерланды 1 · TCP (VLESS/xhttp): ok 180 мс", "Германия 1 · UDP (HYSTERIA2): нет ответа"]));
        foreach (var must in new[] { "Приложение: TER 0.6.0", "Система: macOS 26.0.1 (arm64)", "Режим: системный прокси", "Сервер: Нидерланды 1", "Состояние: отключено", "ни один сервер не ответил", "Проверка подключения (14:35):", "UDP (HYSTERIA2): нет ответа" })
        {
            await Assert.That(d.Contains(must)).IsTrue();
        }
        await Assert.That(d.Contains("://")).IsFalse();
        await Assert.That(TermitusReport.BuildDiagnostics(Diag()).Contains("не было с запуска")).IsTrue();
    }

    [Test]
    public async Task Compose_Attach_Off_Has_No_Diagnostics_And_Is_Bounded()
    {
        var diag = TermitusReport.BuildDiagnostics(Diag());
        var off = TermitusReport.Compose("Медленно", "очень медленно", diag, attach: false);
        await Assert.That(off).IsEqualTo("[Сообщение из приложения TER] Медленно\nочень медленно");
        var on = TermitusReport.Compose("Медленно", "очень медленно", diag, attach: true);
        await Assert.That(on.Contains("--- Диагностика")).IsTrue();
        await Assert.That(on.EndsWith(diag)).IsTrue();

        var huge = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"строка диагностики номер {i}"));
        var cut = TermitusReport.Compose("Другое", new string('я', 5000), huge, attach: true);
        await Assert.That(cut.Length).IsLessThanOrEqualTo(3900);
        await Assert.That(cut.EndsWith("…(обрезано)")).IsTrue();
        await Assert.That(TermitusReport.Compose("x", "", null, true)).IsEqualTo("[Сообщение из приложения TER] x");
    }

    [Test]
    public async Task Compose_Masks_Secrets_In_Users_Own_Text()
    {
        var m = TermitusReport.Compose("Другое", "вот моя ссылка https://termitus.online/subscription/SeCrEtToKeN12345 и vless://abc@1.2.3.4:443", null, false);
        await Assert.That(m.Contains("SeCrEtToKeN12345")).IsFalse();
        await Assert.That(m.Contains("1.2.3.4")).IsFalse();
    }

    [Test]
    public async Task ErrorLog_Keeps_Last_N_Masked()
    {
        TermitusErrorLog.Clear();
        for (var i = 0; i < TermitusErrorLog.Capacity + 5; i++)
        {
            TermitusErrorLog.Add($"ошибка {i} на 10.0.0.{i % 200}");
        }
        var last = TermitusErrorLog.Last(3);
        await Assert.That(last.Count).IsEqualTo(3);
        await Assert.That(last[2].Text).IsEqualTo($"ошибка {TermitusErrorLog.Capacity + 4} на <ip>");
        await Assert.That(TermitusErrorLog.Last(100).Count).IsEqualTo(TermitusErrorLog.Capacity);
        TermitusErrorLog.Clear();
    }

    #endregion Report

    #region Connection check

    private static TermitusProbe P(string loc, TermitusProbeKind k, int ms, bool ru = false)
        => new($"{loc}-{k}", loc, ru, k, k == TermitusProbeKind.Udp ? "HYSTERIA2" : "VLESS", ms);

    private static readonly TermitusLocale Loc = TermitusLocale.Current;

    [Test]
    public async Task Status_Thresholds()
    {
        await Assert.That(TermitusConnCheck.StatusOf(-1)).IsEqualTo(TermitusProbeStatus.Fail);
        await Assert.That(TermitusConnCheck.StatusOf(0)).IsEqualTo(TermitusProbeStatus.Fail);
        await Assert.That(TermitusConnCheck.StatusOf(180)).IsEqualTo(TermitusProbeStatus.Ok);
        await Assert.That(TermitusConnCheck.StatusOf(TermitusConnCheck.SlowMs)).IsEqualTo(TermitusProbeStatus.Ok);
        await Assert.That(TermitusConnCheck.StatusOf(TermitusConnCheck.SlowMs + 1)).IsEqualTo(TermitusProbeStatus.Slow);
    }

    [Test]
    public async Task UdpCut_Is_Explained_And_Suggests_Tcp()
    {
        var probes = new List<TermitusProbe>
        {
            P("🇳🇱 Нидерланды 1", TermitusProbeKind.Tcp, 150), P("🇳🇱 Нидерланды 1", TermitusProbeKind.Udp, -1),
            P("🇩🇪 Германия 1", TermitusProbeKind.Tcp, 200), P("🇩🇪 Германия 1", TermitusProbeKind.Udp, -1),
        };
        var advice = TermitusConnCheck.Analyze(probes, Loc);
        await Assert.That(advice[0].Action).IsEqualTo(TermitusAdviceAction.UseTcp);
        await Assert.That(advice[0].Text).IsEqualTo(Loc.CheckUdpCut);
        var rows = TermitusConnCheck.Rows(probes, Loc);
        var udpRow = rows.First(r => r.Status == TermitusProbeStatus.Fail);
        await Assert.That(udpRow.Explain).IsEqualTo(Loc.CheckUdpBlockedLine);
        await Assert.That(udpRow.Mark).IsEqualTo("✗");
        await Assert.That(rows.First(r => r.IsOk).Mark).IsEqualTo("✓");
    }

    [Test]
    public async Task Other_Pictures_Get_The_Right_Advice()
    {
        // everything down
        var down = new List<TermitusProbe> { P("A", TermitusProbeKind.Tcp, -1), P("A", TermitusProbeKind.Udp, -1) };
        await Assert.That(TermitusConnCheck.Analyze(down, Loc)[0].Action).IsEqualTo(TermitusAdviceAction.Report);
        // TCP down, UDP up
        var tcpCut = new List<TermitusProbe> { P("A", TermitusProbeKind.Tcp, -1), P("A", TermitusProbeKind.Udp, 90) };
        await Assert.That(TermitusConnCheck.Analyze(tcpCut, Loc)[0].Action).IsEqualTo(TermitusAdviceAction.UseUdp);
        // one location dead, others fine
        var some = new List<TermitusProbe> { P("A", TermitusProbeKind.Tcp, -1), P("B", TermitusProbeKind.Tcp, 100), P("A", TermitusProbeKind.Udp, -1), P("B", TermitusProbeKind.Udp, 80) };
        var adv = TermitusConnCheck.Analyze(some, Loc);
        await Assert.That(adv[0].Action).IsEqualTo(TermitusAdviceAction.OtherServer);
        // only Russia answers (whitelist-like network)
        var ru = new List<TermitusProbe> { P("🇷🇺 Россия", TermitusProbeKind.Tcp, 40, ru: true), P("A", TermitusProbeKind.Tcp, -1) };
        await Assert.That(TermitusConnCheck.Analyze(ru, Loc).Any(a => a.Text == Loc.CheckOnlyRussia)).IsTrue();
        // all fine / all slow
        var good = new List<TermitusProbe> { P("A", TermitusProbeKind.Tcp, 100), P("A", TermitusProbeKind.Udp, 60) };
        await Assert.That(TermitusConnCheck.Analyze(good, Loc)[0].Text).IsEqualTo(Loc.CheckAllGood);
        var slow = new List<TermitusProbe> { P("A", TermitusProbeKind.Tcp, 2500) };
        await Assert.That(TermitusConnCheck.Analyze(slow, Loc).Any(a => a.Text == Loc.CheckAllSlow)).IsTrue();
        await Assert.That(TermitusConnCheck.Rows(slow, Loc)[0].Mark).IsEqualTo("~");
        await Assert.That(TermitusConnCheck.Analyze([], Loc)).IsEmpty();
    }

    [Test]
    public async Task Spec_Simulation_And_Report_Lines_And_FailedWorks()
    {
        var rules = TermitusConnCheck.ParseSpec("udp=-1, tcp=120,Германия=-1,junk,=5");
        await Assert.That(rules.Count).IsEqualTo(3);
        await Assert.That(TermitusConnCheck.ApplySpec(rules, "🇳🇱 Нидерланды 1", TermitusProbeKind.Udp, 77)).IsEqualTo(-1);
        await Assert.That(TermitusConnCheck.ApplySpec(rules, "🇳🇱 Нидерланды 1", TermitusProbeKind.Tcp, 77)).IsEqualTo(120);
        await Assert.That(TermitusConnCheck.ApplySpec([("Германия", -1)], "🇩🇪 Германия 1", TermitusProbeKind.Tcp, 77)).IsEqualTo(-1);
        await Assert.That(TermitusConnCheck.ApplySpec([], "x", TermitusProbeKind.Tcp, 77)).IsEqualTo(77);

        var probes = new List<TermitusProbe> { P("🇳🇱 Нидерланды 1", TermitusProbeKind.Tcp, 150), P("🇳🇱 Нидерланды 1", TermitusProbeKind.Udp, -1) };
        var lines = TermitusConnCheck.ReportLines(probes);
        await Assert.That(lines[0]).IsEqualTo("Нидерланды 1 · TCP (VLESS): ok 150 мс");
        await Assert.That(lines[1]).IsEqualTo("Нидерланды 1 · UDP (HYSTERIA2): нет ответа");
        var (failed, works) = TermitusConnCheck.FailedAndWorks(probes, Loc);
        await Assert.That(failed.Contains(Loc.ProtoUdpFull)).IsTrue();
        await Assert.That(works.Contains(Loc.ProtoTcpFull)).IsTrue();
        var (f0, w0) = TermitusConnCheck.FailedAndWorks([], Loc);
        await Assert.That(f0).IsEqualTo(Loc.ErrFailedUnknown);
        await Assert.That(w0).IsEqualTo(Loc.ErrWorksUnknown);
    }

    #endregion Connection check

    [Test]
    public async Task SourceUrl_Placeholder_Is_Flagged()
    {
        await Assert.That(TermitusAbout.IsPlaceholder(TermitusAbout.PlaceholderSourceUrl)).IsTrue();
        await Assert.That(TermitusAbout.IsPlaceholder("https://github.com/termitus-online/ter")).IsFalse();
    }
}
