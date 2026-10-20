using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ServiceLib.Services;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Common;

/// <summary>
/// 0.6.5 Windows tray: the same TER behaviour as the macOS menu-bar item, on top of upstream's
/// Avalonia TrayIcon (on Windows it separates the clicks: left click raises Clicked, right click
/// opens the menu):
///   left click  -> show + activate the TER window (restores it when minimized);
///   right click -> menu built from TermitusTrayMenu (Connect, Country ›, Open TER, Report,
///                  «Для опытных ›», Quit), actions run by TermitusTray.Execute (same as macOS);
///   icon + tooltip follow the state (grey = off, mint = on, amber = connecting / attention).
/// Everything is local. Linux keeps upstream's tray untouched.
/// </summary>
internal static class TermitusTrayWin
{
    private static TrayIcon? _icon;
    private static TermitusHomeViewModel? _vm;
    private static string _lastState = string.Empty;
    private static readonly Dictionary<string, WindowIcon> _icons = [];
    private static readonly List<IDisposable> _subs = [];

    public static bool Active => _icon != null;

    public static void Start(MainWindow window, TermitusHomeViewModel vm)
    {
        if (!OperatingSystem.IsWindows() || Design.IsDesignMode || _icon != null)
        {
            return;
        }
        try
        {
            var icon = TrayIcon.GetIcons(Application.Current!)?.FirstOrDefault();
            if (icon == null)
            {
                Logging.SaveLog("TER tray: no tray icon to take over");
                return;
            }
            _icon = icon;
            _vm = vm;

            // Upstream binds Command (toggle window) and its own menu in App.axaml; replace both.
            icon.Command = null;
            icon.Clicked += (_, _) => OnLeftClick(window);

            var sb = StatusBarViewModel.Instance;
            _subs.Add(vm.WhenAnyValue(x => x.IsConnected, x => x.IsConnecting, x => x.StatusWarn)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => { ApplyIcon(); RebuildMenu(); }));
            _subs.Add(vm.WhenAnyValue(x => x.HasSubscription, x => x.IsBusy, x => x.IsAutoChoice)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => RebuildMenu()));
            _subs.Add(sb.WhenAnyValue(x => x.BlSystemProxyClear, x => x.BlSystemProxySet, x => x.BlSystemProxyNothing)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => RebuildMenu()));
            vm.Locations.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(RebuildMenu);

            ApplyIcon();
            RebuildMenu();
            Logging.SaveLog("TER tray: Windows tray icon taken over");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"TER tray: Windows tray failed ({ex.GetType().Name})");
        }
    }

    public static void Stop()
    {
        foreach (var s in _subs)
        {
            s.Dispose();
        }
        _subs.Clear();
        if (_icon != null)
        {
            _icon.IsVisible = false;
            _icon = null;
        }
    }

    private static void OnLeftClick(MainWindow window)
    {
        try
        {
            // Always show + bring to front (restore from minimized); never "toggle".
            window.ShowHideWindow(true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"TER tray: left click failed ({ex.GetType().Name})");
        }
    }

    private static void ApplyIcon()
    {
        if (_icon == null || _vm == null)
        {
            return;
        }
        var state = TermitusTrayMenu.IconState(_vm.IsConnecting, _vm.StatusWarn, _vm.IsConnected);
        if (state == _lastState)
        {
            return;
        }
        _lastState = state;
        try
        {
            if (!_icons.TryGetValue(state, out var icon))
            {
                icon = state switch
                {
                    "off" => AvaUtils.GetAppIcon(ESysProxyType.ForcedClear),   // NotifyIcon1.ico (grey T)
                    "on" => AvaUtils.GetAppIcon(ESysProxyType.ForcedChange),   // NotifyIcon2.ico (yellow T + mint dot)
                    // No amber .ico in the set: draw it once with the same glyph the macOS bar uses.
                    _ => RenderAmber(state),
                };
                _icons[state] = icon;
            }
            _icon.Icon = icon;
            _icon.ToolTipText = TermitusTrayMenu.Tooltip(state, TermitusLocale.Current);
            Logging.SaveLog($"TER tray: icon -> {state}");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"TER tray: icon failed ({ex.GetType().Name})");
        }
    }

    private static WindowIcon RenderAmber(string state)
    {
        using var ms = new MemoryStream(TermitusTrayIcon.RenderPng(state));
        using var bitmap = new Bitmap(ms);
        return new WindowIcon(bitmap); // the icon keeps its own copy; the bitmap is released here
    }

    private static void RebuildMenu()
    {
        if (_icon == null || _vm == null)
        {
            return;
        }
        try
        {
            // Rebuilt only when the state changes (never from an "opening" handler: that would swap the tree under an open popup).
            var items = TermitusTrayMenu.Build(_vm.TrayState(), TermitusLocale.Current);
            _icon.Menu = ToNative(items);
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"TER tray: menu failed ({ex.GetType().Name})");
        }
    }

    private static NativeMenu ToNative(IReadOnlyList<TermitusTrayItem> items)
    {
        var menu = new NativeMenu();
        foreach (var it in items)
        {
            if (it.Kind == TermitusTrayKind.Separator)
            {
                menu.Add(new NativeMenuItemSeparator());
                continue;
            }
            var mi = new NativeMenuItem(it.Title) { IsEnabled = it.Enabled };
            if (it.Kind == TermitusTrayKind.Submenu)
            {
                mi.Menu = ToNative(it.Children ?? []);
            }
            else
            {
                mi.ToggleType = it.Checked ? MenuItemToggleType.CheckBox : MenuItemToggleType.None;
                mi.IsChecked = it.Checked;
                var id = it.Id ?? string.Empty;
                mi.Click += (_, _) => Dispatcher.UIThread.Post(() => TermitusTray.Execute(id), DispatcherPriority.Background);
            }
            menu.Add(mi);
        }
        return menu;
    }
}
