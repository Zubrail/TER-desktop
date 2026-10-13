using System.Collections.ObjectModel;
using ServiceLib.Services;

namespace ServiceLib.ViewModels;

/// <summary>
/// 0.6 «Сообщить о проблеме». The user picks what is wrong, may add text, and sees EXACTLY the message that
/// will be sent (the preview is the sent string). It goes through the existing app support endpoint
/// (POST &lt;subscription&gt;/support, the owner sees it in the support panel / CRM and answers in the chat and
/// in Telegram). Nothing is sent before «Отправить»; offline keeps a draft on this computer and offers «Повторить».
/// </summary>
public partial class TermitusHomeViewModel
{
    private enum ReportStage { Editing, Sending, Sent, Offline, Bot }

    [Reactive] public partial bool ShowReport { get; set; }
    [Reactive] public partial bool ReportEditing { get; set; }
    [Reactive] public partial bool ReportSending { get; set; }
    [Reactive] public partial bool ReportSent { get; set; }
    [Reactive] public partial bool ReportOffline { get; set; }
    [Reactive] public partial bool ReportBot { get; set; }
    [Reactive] public partial string ReportText { get; set; }
    [Reactive] public partial bool ReportAttach { get; set; }
    [Reactive] public partial string ReportPreview { get; set; }
    [Reactive] public partial string ReportError { get; set; }
    [Reactive] public partial bool HasReportError { get; set; }
    [Reactive] public partial bool ReportCanSend { get; set; }
    [Reactive] public partial bool ReportHasCheck { get; set; }
    [Reactive] public partial bool ReportCheckMissing { get; set; }
    [Reactive] public partial bool ReportCheckRunning { get; set; }

    public ObservableCollection<TermitusReportChip> ReportChips { get; } = [];

    public ReactiveCommand<RxVoid, RxVoid> ReportOpenCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> ReportConnectCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> ReportCloseCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> ReportSendCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> ReportRunCheckCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> ReportCopyCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> ReportToChatCmd { get; private set; } = null!;
    public ReactiveCommand<string, RxVoid> ReportPickChipCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> CheckReportCmd { get; private set; } = null!;

    /// <summary>The view puts this text on the clipboard (the VM has no clipboard).</summary>
    public EventChannel<string> ReportCopyRequested { get; } = new();

    private string? _reportChip;
    private volatile string _serverDesc = "—";
    private static readonly TimeSpan CheckFreshFor = TimeSpan.FromMinutes(30);

    private void InitReport()
    {
        ReportText = string.Empty;
        ReportPreview = string.Empty;
        ReportError = string.Empty;
        ReportAttach = true;
        SetReportStage(ReportStage.Editing);

        ReportOpenCmd = ReactiveCommand.CreateFromTask(() => Bg(() => OpenReportAsync(null)));
        ReportConnectCmd = ReactiveCommand.CreateFromTask(() => Bg(() => OpenReportAsync("connect")));
        CheckReportCmd = ReportConnectCmd;
        ReportCloseCmd = ReactiveCommand.CreateFromTask(() =>
        {
            CloseReport();
            return Task.CompletedTask;
        });
        ReportSendCmd = ReactiveCommand.CreateFromTask(() => Bg(SendReportAsync));
        ReportRunCheckCmd = ReactiveCommand.CreateFromTask(() => Bg(async () =>
        {
            OnUi(() => ReportCheckRunning = true);
            try
            {
                await RunCheckAsync();
            }
            finally
            {
                OnUi(() => ReportCheckRunning = false);
            }
            RefreshReportPreview();
        }));
        ReportCopyCmd = ReactiveCommand.CreateFromTask(() =>
        {
            ReportCopyRequested.Publish(ReportPreview);
            return Task.CompletedTask;
        });
        ReportToChatCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            CloseReport();
            await NavigateAsync("support");
        });
        ReportPickChipCmd = ReactiveCommand.CreateFromTask<string>(id =>
        {
            _reportChip = _reportChip == id ? null : id;
            RebuildReportChips();
            RefreshReportPreview();
            return Task.CompletedTask;
        });

        this.WhenAnyValue(x => x.ReportText, x => x.ReportAttach).Skip(1).Subscribe(_ => RefreshReportPreview());
    }

    private void SetReportStage(ReportStage s)
    {
        ReportEditing = s == ReportStage.Editing;
        ReportSending = s == ReportStage.Sending;
        ReportSent = s == ReportStage.Sent;
        ReportOffline = s == ReportStage.Offline;
        ReportBot = s == ReportStage.Bot;
    }

    private void RebuildReportChips()
    {
        var chips = TermitusReport.ChipIds.Select(id => new TermitusReportChip(id, TermitusReport.ChipTitle(id, Loc), id == _reportChip)).ToList();
        OnUi(() =>
        {
            ReportChips.Clear();
            foreach (var c in chips)
            {
                ReportChips.Add(c);
            }
        });
    }

    /// <summary>Opens the sheet (chip null = keep the draft's / nothing selected). Safe from any thread.</summary>
    public async Task OpenReportAsync(string? chip)
    {
        var draft = TermitusReportDraft.Load();
        _reportChip = chip ?? (draft?.Chip is { } c && TermitusReport.ChipIds.Contains(c) && draft.Text.Length > 0 ? c : null);
        var text = draft?.Text ?? string.Empty;
        var attach = draft?.Attach ?? true;
        var pending = draft?.Pending == true;
        var sub = await CurrentSubAsync();
        OnUi(() =>
        {
            ReportText = text;
            ReportAttach = attach;
            HasReportError = false;
            ReportError = string.Empty;
            RebuildReportChips();
            SetReportStage(sub == null ? ReportStage.Bot : pending ? ReportStage.Offline : ReportStage.Editing);
            ShowReport = true;
        });
        RefreshReportPreview();
    }

    private void CloseReport()
    {
        if (ReportSending)
        {
            return;
        }
        if (!ReportSent && (ReportText.Trim().Length > 0))
        {
            new TermitusReportDraft { Chip = _reportChip ?? "other", Text = ReportText, Attach = ReportAttach, Pending = ReportOffline }.Save();
        }
        else if (!ReportOffline)
        {
            TermitusReportDraft.Delete();
        }
        ShowReport = false;
        if (ReportSent)
        {
            ReportText = string.Empty;
            _reportChip = null;
            SetReportStage(ReportStage.Editing);
        }
    }

    /// <summary>Rebuilds the preview from the current inputs (cheap, no network). Any thread.</summary>
    private void RefreshReportPreview()
    {
        if (!ShowReport)
        {
            return;
        }
        var recent = _lastProbeAt is { } at && DateTime.Now - at < CheckFreshFor && _lastProbes.Count > 0;
        var chipTitle = _reportChip == null ? Loc.ChipNone : TermitusReport.ChipTitle(_reportChip, Loc);
        var attach = ReportAttach;
        var text = ReportText;
        var diag = attach ? TermitusReport.BuildDiagnostics(CollectDiagInputNow(recent)) : null;
        var full = TermitusReport.Compose(chipTitle, text, diag, attach);
        var canSend = (_reportChip != null || text.Trim().Length > 0);
        OnUi(() =>
        {
            ReportPreview = full;
            ReportCanSend = canSend;
            ReportHasCheck = recent;
            ReportCheckMissing = !recent;
        });
    }

    private TermitusDiagInput CollectDiagInputNow(bool recent)
    {
        var state = _guardTitle != null ? "защита прервана"
            : IsConnecting || _recovering ? "подключается"
            : _connected ? "подключено" : "отключено";
        return new TermitusDiagInput(
            $"TER {Utils.GetVersionInfo()}",
            TermitusSystemInfo.Describe(),
            _config.TunModeItem?.EnableTun == true ? "TUN (весь трафик)" : "системный прокси",
            _serverDesc,
            state,
            RouteChipText ?? string.Empty,
            ProtocolChipText ?? string.Empty,
            TermitusErrorLog.Last(TermitusReport.MaxErrors),
            recent ? TermitusConnCheck.ReportLines(_lastProbes) : null,
            recent ? _lastProbeAt : null,
            "не запускалась");
    }

    private async Task SendReportAsync()
    {
        if (ReportSending || !ReportCanSend)
        {
            return;
        }
        var message = ReportPreview;
        var sub = await CurrentSubAsync();
        if (sub == null)
        {
            OnUi(() => SetReportStage(ReportStage.Bot));
            return;
        }
        new TermitusReportDraft { Chip = _reportChip ?? "other", Text = ReportText, Attach = ReportAttach, Pending = true }.Save();
        OnUi(() =>
        {
            SetReportStage(ReportStage.Sending);
            HasReportError = false;
        });

        int? port = _connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null;
        var res = new TermitusSendResult(TermitusSendKind.Failed);
        try
        {
            res = await TermitusSupport.SendAsync(sub, message, port, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: report send failed ({ex.GetType().Name})");
        }
        Logging.SaveLog($"{_tag}: report -> {res.Kind}");

        switch (res.Kind)
        {
            case TermitusSendKind.Ok:
                TermitusReportDraft.Delete();
                OnUi(() => SetReportStage(ReportStage.Sent));
                _ = Bg(() => PollSupportAsync(full: !_supportFullLoaded));
                break;

            case TermitusSendKind.Guest or TermitusSendKind.Unavailable:
                OnUi(() => SetReportStage(ReportStage.Bot));
                break;

            case TermitusSendKind.RateLimited:
                ShowReportError(Loc.ChatRateLimited((int)Math.Ceiling(res.RetryAfter?.TotalSeconds ?? 30)));
                break;

            case TermitusSendKind.Invalid:
                ShowReportError(Loc.ReportTooLong);
                break;

            default:
                // No answer / 5xx: keep everything, offer «Повторить».
                OnUi(() => SetReportStage(ReportStage.Offline));
                break;
        }
    }

    private void ShowReportError(string text)
    {
        OnUi(() =>
        {
            ReportError = text;
            HasReportError = true;
            SetReportStage(ReportStage.Editing);
        });
    }
}
