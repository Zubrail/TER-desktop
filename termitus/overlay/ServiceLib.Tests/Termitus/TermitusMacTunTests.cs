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
            "/l/ter-core.log", "/t/ter-core.pid", "/t/ter-sup.pid", "/t/ter-core.stop", "/t/ter-core.halt", "/t/ter-core.reload", orphans);

    [Test]
    public async Task Supervisor_ContainsAllPartsOnOneLine()
    {
        var s = Sup(4242, "/Users/a b/bin/sing-box", "/Users/a b/guiConfigs/config.json",
            new Dictionary<string, string?> { ["XRAY_LOCATION_ASSET"] = "/x y" }, "/Users/a b/bin/xray/xray");
        await Assert.That(s).IsNotNull();
        await Assert.That(s!.Contains('\n')).IsFalse();
        await Assert.That(s).Contains("cd '/Users/a b/bin' ||");
        await Assert.That(s).Contains("env XRAY_LOCATION_ASSET='/x y' '/Users/a b/bin/sing-box' 'run' '-c' '/Users/a b/guiConfigs/config.json' '--disable-color' >&3 2>&3");
        // the log is opened once, exclusively, and kept as fd 3; no write to a path as root
        await Assert.That(s).Contains("set -C; exec 3>'/l/ter-core.log' || exit 1; set +C;");
        await Assert.That(s).DoesNotContain(">>");
        await Assert.That(s).DoesNotContain("chown");
        await Assert.That(s).DoesNotContain("chmod");
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
