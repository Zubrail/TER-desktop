using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using ServiceLib.Services;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Common;

/// <summary>
/// Self-driving UI tour for live checks of a build (TER 0.4.1). Runs ONLY on a test
/// instance (TER_TEST_INSTANCE set, see TermitusTestHooks) — it can log out, which on a
/// real install would remove the user's subscription. Steps come from TER_TEST_TOUR,
/// comma-separated, executed in order on the UI thread:
///   wait:MS          pause
///   pages:N          N rounds of Серверы → Настройки → Поддержка → Аккаунт → Главная,
///                    each logged as "tour-click-to-frame:&lt;page&gt;#round" (command → 2nd frame)
///   nav:PAGE         one page
///   login            «Войти через Telegram»          cancel-login   «Отмена»
///   import           paste TER_TEST_IMPORT_URL and «Подключить подписку»
///   retry-account    «Повторить» on «Аккаунт»        retry-support  «Повторить» on «Поддержка»
///   logout-ask       open the «Выйти» sheet          logout-confirm «Выйти» in the sheet
///   connect          press the big Connect button (other-VPN check runs first)
///   tun-sheet / tun-skip   the 0.6.4 Windows administrator sheet
///   other-vpn-retry / other-vpn-force   the two buttons of the "another VPN" card
///   fake-connected   show the connected look with sample numbers (no real tunnel)
///   pay / pay-start / pay-cancel-wait / pay-close   the payment sheet (0.5)
///   tray-click       the menu-bar icon's real action with no event = left click (toggles the popover)
///   tray-geometry    log where the icon and the popover window really are (AppKit coordinates)
///   tray-menu        build the native right-click NSMenu from the live state (not shown) and log what AppKit holds
///   tray-pick:ID     run a menu action (connect | open | country:<key> | adv:...) 
///   shot-pop:LABEL   PNG of the popover content
///   dump:LABEL       log what the screen shows (flags + status texts)
///   shot:LABEL       save a PNG of the window content to guiLogs/ter-shot-LABEL.png
///   report[:CHIP]    open the problem-report sheet (0.6; chip = connect|slow|sites|drops|other)
///   report-chip:ID / report-text:TEXT / report-attach:0|1 / report-send / report-close / report-run-check / report-copy / report-expand
///   check            open «Проверка подключения» (runs the check)   advice:ACTION  press a suggestion (UseTcp|UseUdp|OtherServer|Report)
///   conn-other       «Попробовать другой сервер» on the connection-error card   conn-dismiss
///   update-check / update-now / update-later / update-open / update-close / autoupdate:0|1   the updater (0.6)
///   scroll-end:PAGE  scroll settings|support|check to the bottom
///   dump2:LABEL      log the 0.6 state (check rows + advice, report stage + preview, error card, updater)
///   Account 2.0 (devices / friends / payments / change link; talks to the fake backend):
///   acc-devices      (re)load the three cards   acc-menu[:ID] open the ⋯ row menu (default: first other device)
///   acc-invite       «Добавить устройство» (pay sheet when past the free limit, otherwise the QR)   acc-invite-pay   «Продолжить» in the pay sheet
///   acc-rename-ask[:ID] / acc-rename:TEXT   the rename sheet / type TEXT and save
///   acc-remove-ask[:ID] / acc-remove        the remove sheet / confirm it   acc-manage[:ID]   give/take managing
///   acc-friends      scroll to the «Друзья» card   acc-reward:money|days   acc-payments   scroll to «Оплаты»   acc-payments-all   expand
///   acc-rotate-ask / acc-rotate   «Сменить ссылку» sheet / confirm   acc-relogin   «Войти заново» (guest card)   acc-close   close the sheet
///   scroll-acct:FRACTION   scroll the account page (0 top .. 1 bottom)   dump3:LABEL   log the Account 2.0 state
///   quit             close the app
/// </summary>
internal static class TermitusSelfTest
{
    private static int _started;
    private static readonly string[] TourPages = ["servers", "settings", "support", "account", "home"];

    public static void StartOnce(TermitusHomeView view, TermitusHomeViewModel vm)
    {
        if (TermitusTestHooks.Tour.IsNullOrEmpty() || Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }
        if (!TermitusTestHooks.IsTestInstance)
        {
            Logging.SaveLog("TER tour: ignored (only on a TER_TEST_INSTANCE)");
            return;
        }
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await RunAsync(view, vm, TermitusTestHooks.Tour!);
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"TER tour: failed ({ex.GetType().Name}: {ex.Message})");
            }
        }, DispatcherPriority.Background);
    }

    private static async Task RunAsync(TermitusHomeView view, TermitusHomeViewModel vm, string tour)
    {
        Log($"start '{tour}'");
        foreach (var raw in tour.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (cmd, arg) = raw.IndexOf(':') is var i and > 0 ? (raw[..i], raw[(i + 1)..]) : (raw, string.Empty);
            Log($"step {raw}");
            switch (cmd)
            {
                case "wait":
                    await Task.Delay(int.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
                    break;

                case "pages":
                    var rounds = arg.Length == 0 ? 1 : int.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                    for (var r = 1; r <= rounds; r++)
                    {
                        foreach (var p in TourPages)
                        {
                            await NavAsync(view, vm, p, r);
                            await Task.Delay(900);
                        }
                    }
                    break;

                case "nav":
                    await NavAsync(view, vm, arg, 0);
                    break;

                case "login":
                    Exec(vm.LoginTelegramCmd);
                    break;

                case "cancel-login":
                    Exec(vm.CancelLoginCmd);
                    break;

                case "import":
                    vm.SubscriptionInput = Environment.GetEnvironmentVariable("TER_TEST_IMPORT_URL") ?? string.Empty;
                    Exec(vm.ImportSubscriptionCmd);
                    break;

                case "retry-account":
                    Exec(vm.RetryAccountCmd);
                    break;

                case "retry-support":
                    Exec(vm.RetrySupportCmd);
                    break;

                case "logout-ask":
                    Exec(vm.LogoutAskCmd);
                    break;

                case "tun-sheet": // 0.6.4: the Windows administrator sheet, no Connect
                    vm.TestOpenTunAsk();
                    await Task.Delay(300);
                    break;

                case "tun-skip":
                    Exec(vm.TunAskSkipCmd);
                    await Task.Delay(400);
                    break;

                case "logout-confirm":
                    Exec(vm.LogoutConfirmCmd);
                    break;

                case "connect":
                    Exec(vm.ConnectToggleCmd);
                    break;

                case "other-vpn-retry":
                    Exec(vm.OtherVpnRetryCmd);
                    break;

                case "other-vpn-force":
                    Exec(vm.OtherVpnForceCmd);
                    break;

                case "fake-connected":
                    vm.DebugFakeConnected();
                    break;

                case "fake-idle": // 0.6.2: connected, ~0 B/s (the idle-graph regression)
                    vm.DebugFakeConnected(idle: true);
                    break;

                case "pay":
                    Exec(vm.PayOpenCmd);
                    break;

                case "pay-start":
                    Exec(vm.PayStartCmd);
                    break;

                case "pay-cancel-wait":
                    Exec(vm.PayCancelWaitCmd);
                    break;

                case "pay-close":
                    Exec(vm.PayCloseCmd);
                    break;

                case "tray-click":
                    TermitusTray.DebugLeftClick();
                    await Task.Delay(700);
                    break;

                case "tray-geometry":
                    Log($"tray {TermitusTray.DebugGeometry()}");
                    break;

                case "theme":
                    vm.SetThemeCmd.Execute(arg).Subscribe(_ => { }, _ => { });
                    await Task.Delay(500);
                    break;

                case "tray-icons":
                    foreach (var st in new[] { "off", "on", "busy", "warn" })
                    {
                        File.WriteAllBytes(Path.Combine(Utils.GetLogPath(), $"ter-trayicon-{st}.png"), TermitusTrayIcon.RenderPng(st));
                    }
                    Log("tray icons written");
                    break;

                case "tray-menu":
                    Log($"tray menu: {TermitusTray.DebugMenuTitles()}");
                    break;

                case "tray-pick":
                    TermitusTray.Execute(arg);
                    break;

                case "shot-pop":
                    if (TermitusTray.Popover is { } pop)
                    {
                        await NextFrameAsync(pop);
                        Shot(pop.Content as Control ?? view, arg, pop.Content as Control);
                    }
                    else
                    {
                        Log("shot-pop: popover is not open");
                    }
                    break;

                case "dump":
                    Dump(vm, arg);
                    break;

                case "dump2":
                    Dump2(vm, arg);
                    break;

                case "dump3":
                    Log($"dump3 {arg}: {vm.DebugAcct2State()}");
                    break;

                case "scroll-acct":
                    view.DebugScrollAccount(double.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
                    await Task.Delay(400);
                    break;

                case "acc-devices":
                    await vm.DebugAcct2Async("load", arg);
                    await Task.Delay(600);
                    break;

                case "acc-friends":
                    view.DebugScrollAccount(0.62);
                    await Task.Delay(400);
                    break;

                case "acc-payments":
                    view.DebugScrollAccount(1);
                    await Task.Delay(400);
                    break;

                case "acc-menu" or "acc-invite" or "acc-invite-pay" or "acc-rename-ask" or "acc-rename" or "acc-remove-ask" or "acc-remove"
                    or "acc-manage" or "acc-reward" or "acc-payments-all" or "acc-rotate-ask" or "acc-rotate" or "acc-relogin" or "acc-close":
                    await vm.DebugAcct2Async(cmd[4..], arg);
                    await Task.Delay(900);
                    break;

                case "report":
                    if (arg.Length > 0)
                    {
                        await vm.OpenReportAsync(arg);
                    }
                    else
                    {
                        Exec(vm.ReportOpenCmd);
                    }
                    await Task.Delay(500);
                    break;

                case "report-chip":
                    vm.ReportPickChipCmd.Execute(arg).Subscribe(_ => { }, _ => { });
                    await Task.Delay(300);
                    break;

                case "report-text":
                    vm.ReportText = arg.Replace("\\n", "\n");
                    await Task.Delay(300);
                    break;

                case "report-attach":
                    vm.ReportAttach = arg != "0";
                    await Task.Delay(300);
                    break;

                case "report-send":
                    Exec(vm.ReportSendCmd);
                    await Task.Delay(1500);
                    break;

                case "report-close":
                    Exec(vm.ReportCloseCmd);
                    await Task.Delay(300);
                    break;

                case "report-run-check":
                    Exec(vm.ReportRunCheckCmd);
                    await Task.Delay(1500);
                    break;

                case "scroll-end":
                    view.DebugScrollEnd(arg);
                    await Task.Delay(400);
                    break;

                case "report-expand":
                    view.DebugExpandReportPreview();
                    await Task.Delay(400);
                    break;

                case "report-copy":
                    Exec(vm.ReportCopyCmd);
                    await Task.Delay(300);
                    break;

                case "check":
                    await NavAsync(view, vm, "check", 0);
                    await Task.Delay(1800);
                    break;

                case "advice":
                    vm.CheckAdviceCmd.Execute(arg).Subscribe(_ => { }, _ => { });
                    await Task.Delay(1200);
                    break;

                case "conn-other":
                    Exec(vm.ConnErrTryOtherCmd);
                    await Task.Delay(1500);
                    break;

                case "conn-dismiss":
                    Exec(vm.ConnErrDismissCmd);
                    break;

                case "update-check":
                    Exec(vm.UpdateCheckCmd);
                    await Task.Delay(1800);
                    break;

                case "update-now":
                    Exec(vm.UpdateNowCmd);
                    await Task.Delay(1500);
                    break;

                case "update-later":
                    Exec(vm.UpdateLaterCmd);
                    break;

                case "update-open":
                    Exec(vm.UpdateOpenCmd);
                    break;

                case "update-close":
                    Exec(vm.UpdateCloseCmd);
                    break;

                case "autoupdate":
                    vm.AutoUpdate = arg != "0";
                    await Task.Delay(300);
                    break;

                case "shot":
                    await NextFrameAsync(view);
                    Shot(view, arg);
                    break;

                case "quit":
                    Log("quit");
                    (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
                    await Task.Delay(5000);
                    Environment.Exit(0);
                    break;

                default:
                    Log($"unknown step '{cmd}'");
                    break;
            }
        }
        Log("done");
    }

    private static void Exec<T>(ReactiveCommand<RxVoid, T> cmd) => cmd.Execute().Subscribe(_ => { }, _ => { });

    /// <summary>Command → the frame after the page's first layout+render; plus how much of it was JIT/GC.</summary>
    private static async Task NavAsync(TermitusHomeView view, TermitusHomeViewModel vm, string page, int round)
    {
        var before = TermitusPerf.Snapshot.Take();
        var start = Stopwatch.GetTimestamp();
        TermitusPerf.Mark($"tour:{page}");
        vm.NavCmd.Execute(page).Subscribe(_ => { }, _ => { });
        await NextFrameAsync(view);
        await NextFrameAsync(view);
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Logging.SaveLog($"TER perf: tour-click-to-frame:{page}#{round} {TermitusPerf.Fmt(ms)} ms | {TermitusPerf.Snapshot.Take().Diff(before)}");
    }

    private static Task NextFrameAsync(Visual view)
    {
        var tcs = new TaskCompletionSource();
        var top = TopLevel.GetTopLevel(view);
        if (top == null)
        {
            tcs.SetResult();
        }
        else
        {
            top.RequestAnimationFrame(_ => tcs.TrySetResult());
        }
        // A hidden window gets no frames: do not hang the tour.
        return Task.WhenAny(tcs.Task, Task.Delay(3000));
    }

    private static void Dump(TermitusHomeViewModel vm, string label)
    {
        Log($"dump {label}: home={vm.ShowHome} firstRun={vm.ShowFirstRun} choice={vm.ShowLoginChoice} waiting={vm.ShowLoginWaiting} "
            + $"link={vm.ShowLinkPaste} tg={vm.CanUseTelegramLogin} hasSub={vm.HasSubscription} status='{vm.StatusText}' "
            + $"firstRunStatus='{vm.FirstRunStatus}' | account: signedIn={vm.AccountSignedIn} offline={vm.AccountOffline} "
            + $"saved={vm.AccountShowingSaved} title='{vm.AccountStatusTitle}' telegram={(vm.HasTelegram ? $"'{vm.AccountTelegram}'" : "-")} "
            + $"balance='{vm.AccountBalance}' refreshing={vm.IsAccountRefreshing} | support: chat={vm.SupportChatVisible} "
            + $"bot={vm.SupportBotVisible} offline={vm.SupportOffline} canType={vm.SupportCanType} loading={vm.IsSupportLoading} "
            + $"empty={vm.SupportEmpty} msgs={vm.SupportMessages.Count} placeholder='{vm.SupportPlaceholder}' | "
            + $"logoutSheet={vm.ShowLogoutConfirm} loggingOut={vm.IsLoggingOut} servers={vm.Locations.Count} | "
            + $"connected={vm.IsConnected} connecting={vm.IsConnecting} otherVpn={vm.ShowOtherVpn} | live: ping='{vm.LivePingText}' "
            + $"down='{vm.LiveDownText}' up='{vm.LiveUpText}' route='{vm.RouteChipText}' proto='{vm.ProtocolChipText}' | "
            + $"pay: sheet={vm.ShowPay} loading={vm.PayLoading} bot={vm.PayBot} offline={vm.PayOffline} error={vm.PayError} "
            + $"ready={vm.PayReady} waiting={vm.PayWaiting} done={vm.PayDone} problem={vm.PayProblem}('{vm.PayProblemText}') "
            + $"plans={vm.PayPlans.Count} methods={vm.PayMethods.Count} offer={vm.PayHasOffer}");
    }

    private static void Dump2(TermitusHomeViewModel vm, string label)
    {
        Log($"dump2 {label}: check: running={vm.CheckRunning} results={vm.CheckHasResults} rows={vm.CheckRows.Count} [{string.Join(" | ", vm.CheckRows.Select(r => $"{r.Mark} {r.Title} {r.Protocol} {r.MsText}: {r.Explain}"))}] "
            + $"advice=[{string.Join(" | ", vm.CheckAdvice.Select(a => a.ActionId + ": " + a.Text))}] | connError={vm.ShowConnError} '{vm.ConnErrTitle}' failed='{vm.ConnErrFailed}' works='{vm.ConnErrWorks}' | "
            + $"report: sheet={vm.ShowReport} editing={vm.ReportEditing} sending={vm.ReportSending} sent={vm.ReportSent} offline={vm.ReportOffline} bot={vm.ReportBot} "
            + $"canSend={vm.ReportCanSend} attach={vm.ReportAttach} hasCheck={vm.ReportHasCheck} error='{vm.ReportError}' chips=[{string.Join(",", vm.ReportChips.Where(c => c.IsSelected).Select(c => c.Id))}] "
            + $"| update: banner={vm.ShowUpdateBanner} '{vm.UpdateBannerText}' sheet={vm.ShowUpdateSheet} avail={vm.UpdateAvailableStage} busy={vm.UpdateBusyStage} manual={vm.UpdateManualStage} failed={vm.UpdateFailedStage} "
            + $"status='{vm.UpdateStatusText}' check='{vm.UpdateCheckResult}' auto={vm.AutoUpdate} protocol={TermitusSettings.Protocol}");
        // The preview is multi-line: one log line per preview line so the log stays greppable.
        if (vm.ShowReport)
        {
            foreach (var l in vm.ReportPreview.Split('\n'))
            {
                Log($"dump2 {label} preview> {l}");
            }
        }
    }

    private static void Shot(Visual view, string label, Visual? target = null)
    {
        try
        {
            view = target ?? view;
            var size = (view as Control)?.Bounds.Size ?? default;
            if (size.Width < 1 || size.Height < 1)
            {
                Log($"shot {label}: view has no size (window hidden?)");
                return;
            }
            const double scale = 1; // 2x double-applies the scale inside ScrollViewers (RenderTargetBitmap quirk)
            using var rtb = new RenderTargetBitmap(
                new PixelSize((int)(size.Width * scale), (int)(size.Height * scale)), new Vector(96 * scale, 96 * scale));
            rtb.Render(view);
            var safe = new string(label.Where(char.IsAsciiLetterOrDigit).ToArray());
            var path = Path.Combine(Utils.GetLogPath(), $"ter-shot-{(safe.Length == 0 ? "x" : safe)}.png");
#pragma warning disable CS0618 // test-only PNG; the new overload needs encoder options we do not care about
            rtb.Save(path);
#pragma warning restore CS0618
            Log($"shot {label} -> {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log($"shot {label} failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static void Log(string msg) => Logging.SaveLog($"TER tour: {msg}");
}
