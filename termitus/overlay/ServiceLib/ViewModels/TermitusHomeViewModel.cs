using System.Collections.ObjectModel;
using ServiceLib.Services;

namespace ServiceLib.ViewModels;

/// <summary>
/// View model for the TER design-D home screen (one big Connect button, a single
/// location row, a subscription chip, first-run "paste your link").
///
/// It reuses the upstream engine and adds no protocol logic of its own:
///  - import  = ConfigHandler.AddSubItem + MainWindowViewModel.UpdateSubscriptionProcess
///  - "Auto · fastest" = upstream SpeedtestService (Realping, the same test as
///    "Test real delay" in the server list) over all imported servers; the
///    lowest positive delay becomes the default server (ConfigHandler.SetDefaultServerIndex)
///  - connect = system proxy on (AppEvents.SysProxyChangeRequested) + MainWindowViewModel.Reload()
///    (or the user's TUN mode, if they enabled it under "Advanced")
///  - disconnect = system proxy off + CoreManager.CoreStop()
///  - RU routing is created offline at startup by TermitusRussiaPreset.
///
/// State rule (fixed in 0.1.1): the screen is "first run" exactly when there is
/// no imported server at all. It is re-evaluated on startup, after every import
/// and whenever the user comes back from the advanced UI. All reactive
/// properties are set on the UI thread.
/// </summary>
public partial class TermitusHomeViewModel : MyReactiveObject
{
    private const string BotUrl = "https://t.me/TErpiUsis_bot";
    private const string FaqUrl = "https://termitus.online/#faq";
    private const string _tag = "TermitusHome";
    private readonly MainWindowViewModel _main;
    private System.Threading.Timer? _sessionTimer;
    private DateTime _connectedAt;

    // Source of truth for logic (reactive properties below only mirror these on the UI thread).
    private volatile bool _connected;
    private volatile bool _busy;

    // Last real-delay results of OUR auto-select run: indexId -> ms (>0 = responded).
    private readonly ConcurrentDictionary<string, int> _delays = new();

    // Connection guard (0.3): auto-reconnect on core death / network change + kill switch.
    // Decisions come from TermitusReconnectPolicy (pure, unit-tested); this VM only acts.
    private readonly TermitusReconnectPolicy _policy = new();
    private System.Threading.Timer? _guardTimer;
    private int _guardTickRunning;
    private volatile bool _recovering;
    private volatile bool _interrupted;        // kill switch holding the internet closed
    private DateTime? _reloadBusySince;
    // 0.6.6: sleep/wake. A separate 2 s timer measures its own gap (the guard tick can be busy for a long time).
    private readonly TermitusWakeDetector _wake = new();
    private System.Threading.Timer? _wakeTimer;
    private int _wakeSeen;
    private volatile bool _noConn;             // core alive, automatic fixes used up: «Нет соединения — нажмите…»
    private volatile string? _guardTitle;      // loud state shown instead of the normal status
    private volatile string? _guardHint;
    private static readonly TimeSpan GuardInterval = TimeSpan.FromSeconds(2);

    // Account page (0.4): data from the subscription response headers + optional /info.
    private System.Threading.Timer? _accountTimer;
    private int _accountRefreshRunning;
    private DateTime _accountLastManual = DateTime.MinValue;
    private volatile string? _renewUrl;
    private volatile bool _accountOffline;     // last refresh got no HTTP answer at all (0.4.1)
    private static readonly TimeSpan AccountStaleAfter = TimeSpan.FromMinutes(10);
    private volatile string? _topupUrl;
    private static readonly TimeSpan AccountRefreshEvery = TimeSpan.FromHours(6);

    // Sign in with Telegram (0.4). Codes/URLs are never logged.
    private CancellationTokenSource? _loginCts;
    private System.Threading.Timer? _loginCountdown;
    private volatile bool _loginUnavailable;   // endpoint missing: link field only, for this run

    // Support chat (0.4). One 10 s timer: polls every tick while the page is open,
    // every 2 min in the background (unread dot), every 30 min if the endpoint is missing.
    private readonly object _chatLock = new();
    private List<TermitusChatMessage> _chat = [];
    private TermitusSupportState? _supportState;
    private volatile bool _supportFullLoaded;
    private volatile TermitusSupportAvailability _supportAvail = TermitusSupportAvailability.Unknown;
    private System.Threading.Timer? _supportTimer;
    private int _supportTickRunning;
    private DateTime _supportLastPoll = DateTime.MinValue;
    private int _localSeq;
    private volatile bool _supportOffline;     // last poll got no usable answer (0.4.1)
    private volatile bool _supportCanType;
    private volatile bool _loggingOut;
    private static readonly TimeSpan SupportPageEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SupportBackgroundEvery = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SupportMissingEvery = TimeSpan.FromMinutes(30);

    /// <summary>
    /// False while the window is hidden/minimized (set by the view): the 1 s session
    /// timer and the 10 s chat poll then stay quiet.
    /// </summary>
    public volatile bool UiActive = true;

    public TermitusLocale Loc => TermitusLocale.Current;

    /// <summary>Raised when the user wants the full v2rayN ("Advanced"/Account) surface.</summary>
    public EventChannel<RxVoid> ShowAdvancedRequested { get; } = new();

    #region Reactive state

    [Reactive] public partial bool HasSubscription { get; set; }
    [Reactive] public partial bool ShowFirstRun { get; set; }
    [Reactive] public partial bool IsConnected { get; set; }
    [Reactive] public partial bool IsConnecting { get; set; }
    [Reactive] public partial bool IsBusy { get; set; }

    [Reactive] public partial string StatusText { get; set; }
    [Reactive] public partial bool StatusWarn { get; set; }
    [Reactive] public partial string SubStatusText { get; set; }
    [Reactive] public partial string LocationName { get; set; }
    [Reactive] public partial string LocationBadge { get; set; }
    [Reactive] public partial bool HasLocationBadge { get; set; }
    [Reactive] public partial string LocationDetail { get; set; }
    [Reactive] public partial string SubChipText { get; set; }
    [Reactive] public partial bool SubChipWarn { get; set; }
    [Reactive] public partial bool SubChipExpired { get; set; }

    // Pages of the simple UI (sidebar). ShowPicker == the "Серверы" page.
    [Reactive] public partial bool ShowPicker { get; set; }
    [Reactive] public partial bool ShowHome { get; set; }
    [Reactive] public partial bool ShowSettings { get; set; }
    [Reactive] public partial bool ShowSupport { get; set; }
    [Reactive] public partial bool ShowAccount { get; set; }

    // Account page + expiry banner on the home screen.
    [Reactive] public partial string AccountStatusTitle { get; set; }
    [Reactive] public partial string AccountStatusDetail { get; set; }
    [Reactive] public partial bool AccountOk { get; set; }
    [Reactive] public partial bool AccountWarn { get; set; }
    [Reactive] public partial bool AccountBad { get; set; }
    [Reactive] public partial string AccountTraffic { get; set; }
    [Reactive] public partial bool HasAccountTraffic { get; set; }
    [Reactive] public partial string AccountPlan { get; set; }
    [Reactive] public partial bool HasAccountPlan { get; set; }
    [Reactive] public partial string AccountDevices { get; set; }
    [Reactive] public partial bool HasAccountDevices { get; set; }
    [Reactive] public partial bool HasDeviceList { get; set; }
    [Reactive] public partial string AccountUpdated { get; set; }
    [Reactive] public partial bool IsAccountRefreshing { get; set; }
    [Reactive] public partial bool ShowExpiryBanner { get; set; }
    [Reactive] public partial string ExpiryBannerText { get; set; }
    public ObservableCollection<TermitusDeviceRow> AccountDeviceList { get; } = [];

    // Settings page (each one is a plain upstream setting, explained in words).
    [Reactive] public partial bool AutoRun { get; set; }
    [Reactive] public partial bool AllTraffic { get; set; }
    [Reactive] public partial bool KillSwitch { get; set; }
    [Reactive] public partial bool RouteExceptRu { get; set; }
    [Reactive] public partial bool RouteBlockedOnly { get; set; }
    [Reactive] public partial bool RouteAll { get; set; }
    [Reactive] public partial string AppVersion { get; set; }
    [Reactive] public partial bool IsTesting { get; set; }
    [Reactive] public partial bool IsAutoChoice { get; set; }
    [Reactive] public partial string AutoCaption { get; set; }
    public ObservableCollection<TermitusLocationRow> Locations { get; } = [];
    public IReadOnlyList<TermitusKbItem> KnowledgeBase { get; } = TermitusKbItem.All;

    [Reactive] public partial string SubscriptionInput { get; set; }
    [Reactive] public partial string FirstRunStatus { get; set; }

    // First run (0.4): choice (Telegram / link) -> waiting for the bot -> import.
    [Reactive] public partial bool ShowLoginChoice { get; set; }
    [Reactive] public partial bool ShowLoginWaiting { get; set; }
    [Reactive] public partial bool ShowLinkPaste { get; set; }
    [Reactive] public partial bool CanUseTelegramLogin { get; set; }
    [Reactive] public partial bool IsLoginStarting { get; set; }
    [Reactive] public partial bool IsLoginImporting { get; set; }
    [Reactive] public partial string LoginWaitingTitle { get; set; }
    [Reactive] public partial string LoginCountdown { get; set; }

    // Account balance (0.4)
    [Reactive] public partial string AccountBalance { get; set; }
    [Reactive] public partial bool HasBalance { get; set; }
    [Reactive] public partial bool HasTopup { get; set; }

    // Support chat (0.4)
    [Reactive] public partial bool SupportChatVisible { get; set; }
    [Reactive] public partial bool SupportBotVisible { get; set; }
    [Reactive] public partial bool SupportGuest { get; set; }
    [Reactive] public partial bool IsSupportLoading { get; set; }
    [Reactive] public partial bool SupportEmpty { get; set; }
    [Reactive] public partial string SupportInput { get; set; }
    [Reactive] public partial bool IsSupportSending { get; set; }
    [Reactive] public partial string SupportError { get; set; }
    [Reactive] public partial bool HasSupportError { get; set; }
    [Reactive] public partial bool HasSupportUnread { get; set; }
    public ObservableCollection<TermitusChatRow> SupportMessages { get; } = [];

    // 0.4.1: no-connection states, Telegram account, signed-out page, log out
    [Reactive] public partial bool AccountOffline { get; set; }
    [Reactive] public partial bool AccountShowingSaved { get; set; }
    [Reactive] public partial bool AccountSignedIn { get; set; }
    [Reactive] public partial string AccountTelegram { get; set; }
    [Reactive] public partial bool HasTelegram { get; set; }
    [Reactive] public partial bool SupportOffline { get; set; }
    [Reactive] public partial bool SupportCanType { get; set; }
    [Reactive] public partial bool SupportSendEnabled { get; set; }
    [Reactive] public partial string SupportPlaceholder { get; set; }
    [Reactive] public partial bool ShowLogoutConfirm { get; set; }
    [Reactive] public partial bool IsLoggingOut { get; set; }
    [Reactive] public partial bool ShowOtherVpn { get; set; }
    [Reactive] public partial string OtherVpnText { get; set; }

    #endregion Reactive state

    public ReactiveCommand<RxVoid, RxVoid> ConnectToggleCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> OtherVpnRetryCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> OtherVpnForceCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ImportSubscriptionCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AccountCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> AdvancedCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> RenewCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenPickerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ClosePickerCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> RetestCmd { get; }
    public ReactiveCommand<string, RxVoid> SelectLocationCmd { get; }
    public ReactiveCommand<string, RxVoid> NavCmd { get; }
    public ReactiveCommand<string, RxVoid> SetRoutingCmd { get; }
    public ReactiveCommand<string, RxVoid> SetProtocolCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenBotCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenFaqCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> RefreshAccountCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> TopupCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> LoginTelegramCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelLoginCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> LoginOpenAgainCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ShowLinkCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> ShowLoginCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> SendSupportCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> RetryAccountCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> RetrySupportCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> SignInCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> LogoutAskCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> LogoutCancelCmd { get; }
    public ReactiveCommand<RxVoid, RxVoid> LogoutConfirmCmd { get; }
    private volatile string? _loginBotUrl;
    private bool _settingsLoading;

    // Picker choice: a location key ("🇫🇷 Франция 2") or TermitusLocations.AutoKey.
    private string _choice = TermitusLocations.AutoKey;
    private List<TermitusLocations.Location> _locations = [];

    public TermitusHomeViewModel(MainWindowViewModel main)
    {
        _main = main;
        _config = AppManager.Instance.Config;

        StatusText = Loc.TapToConnect;
        SubStatusText = string.Empty;
        LocationName = Loc.LocationAuto;
        LocationBadge = AutoBadge;
        LocationDetail = Loc.Auto;
        SubscriptionInput = string.Empty;
        FirstRunStatus = string.Empty;
        SubChipText = Loc.SubUnknown;
        AccountStatusTitle = Loc.StatusUnknown;
        AccountStatusDetail = string.Empty;
        AccountTraffic = string.Empty;
        AccountPlan = string.Empty;
        AccountDevices = string.Empty;
        AccountUpdated = string.Empty;
        ExpiryBannerText = string.Empty;
        ShowFirstRun = true;
        AutoCaption = Loc.AutoCaption;
        ShowHome = true;
        AppVersion = $"TER {Utils.GetVersionInfo()}";
        IsAutoChoice = true;
        ShowLoginChoice = true;
        CanUseTelegramLogin = true;
        LoginWaitingTitle = Loc.LoginWaiting;
        LoginCountdown = string.Empty;
        AccountBalance = string.Empty;
        SupportInput = string.Empty;
        SupportError = string.Empty;
        SupportBotVisible = true;
        AccountTelegram = string.Empty;
        SupportPlaceholder = Loc.ChatPlaceholder;

        // Command bodies run on the thread pool (Bg): ReactiveCommand starts them on the
        // UI thread and every await continuation came back there, so DB reads, file IO,
        // JSON and list building between awaits ran on the UI thread (0.4 smoothness pass).
        ConnectToggleCmd = ReactiveCommand.CreateFromTask(() => Bg(ToggleConnectAsync));
        OtherVpnText = string.Empty;
        OtherVpnRetryCmd = ReactiveCommand.CreateFromTask(() => Bg(async () =>
        {
            // "Check again": the user turned the other VPN off -> connect right away.
            OnUi(() => ShowOtherVpn = false);
            await ToggleConnectAsync();
        }));
        OtherVpnForceCmd = ReactiveCommand.CreateFromTask(() => Bg(async () =>
        {
            _skipOtherVpnCheck = true;
            OnUi(() => ShowOtherVpn = false);
            await ToggleConnectAsync();
        }));
        ImportSubscriptionCmd = ReactiveCommand.CreateFromTask(() => Bg(ImportSubscriptionAsync));
        LoginTelegramCmd = ReactiveCommand.CreateFromTask(() => Bg(LoginWithTelegramAsync));
        CancelLoginCmd = ReactiveCommand.CreateFromTask(() =>
        {
            try
            {
                _loginCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // the login just finished on its own
            }
            return Task.CompletedTask;
        });
        LoginOpenAgainCmd = ReactiveCommand.CreateFromTask(() =>
        {
            if (_loginBotUrl is { } u)
            {
                TermitusHttp.OpenUrl(u);
            }
            return Task.CompletedTask;
        });
        ShowLinkCmd = ReactiveCommand.CreateFromTask(() =>
        {
            SetFirstRunMode(link: true);
            return Task.CompletedTask;
        });
        ShowLoginCmd = ReactiveCommand.CreateFromTask(() =>
        {
            SetFirstRunMode(link: false);
            return Task.CompletedTask;
        });
        SendSupportCmd = ReactiveCommand.CreateFromTask(() => Bg(SendSupportAsync));
        TopupCmd = ReactiveCommand.CreateFromTask(() =>
        {
            // topup_url from /info only if it is one of our https links (TermitusAccount.IsSafeRenewUrl).
            if (_topupUrl is { } u)
            {
                TermitusHttp.OpenUrl(u);
            }
            return Task.CompletedTask;
        });
        AccountCmd = ReactiveCommand.CreateFromTask(async () => await NavigateAsync("account"));
        AdvancedCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            ShowAdvanced();
            await Task.CompletedTask;
        });
        InitPayments();
        InitLive();
        InitCheck();
        InitReport();
        InitUpdate();
        InitTun();
        InitAccount2();
        InitTrial();
        // 0.5: «Продлить» opens the in-app pay sheet (plans + methods + offer). When the backend has no
        // payment API yet, the sheet itself says "оплата пока в Telegram-боте" with the bot button.
        RenewCmd = PayOpenCmd;
        RefreshAccountCmd = ReactiveCommand.CreateFromTask(() => Bg(async () =>
        {
            var acct2 = Acct2LoadAsync(force: true); // Account 2.0 blocks: only when the page opens and here, never on a timer
            await RefreshAccountAsync(manual: true);
            await acct2;
        }));
        // «Повторить» after "no connection": not throttled like «Обновить» (one request in flight at most).
        RetryAccountCmd = ReactiveCommand.CreateFromTask(() => Bg(async () =>
        {
            var acct2 = Acct2LoadAsync(force: true);
            await RefreshAccountAsync(manual: true, retry: true);
            await acct2;
        }));
        RetrySupportCmd = ReactiveCommand.CreateFromTask(() => Bg(() => SupportOnOpenAsync(retry: true)));
        SignInCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            SetFirstRunMode(link: false);
            await NavigateAsync("home");
        });
        LogoutAskCmd = ReactiveCommand.CreateFromTask(() =>
        {
            ShowLogoutConfirm = true;
            return Task.CompletedTask;
        });
        LogoutCancelCmd = ReactiveCommand.CreateFromTask(() =>
        {
            _reloginAfterLogout = false;
            ShowLogoutConfirm = false;
            return Task.CompletedTask;
        });
        LogoutConfirmCmd = ReactiveCommand.CreateFromTask(() => Bg(LogoutAsync));
        this.WhenAnyValue(x => x.SupportCanType, x => x.IsSupportSending, (can, sending) => can && !sending)
            .Subscribe(v => SupportSendEnabled = v);

        OpenPickerCmd = ReactiveCommand.CreateFromTask(async () => await NavigateAsync("servers"));
        ClosePickerCmd = ReactiveCommand.CreateFromTask(async () => await NavigateAsync("home"));
        NavCmd = ReactiveCommand.CreateFromTask<string>(NavigateAsync);
        SetRoutingCmd = ReactiveCommand.CreateFromTask<string>(mode => Bg(() => SetRoutingAsync(mode)));
        SetProtocolCmd = ReactiveCommand.CreateFromTask<string>(p => Bg(() => SetProtocolAsync(p)));
        OpenBotCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            TermitusHttp.OpenUrl(BotUrl);
            await Task.CompletedTask;
        });
        OpenFaqCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            TermitusHttp.OpenUrl(FaqUrl);
            await Task.CompletedTask;
        });

        this.WhenAnyValue(x => x.AutoRun).Skip(1).Subscribe(v => _ = Bg(() => SetAutoRunAsync(v)));
        this.WhenAnyValue(x => x.AllTraffic).Skip(1).Subscribe(v =>
        {
            if (_settingsLoading)
            {
                return;
            }
            _ = Bg(() =>
            {
                TermitusSettings.AllTraffic = v;
                return Task.CompletedTask;
            });
            if (!v)
            {
                if (_main.StatusBarViewModel.EnableTun)
                {
                    _main.StatusBarViewModel.EnableTun = false; // no password needed to turn it off
                }
                return;
            }
            // On while connected: the system dialog (Mac) / TER's own sheet (Windows) now (upstream's
            // dialog is not available in the simple UI); otherwise the next Connect does it.
            if (_connected)
            {
                _ = Bg(async () =>
                {
                    if (await EnsureTunAsync(reloadNow: true) == TunResult.Skipped)
                    {
                        OnUi(() => SubStatusText = Loc.TunSkipped);
                    }
                });
            }
        });
        this.WhenAnyValue(x => x.KillSwitch).Skip(1).Subscribe(v =>
        {
            var loading = _settingsLoading; // read on the UI thread, where LoadSettings sets it
            if (!loading)
            {
                _ = Bg(() => SetKillSwitchAsync(v));
            }
        });
        RetestCmd = ReactiveCommand.CreateFromTask(() => Bg(RetestAsync));
        SelectLocationCmd = ReactiveCommand.CreateFromTask<string>(key => Bg(() => SelectLocationAsync(key)));

        TermitusAccount.Changed += () => _ = RenderAccountAsync();

        _ = Bg(Init);
        _supportTimer = new System.Threading.Timer(_ => _ = SupportTickAsync(), null, TimeSpan.FromSeconds(25), SupportPageEvery);
        // Account data: once shortly after start, then every 6 h (one header-only GET of
        // the subscription + one GET of /info each time).
        _accountTimer = new System.Threading.Timer(_ => _ = RefreshAccountAsync(manual: false), null, TimeSpan.FromSeconds(15), AccountRefreshEvery);
        // First tick late: at launch upstream itself (re)starts the core.
        _guardTimer = new System.Threading.Timer(_ => _ = GuardTickAsync(), null, TimeSpan.FromSeconds(10), GuardInterval);
        _wakeTimer = new System.Threading.Timer(_ =>
        {
            if (_wake.Observe(DateTime.UtcNow))
            {
                Volatile.Write(ref _wakeSeen, 1);
            }
        }, null, GuardInterval, GuardInterval);
    }

    private async Task Init()
    {
        try
        {
            _choice = TermitusLocations.LoadChoice();
            var auto = _choice == TermitusLocations.AutoKey;
            OnUi(() => IsAutoChoice = auto);
            var hasProfiles = await HasProfilesAsync();

            // Security defaults (SECURITY.md): no access log, loopback-only inbound.
            await TermitusSecurity.ApplyHardenedDefaults(_config, !hasProfiles);

            // Upstream re-applies the saved system-proxy mode (and starts the core)
            // on launch, so "connected" survives a restart exactly when it was on.
            _connected = hasProfiles
                && (_config.SystemProxyItem?.SysProxyType == ESysProxyType.ForcedChange
                    || _config.TunModeItem?.EnableTun == true);
            if (_connected)
            {
                _connectedAt = DateTime.Now;
            }

            await RefreshStateAsync();
            _ = TunStartupAsync();
            _ = Task.Delay(2500).ContinueWith(_ => AskAboutCrashAsync()); // 0.6.5: a crash file from the last run -> ask, never send by itself
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    /// <summary>
    /// Re-evaluates first-run vs connect screen, location row and chip from the
    /// real data (profiles in the DB, current default server). Safe to call any time.
    /// </summary>
    public async Task RefreshStateAsync()
    {
        using var perf = TermitusPerf.Measure("refresh-state");
        var hasProfiles = await HasProfilesAsync();
        if (hasProfiles && Locations.Count == 0)
        {
            // 0.5: the menu-bar menu / popover list countries even if the Servers page was never opened.
            await BuildRowsAsync();
        }
        var server = hasProfiles ? await ConfigHandler.GetDefaultServer(_config) : null;
        var connected = _connected;
        var busy = _busy || _recovering;
        var interrupted = _interrupted;
        var guardTitle = _guardTitle;
        var guardHint = _guardHint;

        OnUi(() =>
        {
            HasSubscription = hasProfiles;
            ShowFirstRun = !hasProfiles;
            UpdateLocation(server);

            IsConnected = connected && !interrupted;
            if (!busy)
            {
                if (guardTitle != null)
                {
                    // Loud state (kill switch holding / VPN dropped): never looks "fine".
                    StatusWarn = true;
                    StatusText = guardTitle;
                    SubStatusText = guardHint ?? string.Empty;
                }
                else
                {
                    StatusWarn = false;
                    StatusText = connected ? Loc.Connected : Loc.TapToConnect;
                    if (!connected)
                    {
                        SubStatusText = string.Empty;
                    }
                }
            }
        });

        await RenderAccountAsync();
        UpdateLiveForState(connected && !interrupted);

        if (connected && !interrupted && guardTitle == null)
        {
            StartSessionTimer();
        }
        else
        {
            StopSessionTimer();
        }
    }

    #region Connect

    private async Task ToggleConnectAsync()
    {
        if (_busy)
        {
            return;
        }

        try
        {
            if (_connected && _noConn)
            {
                await ManualReconnectAsync(); // «Нет соединения — нажмите, чтобы переподключить»
            }
            else if (_connected)
            {
                await DisconnectAsync();
            }
            else
            {
                await ConnectAsync();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            SetBusy(false);
            OnUi(() => IsConnecting = false);
            await RaiseConnErrorAsync(Loc.ErrTitleConnect, "ошибка при включении: " + ex.GetType().Name);
            await RefreshStateAsync();
        }
    }

    /// <summary>
    /// 0.6: a throw-away test instance (TER_TEST_INSTANCE) never touches the system proxy — it would change the
    /// real TER's / the owner's network settings. Everything else goes through upstream's event as before.
    /// </summary>
    private static volatile bool _testProxyOn;

    private static void PublishSysProxy(ESysProxyType type)
    {
        if (TermitusTestHooks.IsTestInstance)
        {
            Logging.SaveLog($"{_tag}: test instance: system proxy {type} suppressed");
            _testProxyOn = type == ESysProxyType.ForcedChange; // so the guard sees «connected» (live checks of the guard)
            return;
        }
        AppEvents.SysProxyChangeRequested.Publish(type);
    }

    private bool _skipOtherVpnCheck;

    private async Task ConnectAsync()
    {
        if (!await HasProfilesAsync())
        {
            // Nothing imported yet — back to the first-run panel.
            await RefreshStateAsync();
            return;
        }

        // 0.5: another VPN already owns the default route -> say so calmly instead of
        // connecting into a conflict (local routing-table read, nothing is sent).
        var force = _skipOtherVpnCheck;
        _skipOtherVpnCheck = false;
        if (!force)
        {
            var others = await TermitusOtherVpn.DetectAsync();
            if (others.Count > 0)
            {
                // count only: on Windows these are adapter names the user may have typed (no-logs policy, 19.10)
                Logging.SaveLog($"{_tag}: another VPN holds the default route ({others.Count} adapter(s))");
                TermitusErrorLog.Add("включён другой VPN (занят маршрут по умолчанию)");
                OnUi(() =>
                {
                    OtherVpnText = Loc.OtherVpnText;
                    ShowOtherVpn = true;
                });
                return;
            }
        }
        OnUi(() => ShowOtherVpn = false);

        SetBusy(true);
        ResetGuard();
        OnUi(() =>
        {
            ShowConnError = false;
            IsConnecting = true;
            StatusWarn = false;
            StatusText = Loc.Connecting;
            SubStatusText = string.Empty;
        });

        // Make sure the default server is one that actually answered. If we have
        // no fresh result for it (first connect after start/import), test all.
        var server = await ConfigHandler.GetDefaultServer(_config);
        if (server == null || !_delays.TryGetValue(server.IndexId, out var ms) || ms <= 0)
        {
            OnUi(() => StatusText = Loc.FindingBestServer);
            await TestAllAsync();
            server = await ApplyChoiceAsync();
            if (server == null)
            {
                OnUi(() =>
                {
                    IsConnecting = false;
                    StatusText = Loc.TapToConnect;
                    SubStatusText = string.Empty; // the error card below says it
                });
                SetBusy(false);
                await RaiseConnErrorAsync(Loc.ErrTitleNoServer, "ни один сервер не ответил на проверку");
                return;
            }
        }

        var tun = await EnsureTunAsync();
        if (tun == TunResult.Exiting)
        {
            return; // restarting as administrator
        }
        var tunSkipped = tun == TunResult.Skipped;

        // System proxy (no admin rights needed). If the user switched TUN on in
        // the advanced UI (with its sudo prompt), Reload() brings TUN up instead.
        // Kill switch: the system proxy is set in TUN mode too, so proxy-aware apps
        // (browsers) also fail closed if sing-box itself dies (TUN gone = routes gone).
        if (_config.TunModeItem?.EnableTun != true || TermitusSettings.KillSwitch)
        {
            PublishSysProxy(ESysProxyType.ForcedChange);
        }
        // 0.6.5 Windows: a core file removed by the antivirus gets a clear card, not a silent failure
        if (TermitusCoreFiles.MissingOnWindows(_config.TunModeItem?.EnableTun == true) is { Count: > 0 } blocked)
        {
            PublishSysProxy(ESysProxyType.ForcedClear);
            OnUi(() =>
            {
                IsConnecting = false;
                StatusText = Loc.TapToConnect;
                SubStatusText = string.Empty;
            });
            SetBusy(false);
            await RaiseCoreBlockedAsync(blocked);
            return;
        }
        await _main.Reload();
        if (await TunFallbackIfCancelledAsync())
        {
            tunSkipped = true; // the Mac authorization dialog was closed while the core started
        }

        _connected = true;
        _connectedAt = DateTime.Now;
        OnUi(() => IsConnecting = false);
        SetBusy(false);
        await RefreshStateAsync();
        if (tunSkipped)
        {
            OnUi(() => SubStatusText = Loc.TunSkipped);
        }
    }

    private async Task DisconnectAsync()
    {
        SetBusy(true);
        ResetGuard();
        PublishSysProxy(ESysProxyType.ForcedClear);
        await CoreManager.Instance.CoreStop();
        _connected = false;
        SetBusy(false);
        await RefreshStateAsync();
    }

    /// <summary>
    /// Runs v2rayN's real-delay test (through each server, like "Test real delay"
    /// in the server list) over every imported server; results land in _delays.
    /// </summary>
    private async Task TestAllAsync()
    {
        var profiles = await AppManager.Instance.ProfileItems("") ?? [];
        profiles = profiles.Where(p => p.ConfigType != EConfigType.Custom).ToList();
        _locations = TermitusLocations.Group(profiles.Select(p => (p.IndexId, (string?)p.Remarks)));
        NoteTransportKinds(profiles);
        if (profiles.Count == 0)
        {
            return;
        }

        _delays.Clear();
        if (TermitusTestHooks.IsTestInstance && !TermitusTestHooks.ProbeSpec.IsNullOrEmpty())
        {
            // Test instance only (TER_TEST_PROBE): simulated results instead of real probes.
            var rules = TermitusConnCheck.ParseSpec(TermitusTestHooks.ProbeSpec);
            foreach (var p in profiles)
            {
                var kind = p.ConfigType == EConfigType.Hysteria2 ? TermitusProbeKind.Udp : TermitusProbeKind.Tcp;
                _delays[p.IndexId] = TermitusConnCheck.ApplySpec(rules, TermitusLocations.KeyOf(p.Remarks), kind, 120);
            }
            await Task.Delay(400);
            return;
        }
        var service = new SpeedtestService(_config, async result =>
        {
            if (result.IndexId.IsNotEmpty() && int.TryParse(result.Delay, out var delay))
            {
                _delays[result.IndexId] = delay;
            }
            // Mirror results into the advanced server list.
            if (result.IndexId.IsNotEmpty())
            {
                RxSchedulers.MainThreadScheduler.Schedule(() => _ = _main.ProfilesViewModel.SetSpeedTestResult(result));
            }
            await Task.CompletedTask;
        });

        try
        {
            await service.RunLoop(ESpeedActionType.Realping, profiles);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        Logging.SaveLog($"{_tag}: real delay results {_delays.Count(kv => kv.Value > 0)}/{profiles.Count} ok");
    }

    /// <summary>
    /// Makes the server for the user's choice (a location, or Auto = fastest
    /// foreign location, never Russia) the default. Returns it, or
    /// null when nothing in that choice answered.
    /// </summary>
    private async Task<ProfileItem?> ApplyChoiceAsync()
    {
        if (_locations.Count == 0)
        {
            var profiles = await AppManager.Instance.ProfileItems("") ?? [];
            _locations = TermitusLocations.Group(profiles
                .Where(p => p.ConfigType != EConfigType.Custom)
                .Select(p => (p.IndexId, (string?)p.Remarks)));
            NoteTransportKinds(profiles);
        }
        var best = TermitusLocations.Pick(_choice, _locations, _delays, PreferredTransport());
        Logging.SaveLog($"{_tag}: choice='{_choice}' -> {best}");
        if (best.IsNullOrEmpty())
        {
            return null;
        }

        if (_config.IndexId != best)
        {
            await ConfigHandler.SetDefaultServerIndex(_config, best);
        }
        return await AppManager.Instance.GetProfileItem(best);
    }

    #endregion Connect

    #region Connection guard (auto-reconnect + kill switch)

    private void ResetGuard()
    {
        _policy.Reset();
        _interrupted = false;
        _noConn = false;
        _guardTitle = null;
        _guardHint = null;
    }

    private async Task SetKillSwitchAsync(bool on)
    {
        if (_settingsLoading || TermitusSettings.KillSwitch == on)
        {
            return;
        }
        TermitusSettings.KillSwitch = on;
        Logging.SaveLog($"{_tag}: kill switch {(on ? "on" : "off")}");
        // Already connected in TUN mode: add the system-proxy layer right away.
        if (on && _connected && _config.TunModeItem?.EnableTun == true)
        {
            PublishSysProxy(ESysProxyType.ForcedChange);
        }
        // Switched off while it is holding the internet closed: release it (loudly).
        if (!on && _interrupted)
        {
            await GiveUpAsync(new(TermitusGuardAction.GiveUp, TermitusTrigger.CoreDied, _policy.Attempts, false));
        }
    }

    /// <summary>Every 2 s: observe core + network, ask the policy, act. Never re-entrant.</summary>
    private async Task GuardTickAsync()
    {
        if (Interlocked.Exchange(ref _guardTickRunning, 1) == 1)
        {
            return;
        }
        try
        {
            var now = DateTime.UtcNow;
            var fingerprint = TermitusNetwork.Current();
            _policy.ObserveNetwork(fingerprint, now);
            if (Interlocked.Exchange(ref _wakeSeen, 0) == 1)
            {
                Logging.SaveLog($"{_tag}: guard: the Mac woke up (timer gap)");
                _policy.ObserveWake(now);
            }

            if (!_connected || _busy || _recovering)
            {
                return;
            }

            // An upstream Reload is in flight (core is being stopped/started): wait,
            // but not forever (a stuck reload must not disable the guard).
            if (!_main.BlReloadEnabled)
            {
                _reloadBusySince ??= now;
                if (now - _reloadBusySince.Value < TimeSpan.FromSeconds(60))
                {
                    return;
                }
            }
            else
            {
                _reloadBusySince = null;
            }

            // Switched off outside the home screen (tray menu / advanced UI): respect it,
            // never restart a core the user stopped.
            if (_config.SystemProxyItem?.SysProxyType != ESysProxyType.ForcedChange
                && _config.TunModeItem?.EnableTun != true
                && !(TermitusTestHooks.IsTestInstance && _testProxyOn))
            {
                _connected = false;
                ResetGuard();
                await RefreshStateAsync();
                return;
            }

            var alive = CoreManager.Instance.TermitusCoreAlive() == true;
            if (alive && _interrupted)
            {
                // Core is back (slow retry or an upstream reload): lift the loud state.
                await OnRestoredAsync();
            }

            var decision = _policy.Next(alive, TermitusNetwork.IsUp(fingerprint), now);
            switch (decision.Action)
            {
                case TermitusGuardAction.Recover:
                    await RecoverAsync(decision);
                    break;

                case TermitusGuardAction.CheckHealth:
                    await CheckHealthAsync(decision);
                    break;

                case TermitusGuardAction.NoConnection:
                    await NoConnectionAsync();
                    break;

                case TermitusGuardAction.GiveUp:
                    await GiveUpAsync(decision);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        finally
        {
            Volatile.Write(ref _guardTickRunning, 0);
        }
    }

    private IReadOnlyList<string>? _coreBlockedFiles;

    private async Task RecoverAsync(TermitusGuardDecision d)
    {
        // 0.6.5 Windows: the core died because its file is gone (antivirus) -> say so instead of retrying in vain
        if (TermitusCoreFiles.MissingOnWindows(_config.TunModeItem?.EnableTun == true) is { Count: > 0 } gone)
        {
            _coreBlockedFiles = gone;
            await GiveUpAsync(new(TermitusGuardAction.GiveUp, TermitusTrigger.CoreDied, _policy.Attempts, false));
            _coreBlockedFiles = null;
            return;
        }
        _recovering = true;
        Logging.SaveLog($"{_tag}: guard {d.Trigger} attempt {d.Attempt} repick={d.Repick}");
        TermitusErrorLog.Add(d.Trigger switch
        {
            TermitusTrigger.NetworkChanged => "сеть сменилась, переподключаемся",
            TermitusTrigger.TunnelDead => d.Repick ? "туннель не отвечает, ищем другой сервер" : "туннель не отвечает, перезапускаем ядро",
            _ => "ядро остановилось, переподключаемся",
        });
        var reason = d.Trigger switch
        {
            TermitusTrigger.NetworkChanged => Loc.ReasonNetwork,
            TermitusTrigger.TunnelDead => d.Repick ? Loc.ReasonTunnel : Loc.ReasonTunnelRestart,
            _ => Loc.ReasonCoreDied,
        };
        OnUi(() =>
        {
            IsConnecting = true;
            StatusWarn = false;
            StatusText = Loc.Reconnecting;
            SubStatusText = reason;
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var before = _config.IndexId;
        try
        {
            if (d.Repick)
            {
                // Same choice rules as Connect: chosen location's fastest transport,
                // or Auto = fastest foreign location, never Russia.
                await TestAllAsync();
                var picked = await ApplyChoiceAsync(); // null = nothing answered: keep the current server
                // The server that just failed through the live tunnel is still "the best" -> take the next working one
                // (always on fix 3; on fix 2 only when its own test did not answer either).
                var stillBest = picked == null || picked.IndexId == before;
                var answers = before.IsNotEmpty() && _delays.TryGetValue(before, out var ms) && ms > 0;
                if (stillBest && (!answers || d.Attempt >= 3) && PickAnotherServer(before) is { } other)
                {
                    await ConfigHandler.SetDefaultServerIndex(_config, other);
                }
            }

            await _main.Reload();
            await Task.Delay(500);
            if (!_connected)
            {
                // The user pressed "Disconnect" while we were reconnecting: honour it.
                await CoreManager.Instance.CoreStop();
                return;
            }

            var ok = CoreManager.Instance.TermitusCoreAlive() == true;
            if (ok && d.Trigger != TermitusTrigger.CoreDied)
            {
                // After a network change / wake the core can be alive but useless (server not
                // reachable from the new network): check that something passes through.
                ok = await ProbeTunnelAsync();
                _policy.ReportProbe(ok);
            }
            Logging.SaveLog($"{_tag}: guard attempt {d.Attempt} -> {(ok ? "ok" : "failed")} in {watch.ElapsedMilliseconds} ms");

            if (ok)
            {
                if (_config.TunModeItem?.EnableTun != true && _config.SystemProxyItem?.SysProxyType == ESysProxyType.ForcedChange)
                {
                    PublishSysProxy(ESysProxyType.ForcedChange); // a new network service may have come without our proxy
                }
                if (_interrupted || _guardTitle != null)
                {
                    await OnRestoredAsync();
                }
                if (_config.IndexId != before && d.Trigger != TermitusTrigger.CoreDied)
                {
                    await AnnounceSwitchAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        finally
        {
            _recovering = false;
            OnUi(() => IsConnecting = false);
            await RefreshStateAsync();
        }
    }


    /// <summary>
    /// Wake / network change with a live core: one quiet probe through the tunnel. Nothing is restarted,
    /// shown or logged beyond one line when it is fine; a failure is reported to the policy, which decides the fix.
    /// </summary>
    private async Task CheckHealthAsync(TermitusGuardDecision d)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ok = await ProbeTunnelAsync();
        Logging.SaveLog($"{_tag}: guard {d.Trigger} probe -> {(ok ? "ok" : "failed")} in {watch.ElapsedMilliseconds} ms");
        _policy.ReportProbe(ok);
        if (!ok)
        {
            TermitusErrorLog.Add(d.Trigger == TermitusTrigger.Woke ? "после сна туннель не отвечает" : "после смены сети туннель не отвечает");
            return;
        }
        if (_config.TunModeItem?.EnableTun != true && _config.SystemProxyItem?.SysProxyType == ESysProxyType.ForcedChange)
        {
            PublishSysProxy(ESysProxyType.ForcedChange);
        }
        if (_interrupted || _guardTitle != null)
        {
            await OnRestoredAsync();
        }
    }

    /// <summary>Two tries 1.5 s apart (right after wake DNS / routes may need a moment) through the local core.</summary>
    private async Task<bool> ProbeTunnelAsync()
    {
        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        var url = _config.SpeedTestItem?.SpeedPingTestUrl ?? Global.SpeedPingTestUrls.First();
        for (var i = 0; i < 2; i++)
        {
            if (await TermitusNetwork.ProbeThroughCoreAsync(port, url, TimeSpan.FromSeconds(5)))
            {
                return true;
            }
            if (i == 0)
            {
                await Task.Delay(1500);
            }
        }
        return false;
    }

    /// <summary>The fastest server that answered its last test, not the failed one; another country first, Russia last.</summary>
    private string? PickAnotherServer(string? current)
    {
        var all = _locations.SelectMany(l => l.IndexIds.Select(id => (l, id))).ToList();
        var curLoc = all.FirstOrDefault(x => x.id == current).l;
        var preferred = PreferredTransport();
        return all.Where(x => x.id != current && _delays.TryGetValue(x.id, out var ms) && ms > 0)
            .OrderBy(x => x.l.IsRussia ? 2 : x.l == curLoc ? 1 : 0)
            .ThenBy(x => preferred != null && !preferred(x.id) ? 1 : 0)
            .ThenBy(x => _delays[x.id])
            .Select(x => x.id)
            .FirstOrDefault();
    }

    private async Task AnnounceSwitchAsync()
    {
        var now = _config.IndexId;
        var loc = _locations.FirstOrDefault(l => l.IndexIds.Contains(now));
        if (loc == null)
        {
            return;
        }
        var text = Loc.SwitchedAfterNetwork(TermitusLocations.DisplayParts(loc.Key).Name);
        Logging.SaveLog($"{_tag}: guard switched server after a network change");
        NoticeManager.Instance.Enqueue(text);
        OnUi(() => SubStatusText = text);
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => OnUi(() =>
        {
            if (SubStatusText == text)
            {
                SubStatusText = string.Empty;
            }
        }));
        await Task.CompletedTask;
    }

    /// <summary>Core alive, 3 automatic fixes did not help: tell it straight and wait for a click (never a loop).</summary>
    private async Task NoConnectionAsync()
    {
        Logging.SaveLog($"{_tag}: guard: no connection after {TermitusReconnectPolicy.MaxAliveAttempts} automatic fixes; waiting for the user");
        TermitusErrorLog.Add("нет соединения после трёх автоматических попыток");
        _noConn = true;
        _guardTitle = Loc.NoConnectionClick;
        _guardHint = Loc.NoConnectionClickHint;
        NoticeManager.Instance.Enqueue(Loc.NoConnectionClick);
        await RefreshStateAsync();
    }

    /// <summary>The click on «Нет соединения — нажмите, чтобы переподключить»: a fresh round, switching the server.</summary>
    private async Task ManualReconnectAsync()
    {
        Logging.SaveLog($"{_tag}: manual reconnect from the no-connection state");
        ResetGuard();
        await RecoverAsync(new(TermitusGuardAction.Recover, TermitusTrigger.TunnelDead, 1, true));
    }

    private async Task OnRestoredAsync()
    {
        _noConn = false;
        OnUi(() => ShowConnError = false);
        var wasLoud = _interrupted || _guardTitle != null;
        _interrupted = false;
        _guardTitle = null;
        _guardHint = null;
        if (wasLoud)
        {
            NoticeManager.Instance.Enqueue(Loc.Restored);
        }
        await RefreshStateAsync();
    }

    /// <summary>
    /// Fast retries are used up and the core is still dead. Never fail open silently:
    ///  - kill switch ON: keep the system proxy pointing at the dead local port, so
    ///    proxy-aware apps stay offline; slow retries continue (policy);
    ///  - kill switch OFF: give the user the normal internet back, but loudly.
    /// Either way the window comes to the front.
    /// </summary>
    private async Task GiveUpAsync(TermitusGuardDecision d)
    {
        Logging.SaveLog($"{_tag}: guard gave up after {d.Attempt} attempts ({d.Trigger}), killSwitch={TermitusSettings.KillSwitch}");
        if (TermitusSettings.KillSwitch)
        {
            _interrupted = true;
            _guardTitle = Loc.KillSwitchBlocked;
            _guardHint = Loc.KillSwitchBlockedHint;
            if (_config.SystemProxyItem?.SysProxyType != ESysProxyType.ForcedChange)
            {
                PublishSysProxy(ESysProxyType.ForcedChange);
            }
        }
        else
        {
            PublishSysProxy(ESysProxyType.ForcedClear);
            await CoreManager.Instance.CoreStop();
            _connected = false;
            _interrupted = false;
            _guardTitle = Loc.DroppedUnprotected;
            _guardHint = Loc.DroppedUnprotectedHint;
        }

        NoticeManager.Instance.Enqueue(_guardTitle);
        _main.StatusBarViewModel.ShowHideWindowRequested.Publish(true);
        if (_coreBlockedFiles is { Count: > 0 } cb)
        {
            await RaiseCoreBlockedAsync(cb);
        }
        else
        {
            await RaiseConnErrorAsync(TermitusSettings.KillSwitch ? Loc.ErrTitleDroppedBlocked : Loc.ErrTitleDropped,
                $"переподключение не удалось ({d.Attempt} попыток)");
        }
        await RefreshStateAsync();
    }

    #endregion Connection guard (auto-reconnect + kill switch)

    #region Subscription import

    private async Task ImportSubscriptionAsync()
    {
        if (_busy)
        {
            return;
        }

        var url = SubscriptionInput?.Trim() ?? string.Empty;
        if (url.IsNullOrEmpty() || Utils.TryUri(url) == null)
        {
            NoticeManager.Instance.Enqueue(Loc.ImportFailed);
            return;
        }
        await ImportUrlAsync(url);
    }

    /// <summary>
    /// The one import path (pasted link and «Войти через Telegram»): add the subscription,
    /// download it, then pick the fastest answering server. Never logs the URL.
    /// Returns true when servers were imported.
    /// </summary>
    private async Task<bool> ImportUrlAsync(string url, bool fromLogin = false)
    {
        if (_busy)
        {
            return false;
        }
        SetBusy(true);
        OnUi(() => FirstRunStatus = Loc.Importing);

        try
        {
            var ret = await ConfigHandler.AddSubItem(_config, url);
            if (ret != 0)
            {
                NoticeManager.Instance.Enqueue(Loc.ImportFailed);
                OnUi(() => FirstRunStatus = Loc.ImportFailed);
                return false;
            }

            // Download + parse the subscription (base64 list of all our transports).
            // Direct (blProxy=false): nothing is connected yet on first run.
            await _main.UpdateSubscriptionProcess("", false);

            // Right after «Войти через Telegram» the backend provisions keys in the
            // background: the subscription can be empty for 1–2 min. Ask again.
            foreach (var wait in fromLogin ? TermitusLogin.EmptySubscriptionRetries : Array.Empty<TimeSpan>())
            {
                if (await HasProfilesAsync())
                {
                    break;
                }
                OnUi(() =>
                {
                    FirstRunStatus = Loc.PreparingServers;
                    LoginWaitingTitle = Loc.PreparingServers;
                });
                await Task.Delay(wait);
                await _main.UpdateSubscriptionProcess("", false);
            }

            if (!await HasProfilesAsync())
            {
                NoticeManager.Instance.Enqueue(Loc.ImportNoServers);
                OnUi(() => FirstRunStatus = Loc.ImportNoServers);
                return false;
            }

            OnUi(() =>
            {
                SubscriptionInput = string.Empty;
                FirstRunStatus = string.Empty;
            });

            // Switch to the connect screen right away, then pick the fastest
            // server that actually answers (the first server in the list may be
            // blocked from the user's network). Connect stays one click.
            await RefreshStateAsync();
            OnUi(() => StatusText = Loc.FindingBestServer);
            await TestAllAsync();
            await ApplyChoiceAsync();
            if (!_delays.Values.Any(v => v > 0))
            {
                OnUi(() => SubStatusText = Loc.NoServerResponds);
            }
            _ = RefreshAccountAsync(manual: false); // account page + support chat for the new subscription
            return true;
        }
        catch (Exception ex)
        {
            // Type only: upstream exception texts can carry the URL.
            Logging.SaveLog($"{_tag}: import failed ({ex.GetType().Name})");
            NoticeManager.Instance.Enqueue(Loc.ImportFailed);
            OnUi(() => FirstRunStatus = Loc.ImportFailed);
            return false;
        }
        finally
        {
            SetBusy(false);
            await RefreshStateAsync();
        }
    }

    #endregion Subscription import

    #region Sign in with Telegram

    private void SetFirstRunMode(bool link, bool waiting = false)
    {
        var tg = !_loginUnavailable;
        OnUi(() =>
        {
            CanUseTelegramLogin = tg;
            ShowLoginWaiting = waiting;
            ShowLinkPaste = !waiting && (link || !tg);
            ShowLoginChoice = !waiting && !link && tg;
        });
    }

    /// <summary>
    /// POST /app/login/start -> open the bot -> poll until confirmed / expired / cancelled
    /// -> import the subscription URL through the normal import path. Missing endpoint
    /// (404, junk) = the feature hides itself and the link field is shown.
    /// </summary>
    private async Task LoginWithTelegramAsync()
    {
        if (_busy || _loginCts != null)
        {
            return;
        }
        using var cts = new CancellationTokenSource();
        _loginCts = cts;
        OnUi(() =>
        {
            IsLoginStarting = true;
            FirstRunStatus = string.Empty;
        });
        TermitusPollOutcome outcome = new(TermitusPollKind.Failed);
        try
        {
            var start = await TermitusLogin.StartAsync(cts.Token);
            OnUi(() => IsLoginStarting = false);
            if (start.Kind == TermitusLoginStartKind.Unavailable)
            {
                _loginUnavailable = true;
                SetFirstRunMode(link: true);
                OnUi(() => FirstRunStatus = Loc.LoginUnavailable);
                return;
            }
            if (start.Kind == TermitusLoginStartKind.Offline || start.Session == null)
            {
                OnUi(() => FirstRunStatus = Loc.LoginOffline);
                return;
            }

            var session = start.Session;
            _loginBotUrl = session.BotUrl;
            TermitusHttp.OpenUrl(session.BotUrl);
            OnUi(() =>
            {
                LoginWaitingTitle = Loc.LoginWaiting;
                IsLoginImporting = false;
            });
            SetFirstRunMode(link: false, waiting: true);
            StartLoginCountdown(session);

            outcome = await TermitusLogin.WaitForConfirmationAsync(
                session,
                ct => TermitusLogin.PollAsync(session, ct),
                Task.Delay,
                () => DateTimeOffset.UtcNow,
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = new(TermitusPollKind.Failed);
            StopLoginCountdown();
            SetFirstRunMode(link: false);
            OnUi(() =>
            {
                IsLoginStarting = false;
                FirstRunStatus = string.Empty;
            });
            return;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: login failed ({ex.GetType().Name})");
            outcome = new(TermitusPollKind.Failed);
        }
        finally
        {
            StopLoginCountdown();
            _loginCts = null;
            _loginBotUrl = null;
            OnUi(() => IsLoginStarting = false);
        }

        if (outcome.Kind == TermitusPollKind.Ok && outcome.SubscriptionUrl is { } url)
        {
            // Confirmed: bring TER to the front (the user is in Telegram) and import.
            _main.StatusBarViewModel.ShowHideWindowRequested.Publish(true);
            OnUi(() =>
            {
                LoginWaitingTitle = Loc.Importing;
                LoginCountdown = string.Empty;
                IsLoginImporting = true;
            });
            var ok = await ImportUrlAsync(url, fromLogin: true);
            OnUi(() => IsLoginImporting = false);
            if (!ok)
            {
                SetFirstRunMode(link: false);
            }
            return;
        }

        var message = outcome.Kind switch
        {
            TermitusPollKind.Expired => Loc.LoginExpired,
            TermitusPollKind.Declined => Loc.LoginDeclined,
            TermitusPollKind.Used => Loc.LoginUsed,
            _ => Loc.LoginFailed,
        };
        SetFirstRunMode(link: false);
        OnUi(() => FirstRunStatus = message);
    }

    private void StartLoginCountdown(TermitusLoginSession session)
    {
        StopLoginCountdown();
        _loginCountdown = new System.Threading.Timer(_ =>
        {
            var text = Loc.LoginTimeLeft(TermitusLogin.FormatCountdown(session.SecondsLeft(DateTimeOffset.UtcNow)));
            OnUi(() => LoginCountdown = text);
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void StopLoginCountdown()
    {
        _loginCountdown?.Dispose();
        _loginCountdown = null;
    }

    #endregion Sign in with Telegram

    #region Navigation and settings

    /// <summary>
    /// Switches the page at once and loads its data in the background. The
    /// sidebar shares one ReactiveCommand, which stays disabled while it runs:
    /// awaiting the loaders here (the first «Серверы» visit runs a real-delay
    /// test of every server, several seconds) froze every sidebar button.
    /// </summary>
    private Task NavigateAsync(string page)
    {
        TermitusPerf.Mark($"nav:{page}");
        if (TermitusTestHooks.IsTestInstance)
        {
            // Test instance only: who navigated (method names, no data).
            var callers = string.Join(" < ", new StackTrace(1, false).GetFrames().Take(6).Select(f => f.GetMethod()?.Name ?? "?"));
            Logging.SaveLog($"TER nav: {page} ({callers})");
        }
        ShowHome = page == "home";
        ShowPicker = page == "servers";
        ShowSettings = page == "settings";
        ShowSupport = page == "support";
        ShowAccount = page == "account";
        ShowCheck = page == "check";

        Func<Task>? load = page switch
        {
            "servers" => OpenPickerAsync,
            "settings" => LoadSettingsAsync,
            "home" => async () =>
            {
                TickSession();
                await RefreshStateAsync();
            },
            "account" => AccountOnOpenAsync,
            "support" => () => SupportOnOpenAsync(retry: false),
            "check" => RunCheckAsync,
            _ => null,
        };
        if (load != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var perf = TermitusPerf.Measure($"load:{page}");
                    await load();
                }
                catch (Exception ex)
                {
                    Logging.SaveLog(_tag, ex);
                }
            });
        }
        return Task.CompletedTask;
    }

    private async Task LoadSettingsAsync()
    {
        var routing = await ConfigHandler.GetDefaultRouting(_config);
        var name = routing?.Remarks ?? string.Empty;
        var killSwitch = TermitusSettings.KillSwitch; // file read: here, not on the UI thread
        var allTraffic = TermitusSettings.AllTraffic;
        OnUi(() =>
        {
            _settingsLoading = true;
            AutoRun = _config.GuiItem.AutoRun;
            // 0.6.1: the user's lasting choice, not upstream's EnableTun (reset on every launch).
            AllTraffic = allTraffic;
            KillSwitch = killSwitch;
            RouteExceptRu = name.EndsWith(TermitusRussiaPreset.DefaultRemarks, StringComparison.Ordinal) || name.Length == 0;
            RouteBlockedOnly = name.EndsWith("Заблокированное", StringComparison.Ordinal);
            RouteAll = name.EndsWith("-Всё", StringComparison.Ordinal);
            _settingsLoading = false;
        });
    }

    private async Task SetAutoRunAsync(bool on)
    {
        if (_settingsLoading || _config.GuiItem.AutoRun == on)
        {
            return;
        }
        _config.GuiItem.AutoRun = on;
        await ConfigHandler.SaveConfig(_config);
        await AutoStartupHandler.UpdateTask(_config);
    }

    /// <summary>"except_ru" | "blocked" | "all" -> the matching RUv1 routing preset.</summary>
    private async Task SetRoutingAsync(string mode)
    {
        var suffix = mode switch
        {
            "blocked" => "Заблокированное",
            "all" => "Всё",
            _ => TermitusRussiaPreset.DefaultRemarks,
        };
        var items = await AppManager.Instance.RoutingItems() ?? [];
        var item = items.FirstOrDefault(r => r.Remarks == $"{TermitusRussiaPreset.TemplateVersion}-{suffix}");
        if (item == null)
        {
            NoticeManager.Instance.Enqueue(Loc.RoutingMissing);
            return;
        }
        // Same path as the routing dropdown of the advanced UI: saves + reloads the core.
        OnUi(() => _main.StatusBarViewModel.SelectedRouting = item);
        await Task.Delay(300);
        await LoadSettingsAsync();
    }

    #endregion Navigation and settings

    #region Account

    /// <summary>The subscription the account page talks about: the one the current server came from.</summary>
    private async Task<SubItem?> CurrentSubAsync()
    {
        var subs = (await AppManager.Instance.SubItems() ?? [])
            .Where(s => s.Enabled && s.Url.IsNotEmpty()
                && (s.Url.StartsWith(Global.HttpsProtocol) || s.Url.StartsWith(Global.HttpProtocol)))
            .OrderBy(s => s.Sort)
            .ToList();
        if (subs.Count <= 1)
        {
            return subs.FirstOrDefault();
        }
        var server = await ConfigHandler.GetDefaultServer(_config);
        return subs.FirstOrDefault(s => s.Id == server?.Subid) ?? subs.First();
    }

    /// <summary>
    /// Account page opened: show the cache at once; ask the server again when the data is
    /// old or the last try had no connection (so a stale "no connection" never sticks).
    /// </summary>
    private async Task AccountOnOpenAsync()
    {
        _ = Acct2LoadAsync(); // devices / friends / payments (Account 2.0), in parallel with the status refresh
        await RenderAccountAsync();
        var fetched = TermitusAccount.LoadCache()?.LastFetchedAt;
        var stale = fetched is not { } f || DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(f) > AccountStaleAfter;
        if (stale || _accountOffline)
        {
            await RefreshAccountAsync(manual: false);
        }
    }

    private async Task RefreshAccountAsync(bool manual, bool retry = false)
    {
        if (manual && !retry && DateTime.UtcNow - _accountLastManual < TimeSpan.FromSeconds(20))
        {
            return; // no hammering the backend with clicks
        }
        if (Interlocked.Exchange(ref _accountRefreshRunning, 1) == 1)
        {
            return;
        }
        try
        {
            var sub = await CurrentSubAsync();
            if (sub == null)
            {
                return;
            }
            if (manual)
            {
                _accountLastManual = DateTime.UtcNow;
            }
            OnUi(() => IsAccountRefreshing = true);
            // Through the tunnel when we are connected, otherwise direct (falls back to direct).
            int? port = _connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null;
            var res = await TermitusAccount.RefreshAsync(sub, port);
            _accountOffline = !res.Reached;
            // No connection is shown on the page itself (calm, with «Повторить»), not as a toast.
            if (!res.Updated && res.Reached && manual)
            {
                NoticeManager.Instance.Enqueue(Loc.RefreshFailed);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: account refresh failed ({ex.GetType().Name})");
        }
        finally
        {
            Volatile.Write(ref _accountRefreshRunning, 0);
            OnUi(() => IsAccountRefreshing = false);
            await RenderAccountAsync();
            if (!ShowInviteRow && !_accountOffline)
            {
                _ = InviteLoadAsync(); // Home «Пригласить друга» appears once the referral link is known
            }
        }
    }

    /// <summary>Cache -> page, sidebar chip and home banner. Cheap; safe from any thread.</summary>
    private async Task RenderAccountAsync()
    {
        using var perf = TermitusPerf.Measure("render-account");
        try
        {
            var sub = await CurrentSubAsync();
            var snap = TermitusAccount.LoadCache();
            if (snap != null && snap.SubId != sub?.Id)
            {
                snap = null; // cached for another (removed) subscription
            }
            var now = DateTimeOffset.UtcNow;
            var st = TermitusAccount.Compute(snap, now);
            var en = Loc.En;
            _renewUrl = st.RenewUrl;
            _topupUrl = st.TopupUrl;
            var balance = st.Balance is { } b ? TermitusAccount.FormatMoney(b, en) : string.Empty;
            var devicesList = st.Devices;

            var date = st.Until is { } u ? TermitusAccount.FormatDate(u, now, en) : null;
            string title, detail, chip, banner = string.Empty;
            switch (st.State)
            {
                case TermitusAccountState.Active:
                    title = Loc.StatusActive;
                    detail = date != null ? Loc.Until(date) : Loc.NoEndDate;
                    chip = date != null ? Loc.SubUntil(date) : Loc.SubActive;
                    break;

                case TermitusAccountState.Expiring:
                    // one rule everywhere (hours left -> days / hours), see TermitusAccount.Left
                    if (st.Until is { } expUntil && TermitusAccount.Left(expUntil, now) is { } left)
                    {
                        title = Loc.ExpiresInLeft(left);
                        chip = Loc.SubLeft(left);
                        banner = Loc.BannerExpiringLeft(left);
                    }
                    else
                    {
                        title = Loc.ExpiresIn(st.DaysLeft ?? 0);
                        chip = Loc.SubDaysLeft(st.DaysLeft ?? 0);
                        banner = Loc.BannerExpiring(st.DaysLeft ?? 0);
                    }
                    detail = date != null ? Loc.Until(date) : string.Empty;
                    break;

                case TermitusAccountState.Expired:
                    title = Loc.StatusExpired;
                    detail = date != null ? Loc.ExpiredOn(date) : Loc.InactiveHint;
                    chip = Loc.SubExpired;
                    banner = Loc.BannerExpired;
                    break;

                case TermitusAccountState.Inactive:
                    title = Loc.StatusInactive;
                    detail = Loc.InactiveHint;
                    chip = Loc.SubInactive;
                    banner = Loc.BannerExpired;
                    break;

                default:
                    title = Loc.StatusUnknown;
                    detail = Loc.UnknownHint;
                    chip = Loc.SubUnknown;
                    break;
            }

            // The trial card follows the same clock and the same «time left» rule as the banner and the chip.
            if (TermitusTrial.DecodeInfo(TermitusSettings.TrialInfo) is { } trialInfo && TermitusSettings.TrialCode.IsNotEmpty())
            {
                var trialLine = TermitusTrial.TrialLine(trialInfo.Days, st.Until ?? trialInfo.Until, now, en);
                OnUi(() => TrialLineText = trialLine);
            }

            var traffic = st.TrafficUsed is { } used
                ? st.TrafficTotal is { } total
                    ? Loc.TrafficOf(TermitusAccount.FormatBytes(used, en), TermitusAccount.FormatBytes(total, en))
                    : Loc.TrafficUnlimited(TermitusAccount.FormatBytes(used, en))
                : string.Empty;
            var devices = st.DevicesUsed is { } du && st.DevicesAllowed is { } da ? Loc.DevicesOf(du, da)
                : st.DevicesUsed is { } du2 ? du2.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty;
            var fetched = snap?.LastFetchedAt is { } ts
                ? DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime()
                : (DateTimeOffset?)null;
            var updated = fetched is { } f
                ? Loc.UpdatedAt($"{TermitusAccount.FormatDate(f, now, en)}, {f:HH:mm}")
                : Loc.UpdatedAt(Loc.Never);
            var hasSub = sub != null;
            if (!hasSub)
            {
                chip = Loc.SubSignedOut;
            }
            var offline = hasSub && _accountOffline;
            var savedShown = offline && st.State != TermitusAccountState.Unknown;
            var telegram = hasSub ? st.Telegram : null;

            OnUi(() =>
            {
                AccountSignedIn = hasSub;
                AccountOffline = offline;
                AccountShowingSaved = savedShown;
                AccountTelegram = telegram ?? string.Empty;
                HasTelegram = telegram != null;
                AccountStatusTitle = title;
                AccountStatusDetail = detail;
                AccountOk = st.State == TermitusAccountState.Active;
                AccountWarn = st.State == TermitusAccountState.Expiring;
                AccountBad = st.State is TermitusAccountState.Expired or TermitusAccountState.Inactive;
                AccountTraffic = traffic;
                HasAccountTraffic = traffic.Length > 0;
                AccountPlan = st.Plan ?? string.Empty;
                HasAccountPlan = st.Plan != null;
                AccountDevices = devices;
                HasAccountDevices = devices.Length > 0;
                TermitusCollections.SyncReplace(AccountDeviceList, devicesList, d => d);
                HasDeviceList = devicesList.Count > 0;
                AccountBalance = balance;
                HasBalance = balance.Length > 0;
                HasTopup = st.TopupUrl != null;
                AccountUpdated = updated;

                SubChipText = chip;
                SubChipWarn = st.State == TermitusAccountState.Expiring;
                SubChipExpired = AccountBad;
                ShowExpiryBanner = hasSub && banner.Length > 0;
                ExpiryBannerText = banner;
            });
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: account render failed ({ex.GetType().Name})");
        }
    }

    #endregion Account

    #region Log out (0.4.1)

    /// <summary>
    /// «Выйти» (confirmed in the sheet): disconnect, remove our subscriptions and their
    /// servers through the upstream APIs (as «Удалить подписку» in the advanced UI does),
    /// forget the account/support/location caches and show the first-run screen.
    /// Servers added by hand under «Для опытных» (no subscription) are left alone.
    /// The device stays in the user's list in the bot: the backend has no self-removal yet.
    /// </summary>
    private async Task LogoutAsync()
    {
        if (_loggingOut)
        {
            return;
        }
        _loggingOut = true;
        OnUi(() => IsLoggingOut = true);
        try
        {
            try
            {
                _loginCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // finished on its own
            }

            // 1) Disconnect (proxy off + core stop), like «Отключиться».
            ResetGuard();
            _connected = false;
            StopSessionTimer();
            if (_config.SystemProxyItem?.SysProxyType == ESysProxyType.ForcedChange)
            {
                PublishSysProxy(ESysProxyType.ForcedClear);
            }
            await CoreManager.Instance.CoreStop();

            // 2) Subscriptions + their servers (upstream ConfigHandler.DeleteSubItem).
            var subs = await AppManager.Instance.SubItems() ?? [];
            foreach (var sub in subs)
            {
                await ConfigHandler.DeleteSubItem(_config, sub.Id);
            }
            if (_config.IndexId.IsNotEmpty() && await AppManager.Instance.GetProfileItem(_config.IndexId) == null)
            {
                _config.IndexId = string.Empty;
            }
            await ConfigHandler.SaveConfig(_config);

            // 3) Our caches.
            await TermitusAccount.ClearCacheAsync();
            TermitusSupport.ClearState();
            TermitusLocations.ClearChoice();
            _choice = TermitusLocations.AutoKey;
            _delays.Clear();
            _locations = [];
            lock (_chatLock)
            {
                _chat = [];
            }
            _supportState = null;
            _supportAvail = TermitusSupportAvailability.Unknown;
            _supportFullLoaded = false;
            _supportOffline = false;
            _accountOffline = false;
            _loginUnavailable = false;
            _renewUrl = null;
            _topupUrl = null;
            Acct2Reset();
            ClearTrialState();
            Logging.SaveLog($"{_tag}: logged out ({subs.Count} subscription(s) removed)");

            // 4) Upstream lists (advanced UI) + our screens.
            OnUi(() =>
            {
                _ = _main.ProfilesViewModel.RefreshSubscriptions();
                _ = _main.ProfilesViewModel.RefreshServers();
            });
            SetFirstRunMode(link: false);
            OnUi(() =>
            {
                ShowLogoutConfirm = false;
                SubscriptionInput = string.Empty;
                FirstRunStatus = Loc.LoggedOut;
                IsAutoChoice = true;
                ShowHome = true;
                ShowPicker = ShowSettings = ShowSupport = ShowAccount = false;
            });
            OnUi(() => Locations.Clear());
            PublishSupport();
            await RefreshStateAsync();
            if (_reloginAfterLogout)
            {
                // «Войти заново» (this device is not a manager): straight on to the Telegram sign-in.
                _reloginAfterLogout = false;
                _ = Bg(LoginWithTelegramAsync);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: logout failed ({ex.GetType().Name})");
            NoticeManager.Instance.Enqueue(Loc.LogoutFailed);
        }
        finally
        {
            _reloginAfterLogout = false;
            _loggingOut = false;
            OnUi(() =>
            {
                IsLoggingOut = false;
                ShowLogoutConfirm = false;
            });
        }
    }

    #endregion Log out (0.4.1)

    #region Support chat

    /// <summary>Every 10 s on the thread pool: decides whether a poll is due. Never re-entrant.</summary>
    private async Task SupportTickAsync()
    {
        if (Interlocked.Exchange(ref _supportTickRunning, 1) == 1)
        {
            return;
        }
        try
        {
            var pageOpen = ShowSupport && UiActive;
            var every = _supportAvail is TermitusSupportAvailability.Unavailable or TermitusSupportAvailability.Guest
                ? (pageOpen ? SupportBackgroundEvery : SupportMissingEvery)
                : pageOpen ? SupportPageEvery
                : SupportBackgroundEvery;
            // Small slack so a 10 s timer does not skip a 10 s poll by a few ms.
            if (DateTime.UtcNow - _supportLastPoll < every - TimeSpan.FromSeconds(1))
            {
                return;
            }
            await PollSupportAsync(full: pageOpen && !_supportFullLoaded);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: support tick failed ({ex.GetType().Name})");
        }
        finally
        {
            Volatile.Write(ref _supportTickRunning, 0);
        }
    }

    /// <summary>Page opened: clear the dot at once, then load (all history the first time).</summary>
    private async Task SupportOnOpenAsync(bool retry)
    {
        MarkSupportSeen();
        if (Interlocked.Exchange(ref _supportTickRunning, 1) == 1)
        {
            // A poll is running right now; its result renders as seen. «Повторить» still
            // answers at once: "loading" until that request ends.
            if (retry && _supportOffline)
            {
                _supportOffline = false;
                PublishSupport();
            }
            return;
        }
        try
        {
            var first = !_supportFullLoaded;
            // Show the chat card (loading, input disabled) right away — never a blank page
            // while the first answer is on its way (up to 2 x 12 s when the server is unreachable).
            await SupportSubAsync();
            if (retry && _supportOffline)
            {
                // «Повторить»: "loading" while we try again. Just opening the page keeps the
                // known "no connection" on screen until a new answer says otherwise.
                _supportOffline = false;
            }
            PublishSupport();
            await PollSupportAsync(full: first);
        }
        finally
        {
            Volatile.Write(ref _supportTickRunning, 0);
        }
    }

    /// <summary>The subscription the chat belongs to; resets the chat when it changed.</summary>
    private async Task<SubItem?> SupportSubAsync()
    {
        var sub = await CurrentSubAsync();
        var id = sub?.Id;
        if (_supportState?.SubId != id)
        {
            lock (_chatLock)
            {
                _chat = [];
            }
            _supportFullLoaded = false;
            _supportAvail = TermitusSupportAvailability.Unknown;
            _supportState = id == null ? null : TermitusSupport.LoadState(id);
        }
        return sub;
    }

    private async Task PollSupportAsync(bool full)
    {
        using var perf = TermitusPerf.Measure(full ? "support-poll-full" : "support-poll");
        _supportLastPoll = DateTime.UtcNow;
        var sub = await SupportSubAsync();
        var state = _supportState;
        if (sub == null || state == null)
        {
            _supportAvail = TermitusSupportAvailability.Unknown;
            PublishSupport();
            return;
        }

        int? port = _connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null;
        var fetch = await TermitusSupport.FetchAsync(sub, full ? null : state.LastKnownId, port, CancellationToken.None);
        if (fetch.Availability is TermitusSupportAvailability.Unknown or TermitusSupportAvailability.Offline)
        {
            // No connection (or 5xx): keep the messages we have, say so calmly, offer «Повторить».
            _supportOffline = true;
            PublishSupport();
            return;
        }
        _supportOffline = false;
        _supportAvail = fetch.Availability;
        if (fetch.Messages is { } msgs)
        {
            lock (_chatLock)
            {
                _chat = TermitusSupport.Merge(_chat, msgs);
            }
            if (full)
            {
                _supportFullLoaded = true;
            }
            // ?after= only ever advances on FETCHED ids (a posted id must not skip an
            // operator reply that was written just before it).
            var known = TermitusSupport.MaxId(state.LastKnownId, TermitusSupport.LastServerId(msgs));
            var lastOp = TermitusSupport.MaxId(state.LastOperatorIdSeenInFetch, TermitusSupport.LastOperatorId(msgs));
            if (known != state.LastKnownId || lastOp != state.LastOperatorIdSeenInFetch)
            {
                state.LastKnownId = known;
                state.LastOperatorIdSeenInFetch = lastOp;
                TermitusSupport.SaveState(state);
            }
        }
        if (ShowSupport && UiActive)
        {
            MarkSupportSeen();
        }
        PublishSupport();
    }

    /// <summary>The user is looking at the chat: everything the operator wrote so far is read.</summary>
    private void MarkSupportSeen()
    {
        var state = _supportState;
        if (state == null)
        {
            OnUi(() => HasSupportUnread = false);
            return;
        }
        string? lastOp;
        lock (_chatLock)
        {
            lastOp = TermitusSupport.LastOperatorId(_chat);
        }
        lastOp = TermitusSupport.MaxId(lastOp, state.LastOperatorIdSeenInFetch);
        if (TermitusSupport.HasUnread(lastOp, state.LastSeenOperatorId))
        {
            state.LastSeenOperatorId = lastOp;
            TermitusSupport.SaveState(state);
        }
        OnUi(() => HasSupportUnread = false);
    }

    /// <summary>Chat list + flags -> UI, in place (only new/changed bubbles touch the UI).</summary>
    private void PublishSupport()
    {
        var avail = _supportAvail;
        var state = _supportState;
        List<TermitusChatMessage> chat;
        lock (_chatLock)
        {
            chat = _chat;
        }
        var now = DateTimeOffset.UtcNow;
        var en = Loc.En;
        var rows = chat.Select(m => new TermitusChatRow(
            m.Id,
            m.Text,
            m.FromOperator,
            m.Local ? Loc.ChatSending
                : (m.FromOperator ? Loc.ChatOperator + " · " : string.Empty) + FormatChatTime(m.At, now, en)))
            .ToList();
        var lastOp = TermitusSupport.MaxId(TermitusSupport.LastOperatorId(chat), state?.LastOperatorIdSeenInFetch);
        var unread = state != null && !(ShowSupport && UiActive) && TermitusSupport.HasUnread(lastOp, state.LastSeenOperatorId);
        var view = TermitusSupport.ViewOf(state != null, avail, _supportOffline, rows.Count);
        _supportCanType = view.CanType;
        var placeholder = view.Offline ? Loc.NoConnectionShort : Loc.ChatPlaceholder;
        OnUi(() =>
        {
            TermitusCollections.SyncReplace(SupportMessages, rows, r => r.Id);
            SupportChatVisible = view.ChatVisible;
            SupportBotVisible = view.BotVisible;
            SupportOffline = view.Offline;
            SupportCanType = view.CanType;
            SupportPlaceholder = placeholder;
            IsSupportLoading = view.Loading;
            SupportGuest = avail == TermitusSupportAvailability.Guest;
            SupportEmpty = view.Empty;
            HasSupportUnread = unread;
        });
    }

    private string FormatChatTime(long? at, DateTimeOffset now, bool en)
    {
        if (at is not > 0)
        {
            return string.Empty;
        }
        var t = DateTimeOffset.FromUnixTimeSeconds(at.Value).ToLocalTime();
        return t.Date == now.ToLocalTime().Date
            ? t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : $"{TermitusAccount.FormatDate(t, now, en)}, {t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private async Task SendSupportAsync()
    {
        if (IsSupportSending || !_supportCanType)
        {
            return;
        }
        var raw = SupportInput;
        var text = TermitusSupport.NormalizeOutgoing(raw);
        if (text == null)
        {
            if (raw?.Trim().Length > TermitusSupport.MaxTextLength)
            {
                ShowSupportError(Loc.ChatTooLong(TermitusSupport.MaxTextLength));
            }
            return;
        }
        var sub = await SupportSubAsync();
        if (sub == null)
        {
            return;
        }

        var local = new TermitusChatMessage($"{TermitusSupport.LocalPrefix}{Interlocked.Increment(ref _localSeq)}", false, text,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Local: true);
        lock (_chatLock)
        {
            _chat = [.. _chat, local];
        }
        OnUi(() =>
        {
            SupportInput = string.Empty;
            IsSupportSending = true;
            HasSupportError = false;
        });
        PublishSupport();

        int? port = _connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null;
        var res = new TermitusSendResult(TermitusSendKind.Failed);
        try
        {
            res = await TermitusSupport.SendAsync(sub, text, port, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: support send failed ({ex.GetType().Name})");
        }

        var ok = res.Kind == TermitusSendKind.Ok;
        lock (_chatLock)
        {
            var rest = _chat.Where(m => m.Id != local.Id).ToList();
            // 201 carries the server copy (id, time): show it at once, the next poll dedupes it.
            _chat = ok && res.Message is { } m
                ? TermitusSupport.Merge(rest, [m with { From = "user", Text = m.Text.IsNullOrEmpty() ? text : m.Text, At = m.At ?? local.At }])
                : rest;
        }
        if (!ok)
        {
            switch (res.Kind)
            {
                case TermitusSendKind.Unavailable:
                    _supportAvail = TermitusSupportAvailability.Unavailable;
                    break;

                case TermitusSendKind.Guest:
                    _supportAvail = TermitusSupportAvailability.Guest;
                    break;
            }
            var error = res.Kind switch
            {
                TermitusSendKind.Invalid => Loc.ChatTooLong(TermitusSupport.MaxTextLength),
                TermitusSendKind.RateLimited => Loc.ChatRateLimited((int)Math.Ceiling(res.RetryAfter?.TotalSeconds ?? 30)),
                TermitusSendKind.Guest => Loc.ChatGuest,
                _ => Loc.ChatSendFailed,
            };
            // Give the text back so nothing typed is lost.
            OnUi(() =>
            {
                if (SupportInput.IsNullOrEmpty())
                {
                    SupportInput = text;
                }
            });
            ShowSupportError(error);
        }
        OnUi(() => IsSupportSending = false);
        PublishSupport();
        if (ok)
        {
            await PollSupportAsync(full: !_supportFullLoaded); // server copy, time, maybe a reply
        }
    }

    private void ShowSupportError(string message)
    {
        OnUi(() =>
        {
            SupportError = message;
            HasSupportError = true;
        });
    }

    #endregion Support chat

    #region Location picker

    private async Task OpenPickerAsync()
    {
        await BuildRowsAsync();
        // First open (nothing tested yet): measure right away so the list is useful.
        if (_delays.IsEmpty)
        {
            await RetestAsync();
        }
    }

    private async Task RetestAsync()
    {
        if (_busy || IsTesting)
        {
            return;
        }
        OnUi(() => IsTesting = true);
        try
        {
            await TestAllAsync();
            // Fresh numbers: re-pick for Auto, but never switch servers under a
            // live connection behind the user's back.
            if (!_connected)
            {
                await ApplyChoiceAsync();
            }
        }
        finally
        {
            OnUi(() => IsTesting = false);
            await BuildRowsAsync();
            await RefreshStateAsync();
        }
    }

    private async Task SelectLocationAsync(string key)
    {
        if (_busy)
        {
            return;
        }
        _choice = key ?? TermitusLocations.AutoKey;
        TermitusLocations.SaveChoice(_choice);
        OnUi(() =>
        {
            IsAutoChoice = _choice == TermitusLocations.AutoKey;
            ShowPicker = false;
            ShowHome = true;
        });

        if (_delays.IsEmpty)
        {
            OnUi(() => StatusText = Loc.FindingBestServer);
            await TestAllAsync();
        }
        var server = await ApplyChoiceAsync();
        if (server == null)
        {
            NoticeManager.Instance.Enqueue(Loc.LocationDown);
        }
        else if (_connected)
        {
            SetBusy(true);
            try
            {
                await _main.Reload(); // switch the live connection to the new server
            }
            finally
            {
                SetBusy(false);
            }
        }
        await BuildRowsAsync();
        await RefreshStateAsync();
    }

    private async Task BuildRowsAsync()
    {
        using var perf = TermitusPerf.Measure("build-rows");
        if (_locations.Count == 0)
        {
            var profiles = await AppManager.Instance.ProfileItems("") ?? [];
            _locations = TermitusLocations.Group(profiles
                .Where(p => p.ConfigType != EConfigType.Custom)
                .Select(p => (p.IndexId, (string?)p.Remarks)));
        }
        var tested = !_delays.IsEmpty;
        var rows = _locations
            .Select((l, i) => (l, i, ms: TermitusLocations.BestDelay(l, _delays)))
            // Answering foreign first (fastest on top), then Russia, then silent ones.
            .OrderBy(x => !tested ? 0 : x.ms <= 0 ? 2 : x.l.IsRussia ? 1 : 0)
            .ThenBy(x => tested && x.ms > 0 && !x.l.IsRussia ? x.ms : x.i)
            .Select(x => new TermitusLocationRowData(
                x.l.Key,
                TermitusLocations.DisplayParts(x.l.Title).Name,
                TermitusLocations.DisplayParts(x.l.Title).Badge,
                !tested ? (x.l.IsRussia ? Loc.RussiaCaption : string.Empty)
                    : x.ms <= 0 ? Loc.NotResponding
                    : x.l.IsRussia ? Loc.RussiaCaption : string.Empty,
                !tested ? string.Empty : x.ms > 0 ? Loc.Ms(x.ms) : "—",
                tested ? x.ms : 0,
                x.l.Key == _choice))
            .ToList();

        var autoServer = TermitusLocations.Pick(TermitusLocations.AutoKey, _locations, _delays, PreferredTransport());
        var autoLoc = autoServer == null ? null : _locations.FirstOrDefault(l => l.IndexIds.Contains(autoServer));
        var autoCaption = autoLoc == null ? Loc.AutoCaption : Loc.AutoNow(TermitusLocations.DisplayParts(autoLoc.Title).Name);

        OnUi(() =>
        {
            // In place: unchanged rows keep their controls (was Clear()+Add() = every row rebuilt).
            TermitusCollections.Sync(Locations, rows, r => r.Key, d => d.Key, d => new TermitusLocationRow(d), (r, d) => r.Update(d));
            AutoCaption = autoCaption;
            IsAutoChoice = _choice == TermitusLocations.AutoKey;
        });
    }

    #endregion Location picker

    #region Helpers

    private static async Task<bool> HasProfilesAsync()
    {
        // COUNT(*) instead of loading every profile row (called on every state refresh).
        return await SQLiteHelper.Instance.TableAsync<ProfileItem>().CountAsync() > 0;
    }

    private void UpdateLocation(ProfileItem? server)
    {
        if (server == null)
        {
            _serverDesc = "не выбран";
            LocationName = Loc.LocationAuto;
            LocationBadge = AutoBadge;
            HasLocationBadge = false;
            LocationDetail = Loc.Auto;
            return;
        }
        var auto = _choice == TermitusLocations.AutoKey;
        var title = TermitusLocations.KeyOf(server.Remarks);
        _serverDesc = $"{TermitusLocations.DisplayParts(title).Name} · "
            + (server.ConfigType == EConfigType.Hysteria2 ? "UDP" : "TCP")
            + $" ({server.ConfigType.ToString().ToUpperInvariant()}{(server.Network.IsNullOrEmpty() || server.Network == "tcp" ? string.Empty : "/" + server.Network)})";
        // No emoji in rendered text (see TermitusLocations.DisplayParts): the flag is a badge.
        var (badge, name) = TermitusLocations.DisplayParts(title);
        LocationName = title.IsNullOrEmpty() ? Loc.LocationAuto : name;
        LocationBadge = title.IsNullOrEmpty() ? AutoBadge : badge;
        HasLocationBadge = LocationBadge.Length > 0;
        var head = auto ? Loc.Auto : Loc.ChosenByYou;
        LocationDetail = _delays.TryGetValue(server.IndexId, out var ms) && ms > 0
            ? $"{head} · {Loc.Ms(ms)}"
            : head;
    }

    private const string AutoBadge = ""; // empty = the view draws its lightning icon (no emoji glyphs)

    private void ShowAdvanced()
    {
        ShowAdvancedRequested.Publish();
    }

    private static void OnUi(Action action,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!TermitusPerf.Enabled)
        {
            RxSchedulers.MainThreadScheduler.Schedule(action);
            return;
        }
        // TER_PERF: every UI post is timed; slow ones become breadcrumbs for the watchdog.
        var queued = Stopwatch.GetTimestamp();
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            var start = Stopwatch.GetTimestamp();
            action();
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (ms >= 8)
            {
                TermitusPerf.Mark($"ui-post {caller}:{line} {TermitusPerf.Fmt(ms)}ms (queued {TermitusPerf.Fmt(Stopwatch.GetElapsedTime(queued, start).TotalMilliseconds)}ms)");
            }
        });
    }

    /// <summary>Called by the view when the window is shown/hidden/minimized (UI thread).</summary>
    public void SetUiActive(bool active)
    {
        if (UiActive == active)
        {
            return;
        }
        UiActive = active;
        if (active)
        {
            _ = Task.Run(() =>
            {
                TickSession();
                if (ShowSupport)
                {
                    MarkSupportSeen();
                }
            });
        }
    }

    /// <summary>Runs <paramref name="work"/> on the thread pool; the UI thread only gets OnUi posts.</summary>
    private static Task Bg(Func<Task> work)
    {
        return Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
            }
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnUi(() => IsBusy = busy);
    }

    private void StartSessionTimer()
    {
        if (_sessionTimer != null)
        {
            return;
        }
        _sessionTimer = new System.Threading.Timer(_ => TickSession(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        TickSession();
    }

    private string? _lastSessionText;

    /// <summary>
    /// The 1 s "Защищено · 00:12:34" line. Posts to the UI thread only when the home page
    /// is on screen and the window is visible (it used to post every second regardless,
    /// re-laying out the centred status block even with the window hidden in the tray).
    /// </summary>
    private void TickSession()
    {
        if (_sessionTimer == null || !_connected || _recovering || _interrupted || _guardTitle != null || !ShowHome || !UiActive)
        {
            return;
        }
        var elapsed = DateTime.Now - _connectedAt;
        var text = $"{Loc.Protected} · {elapsed:hh\\:mm\\:ss}";
        if (text == _lastSessionText)
        {
            return;
        }
        _lastSessionText = text;
        OnUi(() => SubStatusText = text);
    }

    private void StopSessionTimer()
    {
        _sessionTimer?.Dispose();
        _sessionTimer = null;
        _lastSessionText = null;
    }

    #endregion Helpers
}

/// <summary>Values of one picker row (computed off the UI thread).</summary>
public sealed record TermitusLocationRowData(string Key, string Title, string Badge, string Caption, string PingText, int Ms, bool IsSelected);

/// <summary>
/// One row of the TER location picker. Updated in place (TermitusCollections.Sync) so a
/// refresh changes only the texts/colours that differ instead of re-creating the row.
/// </summary>
public sealed class TermitusLocationRow : ReactiveObject
{
    private TermitusLocationRowData _d;

    public TermitusLocationRow(TermitusLocationRowData d)
    {
        _d = d;
    }

    public string Key => _d.Key;
    public string Title => _d.Title;
    public string Badge => _d.Badge;
    public string Caption => _d.Caption;
    public bool HasCaption => _d.Caption.Length > 0;
    public string PingText => _d.PingText;
    public bool IsSelected => _d.IsSelected;
    public bool IsGood => _d.Ms is > 0 and <= 150;
    public bool IsMid => _d.Ms is > 150 and <= 300;
    public bool IsBad => _d.Ms > 300;
    public bool IsDown => _d.Ms < 0;

    public void Update(TermitusLocationRowData d)
    {
        var old = _d;
        if (old == d)
        {
            return;
        }
        _d = d;
        if (old.Title != d.Title)
        {
            this.RaisePropertyChanged(nameof(Title));
        }
        if (old.Badge != d.Badge)
        {
            this.RaisePropertyChanged(nameof(Badge));
        }
        if (old.Caption != d.Caption)
        {
            this.RaisePropertyChanged(nameof(Caption));
            this.RaisePropertyChanged(nameof(HasCaption));
        }
        if (old.PingText != d.PingText)
        {
            this.RaisePropertyChanged(nameof(PingText));
        }
        if (old.IsSelected != d.IsSelected)
        {
            this.RaisePropertyChanged(nameof(IsSelected));
        }
        if (old.Ms != d.Ms)
        {
            this.RaisePropertyChanged(nameof(IsGood));
            this.RaisePropertyChanged(nameof(IsMid));
            this.RaisePropertyChanged(nameof(IsBad));
            this.RaisePropertyChanged(nameof(IsDown));
        }
    }
}

/// <summary>One chat bubble.</summary>
public sealed record TermitusChatRow(string Id, string Text, bool IsOperator, string Meta)
{
    public bool IsMine => !IsOperator;
}

/// <summary>
/// In-app knowledge base for the "Поддержка" page: how the app is laid out and
/// what to do when something does not work. Plain text, Russian (the app UI default).
/// </summary>
public sealed record TermitusKbItem(string Question, string Answer)
{
    public static IReadOnlyList<TermitusKbItem> All { get; } =
    [
        new("Как подключиться?",
            "Нажмите большую круглую кнопку на «Главной». TER сам проверит все страны и выберет самую быструю. Когда кнопка стала зелёной и пошёл таймер — защита включена. Нажмите ещё раз, чтобы выключить."),
        new("Как выбрать страну?",
            "Нажмите строку со страной под кнопкой или «Серверы» в меню слева. «Авто» — самая быстрая из работающих сейчас (рекомендуем). Можно выбрать конкретную страну — TER запомнит выбор. Цифра справа — задержка: чем меньше, тем быстрее. «Проверить скорость» измеряет всё заново."),
        new("Почему в списке есть Россия?",
            "Это запасной сервер. Он не выбирается автоматически, пока работает хоть одна зарубежная страна. Пригодится, если у вашего провайдера или мобильного оператора включены «белые списки»."),
        new("Что значит «Российские сайты — напрямую»?",
            "Банки, Госуслуги, маркетплейсы и другие российские сайты открываются без VPN — так они не ругаются и работают быстрее. Всё остальное идёт через TER. Изменить можно в «Настройки» → «Какие сайты идут через TER»."),
        new("Telegram или игра не работают через TER",
            "Проверьте «Настройки» → «Защищать весь трафик компьютера»: должно быть включено. Тогда при первом подключении после запуска TER Mac спросит пароль администратора — введите его. Если пароль не ввести, через TER пойдут только браузеры."),
        new("Что будет, если VPN оборвётся?",
            "TER следит за соединением сам: если оно оборвалось или вы сменили сеть (например, Wi-Fi на телефонную точку доступа), он переподключится и при необходимости выберет другую страну по тем же правилам, что и «Авто». Если переподключиться не удалось: при включённой опции «Блокировать интернет, если VPN оборвался» (Настройки → Безопасность) браузер и программы, работающие через системный прокси, остаются без интернета, пока VPN не вернётся, — на «Главной» будет красная надпись «Защита прервана». Когда опция выключена, TER вернёт обычный интернет и прямо напишет, что вы без защиты. Честное ограничение: программы, которые не слушают системный прокси, при аварии могут выйти в интернет напрямую — полностью закрыть это на macOS без системного расширения нельзя."),
        new("Не подключается или очень медленно",
            "1) Нажмите «Проверить скорость» в «Серверах» и выберите «Авто». 2) Выключите и включите защиту. 3) Если стоит другой VPN или прокси — выключите его. 4) Не помогло — напишите нам в бот, приложим шаги."),
        new("Подписка и продление",
            "Нажмите карточку «Аккаунт» внизу слева: там срок подписки, сколько трафика ушло и сколько устройств подключено. Кнопка «Продлить в Telegram» откроет наш бот — там продление и оплата. За 3 дня до конца подписки TER предупредит на «Главной»."),
        new("Сколько устройств можно подключить?",
            "На одну подписку — до 3 устройств: компьютер, телефон, планшет. Ссылку для нового устройства выдаёт бот. На телефоне можно пользоваться приложениями Happ, Streisand или v2RayTun — ссылка та же."),
        new("Что приложение знает обо мне?",
            "Приложение не ведёт журнал посещённых сайтов и не отправляет нам историю браузера. Что и сколько хранится на нашей стороне — в политике конфиденциальности на termitus.online."),
        new("Что такое «Для опытных»?",
            "Полный интерфейс движка: все серверы по отдельным протоколам (VLESS, Hysteria2), правила маршрутизации, DNS и журнал работы. Обычно туда заходить не нужно — всё настраивается само. Вернуться назад — кнопка «Главная» вверху справа."),
    ];
}
