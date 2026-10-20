using ServiceLib.Services;

namespace ServiceLib.ViewModels;

/// <summary>
/// 0.6.5: «Попробовать бесплатно — 2 дня» on the first-run screen (POST /app/trial, see TermitusTrial), the trial card
/// (gift code + «Привязать к Telegram») and the prominent «Пригласить друга» row on Home (sheet with a big QR of the
/// referral link; same Friends API / state as Account 2.0).
/// </summary>
public partial class TermitusHomeViewModel
{
    // Trial
    [Reactive] public partial bool TrialAvailable { get; set; }
    [Reactive] public partial bool TrialConsent { get; set; }
    [Reactive] public partial bool IsTrialStarting { get; set; }
    [Reactive] public partial bool CanStartTrial { get; set; }
    [Reactive] public partial bool TrialRefused { get; set; }
    [Reactive] public partial string TrialRefusalText { get; set; }
    [Reactive] public partial bool HasTrial { get; set; }
    [Reactive] public partial string TrialLineText { get; set; }
    [Reactive] public partial string TrialCode { get; set; }
    [Reactive] public partial bool TrialBindBusy { get; set; }
    [Reactive] public partial string TrialBindStatus { get; set; }

    // Invite a friend
    [Reactive] public partial bool ShowInviteRow { get; set; }

    public ReactiveCommand<RxVoid, RxVoid> TrialStartCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> TrialBotCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> TrialBindCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> TrialBindCancelCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> TrialCopyCodeCmd { get; private set; } = null!;
    public ReactiveCommand<string, RxVoid> LegalOpenCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> InviteOpenCmd { get; private set; } = null!;

    private int _inviteLoading;

    private void InitTrial()
    {
        TrialRefusalText = TrialLineText = TrialCode = TrialBindStatus = string.Empty;
        TrialAvailable = TermitusTrial.TrialPlatform() != null;

        TrialStartCmd = ReactiveCommand.CreateFromTask(() => Bg(StartTrialAsync));
        TrialBotCmd = ReactiveCommand.CreateFromTask(() =>
        {
            TermitusHttp.OpenUrl(TermitusTrial.BotUrl);
            return Task.CompletedTask;
        });
        // «Привязать к Telegram»: TER's normal Telegram login, with the stored trial code (the bot redeems it for that account).
        TrialBindCmd = ReactiveCommand.CreateFromTask(() => Bg(BindTrialAsync));
        TrialBindCancelCmd = ReactiveCommand.CreateFromTask(() =>
        {
            try
            {
                _loginCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // finished on its own
            }
            return Task.CompletedTask;
        });
        TrialCopyCodeCmd = ReactiveCommand.CreateFromTask(() =>
        {
            CopyToClipboard(TrialCode);
            return Task.CompletedTask;
        });
        LegalOpenCmd = ReactiveCommand.CreateFromTask<string>(kind =>
        {
            TermitusHttp.OpenUrl(TermitusTrial.LegalUrl(kind, Loc.En));
            return Task.CompletedTask;
        });
        InviteOpenCmd = ReactiveCommand.CreateFromTask(() => Bg(InviteOpenAsync));

        this.WhenAnyValue(x => x.TrialConsent, x => x.IsTrialStarting, x => x.IsLoginStarting, (c, s, l) => c && !s && !l)
            .Subscribe(v => CanStartTrial = v);
        LoadTrialState();
    }

    /// <summary>Settings -> card. The trial card stays after the end (it says so) until «Выйти».</summary>
    private void LoadTrialState()
    {
        var code = TermitusSettings.TrialCode;
        var info = TermitusTrial.DecodeInfo(TermitusSettings.TrialInfo);
        if (code.IsNullOrEmpty() || info == null)
        {
            HasTrial = false;
            TrialCode = TrialLineText = string.Empty;
            return;
        }
        TrialCode = code;
        TrialLineText = TermitusTrial.TrialLine(info.Value.Days, info.Value.Until, DateTimeOffset.UtcNow, Loc.En);
        HasTrial = true;
    }

    private void ClearTrialState()
    {
        TermitusSettings.TrialCode = string.Empty;
        TermitusSettings.TrialInfo = string.Empty;
        OnUi(() =>
        {
            HasTrial = false;
            TrialCode = TrialLineText = string.Empty;
            TrialRefused = false;
            TrialConsent = false;
            ShowInviteRow = false;
        });
    }

    /// <summary>What happens to the stored trial after the login answer: attached / unusable code = done with it; "error" keeps it for another try.</summary>
    public static bool ShouldClearTrial(bool attached, string? note) => attached || note is "code_unavailable" or "cooldown";

    /// <summary>The one line the user sees after the bind login.</summary>
    public static string BindResultLine(TermitusLocale loc, bool attached, string? note)
        => attached ? loc.TrialBindDone : note != null ? loc.TrialBindNote(note) : loc.TrialBindLoggedIn;

    private async Task BindTrialAsync()
    {
        var code = TermitusSettings.TrialCode;
        if (code.IsNullOrEmpty() || _busy || _loginCts != null || TrialBindBusy)
        {
            return;
        }
        using var cts = new CancellationTokenSource();
        _loginCts = cts;
        OnUi(() =>
        {
            TrialBindBusy = true;
            TrialBindStatus = Loc.TrialBindStarting;
        });
        TermitusPollOutcome outcome = new(TermitusPollKind.Failed);
        try
        {
            var start = await TermitusLogin.StartAsync(cts.Token, code);
            if (start.Kind == TermitusLoginStartKind.Unavailable)
            {
                OnUi(() => TrialBindStatus = Loc.LoginUnavailable);
                return;
            }
            if (start.Kind == TermitusLoginStartKind.Offline || start.Session == null)
            {
                OnUi(() => TrialBindStatus = Loc.LoginOffline);
                return;
            }
            var session = start.Session;
            TermitusHttp.OpenUrl(session.BotUrl);
            OnUi(() => TrialBindStatus = Loc.TrialBindWaiting);
            outcome = await TermitusLogin.WaitForConfirmationAsync(
                session,
                ct => TermitusLogin.PollAsync(session, ct),
                Task.Delay,
                () => DateTimeOffset.UtcNow,
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            OnUi(() => TrialBindStatus = string.Empty);
            return;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: trial bind failed ({ex.GetType().Name})");
        }
        finally
        {
            _loginCts = null;
            OnUi(() => TrialBindBusy = false);
        }

        if (outcome.Kind != TermitusPollKind.Ok || outcome.SubscriptionUrl is not { } url)
        {
            var msg = outcome.Kind switch
            {
                TermitusPollKind.Expired => Loc.LoginExpired,
                TermitusPollKind.Declined => Loc.LoginDeclined,
                TermitusPollKind.Used => Loc.LoginUsed,
                _ => Loc.LoginFailed,
            };
            OnUi(() => TrialBindStatus = msg);
            return;
        }

        // Confirmed. The guest trial device is revoked by the server: swap to the new per-device subscription of the REAL account
        // exactly like a normal login (old subscription + servers out, new one imported, cache and account refreshed).
        _main.StatusBarViewModel.ShowHideWindowRequested.Publish(true);
        OnUi(() => TrialBindStatus = Loc.Importing);
        try
        {
            if (_connected)
            {
                await DisconnectAsync();
            }
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
            await TermitusAccount.ClearCacheAsync();
            Acct2Reset();
            _renewUrl = null;
            _topupUrl = null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: trial bind swap failed ({ex.GetType().Name})");
        }
        var ok = await ImportUrlAsync(url, fromLogin: true);
        if (ShouldClearTrial(outcome.TrialAttached, outcome.TrialNote))
        {
            ClearTrialState();
        }
        var line = BindResultLine(Loc, outcome.TrialAttached, outcome.TrialNote);
        Logging.SaveLog($"{_tag}: trial bind -> {(outcome.TrialAttached ? "attached" : outcome.TrialNote ?? "no trial info")}, import {(ok ? "ok" : "failed")}");
        NoticeManager.Instance.Enqueue(line);
        OnUi(() => TrialBindStatus = string.Empty);
    }

    private async Task StartTrialAsync()
    {
        if (_busy || _loginCts != null || IsTrialStarting)
        {
            return;
        }
        if (!TrialConsent)
        {
            OnUi(() => FirstRunStatus = Loc.TrialNeedConsent);
            return;
        }
        OnUi(() =>
        {
            IsTrialStarting = true;
            TrialRefused = false;
            FirstRunStatus = string.Empty;
        });
        TermitusTrialOutcome o;
        try
        {
            o = await TermitusTrial.StartAsync(CancellationToken.None); // one attempt, never retried automatically
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: trial failed ({ex.GetType().Name})");
            o = new(TermitusTrialKind.Failed);
        }
        finally
        {
            OnUi(() => IsTrialStarting = false);
        }

        switch (o.Kind)
        {
            case TermitusTrialKind.Ok when o.SubscriptionUrl != null && o.Code != null:
                var now = DateTimeOffset.UtcNow;
                var until = o.ExpiresAt ?? now.AddDays(o.Days);
                TermitusSettings.TrialCode = o.Code;
                TermitusSettings.TrialInfo = TermitusTrial.EncodeInfo(o.Days, until);
                OnUi(LoadTrialState);
                SetFirstRunMode(link: false, waiting: true);
                OnUi(() =>
                {
                    LoginWaitingTitle = Loc.Importing;
                    LoginCountdown = string.Empty;
                    IsLoginImporting = true;
                });
                var ok = await ImportUrlAsync(o.SubscriptionUrl, fromLogin: true);
                OnUi(() => IsLoginImporting = false);
                if (!ok)
                {
                    SetFirstRunMode(link: false);
                }
                else
                {
                    var line = TermitusTrial.TrialLine(o.Days, until, now, Loc.En);
                    NoticeManager.Instance.Enqueue(line);
                }
                break;

            case TermitusTrialKind.Refused:
                var text = Loc.TrialRefusalText(o.Reason ?? "ip", o.Detail);
                OnUi(() =>
                {
                    TrialRefusalText = text;
                    TrialRefused = true;
                });
                break;

            case TermitusTrialKind.Offline:
                OnUi(() => FirstRunStatus = Loc.TrialOffline);
                break;

            case TermitusTrialKind.NoConsent:
                OnUi(() => FirstRunStatus = Loc.TrialNeedConsent);
                break;

            case TermitusTrialKind.NotConfigured:
                OnUi(() => FirstRunStatus = Loc.TrialNotConfigured);
                break;

            default:
                OnUi(() => FirstRunStatus = Loc.TrialFailed);
                break;
        }
    }

    // ---- Invite a friend ----

    /// <summary>One cheap GET of the referral data so the Home row can appear without opening «Аккаунт». Same parser / state as Account 2.0.</summary>
    private async Task InviteLoadAsync()
    {
        if (Interlocked.Exchange(ref _inviteLoading, 1) == 1)
        {
            return;
        }
        try
        {
            var sub = await CurrentSubAsync();
            if (sub == null)
            {
                OnUi(() => ShowInviteRow = false);
                return;
            }
            int? port = _connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null;
            var res = await TermitusAccountApi.GetReferralsAsync(sub, port, CancellationToken.None);
            var refs = res.Kind == TermitusAcctKind.Ok ? TermitusAccountApi.ParseReferrals(res.Body) : null;
            if (refs != null)
            {
                ApplyReferrals(refs);
            }
            OnUi(() => ShowInviteRow = refs != null && RefHasLink);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: invite load failed ({ex.GetType().Name})");
        }
        finally
        {
            Volatile.Write(ref _inviteLoading, 0);
        }
    }

    private async Task InviteOpenAsync()
    {
        if (!RefHasLink)
        {
            await InviteLoadAsync();
        }
        if (!RefHasLink)
        {
            NoticeManager.Instance.Enqueue(Loc.InviteNoLink);
            return;
        }
        byte[]? png = null;
        try
        {
            png = QRCodeUtils.GenQRCode(RefLink);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: friend QR failed ({ex.GetType().Name})");
        }
        OnUi(() =>
        {
            OpenAcctSheet(Acct2Sheet.Friend, Loc.InviteSheetTitle, Loc.InviteSheetScan);
            AcctQrPng = png;
        });
    }

    /// <summary>Test tour: trial + invite state in one line (no code / link values).</summary>
    public string DebugTrialState()
        => $"available={TrialAvailable} consent={TrialConsent} starting={IsTrialStarting} canStart={CanStartTrial} refused={TrialRefused} refusal='{TrialRefusalText}' "
            + $"bind={TrialBindBusy}/'{TrialBindStatus}' hasTrial={HasTrial} line='{TrialLineText}' code={(TrialCode.Length > 0 ? "yes" : "no")} inviteRow={ShowInviteRow} friendSheet={AcctSheetFriend} qr={(AcctQrPng?.Length ?? 0)}b rule='{RefRule}' status='{FirstRunStatus}'";
}
