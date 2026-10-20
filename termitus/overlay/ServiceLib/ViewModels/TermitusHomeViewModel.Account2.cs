using System.Collections.ObjectModel;
using ServiceLib.Common;
using ServiceLib.Services;

namespace ServiceLib.ViewModels;

public sealed record TermitusAcctDeviceRow(int Id, string Name, string Meta, bool IsThis, bool Manages, bool PaidSlot, bool Paused,
    bool MenuOpen, bool CanToggleManage, string ManageText);

public sealed record TermitusAcctPayRow(string What, string Amount, string Caption, bool Problem);

/// <summary>
/// TER Account 2.0 on the «Аккаунт» page: «Устройства» (rename / give-take managing / remove / add by link + QR),
/// «Друзья» (referral link, counters, reward switch), «Оплаты», «Сменить ссылку». Everything goes through
/// TermitusAccountApi (the device's own token). Loaded when the page opens and on «Обновить», never on a timer.
/// A 403 on the device list means this device is not a manager: one card «Подпиской управляет её владелец»
/// with «Войти заново» (the existing log out → Telegram sign-in flow) replaces the three cards.
/// </summary>
public partial class TermitusHomeViewModel
{
    private enum Acct2Sheet { None, Rename, Remove, InvitePay, NoFunds, Qr, Rotate, RotateDone, Friend }

    private const int PaymentsShown = 10;

    // Page state
    [Reactive] public partial bool Acct2Loading { get; set; }
    [Reactive] public partial bool Acct2Manager { get; set; }
    [Reactive] public partial bool Acct2Guest { get; set; }
    [Reactive] public partial bool Acct2Offline { get; set; }
    [Reactive] public partial bool Acct2LoadError { get; set; }
    [Reactive] public partial bool Acct2HasDevices { get; set; }
    [Reactive] public partial bool Acct2Inviting { get; set; }

    // Friends
    [Reactive] public partial bool Acct2HasRef { get; set; }
    [Reactive] public partial string RefLink { get; set; }
    [Reactive] public partial bool RefHasLink { get; set; }
    [Reactive] public partial string RefInvited { get; set; }
    [Reactive] public partial string RefEarned { get; set; }
    [Reactive] public partial string RefRule { get; set; }
    [Reactive] public partial bool RewardIsMoney { get; set; }
    [Reactive] public partial bool RewardIsDays { get; set; }

    // Payments
    [Reactive] public partial bool Acct2HasPayments { get; set; }
    [Reactive] public partial bool Acct2PaymentsEmpty { get; set; }
    [Reactive] public partial bool Acct2PaymentsMore { get; set; }
    [Reactive] public partial bool Acct2PaymentsExpanded { get; set; }

    // The one sheet (rename / remove / add device / no funds / change link)
    [Reactive] public partial bool AcctShowSheet { get; set; }
    [Reactive] public partial bool AcctSheetRename { get; set; }
    [Reactive] public partial bool AcctSheetRemove { get; set; }
    [Reactive] public partial bool AcctSheetInvitePay { get; set; }
    [Reactive] public partial bool AcctSheetNoFunds { get; set; }
    [Reactive] public partial bool AcctSheetQr { get; set; }
    [Reactive] public partial bool AcctSheetRotate { get; set; }
    [Reactive] public partial bool AcctSheetRotateDone { get; set; }
    [Reactive] public partial bool AcctSheetFriend { get; set; }
    [Reactive] public partial bool AcctSheetCloseOnly { get; set; }
    [Reactive] public partial bool AcctSheetBusy { get; set; }
    [Reactive] public partial string AcctSheetTitle { get; set; }
    [Reactive] public partial string AcctSheetBody { get; set; }
    [Reactive] public partial string AcctSheetError { get; set; }
    [Reactive] public partial bool AcctSheetHasError { get; set; }
    [Reactive] public partial bool AcctSheetNoteVisible { get; set; }
    [Reactive] public partial string AcctRenameText { get; set; }
    [Reactive] public partial bool AcctRenameCanSave { get; set; }
    [Reactive] public partial byte[]? AcctQrPng { get; set; }
    [Reactive] public partial string AcctInviteLink { get; set; }

    public ObservableCollection<TermitusAcctDeviceRow> AcctDevices { get; } = [];
    public ObservableCollection<TermitusAcctPayRow> AcctPayments { get; } = [];

    public ReactiveCommand<int, RxVoid> AcctMenuCmd { get; private set; } = null!;
    public ReactiveCommand<int, RxVoid> AcctRenameAskCmd { get; private set; } = null!;
    public ReactiveCommand<int, RxVoid> AcctManageCmd { get; private set; } = null!;
    public ReactiveCommand<int, RxVoid> AcctRemoveAskCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> AcctSheetOkCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> AcctSheetCloseCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> AcctInviteCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> AcctInviteCopyCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> AcctTopupCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> RefCopyCmd { get; private set; } = null!;
    public ReactiveCommand<string, RxVoid> RefRewardCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> AcctPaymentsToggleCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> AcctRotateAskCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> AcctRotateCopyCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> AcctReloginCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> Acct2RetryCmd { get; private set; } = null!;

    /// <summary>The view puts this text on the clipboard.</summary>
    public EventChannel<string> AcctCopyRequested { get; } = new();

    private List<TermitusAcctDevice> _acctDevices = [];
    private List<TermitusPaymentItem> _acctPayments = [];
    private int? _acctMenuId;
    private int? _acctTargetId;
    private Acct2Sheet _acctSheet = Acct2Sheet.None;
    private string? _acctRotatedLink;
    private int _acct2Running;
    private volatile bool _reloginAfterLogout;

    private void InitAccount2()
    {
        RefLink = RefInvited = RefEarned = RefRule = string.Empty;
        AcctSheetTitle = AcctSheetBody = AcctSheetError = AcctRenameText = AcctInviteLink = string.Empty;

        AcctMenuCmd = ReactiveCommand.CreateFromTask<int>(id =>
        {
            _acctMenuId = _acctMenuId == id ? null : id;
            RebuildAcctDevices();
            return Task.CompletedTask;
        });
        AcctRenameAskCmd = ReactiveCommand.CreateFromTask<int>(id =>
        {
            var d = _acctDevices.FirstOrDefault(x => x.Id == id);
            if (d != null)
            {
                _acctTargetId = id;
                OpenAcctSheet(Acct2Sheet.Rename, Loc.AcctRenameTitle, string.Empty, rename: d.Name);
            }
            return Task.CompletedTask;
        });
        AcctRemoveAskCmd = ReactiveCommand.CreateFromTask<int>(id =>
        {
            var d = _acctDevices.FirstOrDefault(x => x.Id == id);
            if (d != null)
            {
                _acctTargetId = id;
                OpenAcctSheet(Acct2Sheet.Remove, Loc.AcctRemoveTitle(d.Name), Loc.AcctRemoveBody, note: d.IsThis);
            }
            return Task.CompletedTask;
        });
        AcctManageCmd = ReactiveCommand.CreateFromTask<int>(id => Bg(() => AcctToggleManageAsync(id)));
        AcctSheetOkCmd = ReactiveCommand.CreateFromTask(() => Bg(AcctSheetOkAsync));
        AcctSheetCloseCmd = ReactiveCommand.CreateFromTask(() =>
        {
            CloseAcctSheet();
            return Task.CompletedTask;
        });
        AcctInviteCmd = ReactiveCommand.CreateFromTask(() => Bg(() => AcctInviteAsync(confirmPay: false)));
        AcctInviteCopyCmd = ReactiveCommand.CreateFromTask(() =>
        {
            CopyToClipboard(AcctInviteLink);
            return Task.CompletedTask;
        });
        AcctTopupCmd = ReactiveCommand.CreateFromTask(() =>
        {
            CloseAcctSheet();
            if (_topupUrl is { } u)
            {
                TermitusHttp.OpenUrl(u);
            }
            return Task.CompletedTask;
        });
        RefCopyCmd = ReactiveCommand.CreateFromTask(() =>
        {
            CopyToClipboard(RefLink);
            return Task.CompletedTask;
        });
        RefRewardCmd = ReactiveCommand.CreateFromTask<string>(r => Bg(() => AcctSetRewardAsync(r)));
        AcctPaymentsToggleCmd = ReactiveCommand.CreateFromTask(() =>
        {
            Acct2PaymentsExpanded = !Acct2PaymentsExpanded;
            RebuildAcctPayments();
            return Task.CompletedTask;
        });
        AcctRotateAskCmd = ReactiveCommand.CreateFromTask(() =>
        {
            OpenAcctSheet(Acct2Sheet.Rotate, Loc.AcctRotateTitle, Loc.AcctRotateBody);
            return Task.CompletedTask;
        });
        AcctRotateCopyCmd = ReactiveCommand.CreateFromTask(() =>
        {
            if (_acctRotatedLink is { } l)
            {
                CopyToClipboard(l);
            }
            return Task.CompletedTask;
        });
        // «Войти заново»: the normal log-out sheet, and once logged out the Telegram sign-in starts by itself.
        AcctReloginCmd = ReactiveCommand.CreateFromTask(() =>
        {
            _reloginAfterLogout = true;
            ShowLogoutConfirm = true;
            return Task.CompletedTask;
        });
        Acct2RetryCmd = ReactiveCommand.CreateFromTask(() => Bg(() => Acct2LoadAsync(force: true)));

        this.WhenAnyValue(x => x.AcctRenameText).Subscribe(t =>
            AcctRenameCanSave = _acctSheet == Acct2Sheet.Rename && t.Trim().Length > 0 && t.Trim().Length <= 100);
    }

    private void CopyToClipboard(string text)
    {
        if (text.IsNullOrEmpty())
        {
            return;
        }
        AcctCopyRequested.Publish(text);
        NoticeManager.Instance.Enqueue(Loc.AcctInviteCopied);
    }

    #region Load

    private async Task Acct2LoadAsync(bool force = false)
    {
        if (Interlocked.Exchange(ref _acct2Running, 1) == 1)
        {
            return;
        }
        try
        {
            var sub = await CurrentSubAsync();
            if (sub == null)
            {
                Acct2Reset();
                return;
            }
            int? port = _connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null;
            var first = !Acct2Manager;
            if (first)
            {
                OnUi(() => Acct2Loading = true);
            }

            var dev = await TermitusAccountApi.GetDevicesAsync(sub, port, CancellationToken.None);
            if (dev.Kind == TermitusAcctKind.NotManager)
            {
                OnUi(() =>
                {
                    Acct2Guest = true;
                    Acct2Manager = false;
                    Acct2Offline = Acct2LoadError = false;
                });
                return;
            }
            var list = dev.Kind == TermitusAcctKind.Ok ? TermitusAccountApi.ParseDevices(dev.Body) : null;
            if (list == null)
            {
                var offline = dev.Kind == TermitusAcctKind.Offline;
                OnUi(() =>
                {
                    Acct2Guest = false;
                    Acct2Offline = offline && !AccountOffline;
                    Acct2LoadError = !offline;
                });
                return;
            }

            var refTask = TermitusAccountApi.GetReferralsAsync(sub, port, CancellationToken.None);
            var payTask = TermitusAccountApi.GetPaymentsAsync(sub, port, CancellationToken.None);
            await Task.WhenAll(refTask, payTask);
            var refs = refTask.Result.Kind == TermitusAcctKind.Ok ? TermitusAccountApi.ParseReferrals(refTask.Result.Body) : null;
            var pays = payTask.Result.Kind == TermitusAcctKind.Ok ? TermitusAccountApi.ParsePayments(payTask.Result.Body) : null;

            _acctDevices = list;
            if (_acctMenuId is { } mid && list.All(d => d.Id != mid))
            {
                _acctMenuId = null;
            }
            if (pays != null)
            {
                _acctPayments = pays;
            }
            OnUi(() =>
            {
                Acct2Guest = false;
                Acct2Offline = Acct2LoadError = false;
                Acct2Manager = true;
            });
            RebuildAcctDevices();
            if (refs != null)
            {
                ApplyReferrals(refs);
            }
            else
            {
                OnUi(() => Acct2HasRef = false);
            }
            if (pays != null)
            {
                RebuildAcctPayments();
            }
            else
            {
                OnUi(() => Acct2HasPayments = false);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: account2 load failed ({ex.GetType().Name})");
            OnUi(() => Acct2LoadError = true);
        }
        finally
        {
            Volatile.Write(ref _acct2Running, 0);
            OnUi(() => Acct2Loading = false);
        }
    }

    /// <summary>Logged out / no subscription: forget everything shown (nothing stale for the next account).</summary>
    private void Acct2Reset()
    {
        _acctDevices = [];
        _acctPayments = [];
        _acctMenuId = null;
        _acctRotatedLink = null;
        OnUi(() =>
        {
            Acct2Manager = Acct2Guest = Acct2Offline = Acct2LoadError = Acct2HasRef = Acct2HasPayments = false;
            ShowInviteRow = false;
            AcctDevices.Clear();
            AcctPayments.Clear();
            AcctQrPng = null;
            AcctInviteLink = string.Empty;
            AcctShowSheet = false;
            _acctSheet = Acct2Sheet.None;
        });
    }

    private void RebuildAcctDevices()
    {
        var en = Loc.En;
        var now = DateTimeOffset.UtcNow;
        var rows = _acctDevices.Select(d =>
        {
            var paused = !d.IsActive;
            var router = d.Platform.Equals("router", StringComparison.OrdinalIgnoreCase);
            var meta = TermitusAccountApi.PlatformLabel(d.Platform, en)
                + (d.IsThis ? string.Empty : " · " + TermitusAccountApi.RelativeTime(d.LastSeen, now, en));
            return new TermitusAcctDeviceRow(d.Id, d.Name, meta, d.IsThis, d.CanManage, d.PaidSlot, paused,
                _acctMenuId == d.Id, !d.IsThis && !paused && !router,
                d.CanManage ? Loc.AcctTakeManage : Loc.AcctGiveManage);
        }).ToList();
        OnUi(() =>
        {
            TermitusCollections.SyncReplace(AcctDevices, rows, r => r.Id);
            Acct2HasDevices = rows.Count > 0;
        });
    }

    private void ApplyReferrals(TermitusReferrals r)
    {
        var en = Loc.En;
        var link = r.Link ?? r.SiteLink ?? string.Empty;
        var earned = new List<string>();
        if (r.BonusRub > 0 || r.BonusDays == 0)
        {
            earned.Add(TermitusAccount.FormatMoney(r.BonusRub, en));
        }
        if (r.BonusDays > 0)
        {
            earned.Add(en ? $"{r.BonusDays} {(r.BonusDays == 1 ? "day" : "days")}"
                : $"{r.BonusDays} {TermitusAccount.RuPlural(r.BonusDays, "день", "дня", "дней")}");
        }
        OnUi(() =>
        {
            Acct2HasRef = true;
            ShowInviteRow = link.Length > 0;
            RefLink = link;
            RefHasLink = link.Length > 0;
            RefInvited = r.Invited.ToString(System.Globalization.CultureInfo.InvariantCulture);
            RefEarned = string.Join(" + ", earned);
            RefRule = TermitusAccountApi.ReferralRule(r.Percent, r.DaysPerPayment, en);
            RewardIsDays = r.RewardIsDays;
            RewardIsMoney = !r.RewardIsDays;
        });
    }

    private void RebuildAcctPayments()
    {
        var en = Loc.En;
        var now = DateTimeOffset.UtcNow;
        var all = _acctPayments;
        var shown = (Acct2PaymentsExpanded ? all : all.Take(PaymentsShown)).Select(p => new TermitusAcctPayRow(
            TermitusAccountApi.PaymentWhat(p.Kind, p.PlanDays, p.Provider, en),
            TermitusAccountApi.PaymentAmount(p.Amount, p.Currency, en),
            TermitusAccountApi.PaymentCaption(p, now, en),
            p.Status is "failed" or "expired")).ToList();
        OnUi(() =>
        {
            TermitusCollections.SyncReplace(AcctPayments, shown, r => r);
            Acct2HasPayments = true;
            Acct2PaymentsEmpty = all.Count == 0;
            Acct2PaymentsMore = all.Count > PaymentsShown;
        });
    }

    #endregion Load

    #region Sheet

    private void OpenAcctSheet(Acct2Sheet sheet, string title, string body, string? rename = null, bool note = false)
    {
        _acctSheet = sheet;
        AcctSheetTitle = title;
        AcctSheetBody = body;
        AcctSheetError = string.Empty;
        AcctSheetHasError = false;
        AcctSheetBusy = false;
        AcctSheetNoteVisible = note;
        if (rename != null)
        {
            AcctRenameText = rename;
        }
        SyncSheetFlags();
        AcctShowSheet = true;
        AcctRenameCanSave = sheet == Acct2Sheet.Rename && AcctRenameText.Trim().Length > 0;
    }

    private void SyncSheetFlags()
    {
        AcctSheetRename = _acctSheet == Acct2Sheet.Rename;
        AcctSheetRemove = _acctSheet == Acct2Sheet.Remove;
        AcctSheetInvitePay = _acctSheet == Acct2Sheet.InvitePay;
        AcctSheetNoFunds = _acctSheet == Acct2Sheet.NoFunds;
        AcctSheetQr = _acctSheet == Acct2Sheet.Qr;
        AcctSheetRotate = _acctSheet == Acct2Sheet.Rotate;
        AcctSheetRotateDone = _acctSheet == Acct2Sheet.RotateDone;
        AcctSheetFriend = _acctSheet == Acct2Sheet.Friend;
        AcctSheetCloseOnly = _acctSheet is Acct2Sheet.Qr or Acct2Sheet.RotateDone or Acct2Sheet.Friend;
    }

    private void CloseAcctSheet()
    {
        if (AcctSheetBusy)
        {
            return;
        }
        AcctShowSheet = false;
        _acctSheet = Acct2Sheet.None;
        SyncSheetFlags();
        // The invite link and QR are one-time secrets: not kept around after the sheet closes.
        AcctQrPng = null;
        AcctInviteLink = string.Empty;
        _acctRotatedLink = null;
        AcctRenameText = string.Empty;
    }

    private void SheetError(string text) => OnUi(() =>
    {
        AcctSheetError = text;
        AcctSheetHasError = true;
        AcctSheetBusy = false;
    });

    private void SheetBusy() => OnUi(() =>
    {
        AcctSheetBusy = true;
        AcctSheetHasError = false;
    });

    /// <summary>The primary button of whichever sheet is open.</summary>
    private async Task AcctSheetOkAsync()
    {
        switch (_acctSheet)
        {
            case Acct2Sheet.Rename:
                await AcctRenameAsync();
                break;
            case Acct2Sheet.Remove:
                await AcctRemoveAsync();
                break;
            case Acct2Sheet.InvitePay:
                await AcctInviteAsync(confirmPay: true);
                break;
            case Acct2Sheet.Rotate:
                await AcctRotateAsync();
                break;
            default:
                OnUi(CloseAcctSheet);
                break;
        }
    }

    #endregion Sheet

    #region Actions

    /// <summary>Common failure mapping; null = not a failure. NotManager flips the page into the guest card.</summary>
    private string? AcctFailText(TermitusAcctResult r)
    {
        switch (r.Kind)
        {
            case TermitusAcctKind.Ok:
                return null;
            case TermitusAcctKind.Offline:
                return Loc.NoConnection;
            case TermitusAcctKind.NotManager:
                OnUi(() =>
                {
                    Acct2Guest = true;
                    Acct2Manager = false;
                    AcctShowSheet = false;
                    _acctSheet = Acct2Sheet.None;
                    SyncSheetFlags();
                });
                return Loc.AcctGuestTitle;
            case TermitusAcctKind.TooMany:
                return Loc.AcctInviteTooMany;
            case TermitusAcctKind.Refused:
                return r.Detail ?? Loc.AcctActionFailed;
            default:
                return Loc.AcctActionFailed;
        }
    }

    private async Task<(SubItem? Sub, int? Port)> AcctCtxAsync()
    {
        var sub = await CurrentSubAsync();
        int? port = _connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null;
        return (sub, port);
    }

    private async Task AcctRenameAsync()
    {
        var name = AcctRenameText.Trim();
        var id = _acctTargetId;
        var (sub, port) = await AcctCtxAsync();
        if (sub == null || id == null || name.Length == 0)
        {
            return;
        }
        SheetBusy();
        var res = await TermitusAccountApi.RenameAsync(sub, id.Value, name, port, CancellationToken.None);
        if (AcctFailText(res) is { } err)
        {
            SheetError(err);
            return;
        }
        OnUi(CloseAcctSheet);
        await Acct2LoadAsync(force: true);
    }

    private async Task AcctRemoveAsync()
    {
        var id = _acctTargetId;
        var (sub, port) = await AcctCtxAsync();
        if (sub == null || id == null)
        {
            return;
        }
        var wasThis = _acctDevices.FirstOrDefault(d => d.Id == id)?.IsThis == true;
        SheetBusy();
        var res = await TermitusAccountApi.RemoveAsync(sub, id.Value, port, CancellationToken.None);
        if (AcctFailText(res) is { } err)
        {
            SheetError(err);
            return;
        }
        OnUi(() =>
        {
            AcctSheetBusy = false;
            CloseAcctSheet();
        });
        if (wasThis)
        {
            // This device just lost its own access: sign out here too (nothing left to show).
            await LogoutAsync();
            return;
        }
        await Acct2LoadAsync(force: true);
    }

    private async Task AcctToggleManageAsync(int id)
    {
        var d = _acctDevices.FirstOrDefault(x => x.Id == id);
        var (sub, port) = await AcctCtxAsync();
        if (d == null || sub == null)
        {
            return;
        }
        var res = await TermitusAccountApi.SetManageAsync(sub, id, !d.CanManage, port, CancellationToken.None);
        if (AcctFailText(res) is { } err)
        {
            NoticeManager.Instance.Enqueue(err);
            return;
        }
        _acctMenuId = null;
        await Acct2LoadAsync(force: true);
    }

    private async Task AcctInviteAsync(bool confirmPay)
    {
        var (sub, port) = await AcctCtxAsync();
        if (sub == null)
        {
            return;
        }
        if (confirmPay)
        {
            SheetBusy();
        }
        else
        {
            if (Acct2Inviting)
            {
                return;
            }
            OnUi(() => Acct2Inviting = true);
        }
        try
        {
            var res = await TermitusAccountApi.InviteAsync(sub, confirmPay, port, CancellationToken.None);
            var en = Loc.En;
            if (res.Kind == TermitusAcctKind.PaymentRequired)
            {
                var nf = TermitusAccountApi.ParseNoFunds(res.Body);
                var body = nf != null ? Loc.AcctNoFundsBody(nf.PriceRub, TermitusAccount.FormatMoney(nf.BalanceRub, en)) : Loc.AcctNoFundsBodyPlain;
                OnUi(() => OpenAcctSheet(Acct2Sheet.NoFunds, Loc.AcctNoFundsTitle, body));
                return;
            }
            if (AcctFailText(res) is { } err)
            {
                if (confirmPay)
                {
                    SheetError(err);
                }
                else if (res.Kind != TermitusAcctKind.NotManager)
                {
                    NoticeManager.Instance.Enqueue(err);
                }
                return;
            }
            var inv = TermitusAccountApi.ParseInvite(res.Body);
            if (inv == null)
            {
                var msg = Loc.AcctInviteFailed;
                if (confirmPay)
                {
                    SheetError(msg);
                }
                else
                {
                    NoticeManager.Instance.Enqueue(msg);
                }
                return;
            }
            if (inv.NeedsPayment)
            {
                var body = Loc.AcctInvitePayBody(inv.PriceRub, TermitusAccount.FormatMoney(inv.BalanceRub, en));
                OnUi(() => OpenAcctSheet(Acct2Sheet.InvitePay, Loc.AcctInvitePayTitle, body));
                return;
            }
            byte[]? png = null;
            try
            {
                png = QRCodeUtils.GenQRCode(inv.JoinUrl);
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"{_tag}: invite QR failed ({ex.GetType().Name})");
            }
            var url = inv.JoinUrl!;
            OnUi(() =>
            {
                OpenAcctSheet(Acct2Sheet.Qr, Loc.AcctInviteTitle, Loc.AcctInviteScan);
                AcctQrPng = png;
                AcctInviteLink = url;
            });
            // A paid slot is only charged when the code is used; the list will show it then (on the next open / «Обновить»).
        }
        finally
        {
            OnUi(() =>
            {
                Acct2Inviting = false;
                AcctSheetBusy = false;
            });
        }
    }

    private async Task AcctSetRewardAsync(string reward)
    {
        var want = reward == "days" ? "days" : "money";
        var (sub, port) = await AcctCtxAsync();
        if (sub == null)
        {
            return;
        }
        var wasDays = RewardIsDays;
        if (wasDays == (want == "days"))
        {
            return;
        }
        OnUi(() =>
        {
            RewardIsDays = want == "days";
            RewardIsMoney = want != "days";
        });
        var res = await TermitusAccountApi.SetRewardAsync(sub, want, port, CancellationToken.None);
        if (AcctFailText(res) is { } err)
        {
            OnUi(() =>
            {
                RewardIsDays = wasDays;
                RewardIsMoney = !wasDays;
            });
            NoticeManager.Instance.Enqueue(res.Kind == TermitusAcctKind.Offline ? err : Loc.AcctRewardFailed);
        }
    }

    private async Task AcctRotateAsync()
    {
        var (sub, port) = await AcctCtxAsync();
        if (sub == null)
        {
            return;
        }
        SheetBusy();
        var res = await TermitusAccountApi.RotateLinkAsync(sub, port, CancellationToken.None);
        if (AcctFailText(res) is { } err)
        {
            SheetError(res.Kind == TermitusAcctKind.Offline ? err : Loc.AcctRotateFailed);
            return;
        }
        _acctRotatedLink = TermitusAccountApi.ParseRotated(res.Body);
        OnUi(() =>
        {
            AcctSheetBusy = false;
            OpenAcctSheet(Acct2Sheet.RotateDone, Loc.AcctRotateDoneTitle, Loc.AcctRotateDoneBody);
        });
    }

    /// <summary>Test tour: the state of the 2.0 blocks in one line.</summary>
    public string DebugAcct2State()
    {
        return $"manager={Acct2Manager} guest={Acct2Guest} offline={Acct2Offline} loadError={Acct2LoadError} loading={Acct2Loading} "
            + $"devices=[{string.Join(" | ", AcctDevices.Select(d => $"{d.Id}:{d.Name} ({d.Meta})" + (d.IsThis ? " THIS" : "") + (d.Manages ? " MGR" : "") + (d.PaidSlot ? " PAID" : "") + (d.Paused ? " PAUSED" : "")))}] "
            + $"friends: has={Acct2HasRef} link={(RefHasLink ? "yes" : "no")} invited={RefInvited} earned='{RefEarned}' reward={(RewardIsDays ? "days" : "money")} rule='{RefRule}' "
            + $"payments={AcctPayments.Count}{(Acct2PaymentsMore ? "+more" : "")} expanded={Acct2PaymentsExpanded} [{string.Join(" | ", AcctPayments.Select(p => $"{p.What} {p.Amount} {p.Caption}"))}] "
            + $"sheet={(AcctShowSheet ? _acctSheet.ToString() : "-")} title='{AcctSheetTitle}' body='{AcctSheetBody}' error='{AcctSheetError}' qr={(AcctQrPng?.Length ?? 0)}b "
            + $"inviteLink={(AcctInviteLink.Length > 0 ? "yes" : "no")} renameText='{AcctRenameText}'";
    }

    /// <summary>Test tour: runs a tour step of the 2.0 blocks on the thread pool; returns when it is done.</summary>
    public Task DebugAcct2Async(string step, string arg)
    {
        return Bg(async () =>
        {
            // "" = the first device that is not this one (the sample data has one guest and one paid slot).
            int Pick() => int.TryParse(arg, out var n) ? n : _acctDevices.FirstOrDefault(d => !d.IsThis)?.Id ?? _acctDevices.FirstOrDefault()?.Id ?? 0;
            switch (step)
            {
                case "load":
                    await Acct2LoadAsync(force: true);
                    break;
                case "invite":
                    await AcctInviteAsync(confirmPay: false);
                    break;
                case "invite-pay":
                    await AcctInviteAsync(confirmPay: true);
                    break;
                case "menu":
                    OnUi(() => AcctMenuCmd.Execute(Pick()).Subscribe(_ => { }, _ => { }));
                    break;
                case "rename-ask":
                    OnUi(() => AcctRenameAskCmd.Execute(Pick()).Subscribe(_ => { }, _ => { }));
                    break;
                case "rename": // "rename:TEXT" -> TEXT goes into the box of the open rename sheet (opened first when needed)
                    if (_acctSheet != Acct2Sheet.Rename)
                    {
                        OnUi(() => AcctRenameAskCmd.Execute(Pick()).Subscribe(_ => { }, _ => { }));
                        await Task.Delay(300);
                    }
                    OnUi(() => AcctRenameText = arg);
                    await Task.Delay(200);
                    await AcctRenameAsync();
                    break;
                case "remove-ask":
                    OnUi(() => AcctRemoveAskCmd.Execute(Pick()).Subscribe(_ => { }, _ => { }));
                    break;
                case "remove":
                    if (_acctSheet != Acct2Sheet.Remove)
                    {
                        OnUi(() => AcctRemoveAskCmd.Execute(Pick()).Subscribe(_ => { }, _ => { }));
                        await Task.Delay(300);
                    }
                    await AcctRemoveAsync();
                    break;
                case "manage":
                    await AcctToggleManageAsync(Pick());
                    break;
                case "reward":
                    await AcctSetRewardAsync(arg);
                    break;
                case "payments-all":
                    OnUi(() => AcctPaymentsToggleCmd.Execute().Subscribe(_ => { }, _ => { }));
                    break;
                case "rotate-ask":
                    OnUi(() => AcctRotateAskCmd.Execute().Subscribe(_ => { }, _ => { }));
                    break;
                case "rotate":
                    if (_acctSheet != Acct2Sheet.Rotate)
                    {
                        OnUi(() => AcctRotateAskCmd.Execute().Subscribe(_ => { }, _ => { }));
                        await Task.Delay(300);
                    }
                    await AcctRotateAsync();
                    break;
                case "relogin":
                    OnUi(() => AcctReloginCmd.Execute().Subscribe(_ => { }, _ => { }));
                    break;
                case "close":
                    OnUi(CloseAcctSheet);
                    break;
            }
        });
    }

    #endregion Actions
}
