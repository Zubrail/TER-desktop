using ServiceLib.Services;

namespace ServiceLib.ViewModels;

/// <summary>
/// 0.6.4: «Защищать весь трафик компьютера» (TUN). Privileges only through the OS's own dialog:
///
///  - macOS: no password sheet of ours any more. TUN is switched on and the core is started by
///    TermitusMacCore through the system authorization dialog (Touch ID or password -- the OS
///    asks, TER never sees it). Closing the dialog = «только браузеры» (TUN off, system proxy).
///  - Windows: the TUN core needs administrator rights. Already administrator -> TUN on. Otherwise
///    TER's own sheet explains it, «Продолжить» saves a one-shot flag and restarts TER elevated
///    (UAC / Windows Hello); the elevated start connects by itself. UAC refused -> browsers only.
///
/// Upstream's own password dialog is registered by StatusBarView in the hidden «Для опытных» panel;
/// calling it from the simple UI hung Connect for minutes (0.6.1, LESSONS #1) -- never used here.
/// The decisions live in <see cref="TermitusTunPlan"/> (unit-tested).
/// </summary>
public partial class TermitusHomeViewModel
{
    [Reactive] public partial bool ShowTunAsk { get; set; }
    public ReactiveCommand<RxVoid, RxVoid> TunAskOkCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> TunAskSkipCmd { get; private set; } = null!;

    private TaskCompletionSource<bool>? _tunAskWait;
    private bool _tunBrowsersOnlyThisSession;
    private static readonly TimeSpan TunAskTimeout = TimeSpan.FromMinutes(3);

    private enum TunResult { Ready, Skipped, Exiting }

    private void InitTun()
    {
        TunAskOkCmd = ReactiveCommand.CreateFromTask(() =>
        {
            _tunAskWait?.TrySetResult(true);
            return Task.CompletedTask;
        });
        TunAskSkipCmd = ReactiveCommand.CreateFromTask(() =>
        {
            _tunAskWait?.TrySetResult(false);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Brings TUN up when the user wants all traffic protected. Ready = TUN is on (or not wanted /
    /// not applicable); Skipped = wanted but not allowed, Connect goes on via the system proxy and
    /// says so; Exiting = TER is restarting as administrator, stop connecting here.
    /// Not on a test instance (TUN forced off there).
    /// </summary>
    private async Task<TunResult> EnsureTunAsync(bool reloadNow = false)
    {
        var step = TermitusTunPlan.Decide(TermitusSettings.AllTraffic, TermitusTestHooks.IsTestInstance, Utils.IsMacOS(),
            Utils.IsWindows(), _config.TunModeItem?.EnableTun == true, Utils.IsWindows() && Utils.IsAdministrator(),
            _tunBrowsersOnlyThisSession);
        switch (step)
        {
            case TermitusTunPlan.Step.NotWanted:
            case TermitusTunPlan.Step.AlreadyOn:
                return TunResult.Ready;

            case TermitusTunPlan.Step.BrowsersOnly:
                return TunResult.Skipped;

            case TermitusTunPlan.Step.MacAuthorize:
                // The system dialog appears when the core starts (Reload below / by the caller).
                OnUi(() => SubStatusText = Loc.TunAsking);
                await SetTunOnAsync();
                if (reloadNow)
                {
                    await _main.Reload();
                    return await TunFallbackIfCancelledAsync() ? TunResult.Skipped : TunResult.Ready;
                }
                return TunResult.Ready;

            case TermitusTunPlan.Step.WinEnableAsAdmin:
                await SetTunOnAsync();
                if (reloadNow)
                {
                    await _main.Reload();
                }
                return TunResult.Ready;

            default: // WinAskRestart
                return await AskWindowsRestartAsync();
        }
    }

    private async Task SetTunOnAsync()
    {
        // Config first: upstream's StatusBar toggle then sees "already on" and does not open any dialog.
        _config.TunModeItem.EnableTun = true;
        await ConfigHandler.SaveConfig(_config);
        OnUi(() => _main.StatusBarViewModel.EnableTun = true);
    }

    /// <summary>The user closed the system dialog: TUN off again, system proxy, say so. True = fell back.</summary>
    private async Task<bool> TunFallbackIfCancelledAsync()
    {
        if (!TermitusMacCore.ConsumeCancelled())
        {
            return false;
        }
        Logging.SaveLog($"{_tag}: all-traffic mode skipped (authorization dialog closed) -- system proxy only");
        TermitusErrorLog.Add("разрешение не дано — защищены только браузеры");
        _config.TunModeItem.EnableTun = false;
        await ConfigHandler.SaveConfig(_config);
        OnUi(() => _main.StatusBarViewModel.EnableTun = false);
        PublishSysProxy(ESysProxyType.ForcedChange);
        await _main.Reload();
        return true;
    }

    private async Task<TunResult> AskWindowsRestartAsync()
    {
        var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tunAskWait = wait;
        OnUi(() => ShowTunAsk = true);
        var first = await Task.WhenAny(wait.Task, Task.Delay(TunAskTimeout));
        var proceed = first == wait.Task && wait.Task.Result;
        _tunAskWait = null;
        OnUi(() => ShowTunAsk = false);

        var started = false;
        if (proceed)
        {
            TermitusSettings.ConnectAfterElevation = true;
            started = ProcUtils.RebootAsAdmin();
        }
        switch (TermitusTunPlan.AfterAsk(proceed, started))
        {
            case TermitusTunPlan.AskOutcome.Restarting:
                Logging.SaveLog($"{_tag}: restarting as administrator for all-traffic mode");
                await AppManager.Instance.AppExitAsync(true);
                return TunResult.Exiting;

            case TermitusTunPlan.AskOutcome.Declined:
                TermitusSettings.ConnectAfterElevation = false;
                Logging.SaveLog($"{_tag}: administrator restart declined -- system proxy only");
                TermitusErrorLog.Add("Windows не дал разрешение — защищены только браузеры");
                return TunResult.Skipped;

            default:
                _tunBrowsersOnlyThisSession = true;
                Logging.SaveLog($"{_tag}: all-traffic mode skipped (browsers only) -- system proxy only");
                return TunResult.Skipped;
        }
    }

    /// <summary>Startup: the elevated restart connects by itself, once.</summary>
    private async Task TunStartupAsync()
    {
        try
        {
            if (Utils.IsMacOS())
            {
                TermitusMacCore.LogStaleOnStartup();
            }
            var step = TermitusTunPlan.OnStartup(TermitusSettings.ConnectAfterElevation, Utils.IsAdministrator(), Utils.IsWindows());
            if (step == TermitusTunPlan.StartupStep.Nothing)
            {
                return;
            }
            TermitusSettings.ConnectAfterElevation = false;
            if (step == TermitusTunPlan.StartupStep.ConnectAfterRestart && !_connected && await HasProfilesAsync())
            {
                await Task.Delay(1500); // let the window and the core manager settle
                Logging.SaveLog($"{_tag}: connecting after the administrator restart");
                await ToggleConnectAsync();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>Self-test tour only (test instance): shows the Windows sheet without a Connect.</summary>
    public void TestOpenTunAsk()
    {
        if (!TermitusTestHooks.IsTestInstance)
        {
            return;
        }
        var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tunAskWait = wait;
        _ = wait.Task.ContinueWith(_ => OnUi(() => ShowTunAsk = false));
        ShowTunAsk = true;
    }
}
