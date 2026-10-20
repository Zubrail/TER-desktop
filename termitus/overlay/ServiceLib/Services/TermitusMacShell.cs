using System.Text;
using System.Text.RegularExpressions;

namespace ServiceLib.Services;

/// <summary>
/// 0.6.4 (Mac): pure helpers that build the ONE command TER hands to the macOS authorization dialog
/// (`osascript -e 'do shell script "…" with administrator privileges'`).
///
/// Everything that ends up inside the command is quoted for BOTH layers (AppleScript string, then
/// /bin/sh). A value with NUL / CR / LF is refused (null = fail closed, nothing is run): a newline
/// would let a crafted path start a second shell command.
///
/// The command starts a detached root SUPERVISOR written inline (no script file the user or
/// malware could swap between write and run). The supervisor runs the core, writes the core PID
/// to a file, and kills the core as soon as TER's own process is gone or a stop-flag file shows
/// up -- so a crash / quit / in-app update can never leave a root core holding the TUN.
/// </summary>
public static class TermitusMacShell
{
    private static readonly Regex EnvName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    /// <summary>Root-only folder (inside root's home, mode 700) holding the verified copy of the core that root runs.</summary>
    public const string RootCoreDir = "/var/root/.ter-core";
    public const string RootCoreExe = RootCoreDir + "/core";
    /// <summary>The ONLY config root ever passes to the core: a validated, root-owned copy of TER's generated config.</summary>
    public const string RootConfig = RootCoreDir + "/config.json";
    /// <summary>Working directory of the root core (its cache file lands here, never in a user folder).</summary>
    public const string RootWorkDir = RootCoreDir + "/work";

    /// <summary>
    /// 0.6.5 security review D-1: JavaScript for Automation program that ROOT runs on the copy of the config
    /// (`osascript -l JavaScript -e this-text SRC DST WORKDIR`). It parses the JSON (fails = refuse), requires a non-empty
    /// inbounds and outbounds list, strips every key that could make the core write to a path chosen by the user
    /// (log.output, xray log.access/error, external_ui*), pins the sing-box cache file to the root work dir and writes the
    /// sanitised result atomically to DST. One line, no double quotes, no backslashes: it travels inside the sh and AppleScript quoting layers.
    /// </summary>
    public const string ConfigValidatorJs =
        "ObjC.import('Foundation');" +
        "function run(a){var src=a[0],dst=a[1],wd=a[2];" +
        "var s=$.NSString.stringWithContentsOfFileEncodingError(src,4,$());" +
        "if(!s||s.isNil())throw new Error('config unreadable');" +
        "var c=JSON.parse(ObjC.unwrap(s));" +
        "if(!c||typeof c!=='object'||Array.isArray(c))throw new Error('config is not an object');" +
        "if(!Array.isArray(c.inbounds)||!c.inbounds.length||!Array.isArray(c.outbounds)||!c.outbounds.length)throw new Error('config has no inbounds/outbounds');" +
        "var D={external_ui:1,external_ui_download_url:1,external_ui_download_detour:1};" +
        "function w(o){if(!o||typeof o!=='object')return;if(Array.isArray(o)){o.forEach(w);return}for(var k in o){if(D[k]){delete o[k];continue}w(o[k])}}" +
        "w(c);" +
        "if(c.log&&typeof c.log==='object'){if(typeof c.log.output==='string'&&c.log.output!=='')delete c.log.output;" +
        "['access','error'].forEach(function(k){if(typeof c.log[k]==='string'&&c.log[k]!==''&&c.log[k]!=='none')delete c.log[k]})}" +
        "if(c.experimental&&typeof c.experimental==='object'&&c.experimental.cache_file&&typeof c.experimental.cache_file==='object')c.experimental.cache_file.path=wd+'/cache.db';" +
        "var out=$(JSON.stringify(c));" +
        "if(!out.writeToFileAtomicallyEncodingError(dst,true,4,$()))throw new Error('cannot write');" +
        "return 'ok'}";

    private static bool Bad(string? s) => s == null || s.IndexOfAny(['\0', '\n', '\r']) >= 0;

    /// <summary>POSIX single-quote: 'it'\''s'. Null when the value holds NUL / CR / LF.</summary>
    public static string? ShQuote(string? s)
    {
        return Bad(s) ? null : "'" + s!.Replace("'", "'\\''") + "'";
    }

    /// <summary>Body of an AppleScript string literal (no surrounding quotes). Null on NUL / CR / LF.</summary>
    public static string? AppleScriptEscape(string? s)
    {
        return Bad(s) ? null : s!.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Core argv from upstream's template ("run -c {0} --disable-color"): split on spaces, {0} replaced by the
    /// config path, each token shell-quoted. Null if anything is unsafe.
    /// </summary>
    public static List<string>? BuildArgv(string template, string configPath)
    {
        if (Bad(template) || Bad(configPath))
        {
            return null;
        }
        var list = new List<string>();
        foreach (var tok in template.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var q = ShQuote(tok.Replace("{0}", configPath));
            if (q == null)
            {
                return null;
            }
            list.Add(q);
        }
        return list;
    }

    /// <summary>
    /// The inline, LONG-LIVED supervisor as ONE line of /bin/sh (null = refused, unsafe input). One authorization per
    /// TER run: the supervisor stays and is steered by user-owned flag files (existence only, content is never read):
    ///   reload = (re)start the core (stops a running one first), halt = stop the core and stay idle,
    ///   stop = stop the core and exit. It also exits when TER's PID is gone. Before the first start it kills leftover
    /// cores (a 0.6.3 root sing-box that survived an update) -- matched by their FULL executable path only.
    /// </summary>
    public static string? BuildSupervisor(int appPid, string workDir, string coreExe, string argvTemplate, string configPath,
        IEnumerable<KeyValuePair<string, string?>> env, string logFile, string pidFile, string supPidFile, string stopFile,
        string haltFile, string reloadFile, IEnumerable<string> orphanPaths, string? coreSha256 = null)
    {
        // 0.6.5 security review D-1: root used to exec the core straight from the user-writable data folder and re-read the
        // config from there on every reload. Now: the core is copied ONCE into a root-owned folder (root:wheel 755) and
        // checked against the sha256 EMBEDDED in the signed app (TermitusCoreHashes), never a hash computed at run time
        // from a user-writable file; the config is copied by root on every start/reload into the same folder, parsed,
        // sanitised (ConfigValidatorJs) and only that copy is passed to the core. The reload/halt/stop flags carry no data:
        // they can only make root re-validate everything and restart. workDir is ignored (the core runs in RootWorkDir).
        // Without a valid hash nothing is built (fail closed).
        if (appPid <= 1 || coreSha256 == null || !Sha256Hex.IsMatch(coreSha256))
        {
            return null;
        }
        var exe = ShQuote(coreExe);
        var log = ShQuote(logFile);
        var pid = ShQuote(pidFile);
        var sup = ShQuote(supPidFile);
        var stop = ShQuote(stopFile);
        var halt = ShQuote(haltFile);
        var reload = ShQuote(reloadFile);
        var argv = BuildArgv(argvTemplate, RootConfig);
        var cfgSrc = ShQuote(configPath);
        var validator = ShQuote(ConfigValidatorJs);
        var rootCfg = ShQuote(RootConfig)!;
        var rootWork = ShQuote(RootWorkDir)!;
        if (exe == null || log == null || pid == null || sup == null || stop == null || halt == null
            || reload == null || argv == null || cfgSrc == null || validator == null)
        {
            return null;
        }
        var envPart = new StringBuilder();
        foreach (var kv in env)
        {
            if (string.IsNullOrEmpty(kv.Value))
            {
                continue;
            }
            var v = ShQuote(kv.Value);
            if (!EnvName.IsMatch(kv.Key) || v == null)
            {
                return null;
            }
            envPart.Append(kv.Key).Append('=').Append(v).Append(' ');
        }
        var rootExe = ShQuote(RootCoreExe)!;
        var rootDir = ShQuote(RootCoreDir)!;
        var run = (envPart.Length > 0 ? "env " + envPart : "") + rootExe + (argv.Count > 0 ? " " + string.Join(' ', argv) : "");

        var orphanTests = new List<string>();
        foreach (var o in orphanPaths.Append(coreExe).Append(RootCoreExe).Distinct())
        {
            var q = ShQuote(o);
            if (q == null)
            {
                return null;
            }
            orphanTests.Add("[ \"$c\" = " + q + " ]");
        }
        // leftover cores: exact full-path match on the process' executable (ps comm), TERM, 1 s, then KILL.
        // Never a child of the running TER (19.10, 0.6.5): the Xray path is on the list, and TER starts its own
        // non-root Xray right before asking for the TUN core -- the first start used to kill it, and the guard
        // saw «CoreDied» ~3 s after every first connect (reconnect in 7 s).
        var orphanFn = "oc() { ps -axo pid=,ppid=,comm= | while read -r p pp c; do " + (orphanTests.Count > 0 ? "if " + string.Join(" || ", orphanTests) + "; then " : "if false; then ")
                       + "[ \"$p\" != \"$$\" ] && [ \"$pp\" != \"" + appPid + "\" ] && { echo \"ter: stopping a leftover core (pid $p) $1\" >&3; kill $2 $p 2>/dev/null; }; fi; done; }";

        var sb = new StringBuilder();
        // The log is opened ONCE, exclusively (noclobber = O_CREAT|O_EXCL: a planted file or symlink makes it fail closed) and
        // kept as fd 3; root never writes to the log by path again. Per-run file name, chosen by the caller.
        sb.Append("cd / || exit 1; umask 022; C=; rm -f ").Append(log).Append("; set -C; exec 3>").Append(log).Append(" || exit 1; set +C; ");
        sb.Append("rm -f ").Append(sup).Append("; ( set -C; echo $$ >").Append(sup).Append(" ) 2>/dev/null || exit 1; ");
        sb.Append(orphanFn).Append("; ");
        // pin: copy the core into the root-owned folder, verify its sha256 against the value embedded in the app, never read
        // the user's copy again. Dir root:wheel 755, core 755 (nothing a user process can write on the path root executes).
        sb.Append("mkdir -p ").Append(rootDir).Append(' ').Append(rootWork).Append(" && chown root:wheel ").Append(rootDir).Append(' ').Append(rootWork)
          .Append(" && chmod 755 ").Append(rootDir).Append(' ').Append(rootWork)
          .Append(" && rm -f ").Append(rootDir).Append("/core.new && cp ").Append(exe).Append(' ').Append(rootDir).Append("/core.new && chown root:wheel ").Append(rootDir).Append("/core.new && chmod 755 ").Append(rootDir).Append("/core.new")
          .Append(" && [ \"$(/usr/bin/shasum -a 256 ").Append(rootDir).Append("/core.new | /usr/bin/cut -d' ' -f1)\" = ").Append(coreSha256)
          .Append(" ] && mv -f ").Append(rootDir).Append("/core.new ").Append(rootExe)
          .Append(" || { echo \"ter: core checksum mismatch, refusing to run\" >&3; rm -f ").Append(rootDir).Append("/core.new ").Append(sup).Append("; exit 1; }; ");
        // vc = copy the config to a root-owned file, parse + sanitise it there (ConfigValidatorJs), swap it in
        sb.Append("vc() { rm -f ").Append(rootCfg).Append(".new; /usr/bin/osascript -l JavaScript -e ").Append(validator).Append(' ').Append(cfgSrc).Append(' ').Append(rootCfg).Append(".new ").Append(rootWork)
          .Append(" >&3 2>&3 && chown root:wheel ").Append(rootCfg).Append(".new && chmod 644 ").Append(rootCfg).Append(".new && mv -f ").Append(rootCfg).Append(".new ").Append(rootCfg)
          .Append(" || { echo \"ter: config refused (unreadable, not valid JSON or no inbounds/outbounds)\" >&3; rm -f ").Append(rootCfg).Append(".new; return 1; }; }; ");
        // sc = stop our core (TERM, up to 3 s, KILL), forget it
        sb.Append("sc() { [ -n \"$C\" ] || return 0; kill $C 2>/dev/null; n=0; while kill -0 $C 2>/dev/null && [ $n -lt 3 ]; do sleep 1; n=$((n+1)); done; ")
          .Append("kill -9 $C 2>/dev/null; C=; rm -f ").Append(pid).Append("; }; ");
        // st = start it; the pid file is created exclusively (a planted symlink makes it fail)
        sb.Append("st() { rm -f ").Append(pid).Append("; vc || return 1; cd ").Append(rootWork).Append(" || return 1; ").Append(run).Append(" >&3 2>&3 </dev/null & C=$!; ")
          .Append("( set -C; echo $C >").Append(pid).Append(" ) 2>/dev/null || { sc; return 1; }; }; ");
        sb.Append("oc first ''; sleep 1; oc again -9; rm -f ").Append(halt).Append(' ').Append(reload).Append("; st || { rm -f ").Append(sup).Append("; exit 1; }; ");
        sb.Append("while kill -0 ").Append(appPid).Append(" 2>/dev/null && [ ! -e ").Append(stop).Append(" ]; do ");
        sb.Append("if [ -e ").Append(halt).Append(" ]; then rm -f ").Append(halt).Append("; sc; fi; ");
        sb.Append("if [ -e ").Append(reload).Append(" ]; then rm -f ").Append(reload).Append("; sc; st; fi; ");
        sb.Append("if [ -n \"$C\" ] && ! kill -0 $C 2>/dev/null; then C=; rm -f ").Append(pid).Append("; fi; ");
        sb.Append("sleep 1; done; sc; rm -f ").Append(sup).Append("; exec 3>&-");
        return sb.ToString();
    }

    /// <summary>The AppleScript passed to `osascript -e`, with the dialog text. Null = refused.</summary>
    public static string? BuildAppleScript(string supervisor, string prompt)
    {
        var inner = ShQuote(supervisor);
        var p = AppleScriptEscape(prompt);
        if (inner == null || p == null)
        {
            return null;
        }
        // detached: output closed, backgrounded -> `do shell script` returns at once
        var cmd = "/bin/sh -c " + inner + " >/dev/null 2>&1 &";
        var esc = AppleScriptEscape(cmd);
        return esc == null ? null : "do shell script \"" + esc + "\" with administrator privileges with prompt \"" + p + "\"";
    }

    public enum StartMode { Reuse, Authorize }

    /// <summary>A live supervisor with the same command line is reused (no dialog); otherwise ask the OS again.</summary>
    public static StartMode ChooseStart(bool supervisorAlive, string? runningSignature, string wantedSignature)
    {
        return supervisorAlive && runningSignature != null && runningSignature == wantedSignature
            ? StartMode.Reuse
            : StartMode.Authorize;
    }

    /// <summary>After a reload request: the new core is up when the pid file names another live process.</summary>
    public static bool NewCoreReady(int oldPid, int currentPid, bool currentAlive)
    {
        return currentPid > 1 && currentAlive && currentPid != oldPid;
    }

    public enum AuthResult { Ok, Cancelled, Failed }

    /// <summary>osascript exit code + stderr -> outcome. "User canceled. (-128)" = the user closed the dialog.</summary>
    public static AuthResult Classify(int exitCode, string stderr)
    {
        if (exitCode == 0)
        {
            return AuthResult.Ok;
        }
        return stderr.Contains("(-128)", StringComparison.Ordinal)
               || stderr.Contains("User canceled", StringComparison.OrdinalIgnoreCase)
            ? AuthResult.Cancelled
            : AuthResult.Failed;
    }
}
