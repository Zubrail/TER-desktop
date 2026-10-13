using System.Collections.ObjectModel;
using ServiceLib.Services;

namespace ServiceLib.ViewModels;

/// <summary>One selectable plan / payment method in the pay sheet.</summary>
public sealed record TermitusPayRow(string Id, string Title, string Caption, bool IsSelected);

/// <summary>
/// 0.5 in-app payment: plans from the backend, pick a plan + method, «Оплатить» opens the
/// provider page in the browser, then the status is polled every 3 s for up to 15 min.
/// If the backend has no payment API yet the sheet just says payment is in the Telegram bot.
/// </summary>
public partial class TermitusHomeViewModel
{
    private enum PayStage { Loading, Bot, Offline, Error, Ready, Starting, Waiting, Paid, Failed, Expired, TimedOut }

    [Reactive] public partial bool ShowPay { get; set; }
    [Reactive] public partial bool PayLoading { get; set; }
    [Reactive] public partial bool PayBot { get; set; }
    [Reactive] public partial bool PayOffline { get; set; }
    [Reactive] public partial bool PayError { get; set; }
    [Reactive] public partial bool PayReady { get; set; }
    [Reactive] public partial bool PayWaiting { get; set; }
    [Reactive] public partial bool PayDone { get; set; }
    [Reactive] public partial bool PayProblem { get; set; }
    [Reactive] public partial bool PayCanStart { get; set; }
    [Reactive] public partial bool PayHasOffer { get; set; }
    [Reactive] public partial string PayProblemText { get; set; }
    [Reactive] public partial string PayWaitText { get; set; }

    public ObservableCollection<TermitusPayRow> PayPlans { get; } = [];
    public ObservableCollection<TermitusPayRow> PayMethods { get; } = [];

    public ReactiveCommand<RxVoid, RxVoid> PayOpenCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> PayCloseCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> PayStartCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> PayRetryCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> PayCancelWaitCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> PayOfferCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> PayBotCmd { get; private set; } = null!;
    public ReactiveCommand<string, RxVoid> PayPickPlanCmd { get; private set; } = null!;
    public ReactiveCommand<string, RxVoid> PayPickMethodCmd { get; private set; } = null!;

    private TermitusPlans? _payPlans;
    private string? _payPlanId;
    private string? _payMethodId;
    private string? _payPageUrl;
    private CancellationTokenSource? _payCts;
    private volatile bool _payWaiting;
    private volatile bool _payOpenRequested;

    private void InitPayments()
    {
        PayProblemText = string.Empty;
        PayWaitText = string.Empty;
        PayOpenCmd = ReactiveCommand.CreateFromTask(() => Bg(PayOpenAsync));
        PayRetryCmd = ReactiveCommand.CreateFromTask(() => Bg(PayLoadPlansAsync));
        PayCloseCmd = ReactiveCommand.CreateFromTask(() =>
        {
            // The sheet closes; a running wait keeps going in the background and refreshes «Аккаунт» when paid.
            ShowPay = false;
            return Task.CompletedTask;
        });
        PayStartCmd = ReactiveCommand.CreateFromTask(() => Bg(PayStartAsync));
        PayCancelWaitCmd = ReactiveCommand.CreateFromTask(() =>
        {
            try
            {
                _payCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // finished on its own
            }
            return Task.CompletedTask;
        });
        PayOfferCmd = ReactiveCommand.CreateFromTask(() =>
        {
            if (_payPlans?.OfferUrl is { } u)
            {
                TermitusHttp.OpenUrl(u);
            }
            return Task.CompletedTask;
        });
        PayBotCmd = ReactiveCommand.CreateFromTask(() =>
        {
            TermitusHttp.OpenUrl(BotUrl);
            return Task.CompletedTask;
        });
        PayPickPlanCmd = ReactiveCommand.CreateFromTask<string>(id =>
        {
            _payPlanId = id;
            RebuildPayRows();
            return Task.CompletedTask;
        });
        PayPickMethodCmd = ReactiveCommand.CreateFromTask<string>(id =>
        {
            _payMethodId = id;
            RebuildPayRows();
            return Task.CompletedTask;
        });
    }

    private void SetPayStage(PayStage stage, string? problem = null, string? wait = null)
    {
        PayLoading = stage == PayStage.Loading;
        PayBot = stage == PayStage.Bot;
        PayOffline = stage == PayStage.Offline;
        PayError = stage == PayStage.Error;
        PayReady = stage is PayStage.Ready or PayStage.Starting;
        PayWaiting = stage == PayStage.Waiting;
        PayDone = stage == PayStage.Paid;
        PayProblem = stage is PayStage.Failed or PayStage.Expired or PayStage.TimedOut;
        PayCanStart = stage == PayStage.Ready;
        PayProblemText = problem ?? string.Empty;
        PayWaitText = wait ?? string.Empty;
    }

    private async Task PayOpenAsync()
    {
        OnUi(() => ShowPay = true);
        if (_payWaiting)
        {
            return; // reopened during a wait: it keeps showing the waiting state
        }
        await PayLoadPlansAsync();
    }

    private async Task PayLoadPlansAsync()
    {
        OnUi(() => SetPayStage(PayStage.Loading));
        var sub = await CurrentSubAsync();
        if (sub == null)
        {
            OnUi(() => SetPayStage(PayStage.Bot));
            return;
        }
        int? port = _connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null;
        var res = await TermitusPayments.GetPlansAsync(sub, port, CancellationToken.None);
        switch (res.Kind)
        {
            case TermitusPaymentsKind.Ok when res.Plans != null:
                _payPlans = res.Plans;
                _payPlanId = res.Plans.Plans[0].Id;
                _payMethodId = res.Plans.Methods[0].Id;
                OnUi(() =>
                {
                    PayHasOffer = res.Plans.OfferUrl != null;
                    RebuildPayRows();
                    SetPayStage(PayStage.Ready);
                });
                break;

            case TermitusPaymentsKind.Offline:
                OnUi(() => SetPayStage(PayStage.Offline));
                break;

            case TermitusPaymentsKind.Error:
                OnUi(() => SetPayStage(PayStage.Error));
                break;

            default:
                OnUi(() => SetPayStage(PayStage.Bot));
                break;
        }
    }

    private void RebuildPayRows()
    {
        var p = _payPlans;
        if (p == null)
        {
            return;
        }
        var plans = p.Plans
            .Select(x => new TermitusPayRow(x.Id, x.Title,
                TermitusPayments.FormatPrice(x.PriceRub) + (x.Days > 0 ? " · " + Loc.PayDays(x.Days) : string.Empty),
                x.Id == _payPlanId))
            .ToList();
        var methods = p.Methods.Select(x => new TermitusPayRow(x.Id, x.Title, string.Empty, x.Id == _payMethodId)).ToList();
        OnUi(() =>
        {
            TermitusCollections.SyncReplace(PayPlans, plans, r => r);
            TermitusCollections.SyncReplace(PayMethods, methods, r => r);
        });
    }

    private async Task PayStartAsync()
    {
        if (_payPlans == null || _payPlanId == null || _payMethodId == null || _payWaiting)
        {
            return;
        }
        var sub = await CurrentSubAsync();
        if (sub == null)
        {
            return;
        }
        OnUi(() => SetPayStage(PayStage.Starting));
        int? port = _connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null;
        var start = await TermitusPayments.StartAsync(sub, _payPlanId, _payMethodId, port, CancellationToken.None);
        switch (start.Kind)
        {
            case TermitusPaymentsKind.Ok:
                break;

            case TermitusPaymentsKind.Offline:
                OnUi(() => SetPayStage(PayStage.Offline));
                return;

            case TermitusPaymentsKind.Unavailable:
                OnUi(() => SetPayStage(PayStage.Bot));
                return;

            default:
                OnUi(() => SetPayStage(PayStage.Error));
                return;
        }

        _payPageUrl = start.PayUrl;
        TermitusHttp.OpenUrl(start.PayUrl!);
        _payWaiting = true;
        var cts = new CancellationTokenSource();
        _payCts = cts;
        OnUi(() => SetPayStage(PayStage.Waiting, wait: Loc.PayWaiting));
        try
        {
            var paymentId = start.PaymentId!;
            var outcome = await TermitusPayments.WaitAsync(
                async ct =>
                {
                    int? p = _connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null;
                    return await TermitusPayments.GetStatusAsync(sub, paymentId, p, ct);
                },
                TermitusPayments.PollInterval, TermitusPayments.MaxWait, Task.Delay, cts.Token);
            Logging.SaveLog($"{_tag}: payment finished: {outcome}");
            switch (outcome)
            {
                case TermitusWaitOutcome.Paid:
                    OnUi(() => SetPayStage(PayStage.Paid));
                    NoticeManager.Instance.Enqueue(Loc.PayPaidToast);
                    await RefreshAccountAsync(manual: true, retry: true);
                    break;

                case TermitusWaitOutcome.Failed:
                    OnUi(() => SetPayStage(PayStage.Failed, Loc.PayFailed));
                    break;

                case TermitusWaitOutcome.Expired:
                    OnUi(() => SetPayStage(PayStage.Expired, Loc.PayExpired));
                    break;

                case TermitusWaitOutcome.TimedOut:
                    OnUi(() => SetPayStage(PayStage.TimedOut, Loc.PayTimedOut));
                    // It may still be processing on the bank side: refresh once so a late payment shows up.
                    await RefreshAccountAsync(manual: true, retry: true);
                    break;

                default: // cancelled by the user: back to the choice
                    OnUi(() => SetPayStage(PayStage.Ready));
                    break;
            }
        }
        finally
        {
            _payWaiting = false;
            _payCts = null;
            cts.Dispose();
        }
    }
}
