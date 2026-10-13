using ServiceLib.Services;

namespace ServiceLib.ViewModels;

/// <summary>
/// 0.5: live numbers on the connect screen and in the tray popover (ping, down/up speed, a
/// 60-second graph), the route/protocol chips. Speeds come from upstream's local statistics
/// event; the ping is one HEAD request through our own tunnel every 10 s while a TER window
/// or the tray popover is open. Nothing is stored or sent to TER.
/// </summary>
public partial class TermitusHomeViewModel
{
    [Reactive] public partial string LiveDownText { get; set; }
    [Reactive] public partial string LiveUpText { get; set; }
    [Reactive] public partial string LivePingText { get; set; }
    [Reactive] public partial bool LivePingGood { get; set; }
    [Reactive] public partial bool LivePingMid { get; set; }
    [Reactive] public partial bool LivePingBad { get; set; }
    [Reactive] public partial string GraphDownPath { get; set; }
    [Reactive] public partial string GraphUpPath { get; set; }
    [Reactive] public partial string RouteChipText { get; set; }
    [Reactive] public partial string ProtocolChipText { get; set; }
    [Reactive] public partial string Protocol { get; set; }
    [Reactive] public partial bool ProtocolAuto { get; set; }
    [Reactive] public partial bool ProtocolTcp { get; set; }
    [Reactive] public partial bool ProtocolUdp { get; set; }
    [Reactive] public partial string ConnectCaption { get; set; }
    [Reactive] public partial string ThemeChoice { get; set; }
    [Reactive] public partial bool ThemeSystem { get; set; }
    [Reactive] public partial bool ThemeLight { get; set; }
    [Reactive] public partial bool ThemeDark { get; set; }
    public ReactiveCommand<string, RxVoid> SetThemeCmd { get; private set; } = null!;
    [Reactive] public partial string PopStatus { get; set; }
    [Reactive] public partial string PopSubline { get; set; }

    /// <summary>Set by the tray popover while it is open (it shows the same live numbers).</summary>
    public volatile bool PopoverActive;

    private readonly TermitusSpeedHistory _speed = new();
    private System.Threading.Timer? _liveTimer;
    private DateTime _lastPing = DateTime.MinValue;
    private DateTime _lastSpeedUi = DateTime.MinValue;
    private int _pingRunning;
    private volatile HashSet<string> _udpIds = [];

    private void InitLive()
    {
        ThemeChoice = TermitusSettings.Theme;
        ApplyThemeFlags(ThemeChoice);
        SetThemeCmd = ReactiveCommand.CreateFromTask<string>(t =>
        {
            TermitusSettings.Theme = t;
            ThemeChoice = TermitusSettings.Theme;
            ApplyThemeFlags(ThemeChoice);
            return Task.CompletedTask;
        });
        LiveDownText = TermitusLive.FormatSpeed(0);
        LiveUpText = TermitusLive.FormatSpeed(0);
        LivePingText = "—";
        (GraphDownPath, GraphUpPath) = TermitusLive.GraphPaths(new long[TermitusSpeedHistory.Capacity], new long[TermitusSpeedHistory.Capacity]);
        Protocol = TermitusSettings.Protocol;
        ApplyProtocolFlags(Protocol);
        RouteChipText = Loc.RouteChipExceptRu;
        ConnectCaption = Loc.ConnectCaptionOff;

        this.WhenAnyValue(x => x.RouteExceptRu, x => x.RouteBlockedOnly, x => x.RouteAll)
            .Subscribe(t => RouteChipText = t.Item2 ? Loc.RouteChipBlocked : t.Item3 ? Loc.RouteChipAll : Loc.RouteChipExceptRu);
        this.WhenAnyValue(x => x.IsConnected, x => x.IsConnecting)
            .Subscribe(t => ConnectCaption = t.Item2 ? Loc.ConnectCaptionBusy : t.Item1 ? Loc.ConnectCaptionOn : Loc.ConnectCaptionOff);

        PopStatus = Loc.PopOff;
        PopSubline = Loc.ConnectCaptionOff;
        this.WhenAnyValue(x => x.IsConnected, x => x.IsConnecting, x => x.StatusWarn)
            .Subscribe(t =>
            {
                PopStatus = t.Item2 ? Loc.PopBusy : t.Item3 ? Loc.PopWarn : t.Item1 ? Loc.PopOn : Loc.PopOff;
                PopSubline = t.Item2 ? Loc.ConnectCaptionBusy : t.Item1 ? Loc.ConnectCaptionOn : Loc.ConnectCaptionOff;
            });

        AppEvents.DispatcherStatisticsRequested
            .AsObservable()
            .Subscribe(item => OnSpeedSample(item.ProxyDown, item.ProxyUp));
    }

    private void ApplyThemeFlags(string t)
    {
        ThemeSystem = t == "system";
        ThemeLight = t == "light";
        ThemeDark = t == "dark";
    }

    private void ApplyProtocolFlags(string p)
    {
        ProtocolAuto = p == "auto";
        ProtocolTcp = p == "tcp";
        ProtocolUdp = p == "udp";
        ProtocolChipText = p switch { "tcp" => Loc.ProtocolTcpShort, "udp" => Loc.ProtocolUdpShort, _ => Loc.ProtocolAutoShort };
    }

    /// <summary>Knows which servers are Hysteria2 (UDP) so the protocol preference can pick by kind.</summary>
    private void NoteTransportKinds(IEnumerable<ProfileItem> profiles)
    {
        _udpIds = profiles.Where(p => p.ConfigType == EConfigType.Hysteria2).Select(p => p.IndexId).ToHashSet();
    }

    private Func<string, bool>? PreferredTransport()
    {
        var udp = _udpIds;
        return TermitusSettings.Protocol switch
        {
            "udp" => id => udp.Contains(id),
            "tcp" => id => !udp.Contains(id),
            _ => null,
        };
    }

    private async Task SetProtocolAsync(string p)
    {
        p = p is "tcp" or "udp" ? p : "auto";
        TermitusSettings.Protocol = p;
        OnUi(() =>
        {
            Protocol = p;
            ApplyProtocolFlags(p);
        });
        // Connected and we know the test results: switch right away, otherwise it applies on the next Connect.
        if (_connected && !_busy && !_recovering && !_delays.IsEmpty)
        {
            SetBusy(true);
            try
            {
                var server = await ApplyChoiceAsync();
                if (server != null)
                {
                    await _main.Reload();
                }
            }
            finally
            {
                SetBusy(false);
                await RefreshStateAsync();
            }
        }
    }

    private void UpdateLiveForState(bool connected)
    {
        if (connected)
        {
            _liveTimer ??= new System.Threading.Timer(_ => LiveTick(), null, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2));
            return;
        }
        _liveTimer?.Dispose();
        _liveTimer = null;
        _speed.Clear();
        _lastPing = DateTime.MinValue;
        var (a, l) = TermitusLive.GraphPaths(new long[TermitusSpeedHistory.Capacity], new long[TermitusSpeedHistory.Capacity]);
        OnUi(() =>
        {
            LiveDownText = TermitusLive.FormatSpeed(0);
            LiveUpText = TermitusLive.FormatSpeed(0);
            LivePingText = "—";
            SetPingClass(-1);
            GraphDownPath = a;
            GraphUpPath = l;
        });
    }

    private void SetPingClass(int ms)
    {
        var c = TermitusLive.PingClass(ms);
        LivePingGood = c == "good";
        LivePingMid = c == "mid";
        LivePingBad = c == "bad";
    }

    private void OnSpeedSample(long down, long up)
    {
        if (!_connected)
        {
            return;
        }
        _speed.Add(down, up);
        // Only repaint when someone can see it, at most ~2 times a second.
        if (!(UiActive || PopoverActive) || (DateTime.UtcNow - _lastSpeedUi).TotalMilliseconds < 450)
        {
            return;
        }
        _lastSpeedUi = DateTime.UtcNow;
        var (d, u) = _speed.Snapshot();
        var (area, line) = TermitusLive.GraphPaths(d, u);
        var dt = TermitusLive.FormatSpeed(down);
        var ut = TermitusLive.FormatSpeed(up);
        OnUi(() =>
        {
            LiveDownText = dt;
            LiveUpText = ut;
            GraphDownPath = area;
            GraphUpPath = line;
        });
    }

    private void LiveTick()
    {
        try
        {
            if (!_connected || _busy || _recovering || !(UiActive || PopoverActive))
            {
                return;
            }
            if ((DateTime.UtcNow - _lastPing).TotalSeconds < 10 || Interlocked.Exchange(ref _pingRunning, 1) == 1)
            {
                return;
            }
            _lastPing = DateTime.UtcNow;
            _ = Task.Run(async () =>
            {
                try
                {
                    var ms = await TermitusLive.PingThroughTunnelAsync(
                        AppManager.Instance.GetLocalPort(EInboundProtocol.socks),
                        _config.SpeedTestItem?.SpeedPingTestUrl ?? Global.SpeedPingTestUrls.First(),
                        CancellationToken.None);
                    OnUi(() =>
                    {
                        LivePingText = ms > 0 ? $"{ms} {Loc.MsUnit}" : "—";
                        SetPingClass(ms);
                    });
                }
                finally
                {
                    Volatile.Write(ref _pingRunning, 0);
                }
            });
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: live tick failed ({ex.GetType().Name})");
        }
    }

    /// <summary>Menu-bar menu state (UI thread).</summary>
    public TermitusTrayState TrayState()
    {
        var sb = StatusBarViewModel.Instance;
        return new TermitusTrayState(
            IsConnected && !IsConnecting,
            IsConnecting || IsBusy,
            HasSubscription,
            IsAutoChoice,
            Locations.Select(l => (l.Key, l.Title, l.IsSelected)).ToList(),
            sb.BlSystemProxyClear,
            sb.BlSystemProxySet,
            sb.BlSystemProxyNothing);
    }

    /// <summary>Test-tour only: shows the connected look with sample numbers (no real tunnel).</summary>
    public void DebugFakeConnected()
    {
        _connected = true;
        _connectedAt = DateTime.Now.AddMinutes(-12);
        var rnd = new Random(7);
        for (var i = 0; i < TermitusSpeedHistory.Capacity; i++)
        {
            _speed.Add((long)(300_000 + 1_800_000 * Math.Abs(Math.Sin(i / 6.0)) + rnd.Next(0, 200_000)), rnd.Next(20_000, 260_000));
        }
        var (d, u) = _speed.Snapshot();
        var (area, line) = TermitusLive.GraphPaths(d, u);
        OnUi(() =>
        {
            IsConnected = true;
            StatusText = Loc.Connected;
            LiveDownText = TermitusLive.FormatSpeed(d[^1]);
            LiveUpText = TermitusLive.FormatSpeed(u[^1]);
            LivePingText = $"46 {Loc.MsUnit}";
            SetPingClass(46);
            GraphDownPath = area;
            GraphUpPath = line;
        });
    }
}
