using System.Diagnostics;
using ServiceLib.Services;

namespace ServiceLib.ViewModels;

/// <summary>
/// 0.6.2: «Защищать весь трафик компьютера» (TUN) with TER's OWN admin-password sheet.
///
/// Why not upstream's: its password dialog is registered by StatusBarView, which lives in
/// the hidden «Для опытных» panel and is never activated in the simple UI -- 0.6.1 called
/// it anyway, the interaction had no handler ("Failed to find a registration for an
/// Interaction" in DoEnableTun) and Connect waited 3 minutes on «Ищем самый быстрый сервер…»
/// (owner's Mac, 13.10). Here TER asks itself, checks the password with
/// `sudo -S -k -v` (stdin only, never argv/log), stores it where upstream expects it
/// (AppManager.LinuxSudoPwd, memory only) and only then flips EnableTun -- upstream's
/// AllowEnableTun() is true at that point, so it saves the config and restarts the core
/// without any dialog of its own.
///
/// «Только браузеры» = this run keeps the system proxy; the sheet is not shown again until
/// the user presses Connect themselves (auto-reconnects never pop it up).
/// </summary>
public partial class TermitusHomeViewModel
{
    [Reactive] public partial bool ShowTunPassword { get; set; }
    [Reactive] public partial string TunPasswordText { get; set; } = string.Empty;
    [Reactive] public partial string TunPasswordError { get; set; } = string.Empty;
    [Reactive] public partial bool TunPasswordChecking { get; set; }
    public ReactiveCommand<RxVoid, RxVoid> TunPasswordOkCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> TunPasswordSkipCmd { get; private set; } = null!;

    private TaskCompletionSource<string?>? _tunPwdWait;
    private static readonly TimeSpan TunPasswordTimeout = TimeSpan.FromMinutes(3);

    private void InitTun()
    {
        TunPasswordOkCmd = ReactiveCommand.CreateFromTask(() => Bg(CheckTunPasswordAsync));
        TunPasswordSkipCmd = ReactiveCommand.CreateFromTask(() =>
        {
            _tunPwdWait?.TrySetResult(null);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Brings TUN up when the user wants all traffic protected. True = TUN is on (or not
    /// wanted / not applicable); false = wanted but no password, Connect goes on via the
    /// system proxy and says so. Not on a test instance (TUN forced off there) and not on
    /// Windows yet (upstream would restart the app as administrator).
    /// </summary>
    private async Task<bool> EnsureTunAsync()
    {
        if (!TermitusSettings.AllTraffic || TermitusTestHooks.IsTestInstance || !Utils.IsMacOS()
            || _config.TunModeItem?.EnableTun == true)
        {
            return true;
        }

        if (AppManager.Instance.LinuxSudoPwd.IsNullOrEmpty())
        {
            var wait = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _tunPwdWait = wait;
            OnUi(() =>
            {
                TunPasswordText = string.Empty;
                TunPasswordError = string.Empty;
                TunPasswordChecking = false;
                SubStatusText = Loc.TunAsking;
                ShowTunPassword = true;
            });
            var first = await Task.WhenAny(wait.Task, Task.Delay(TunPasswordTimeout));
            var password = first == wait.Task ? wait.Task.Result : null;
            _tunPwdWait = null;
            OnUi(() =>
            {
                ShowTunPassword = false;
                TunPasswordText = string.Empty; // do not keep it in the view model
            });
            if (password.IsNullOrEmpty())
            {
                Logging.SaveLog($"{_tag}: all-traffic mode skipped (no admin password) -- system proxy only");
                TermitusErrorLog.Add("пароль администратора не введён — защищены только браузеры");
                return false;
            }
            AppManager.Instance.LinuxSudoPwd = password;
        }

        // Password known -> upstream's AllowEnableTun() is true: DoEnableTun saves the config
        // and reloads the core itself, no dialog.
        OnUi(() => _main.StatusBarViewModel.EnableTun = true);
        var until = DateTime.Now.AddSeconds(5);
        while (_config.TunModeItem?.EnableTun != true && DateTime.Now < until)
        {
            await Task.Delay(100);
        }
        var on = _config.TunModeItem?.EnableTun == true;
        if (!on)
        {
            Logging.SaveLog($"{_tag}: EnableTun did not stick after the password -- system proxy only");
        }
        return on;
    }

    private async Task CheckTunPasswordAsync()
    {
        var password = TunPasswordText;
        if (password.IsNullOrEmpty() || _tunPwdWait == null)
        {
            return;
        }
        OnUi(() =>
        {
            TunPasswordChecking = true;
            TunPasswordError = string.Empty;
        });
        var verdict = await VerifyAdminPasswordAsync(password);
        OnUi(() => TunPasswordChecking = false);
        switch (verdict)
        {
            case AdminCheck.Ok:
                _tunPwdWait?.TrySetResult(password);
                break;
            case AdminCheck.NotAdmin:
                OnUi(() => TunPasswordError = Loc.TunPwdNotAdmin);
                break;
            default:
                OnUi(() =>
                {
                    TunPasswordError = Loc.TunPwdWrong;
                    TunPasswordText = string.Empty;
                });
                break;
        }
    }

    /// <summary>Self-test tour only (test instance): shows the sheet without a Connect, so its
    /// look, the wrong-password path and «Только браузеры» can be checked live.</summary>
    public void TestOpenTunPassword()
    {
        if (!TermitusTestHooks.IsTestInstance)
        {
            return;
        }
        var wait = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tunPwdWait = wait;
        _ = wait.Task.ContinueWith(_ => OnUi(() => ShowTunPassword = false));
        TunPasswordText = string.Empty;
        TunPasswordError = string.Empty;
        ShowTunPassword = true;
    }

    private enum AdminCheck { Ok, Wrong, NotAdmin }

    /// <summary>
    /// `sudo -S -k -v -p ""`: -k forgets any cached ticket so the password really is checked,
    /// -v only validates (runs nothing), the password goes through stdin. Exit 0 = correct
    /// password of an administrator account.
    /// </summary>
    private static async Task<AdminCheck> VerifyAdminPasswordAsync(string password)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/sudo")
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-S", "-k", "-v", "-p", "" })
            {
                psi.ArgumentList.Add(a);
            }
            psi.Environment["LC_ALL"] = "C"; // stable English stderr for the not-admin check
            using var p = Process.Start(psi);
            if (p == null)
            {
                return AdminCheck.Wrong;
            }
            await p.StandardInput.WriteLineAsync(password);
            p.StandardInput.Close();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string err;
            try
            {
                err = await p.StandardError.ReadToEndAsync(cts.Token);
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch { /* already gone */ }
                return AdminCheck.Wrong;
            }
            if (p.ExitCode == 0)
            {
                return AdminCheck.Ok;
            }
            return err.Contains("sudoers", StringComparison.OrdinalIgnoreCase)
                   || err.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
                ? AdminCheck.NotAdmin
                : AdminCheck.Wrong;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TermitusHome: admin password check failed", ex);
            return AdminCheck.Wrong;
        }
    }
}
