using Avalonia.Styling;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.VisualTree;
using ServiceLib.Services;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// TeRMiTuS design-D home screen. Rendered by SimpleViewLocator when a
/// <see cref="ServiceLib.ViewModels.TermitusHomeViewModel"/> instance is set as
/// a ContentControl's Content; the data template assigns our DataContext, and
/// the compiled XAML bindings (x:DataType) resolve against it.
///
/// View-only behaviour (0.4):
///  - pages and first-run steps fade/slide in (140 ms, ease-out) when they appear;
///    off when the OS asks for reduced motion (TermitusMotion);
///  - tells the VM whether the window is actually on screen (timers stay quiet when not);
///  - chat: Enter sends (Shift+Enter is ignored: one-line input), list sticks to the bottom;
///  - TER_PERF=1: logs the time from a page appearing to its first rendered frame.
/// </summary>
public partial class TermitusHomeView : ReactiveUserControl<TermitusHomeViewModel>
{
    private static readonly TimeSpan PageIn = TimeSpan.FromMilliseconds(140);
    private const double PageInOffset = 6;
    private static readonly Easing PageEasing = new CubicEaseOut();

    private TermitusHomeViewModel? _vm;
    private Window? _window;
    private readonly List<Visual> _watchedAncestors = [];

    public TermitusHomeView()
    {
        InitializeComponent();
        TermitusMotion.DetectOnce();
        ApplySystemFont();

        // Tabular digits: the 1 s session timer / countdown / balance do not jiggle in width.
        var tnum = FontFeatureCollectionParse("+tnum");
        if (tnum != null)
        {
            txtSubStatus.FontFeatures = tnum;
            txtCountdown.FontFeatures = tnum;
            txtBalance.FontFeatures = tnum;
        }

        foreach (var (control, name) in new (Control, string)[]
                 {
                     (pageHome, "home"), (pagePicker, "servers"), (pageSettings, "settings"),
                     (pageAccount, "account"), (pageSupport, "support"),
                     (firstRun, "first-run"), (frChoice, "fr-choice"), (frWaiting, "fr-waiting"), (frLink, "fr-link"),
                     (logoutSheet, "logout-sheet"), (paySheet, "pay-sheet"), (acctSheet, "acct-sheet"),
                     (pageCheck, "check"), (reportSheet, "report-sheet"), (updateSheet, "update-sheet"),
                 })
        {
            control.RenderTransform = new TranslateTransform();
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && e.NewValue is true)
                {
                    OnShown(control, name);
                }
            };
        }

        txtSupportInput.KeyDown += OnSupportInputKeyDown;
        // Esc closes the «Выйти» sheet (tunnel: works wherever the focus is).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        logoutSheet.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.NewValue is true)
            {
                // Like macOS sheets for destructive actions: the safe button has the focus.
                Dispatcher.UIThread.Post(() => btnLogoutCancel.Focus(), DispatcherPriority.Input);
            }
        };
        acctSheet.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.NewValue is true && _vm is { AcctSheetRename: true })
            {
                Dispatcher.UIThread.Post(() =>
                {
                    txtAcctRename.Focus();
                    txtAcctRename.SelectAll();
                }, DispatcherPriority.Input);
            }
        };
        tunPasswordSheet.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.NewValue is true)
            {
                Dispatcher.UIThread.Post(() => txtTunPassword.Focus(), DispatcherPriority.Input);
            }
        };
        DataContextChanged += (_, _) => HookVm(DataContext as TermitusHomeViewModel);
        AttachedToVisualTree += (_, _) =>
        {
            HookWindow();
            TermitusUiWatchdog.StartOnce(); // TER_PERF=1 only
        };
        DetachedFromVisualTree += (_, _) => UnhookWindow();
    }

    /// <summary>
    /// Apple-like (0.4.1): our screens use a macOS system font instead of the app-wide
    /// default (upstream's bundled Noto Sans SC: regular weight only, synthesized bold, no ₽ —
    /// every "₽" went through the system font fallback = a ~300 ms stall). Only if the
    /// system font really resolves; otherwise the upstream default stays.
    /// </summary>
    private void ApplySystemFont()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        try
        {
            var family = new FontFamily(SystemFontName);
            if (FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var gt)
                && gt.FamilyName.Equals(SystemFontName, StringComparison.OrdinalIgnoreCase))
            {
                FontFamily = family;
                Logging.SaveLog($"TER: system font '{gt.FamilyName}'");
            }
            else
            {
                Logging.SaveLog($"TER: system font not available ({gt?.FamilyName ?? "none"}), keeping the default");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"TER: system font failed ({ex.GetType().Name})");
        }
    }

    // Helvetica Neue, not SF (".AppleSystemUIFont"): SF does resolve, but Avalonia shapes it with
    // HarfBuzz without Apple's size-dependent tracking, so it rendered visibly cramped (checked
    // on screenshots). Helvetica Neue ships with every macOS, has real bold weights and ₽.
    private const string SystemFontName = "Helvetica Neue";

    private static FontFeatureCollection? FontFeatureCollectionParse(string s)
    {
        try
        {
            return [FontFeature.Parse(s)];
        }
        catch
        {
            return null;
        }
    }

    #region TER_PERF: layout passes of this view

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!TermitusPerf.Enabled)
        {
            return base.MeasureOverride(availableSize);
        }
        var t = Stopwatch.GetTimestamp();
        var r = base.MeasureOverride(availableSize);
        MarkIfSlow("measure", t);
        return r;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!TermitusPerf.Enabled)
        {
            return base.ArrangeOverride(finalSize);
        }
        var t = Stopwatch.GetTimestamp();
        var r = base.ArrangeOverride(finalSize);
        MarkIfSlow("arrange", t);
        return r;
    }

    private static void MarkIfSlow(string what, long start)
    {
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (ms >= 4)
        {
            TermitusPerf.Mark($"{what} {TermitusPerf.Fmt(ms)}ms");
        }
    }

    #endregion TER_PERF: layout passes of this view

    #region Page transitions

    private void OnShown(Control page, string name)
    {
        if (TermitusPerf.Enabled)
        {
            MeasureFirstFrame(name);
        }
        if (TermitusMotion.Reduced || page.RenderTransform is not TranslateTransform tt)
        {
            return;
        }
        // Local values + Transitions (not an Animation with fill modes): jump to the start
        // state with transitions off, switch them on, set the end state -> it animates
        // from start to end and simply stays there. No first-frame flash.
        page.Transitions = null;
        tt.Transitions = null;
        page.Opacity = 0;
        tt.Y = PageInOffset;
        page.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = PageIn, Easing = PageEasing }];
        tt.Transitions = [new DoubleTransition { Property = TranslateTransform.YProperty, Duration = PageIn, Easing = PageEasing }];
        page.Opacity = 1;
        tt.Y = 0;
    }

    /// <summary>Page appeared -> second animation frame = its first layout + render are done.</summary>
    private void MeasureFirstFrame(string name)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null)
        {
            return;
        }
        var start = Stopwatch.GetTimestamp();
        top.RequestAnimationFrame(_ => top.RequestAnimationFrame(_ =>
            TermitusPerf.Report($"page-first-frame:{name}", Stopwatch.GetElapsedTime(start).TotalMilliseconds)));
    }

    #endregion Page transitions

    #region VM hooks (chat scroll)

    private void HookVm(TermitusHomeViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.SupportMessages.CollectionChanged -= OnChatChanged;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.SupportMessages.CollectionChanged += OnChatChanged;
            // 0.6: «Скопировать текст» in the report sheet; the update helper needs TER to quit.
            _vm.ReportCopyRequested.AsObservable()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(async text =>
                {
                    try
                    {
                        await AvaUtils.SetClipboardData(this, text);
                    }
                    catch (Exception ex)
                    {
                        Logging.SaveLog($"TER: copy failed ({ex.GetType().Name})");
                    }
                });
            // Account 2.0: link / invite link copy, QR of the one-time join link (PNG from the local QR library).
            _vm.AcctCopyRequested.AsObservable()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(async text =>
                {
                    try
                    {
                        await AvaUtils.SetClipboardData(this, text);
                    }
                    catch (Exception ex)
                    {
                        Logging.SaveLog($"TER: copy failed ({ex.GetType().Name})");
                    }
                });
            _vm.WhenAnyValue(x => x.AcctQrPng)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(png =>
                {
                    try
                    {
                        var old = acctQr.Source as IDisposable;
                        acctQr.Source = png == null ? null : new Bitmap(new MemoryStream(png));
                        old?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        acctQr.Source = null;
                        Logging.SaveLog($"TER: QR image failed ({ex.GetType().Name})");
                    }
                });
            _vm.QuitForUpdateRequested.AsObservable()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => TermitusTray.QuitApp());
            // 0.5: Системная / Светлая / Тёмная. "system" = Default = follows macOS live.
            _vm.WhenAnyValue(x => x.ThemeChoice)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(t =>
                {
                    if (Application.Current is { } app)
                    {
                        app.RequestedThemeVariant = t switch
                        {
                            "light" => ThemeVariant.Light,
                            "dark" => ThemeVariant.Dark,
                            _ => ThemeVariant.Default,
                        };
                    }
                });
            UpdateUiActive();
            TermitusSelfTest.StartOnce(this, _vm); // TER_TEST_TOUR on a test instance only
        }
    }

    /// <summary>Test tour only: scrolls a page's ScrollViewer to the end (settings | support | check).</summary>
    public void DebugScrollEnd(string page)
    {
        (page switch { "settings" => pageSettings, "support" => pageSupport, "check" => pageCheck, "account" => pageAccount, _ => pageSettings }).ScrollToEnd();
    }

    /// <summary>Test tour only: scrolls the account page by a fraction (0 = top, 1 = bottom) of its scrollable height.</summary>
    public void DebugScrollAccount(double fraction)
    {
        var max = Math.Max(0, pageAccount.Extent.Height - pageAccount.Viewport.Height);
        pageAccount.Offset = new Vector(0, max * Math.Clamp(fraction, 0, 1));
    }

    /// <summary>Test tour only: opens the «Что именно будет отправлено» expander of the report sheet.</summary>
    public void DebugExpandReportPreview()
    {
        foreach (var e in reportSheet.GetVisualDescendants().OfType<Expander>())
        {
            e.IsExpanded = true;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm is { ShowReport: true, ReportSending: false })
        {
            e.Handled = true;
            _vm.ReportCloseCmd.Execute().Subscribe(_ => { }, _ => { });
            return;
        }
        if (e.Key == Key.Escape && _vm is { ShowUpdateSheet: true, UpdateBusyStage: false })
        {
            e.Handled = true;
            _vm.UpdateCloseCmd.Execute().Subscribe(_ => { }, _ => { });
            return;
        }
        if (e.Key == Key.Escape && _vm is { ShowTunPassword: true, TunPasswordChecking: false })
        {
            e.Handled = true;
            _vm.TunPasswordSkipCmd.Execute().Subscribe(_ => { }, _ => { });
            return;
        }
        if (e.Key == Key.Escape && _vm is { AcctShowSheet: true, AcctSheetBusy: false })
        {
            e.Handled = true;
            _vm.AcctSheetCloseCmd.Execute().Subscribe(_ => { }, _ => { });
            return;
        }
        if (e.Key == Key.Escape && _vm is { ShowLogoutConfirm: true, IsLoggingOut: false })
        {
            e.Handled = true;
            _vm.LogoutCancelCmd.Execute().Subscribe(_ => { }, _ => { });
        }
    }

    private void OnChatChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            // After layout of the new bubble.
            Dispatcher.UIThread.Post(() => chatScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private void OnSupportInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && _vm != null)
        {
            e.Handled = true;
            _vm.SendSupportCmd.Execute().Subscribe(_ => { }, _ => { });
        }
    }

    #endregion VM hooks (chat scroll)

    #region Window visibility -> VM.UiActive

    private void HookWindow()
    {
        UnhookWindow();
        _window = TopLevel.GetTopLevel(this) as Window;
        // Every ancestor's IsVisible (the advanced UI hides our ContentControl), and the
        // window itself (last ancestor: IsVisible = tray, WindowState = minimized).
        for (var v = this.GetVisualParent(); v != null; v = v.GetVisualParent())
        {
            v.PropertyChanged += OnAncestorChanged;
            _watchedAncestors.Add(v);
        }
        UpdateUiActive();
        ApplyTranslucency();
    }

    /// <summary>
    /// 0.5: the window asks the OS for a blurred backdrop (MainWindow.axaml, patched by
    /// apply-changes.sh); the sidebar then uses a see-through brush so it looks like a native
    /// macOS sidebar. When the platform gave no blur, fall back to solid colours.
    /// </summary>
    private void ApplyTranslucency()
    {
        try
        {
            var level = _window?.ActualTransparencyLevel ?? WindowTransparencyLevel.None;
            var blur = level == WindowTransparencyLevel.AcrylicBlur || level == WindowTransparencyLevel.Blur
                || level == WindowTransparencyLevel.Mica;
            // A forced Light/Dark differing from macOS would sit on a wrong-coloured blur: solid sidebar then.
            if (Application.Current?.RequestedThemeVariant is { } rv && rv != ThemeVariant.Default)
            {
                blur = false;
            }
            var variant = ActualThemeVariant;
            if (this.TryFindResource(blur ? "TermSidebar" : "TermSurface", variant, out var side) && side is IBrush sb)
            {
                sidebar.Background = sb;
            }
            if (_window != null && this.TryFindResource("TermBg", variant, out var bg) && bg is IBrush bb)
            {
                _window.Background = blur ? Brushes.Transparent : bb;
            }
            Logging.SaveLog($"TER ui: window transparency {level}, translucent sidebar {(blur ? "on" : "off")}");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"TER ui: translucency failed ({ex.GetType().Name})");
        }
    }

    private void UnhookWindow()
    {
        _window = null;
        foreach (var v in _watchedAncestors)
        {
            v.PropertyChanged -= OnAncestorChanged;
        }
        _watchedAncestors.Clear();
    }

    private void OnAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ActualTransparencyLevelProperty || e.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            Dispatcher.UIThread.Post(ApplyTranslucency, DispatcherPriority.Background);
        }
        if (e.Property == IsVisibleProperty || e.Property == Window.WindowStateProperty)
        {
            // Posted: IsEffectivelyVisible of this control settles after the ancestor's change.
            Dispatcher.UIThread.Post(UpdateUiActive, DispatcherPriority.Background);
        }
    }

    private void UpdateUiActive()
    {
        var active = _window != null
            && _window.IsVisible
            && _window.WindowState != WindowState.Minimized
            && IsEffectivelyVisible;
        _vm?.SetUiActive(active);
    }

    #endregion Window visibility -> VM.UiActive
}
