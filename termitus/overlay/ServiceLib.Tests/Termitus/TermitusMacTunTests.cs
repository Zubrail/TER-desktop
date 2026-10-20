using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

/// <summary>0.6.4: shell/AppleScript quoting for the authorization command, the supervisor script and the TUN decisions.</summary>
public class TermitusMacTunTests
{
    [Test]
    public async Task ShQuote_Plain_Spaces_Quotes_Cyrillic()
    {
        await Assert.That(TermitusMacShell.ShQuote("/a/b")).IsEqualTo("'/a/b'");
        await Assert.That(TermitusMacShell.ShQuote("/Users/Иван Петров/TeRMiTuS/bin")).IsEqualTo("'/Users/Иван Петров/TeRMiTuS/bin'");
        await Assert.That(TermitusMacShell.ShQuote("it's")).IsEqualTo("'it'\\''s'");
        await Assert.That(TermitusMacShell.ShQuote("a\"b$c`d;e")).IsEqualTo("'a\"b$c`d;e'");
    }

    [Test]
    public async Task Quoting_RejectsNulAndNewlines()
    {
        foreach (var bad in new[] { "a\0b", "a\nb", "a\rb", "x\n; rm -rf ~" })
        {
            await Assert.That(TermitusMacShell.ShQuote(bad)).IsNull();
            await Assert.That(TermitusMacShell.AppleScriptEscape(bad)).IsNull();
        }
        await Assert.That(TermitusMacShell.ShQuote(null)).IsNull();
    }

    [Test]
    public async Task AppleScriptEscape_BackslashAndQuote()
    {
        await Assert.That(TermitusMacShell.AppleScriptEscape("a\\b\"c")).IsEqualTo("a\\\\b\\\"c");
        await Assert.That(TermitusMacShell.AppleScriptEscape("Папка с пробелом")).IsEqualTo("Папка с пробелом");
    }

    private static Dictionary<string, string?> NoEnv() => new();

    private static string? Sup(int app, string exe, string cfg, IEnumerable<KeyValuePair<string, string?>> env, params string[] orphans)
        => TermitusMacShell.BuildSupervisor(app, "/Users/a b/bin", exe, "run -c {0} --disable-color", cfg, env,
            "/l/ter-core.log", "/t/ter-core.pid", "/t/ter-sup.pid", "/t/ter-core.stop", "/t/ter-core.halt", "/t/ter-core.reload", orphans, Pin);

    private static readonly string Pin = new string('a', 32) + new string('b', 32);

    [Test]
    public async Task Supervisor_OrphanSweepSparesTersOwnChildren()
    {
        // 19.10: the Xray path is an «orphan» candidate, and TER starts its own Xray right before the TUN core --
        // the first sweep killed it (guard «CoreDied» ~3 s after every first connect).
        var s = Sup(4242, "/Users/a b/bin/sing-box", "/Users/a b/guiConfigs/config.json",
            new Dictionary<string, string?>(), "/Users/a b/bin/xray/xray");
        await Assert.That(s).Contains("ps -axo pid=,ppid=,comm= | while read -r p pp c;");
        await Assert.That(s).Contains("[ \"$pp\" != \"4242\" ]");
    }

    [Test]
    public async Task Supervisor_RunsOnlyAPinnedRootCopy()
    {
        var s = Sup(4242, "/Users/a b/bin/sing-box", "/Users/a b/guiConfigs/config.json", new Dictionary<string, string?>());
        await Assert.That(s).Contains("cp '/Users/a b/bin/sing-box' '/var/root/.ter-core'/core.new");
        await Assert.That(s).Contains("shasum -a 256 '/var/root/.ter-core'/core.new | /usr/bin/cut -d' ' -f1)\" = " + Pin + " ]");
        await Assert.That(s).Contains("chown root:wheel '/var/root/.ter-core' '/var/root/.ter-core/work'");
        await Assert.That(s).Contains("chmod 755 '/var/root/.ter-core' '/var/root/.ter-core/work'");
        await Assert.That(s).Contains("chown root:wheel '/var/root/.ter-core'/core.new && chmod 755 '/var/root/.ter-core'/core.new");
        // the user's copy is never executed
        await Assert.That(s!.Contains("'/Users/a b/bin/sing-box' 'run'")).IsFalse();
        await Assert.That(s).Contains("'/var/root/.ter-core/core' 'run'");
    }

    [Test]
    public async Task Supervisor_RefusesWithoutAValidPin()
    {
        foreach (var bad in new string?[] { null, "", "xyz", new string('A', 64), new string('a', 63) })
        {
            var s = TermitusMacShell.BuildSupervisor(4242, "/w", "/w/sing-box", "run -c {0}", "/w/c.json", new Dictionary<string, string?>(),
                "/l", "/p", "/s", "/st", "/h", "/r", [], bad);
            await Assert.That(s).IsNull();
        }
    }

    [Test]
    public async Task Supervisor_ContainsAllPartsOnOneLine()
    {
        var s = Sup(4242, "/Users/a b/bin/sing-box", "/Users/a b/guiConfigs/config.json",
            new Dictionary<string, string?> { ["XRAY_LOCATION_ASSET"] = "/x y" }, "/Users/a b/bin/xray/xray");
        await Assert.That(s).IsNotNull();
        await Assert.That(s!.Contains('\n')).IsFalse();
        // root never runs in, or hands the core, a user path: root-owned core, root-owned validated config, root work dir
        await Assert.That(s).Contains("cd '/var/root/.ter-core/work' ||");
        await Assert.That(s).Contains("env XRAY_LOCATION_ASSET='/x y' '/var/root/.ter-core/core' 'run' '-c' '/var/root/.ter-core/config.json' '--disable-color' >&3 2>&3");
        await Assert.That(s!.Contains("'-c' '/Users/a b/guiConfigs/config.json'")).IsFalse();
        // the log is opened once, exclusively, and kept as fd 3; no write to a path as root
        await Assert.That(s).Contains("set -C; exec 3>'/l/ter-core.log' || exit 1; set +C;");
        await Assert.That(s).DoesNotContain(">>");
        // chown/chmod only on the root-only pin folder (inside /var/root, unreachable for the user), never on a user path
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s!, "ch(own|mod) [^ ]+( root:wheel)?( '[^']*')+"))
        {
            await Assert.That(m.Value).Contains("'/var/root/.ter-core");
            await Assert.That(m.Value.Contains("/Users/")).IsFalse();
        }
        await Assert.That(System.Text.RegularExpressions.Regex.Matches(s!, "ch(own|mod)").Count).IsEqualTo(System.Text.RegularExpressions.Regex.Matches(s!, "ch(own|mod) [^ ]+( root:wheel)?( '[^']*')+").Count);
        await Assert.That(s).Contains("exec 3>&-");
        await Assert.That(s).Contains("kill -0 4242");
        await Assert.That(s).Contains("[ ! -e '/t/ter-core.stop' ]");
        await Assert.That(s).Contains("[ -e '/t/ter-core.halt' ]");
        await Assert.That(s).Contains("[ -e '/t/ter-core.reload' ]");
        await Assert.That(s).Contains("set -C; echo $C >'/t/ter-core.pid'");
        await Assert.That(s).Contains("kill -9 $C");
        // leftover cores: exact full-path comparison, never a name match
        await Assert.That(s).Contains("[ \"$c\" = '/Users/a b/bin/xray/xray' ]");
        await Assert.That(s).Contains("[ \"$c\" = '/Users/a b/bin/sing-box' ]");
        await Assert.That(s).DoesNotContain("pkill");
        await Assert.That(s).DoesNotContain("killall");
    }

    [Test]
    public async Task Supervisor_RevalidatesTheConfigOnEveryStartAndReload()
    {
        var s = Sup(4242, "/Users/a b/bin/sing-box", "/Users/a b/guiConfigs/config.json", new Dictionary<string, string?>())!;
        // the config is copied+validated by root (osascript JXA, ConfigValidatorJs) into the root folder; st() = vc then run
        await Assert.That(s).Contains("vc() { rm -f '/var/root/.ter-core/config.json'.new; /usr/bin/osascript -l JavaScript -e ");
        await Assert.That(s).Contains("'/Users/a b/guiConfigs/config.json' '/var/root/.ter-core/config.json'.new '/var/root/.ter-core/work'");
        await Assert.That(s).Contains("st() { rm -f '/t/ter-core.pid'; vc || return 1;");
        // reload = flag -> sc; st (st validates again); the flag carries no data
        await Assert.That(s).Contains("if [ -e '/t/ter-core.reload' ]; then rm -f '/t/ter-core.reload'; sc; st; fi;");
    }

    [Test]
    public async Task Supervisor_IsValidShellSyntax()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var s = Sup(4242, "/Users/a b/bin/sing-box", "/Users/a b/guiConfigs/config.json", new Dictionary<string, string?> { ["XRAY_LOCATION_ASSET"] = "/x y" }, "/o/xray")!;
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-n", "-c", s })
        {
            psi.ArgumentList.Add(a);
        }
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        await Assert.That(p.ExitCode).IsEqualTo(0);
        await Assert.That(err).IsEqualTo("");
    }

    [Test]
    public async Task ConfigValidator_FitsTheQuotingLayers()
    {
        var js = TermitusMacShell.ConfigValidatorJs;
        await Assert.That(js.IndexOfAny(['"', '\\', '\n', '\r', '\0'])).IsEqualTo(-1);
        await Assert.That(TermitusMacShell.ShQuote(js)).IsNotNull();
    }

    private static string? RunValidator(string json, out string stderr, out string output)
    {
        stderr = ""; output = "";
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }
        var dir = Path.Combine(Path.GetTempPath(), "ter-val-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "src.json");
            var dst = Path.Combine(dir, "dst.json");
            File.WriteAllText(src, json);
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/osascript") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "-l", "JavaScript", "-e", TermitusMacShell.ConfigValidatorJs, src, dst, "/ROOT/work" })
            {
                psi.ArgumentList.Add(a);
            }
            using var p = System.Diagnostics.Process.Start(psi)!;
            stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            output = File.Exists(dst) ? File.ReadAllText(dst) : "";
            return p.ExitCode == 0 ? "ok" : "refused";
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ConfigValidator_RefusesTamperedConfigs()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        foreach (var bad in new[] { "", "not json", "[]", "{}", "{\"inbounds\":[],\"outbounds\":[{}]}", "{\"inbounds\":[{}]}", "{\"inbounds\":[{}],\"outbounds\":[{}]" })
        {
            await Assert.That(RunValidator(bad, out _, out var o)).IsEqualTo("refused");
            await Assert.That(o).IsEqualTo("");
        }
    }

    [Test]
    public async Task ConfigValidator_StripsRootWritePrimitivesAndPinsTheCacheFile()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        const string evil = "{\"log\":{\"output\":\"/etc/cron.d/x\",\"access\":\"/Users/u/a\",\"error\":\"/Users/u/e\",\"level\":\"warn\"},"
            + "\"inbounds\":[{\"type\":\"tun\"}],\"outbounds\":[{\"type\":\"direct\"}],"
            + "\"experimental\":{\"cache_file\":{\"enabled\":true,\"path\":\"/Users/u/cache.db\"},\"clash_api\":{\"external_controller\":\"127.0.0.1:1\",\"external_ui\":\"/Users/u/ui\"}}}";
        await Assert.That(RunValidator(evil, out var err, out var o)).IsEqualTo("ok");
        await Assert.That(o).DoesNotContain("/etc/cron.d");
        await Assert.That(o).DoesNotContain("/Users/u");
        await Assert.That(o).DoesNotContain("external_ui");
        await Assert.That(o).Contains("\"level\":\"warn\"");
        await Assert.That(o).Contains("/ROOT/work/cache.db");
        // a clean config passes unchanged in substance (disabled log stays, listen stays)
        const string good = "{\"log\":{\"output\":\"\"},\"inbounds\":[{\"type\":\"mixed\",\"listen\":\"127.0.0.1\"}],\"outbounds\":[{\"type\":\"vless\"}]}";
        await Assert.That(RunValidator(good, out _, out var g)).IsEqualTo("ok");
        await Assert.That(g).Contains("127.0.0.1");
    }

    [Test]
    public async Task CoreHashes_TamperedCoreIsRefused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ter-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "sing-box");
            File.WriteAllText(f, "genuine core");
            var good = TermitusCoreHashes.FileSha256(f)!;
            var raw = "sing-box=" + good + ";xray=" + new string('0', 64);
            await Assert.That(TermitusCoreHashes.Verify(f, TermitusCoreHashes.ExpectedFor(f, raw))).IsTrue();
            File.WriteAllText(f, "evil core"); // swapped by a user-level process
            await Assert.That(TermitusCoreHashes.Verify(f, TermitusCoreHashes.ExpectedFor(f, raw))).IsFalse();
            // unknown name / empty table / malformed pin: fail closed
            await Assert.That(TermitusCoreHashes.ExpectedFor(Path.Combine(dir, "other"), raw)).IsNull();
            await Assert.That(TermitusCoreHashes.ExpectedFor(f, "")).IsNull();
            await Assert.That(TermitusCoreHashes.Verify(f, null)).IsFalse();
            await Assert.That(TermitusCoreHashes.Verify(f, "abc")).IsFalse();
            await Assert.That(TermitusCoreHashes.Verify(Path.Combine(dir, "missing"), good)).IsFalse();
            await Assert.That(TermitusCoreHashes.Parse("a=zz;=1;b=" + new string('A', 64)).Count).IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Supervisor_FailsClosed()
    {
        var env = NoEnv();
        await Assert.That(Sup(10, "/c\n; id", "/p", env)).IsNull();
        await Assert.That(Sup(10, "/c", "/p\0x", env)).IsNull();
        await Assert.That(Sup(1, "/c", "/p", env)).IsNull();
        await Assert.That(Sup(10, "/c", "/p", env, "/o\nx")).IsNull();
        await Assert.That(Sup(10, "/c", "/p", new Dictionary<string, string?> { ["A;B"] = "v" })).IsNull();
        await Assert.That(Sup(10, "/c", "/p", new Dictionary<string, string?> { ["A"] = "v\nw" })).IsNull();
    }

    [Test]
    public async Task Argv_ReplacesPlaceholderAndQuotes()
    {
        var a = TermitusMacShell.BuildArgv("run -c {0}", "/a'b/c.json");
        await Assert.That(string.Join(' ', a!)).IsEqualTo("'run' '-c' '/a'\\''b/c.json'");
        await Assert.That(TermitusMacShell.BuildArgv("run", "/x")!.Count).IsEqualTo(1);
        await Assert.That(TermitusMacShell.BuildArgv("", "/x")!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AppleScript_EscapesTheWholeCommandForBothLayers()
    {
        var sup = Sup(10, "/Users/Иван \"Q\"/b/core", "/Users/Иван \"Q\"/c.json", NoEnv())!;
        var script = TermitusMacShell.BuildAppleScript(sup, "TER запрашивает «разрешение» \"x\"")!;
        await Assert.That(script).StartsWith("do shell script \"/bin/sh -c ");
        await Assert.That(script).EndsWith("with administrator privileges with prompt \"TER запрашивает «разрешение» \\\"x\\\"\"");
        // no unescaped double quote may remain inside the AppleScript string literal
        var literal = script["do shell script \"".Length..script.LastIndexOf("\" with administrator privileges with prompt", StringComparison.Ordinal)];
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] == '\\')
            {
                i++; // skip the escaped char
                continue;
            }
            await Assert.That(literal[i]).IsNotEqualTo('"');
        }
        await Assert.That(script).Contains(">/dev/null 2>&1 &");
        await Assert.That(TermitusMacShell.BuildAppleScript("a\nb", "p")).IsNull();
        await Assert.That(TermitusMacShell.BuildAppleScript("ab", "p\nq")).IsNull();
    }

    [Test]
    public async Task ChooseStart_ReusesOnlyALiveSupervisorWithTheSameCommand()
    {
        await Assert.That(TermitusMacShell.ChooseStart(true, "A", "A")).IsEqualTo(TermitusMacShell.StartMode.Reuse);
        await Assert.That(TermitusMacShell.ChooseStart(false, "A", "A")).IsEqualTo(TermitusMacShell.StartMode.Authorize); // killed by the user
        await Assert.That(TermitusMacShell.ChooseStart(true, "A", "B")).IsEqualTo(TermitusMacShell.StartMode.Authorize); // other core / args
        await Assert.That(TermitusMacShell.ChooseStart(true, null, "A")).IsEqualTo(TermitusMacShell.StartMode.Authorize); // not ours
    }

    [Test]
    public async Task NewCoreReady_NeedsAnotherLivePid()
    {
        await Assert.That(TermitusMacShell.NewCoreReady(100, 200, true)).IsTrue();
        await Assert.That(TermitusMacShell.NewCoreReady(-1, 200, true)).IsTrue();
        await Assert.That(TermitusMacShell.NewCoreReady(100, 100, true)).IsFalse();
        await Assert.That(TermitusMacShell.NewCoreReady(100, -1, false)).IsFalse();
        await Assert.That(TermitusMacShell.NewCoreReady(100, 200, false)).IsFalse();
    }

    [Test]
    public async Task Classify_Cancel_Ok_Failed()
    {
        await Assert.That(TermitusMacShell.Classify(0, "")).IsEqualTo(TermitusMacShell.AuthResult.Ok);
        await Assert.That(TermitusMacShell.Classify(1, "execution error: User canceled. (-128)")).IsEqualTo(TermitusMacShell.AuthResult.Cancelled);
        await Assert.That(TermitusMacShell.Classify(1, "execution error: boom (1)")).IsEqualTo(TermitusMacShell.AuthResult.Failed);
    }

    [Test]
    public async Task PidAlive_OwnProcessYes_NonsenseNo()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        await Assert.That(TermitusMacCore.PidAlive(Environment.ProcessId)).IsTrue();
        await Assert.That(TermitusMacCore.PidAlive(-5)).IsFalse();
        await Assert.That(TermitusMacCore.PidAlive(0)).IsFalse();
    }

    [Test]
    public async Task Plan_NotWanted()
    {
        var S = TermitusTunPlan.Step.NotWanted;
        await Assert.That(TermitusTunPlan.Decide(false, false, true, false, false, false, false)).IsEqualTo(S);
        await Assert.That(TermitusTunPlan.Decide(true, true, true, false, false, false, false)).IsEqualTo(S);
        await Assert.That(TermitusTunPlan.Decide(true, false, false, false, false, false, false)).IsEqualTo(S); // Linux
    }

    [Test]
    public async Task Plan_Mac_Windows_Paths()
    {
        await Assert.That(TermitusTunPlan.Decide(true, false, true, false, false, false, false)).IsEqualTo(TermitusTunPlan.Step.MacAuthorize);
        await Assert.That(TermitusTunPlan.Decide(true, false, true, false, true, false, false)).IsEqualTo(TermitusTunPlan.Step.AlreadyOn);
        await Assert.That(TermitusTunPlan.Decide(true, false, false, true, false, true, false)).IsEqualTo(TermitusTunPlan.Step.WinEnableAsAdmin);
        await Assert.That(TermitusTunPlan.Decide(true, false, false, true, false, false, false)).IsEqualTo(TermitusTunPlan.Step.WinAskRestart);
        await Assert.That(TermitusTunPlan.Decide(true, false, false, true, false, false, true)).IsEqualTo(TermitusTunPlan.Step.BrowsersOnly);
        // admin beats "browsers only earlier": already elevated, nothing to ask
        await Assert.That(TermitusTunPlan.Decide(true, false, false, true, false, true, true)).IsEqualTo(TermitusTunPlan.Step.WinEnableAsAdmin);
    }

    [Test]
    public async Task Plan_StartupFlagAndAskOutcome()
    {
        await Assert.That(TermitusTunPlan.OnStartup(false, true, true)).IsEqualTo(TermitusTunPlan.StartupStep.Nothing);
        await Assert.That(TermitusTunPlan.OnStartup(true, true, true)).IsEqualTo(TermitusTunPlan.StartupStep.ConnectAfterRestart);
        await Assert.That(TermitusTunPlan.OnStartup(true, false, true)).IsEqualTo(TermitusTunPlan.StartupStep.ClearFlagOnly);
        await Assert.That(TermitusTunPlan.OnStartup(true, true, false)).IsEqualTo(TermitusTunPlan.StartupStep.ClearFlagOnly);
        await Assert.That(TermitusTunPlan.AfterAsk(true, true)).IsEqualTo(TermitusTunPlan.AskOutcome.Restarting);
        await Assert.That(TermitusTunPlan.AfterAsk(true, false)).IsEqualTo(TermitusTunPlan.AskOutcome.Declined);
        await Assert.That(TermitusTunPlan.AfterAsk(false, false)).IsEqualTo(TermitusTunPlan.AskOutcome.BrowsersOnly);
    }
}
