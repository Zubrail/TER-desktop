using System.Collections.ObjectModel;
using ServiceLib.Services;

namespace ServiceLib.ViewModels;

/// <summary>One suggestion under the check results (button text = what it does).</summary>
public sealed record TermitusAdviceRow(string Text, string ActionId, string ButtonText, bool HasButton);

/// <summary>
/// 0.6: «Проверка подключения» (per server × protocol, local real-delay test) and the friendly
/// connection-error card (what failed, what still works, «Попробовать другой сервер», «Сообщить о проблеме»).
/// The test is the same one «Проверить скорость» and Connect already run (SpeedtestService Realping through the
/// core): it talks to our own servers and nothing else, and its result is only shown here and — if the user
/// attaches it — in the problem report.
/// </summary>
public partial class TermitusHomeViewModel
{
    [Reactive] public partial bool ShowCheck { get; set; }
    [Reactive] public partial bool CheckRunning { get; set; }
    [Reactive] public partial bool CheckHasResults { get; set; }
    [Reactive] public partial bool CheckNoServers { get; set; }
    [Reactive] public partial string CheckStamp { get; set; }

    public ObservableCollection<TermitusCheckRowData> CheckRows { get; } = [];
    public ObservableCollection<TermitusAdviceRow> CheckAdvice { get; } = [];

    public ReactiveCommand<RxVoid, RxVoid> CheckRunCmd { get; private set; } = null!;
    public ReactiveCommand<string, RxVoid> CheckAdviceCmd { get; private set; } = null!;

    // Connection-error card on Home.
    [Reactive] public partial bool ShowConnError { get; set; }
    [Reactive] public partial string ConnErrTitle { get; set; }
    [Reactive] public partial string ConnErrFailed { get; set; }
    [Reactive] public partial string ConnErrWorks { get; set; }

    public ReactiveCommand<RxVoid, RxVoid> ConnErrTryOtherCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> ConnErrDismissCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> ConnErrCheckCmd { get; private set; } = null!;

    private volatile List<TermitusProbe> _lastProbes = [];
    private DateTime? _lastProbeAt;

    private void InitCheck()
    {
        CheckStamp = string.Empty;
        ConnErrTitle = string.Empty;
        ConnErrFailed = string.Empty;
        ConnErrWorks = string.Empty;
        CheckRunCmd = ReactiveCommand.CreateFromTask(() => Bg(RunCheckAsync));
        CheckAdviceCmd = ReactiveCommand.CreateFromTask<string>(id => Bg(() => DoAdviceAsync(id)));
        ConnErrTryOtherCmd = ReactiveCommand.CreateFromTask(() => Bg(TryAnotherServerAsync));
        ConnErrDismissCmd = ReactiveCommand.CreateFromTask(() =>
        {
            ShowConnError = false;
            return Task.CompletedTask;
        });
        ConnErrCheckCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            ShowConnError = false;
            await NavigateAsync("check");
        });
    }

    /// <summary>All imported servers with their last real-delay result (ms, -1 = no answer).</summary>
    private async Task<List<TermitusProbe>> BuildProbesAsync()
    {
        var profiles = await AppManager.Instance.ProfileItems("") ?? [];
        var rules = TermitusTestHooks.IsTestInstance ? TermitusConnCheck.ParseSpec(TermitusTestHooks.ProbeSpec) : [];
        var list = new List<TermitusProbe>();
        foreach (var p in profiles.Where(p => p.ConfigType != EConfigType.Custom))
        {
            var key = TermitusLocations.KeyOf(p.Remarks);
            if (key.Length == 0 || p.IndexId.IsNullOrEmpty())
            {
                continue;
            }
            var kind = p.ConfigType == EConfigType.Hysteria2 ? TermitusProbeKind.Udp : TermitusProbeKind.Tcp;
            var tech = p.ConfigType.ToString().ToUpperInvariant() + (p.Network.IsNullOrEmpty() || p.Network == "tcp" ? string.Empty : "/" + p.Network);
            var ms = _delays.TryGetValue(p.IndexId, out var d) ? d : -1;
            list.Add(new TermitusProbe(p.IndexId, key, TermitusLocations.IsRussia(key), kind, tech, rules.Count > 0 ? TermitusConnCheck.ApplySpec(rules, key, kind, ms) : ms));
        }
        return list;
    }

    private async Task RunCheckAsync()
    {
        if (CheckRunning || _busy || IsTesting)
        {
            return;
        }
        OnUi(() =>
        {
            CheckRunning = true;
            CheckNoServers = false;
        });
        try
        {
            if (!await HasProfilesAsync())
            {
                OnUi(() =>
                {
                    CheckNoServers = true;
                    CheckHasResults = false;
                });
                return;
            }
            await TestAllAsync();
            await PublishCheckAsync();
        }
        finally
        {
            OnUi(() => CheckRunning = false);
        }
    }

    /// <summary>Turns the current delays into rows + advice and remembers them for the report.</summary>
    private async Task PublishCheckAsync()
    {
        var probes = await BuildProbesAsync();
        _lastProbes = probes;
        _lastProbeAt = DateTime.Now;
        var rows = TermitusConnCheck.Rows(probes, Loc);
        var advice = TermitusConnCheck.Analyze(probes, Loc)
            .Select(a => new TermitusAdviceRow(a.Text, a.Action.ToString(), AdviceButton(a.Action), a.Action != TermitusAdviceAction.None))
            .ToList();
        var stamp = Loc.CheckStampText(DateTime.Now);
        OnUi(() =>
        {
            CheckRows.Clear();
            foreach (var r in rows)
            {
                CheckRows.Add(r);
            }
            CheckAdvice.Clear();
            foreach (var a in advice)
            {
                CheckAdvice.Add(a);
            }
            CheckHasResults = rows.Count > 0;
            CheckStamp = stamp;
        });
        RefreshReportPreview();
    }

    private string AdviceButton(TermitusAdviceAction a) => a switch
    {
        TermitusAdviceAction.UseTcp => Loc.CheckBtnUseTcp,
        TermitusAdviceAction.UseUdp => Loc.CheckBtnUseUdp,
        TermitusAdviceAction.OtherServer => Loc.CheckBtnOtherServer,
        TermitusAdviceAction.Report => Loc.ReportOpen,
        _ => string.Empty,
    };

    private async Task DoAdviceAsync(string id)
    {
        switch (id)
        {
            case nameof(TermitusAdviceAction.UseTcp):
                await SetProtocolAsync("tcp");
                NoticeManager.Instance.Enqueue(Loc.CheckProtocolSet);
                break;

            case nameof(TermitusAdviceAction.UseUdp):
                await SetProtocolAsync("udp");
                NoticeManager.Instance.Enqueue(Loc.CheckProtocolSet);
                break;

            case nameof(TermitusAdviceAction.OtherServer):
                await TryAnotherServerAsync();
                break;

            case nameof(TermitusAdviceAction.Report):
                await OpenReportAsync("connect");
                break;
        }
    }

    #region Connection error card

    /// <summary>
    /// A connection step failed: remember it for the report (in memory, masked) and show the card.
    /// <paramref name="logText"/> is one of our fixed phrases (+ an exception type), never a message from outside.
    /// </summary>
    private async Task RaiseConnErrorAsync(string title, string logText)
    {
        TermitusErrorLog.Add(logText);
        var tested = !_delays.IsEmpty;
        var probes = tested ? await BuildProbesAsync() : [];
        if (tested)
        {
            _lastProbes = probes;
            _lastProbeAt = DateTime.Now;
        }
        var (failed, works) = TermitusConnCheck.FailedAndWorks(probes, Loc);
        OnUi(() =>
        {
            ConnErrTitle = title;
            ConnErrFailed = failed;
            ConnErrWorks = works;
            ShowConnError = true;
            ShowOtherVpn = false;
        });
        RefreshReportPreview();
    }

    /// <summary>
    /// «Попробовать другой сервер»: the fastest answering server that is not the one just used, preferring
    /// another country (never Russia unless nothing else answers), then connects (or switches the live connection).
    /// </summary>
    private async Task TryAnotherServerAsync()
    {
        if (_busy)
        {
            return;
        }
        OnUi(() => ShowConnError = false);
        var current = _config.IndexId;
        string? Pick()
        {
            var probes = _locations.SelectMany(l => l.IndexIds.Select(id => (l, id))).ToList();
            var curLoc = probes.FirstOrDefault(x => x.id == current).l;
            bool Ok(string id) => _delays.TryGetValue(id, out var ms) && ms > 0 && id != current;
            var preferred = PreferredTransport();
            IEnumerable<(TermitusLocations.Location l, string id)> cands = probes.Where(x => Ok(x.id));
            var ordered = cands
                .OrderBy(x => x.l.IsRussia ? 2 : x.l == curLoc ? 1 : 0)
                .ThenBy(x => preferred != null && !preferred(x.id) ? 1 : 0)
                .ThenBy(x => _delays[x.id])
                .Select(x => x.id)
                .FirstOrDefault();
            return ordered;
        }

        var pick = _delays.IsEmpty ? null : Pick();
        if (pick == null)
        {
            OnUi(() => StatusText = Loc.FindingBestServer);
            await TestAllAsync();
            pick = Pick();
        }
        if (pick == null)
        {
            await RaiseConnErrorAsync(Loc.ErrTitleNoServer, "нет другого сервера, который отвечает");
            await RefreshStateAsync();
            return;
        }
        Logging.SaveLog($"{_tag}: trying another server");
        await ConfigHandler.SetDefaultServerIndex(_config, pick);
        if (_connected)
        {
            SetBusy(true);
            try
            {
                ResetGuard();
                await _main.Reload();
            }
            finally
            {
                SetBusy(false);
            }
            await RefreshStateAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    #endregion Connection error card
}
