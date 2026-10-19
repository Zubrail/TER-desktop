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
        string haltFile, string reloadFile, IEnumerable<string> orphanPaths)
    {
        if (appPid <= 1)
        {
            return null;
        }
        var wd = ShQuote(workDir);
        var exe = ShQuote(coreExe);
        var log = ShQuote(logFile);
        var pid = ShQuote(pidFile);
        var sup = ShQuote(supPidFile);
        var stop = ShQuote(stopFile);
        var halt = ShQuote(haltFile);
        var reload = ShQuote(reloadFile);
        var argv = BuildArgv(argvTemplate, configPath);
        if (wd == null || exe == null || log == null || pid == null || sup == null || stop == null || halt == null
            || reload == null || argv == null)
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
        var run = (envPart.Length > 0 ? "env " + envPart : "") + exe + (argv.Count > 0 ? " " + string.Join(' ', argv) : "");

        var orphanTests = new List<string>();
        foreach (var o in orphanPaths.Append(coreExe).Distinct())
        {
            var q = ShQuote(o);
            if (q == null)
            {
                return null;
            }
            orphanTests.Add("[ \"$c\" = " + q + " ]");
        }
        // leftover cores: exact full-path match on the process' executable (ps comm), TERM, 1 s, then KILL
        var orphanFn = "oc() { ps -axo pid=,comm= | while read -r p c; do " + (orphanTests.Count > 0 ? "if " + string.Join(" || ", orphanTests) + "; then " : "if false; then ")
                       + "[ \"$p\" != \"$$\" ] && { echo \"ter: stopping a leftover core (pid $p) $1\" >&3; kill $2 $p 2>/dev/null; }; fi; done; }";

        var sb = new StringBuilder();
        // The log is opened ONCE, exclusively (noclobber = O_CREAT|O_EXCL: a planted file or symlink makes it fail closed) and
        // kept as fd 3; root never writes to the log by path again. Per-run file name, chosen by the caller.
        sb.Append("cd ").Append(wd).Append(" || exit 1; umask 022; C=; rm -f ").Append(log).Append("; set -C; exec 3>").Append(log).Append(" || exit 1; set +C; ");
        sb.Append("rm -f ").Append(sup).Append("; ( set -C; echo $$ >").Append(sup).Append(" ) 2>/dev/null || exit 1; ");
        sb.Append(orphanFn).Append("; ");
        // sc = stop our core (TERM, up to 3 s, KILL), forget it
        sb.Append("sc() { [ -n \"$C\" ] || return 0; kill $C 2>/dev/null; n=0; while kill -0 $C 2>/dev/null && [ $n -lt 3 ]; do sleep 1; n=$((n+1)); done; ")
          .Append("kill -9 $C 2>/dev/null; C=; rm -f ").Append(pid).Append("; }; ");
        // st = start it; the pid file is created exclusively (a planted symlink makes it fail)
        sb.Append("st() { rm -f ").Append(pid).Append("; ").Append(run).Append(" >&3 2>&3 </dev/null & C=$!; ")
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
