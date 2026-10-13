using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using ServiceLib.Services;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Common;

/// <summary>
/// 0.5 macOS menu-bar item. Avalonia's TrayIcon cannot tell a left click from a right click
/// once it has a menu (the menu always opens), so on macOS TER owns an NSStatusItem itself:
///   left click  -> TermitusPopover (Avalonia window under the icon: status, big toggle,
///                  country, live speed, subscription, Open TER / Quit);
///   right click -> short native NSMenu built from TermitusTrayMenu (Connect, Country ›,
///                  Open TER, «Для опытных ›», Quit).
/// The icon follows the state (grey template = off, mint = on, amber = connecting / needs
/// attention). Everything is local; nothing is sent anywhere. Other platforms keep upstream's
/// TrayIcon (App.axaml).
/// </summary>
internal static class TermitusTray
{
    private const string LibObjC = "/usr/lib/libobjc.dylib";
    private const string LibAppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private static nint _item;
    private static nint _button;
    private static nint _handler;
    private static MainWindow? _window;
    private static TermitusHomeViewModel? _vm;
    private static TermitusPopover? _popover;
    private static long _popoverHiddenAt;
    private static readonly Dictionary<long, string> _tagToId = [];
    private static readonly IDisposable[] _subs = new IDisposable[1];

    // Must stay referenced: AppKit holds only the raw function pointers.
    private static ActionImp? _clickImp;
    private static ActionImp? _menuImp;

    private delegate void ActionImp(nint self, nint sel, nint sender);

    public static bool Active => _item != 0;

    public static void Start(MainWindow window, TermitusHomeViewModel vm)
    {
        if (!OperatingSystem.IsMacOS() || Design.IsDesignMode || _item != 0)
        {
            return;
        }
        try
        {
            _window = window;
            _vm = vm;
            LoadAppKit();

            var bar = Send(GetClass("NSStatusBar"), Sel("systemStatusBar"));
            _item = Retain(SendDouble(bar, Sel("statusItemWithLength:"), -1.0));
            _button = Send(_item, Sel("button"));

            _clickImp = OnClick;
            _menuImp = OnMenuPicked;
            _handler = MakeHandler();

            Send(_button, Sel("setTarget:"), _handler);
            SendPtr(_button, Sel("setAction:"), Sel("terClick:"));
            SendUInt(_button, Sel("sendActionOn:"), (1UL << 2) | (1UL << 4)); // left + right mouse up
            ApplyIcon();

            _subs[0] = vm.WhenAnyValue(x => x.IsConnected, x => x.IsConnecting, x => x.StatusWarn)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => ApplyIcon());
            Logging.SaveLog("TER tray: macOS status item created");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"TER tray: could not create the status item ({ex.GetType().Name}: {ex.Message})");
            _item = 0;
        }
    }

    public static void Stop()
    {
        if (_item == 0)
        {
            return;
        }
        try
        {
            _subs[0]?.Dispose();
            var bar = Send(GetClass("NSStatusBar"), Sel("systemStatusBar"));
            Send(bar, Sel("removeStatusItem:"), _item);
        }
        catch
        {
            // exiting anyway
        }
        _item = 0;
    }

    #region Icon

    private static string _lastIconState = string.Empty;

    private static void ApplyIcon()
    {
        if (_item == 0 || _vm == null)
        {
            return;
        }
        var state = _vm.IsConnecting ? "busy" : _vm.StatusWarn ? "warn" : _vm.IsConnected ? "on" : "off";
        if (state == _lastIconState)
        {
            return;
        }
        _lastIconState = state;
        try
        {
            var loc = TermitusLocale.Current;
            var png = TermitusTrayIcon.RenderPng(state);
            var data = SendBytes(GetClass("NSData"), Sel("dataWithBytes:length:"), png);
            var img = Send(Send(GetClass("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
            SendSize(img, Sel("setSize:"), 18, 18);
            SendBool(img, Sel("setTemplate:"), state == "off"); // grey/black glyph adapts to the menu bar
            Send(_button, Sel("setImage:"), img);
            Send(_button, Sel("setToolTip:"), NSString(state switch
            {
                "on" => loc.TrayTooltipOn,
                "busy" => loc.TrayTooltipBusy,
                "warn" => loc.TrayTooltipWarn,
                _ => loc.TrayTooltipOff,
            }));
            Logging.SaveLog($"TER tray: icon -> {state}");
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"TER tray: icon failed ({ex.GetType().Name})");
        }
    }

    #endregion Icon

    #region Clicks

    private static void OnClick(nint self, nint sel, nint sender)
    {
        try
        {
            var ev = Send(Send(GetClass("NSApplication"), Sel("sharedApplication")), Sel("currentEvent"));
            var type = ev == 0 ? 2 : (long)SendLong(ev, Sel("type"));
            var flags = ev == 0 ? 0UL : SendULong(ev, Sel("modifierFlags"));
            var right = type == 4 || (flags & (1UL << 18)) != 0; // right mouse up, or ctrl-click
            if (right)
            {
                ShowMenu();
            }
            else
            {
                TogglePopover();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"TER tray: click failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    public static void TogglePopover()
    {
        if (_vm == null || _window == null)
        {
            return;
        }
        if (_popover is { IsVisible: true })
        {
            _popover.Hide();
            return;
        }
        // The click that closes the popover first deactivates it (it hides itself), then reaches us here:
        // do not reopen it at once.
        if (Environment.TickCount64 - _popoverHiddenAt < 300)
        {
            return;
        }
        _popover ??= CreatePopover();
        ShowPopover(_popover);
    }

    private static TermitusPopover CreatePopover()
    {
        var p = new TermitusPopover { DataContext = _vm };
        p.OpenMainRequested += () => OpenMainWindow(null);
        p.QuitRequested += QuitApp;
        p.Closed += (_, _) => _popover = null;
        p.Hidden += () =>
        {
            _popoverHiddenAt = Environment.TickCount64;
            if (_vm != null)
            {
                _vm.PopoverActive = false;
            }
        };
        return p;
    }

    private static void ShowPopover(TermitusPopover p)
    {
        var (x, y, w, h) = ItemFrameDips();
        p.PlaceUnder(x + w / 2, y + h);
        if (_vm != null)
        {
            _vm.PopoverActive = true;
        }
        p.Show();
        ActivateApp();
        p.Activate();
        Logging.SaveLog($"TER tray: popover shown under the icon at x={x:0},y={y:0},w={w:0},h={h:0}");
    }

    /// <summary>The status item's frame in Avalonia's coordinates (DIPs, origin top-left of the primary screen).</summary>
    public static (double X, double Y, double W, double H) ItemFrameDips()
    {
        var win = Send(_button, Sel("window"));
        var f = SendRect(win, Sel("frame"));
        var screens = Send(GetClass("NSScreen"), Sel("screens"));
        var primary = Send(screens, Sel("firstObject"));
        var pf = SendRect(primary, Sel("frame"));
        return (f.X, pf.H - (f.Y + f.H), f.W, f.H);
    }

    private static void ShowMenu()
    {
        if (_vm == null)
        {
            return;
        }
        _popover?.Hide();
        var items = TermitusTrayMenu.Build(_vm.TrayState(), TermitusLocale.Current);
        _tagToId.Clear();
        var menu = BuildMenu(items, 0);
        Send(_item, Sel("setMenu:"), menu);
        Send(_button, Sel("performClick:"), 0);
        Send(_item, Sel("setMenu:"), 0); // back to "click = our handler"
    }

    private static nint BuildMenu(IReadOnlyList<TermitusTrayItem> items, int depth)
    {
        var menu = Send(Send(GetClass("NSMenu"), Sel("alloc")), Sel("init"));
        SendBool(menu, Sel("setAutoenablesItems:"), false);
        foreach (var it in items)
        {
            if (it.Kind == TermitusTrayKind.Separator)
            {
                Send(menu, Sel("addItem:"), Send(GetClass("NSMenuItem"), Sel("separatorItem")));
                continue;
            }
            var mi = Send(Send(GetClass("NSMenuItem"), Sel("alloc")), Sel("init"));
            Send(mi, Sel("setTitle:"), NSString(it.Title));
            SendBool(mi, Sel("setEnabled:"), it.Enabled);
            if (it.Kind == TermitusTrayKind.Submenu)
            {
                Send(mi, Sel("setSubmenu:"), BuildMenu(it.Children ?? [], depth + 1));
            }
            else
            {
                var tag = _tagToId.Count + 1;
                _tagToId[tag] = it.Id ?? string.Empty;
                SendLong2(mi, Sel("setTag:"), tag);
                Send(mi, Sel("setTarget:"), _handler);
                SendPtr(mi, Sel("setAction:"), Sel("terMenu:"));
                SendLong2(mi, Sel("setState:"), it.Checked ? 1 : 0);
            }
            Send(menu, Sel("addItem:"), mi);
        }
        return menu;
    }

    private static void OnMenuPicked(nint self, nint sel, nint sender)
    {
        try
        {
            var tag = (long)SendLong(sender, Sel("tag"));
            if (_tagToId.TryGetValue(tag, out var id))
            {
                // Out of the menu's tracking loop first.
                Dispatcher.UIThread.Post(() => Execute(id), DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"TER tray: menu pick failed ({ex.GetType().Name})");
        }
    }

    #endregion Clicks

    #region Actions

    /// <summary>Runs a menu id (also used by the test tour). UI thread.</summary>
    public static void Execute(string id)
    {
        var vm = _vm;
        if (vm == null)
        {
            return;
        }
        Logging.SaveLog($"TER tray: menu action '{(id.StartsWith(TermitusTrayMenu.CountryPrefix, StringComparison.Ordinal) ? "country" : id)}'");
        var sb = StatusBarViewModel.Instance;
        switch (id)
        {
            case TermitusTrayMenu.Connect:
                vm.ConnectToggleCmd.Execute().Subscribe(_ => { }, _ => { });
                break;

            case TermitusTrayMenu.Open:
                OpenMainWindow(null);
                break;

            case TermitusTrayMenu.Report:
                OpenMainWindow(null);
                vm.ReportOpenCmd.Execute().Subscribe(_ => { }, _ => { });
                break;

            case TermitusTrayMenu.Quit:
                QuitApp();
                break;

            case TermitusTrayMenu.ProxyClear:
                sb.SystemProxyClearCmd.Execute().Subscribe(_ => { }, _ => { });
                break;

            case TermitusTrayMenu.ProxySet:
                sb.SystemProxySetCmd.Execute().Subscribe(_ => { }, _ => { });
                break;

            case TermitusTrayMenu.ProxyNothing:
                sb.SystemProxyNothingCmd.Execute().Subscribe(_ => { }, _ => { });
                break;

            case TermitusTrayMenu.CopyProxy:
                sb.CopyProxyCmdToClipboardCmd.Execute().Subscribe(_ => { }, _ => { });
                break;

            case TermitusTrayMenu.UpdateSub:
                sb.SubUpdateCmd.Execute().Subscribe(_ => { }, _ => { });
                break;

            case TermitusTrayMenu.UpdateSubViaProxy:
                sb.SubUpdateViaProxyCmd.Execute().Subscribe(_ => { }, _ => { });
                break;

            default:
                if (id.StartsWith(TermitusTrayMenu.CountryPrefix, StringComparison.Ordinal))
                {
                    vm.SelectLocationCmd.Execute(id[TermitusTrayMenu.CountryPrefix.Length..]).Subscribe(_ => { }, _ => { });
                }
                break;
        }
    }

    public static void OpenMainWindow(string? page)
    {
        _popover?.Hide();
        if (_window == null)
        {
            return;
        }
        _window.ShowHideWindow(true);
        ActivateApp();
        if (page != null && _vm != null)
        {
            _vm.NavCmd.Execute(page).Subscribe(_ => { }, _ => { });
        }
    }

    public static async void QuitApp()
    {
        try
        {
            _popover?.Hide();
            Stop();
            await AppManager.Instance.AppExitAsync(false);
            AppManager.Instance.Shutdown(true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TER tray: quit", ex);
        }
    }

    private static void ActivateApp()
    {
        try
        {
            Send(Send(GetClass("NSApplication"), Sel("sharedApplication")), Sel("activateIgnoringOtherApps:"), 1);
        }
        catch
        {
            // best effort
        }
    }

    #endregion Actions

    #region Test tour helpers

    /// <summary>Tour: the real button action with no event = a plain left click (toggles the popover).</summary>
    public static void DebugLeftClick()
    {
        if (_item != 0)
        {
            Send(_button, Sel("performClick:"), 0);
        }
    }

    /// <summary>Tour: builds the native NSMenu from the live state WITHOUT showing it; returns what AppKit holds.</summary>
    public static string DebugMenuTitles()
    {
        if (_vm == null)
        {
            return "no vm";
        }
        var items = TermitusTrayMenu.Build(_vm.TrayState(), TermitusLocale.Current);
        _tagToId.Clear();
        return ReadMenu(BuildMenu(items, 0));
    }

    private static string ReadMenu(nint menu)
    {
        var n = (int)SendLong(menu, Sel("numberOfItems"));
        var parts = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var mi = objc_msgSend_p(menu, Sel("itemAtIndex:"), i);
            if (SendBoolRet(mi, Sel("isSeparatorItem")))
            {
                parts.Add("-");
                continue;
            }
            var title = Marshal.PtrToStringUTF8(Send(Send(mi, Sel("title")), Sel("UTF8String"))) ?? string.Empty;
            var state = SendLong(mi, Sel("state")) == 1 ? "✓" : string.Empty;
            var off = SendBoolRet(mi, Sel("isEnabled")) ? string.Empty : "(off)";
            var sub = Send(mi, Sel("submenu"));
            parts.Add(sub != 0 ? $"{title} ›[{ReadMenu(sub)}]" : title + state + off);
        }
        return string.Join(" | ", parts);
    }

    /// <summary>Tour: where the status item and the popover's NSWindow really are (AppKit coordinates, points).</summary>
    public static string DebugGeometry()
    {
        if (_item == 0)
        {
            return "no status item";
        }
        var win = Send(_button, Sel("window"));
        var f = SendRect(win, Sel("frame"));
        var text = $"item x={f.X:0.#} y={f.Y:0.#} w={f.W:0.#} h={f.H:0.#}";
        var h = _popover?.TryGetPlatformHandle()?.Handle ?? 0;
        if (h != 0 && _popover is { IsVisible: true })
        {
            var p = SendRect(h, Sel("frame"));
            var content = p.X + p.W / 2;
            text += $" | popover window x={p.X:0.#} y={p.Y:0.#} w={p.W:0.#} h={p.H:0.#} centreX={content:0.#} (icon centreX={f.X + f.W / 2:0.#}) "
                + $"gap-below-icon={f.Y - (p.Y + p.H):0.#}pt";
        }
        return text;
    }

    public static TermitusPopover? Popover => _popover;

    private static bool SendBoolRet(nint r, nint s) => objc_msgSend_ret_b(r, s);

    #endregion Test tour helpers

    #region Objective-C plumbing

    private static nint MakeHandler()
    {
        var cls = AllocateClassPair(GetClass("NSObject"), "TerTrayHandler", 0);
        if (cls == 0)
        {
            // already registered (should not happen: Start runs once)
            cls = GetClass("TerTrayHandler");
        }
        else
        {
            AddMethod(cls, Sel("terClick:"), Marshal.GetFunctionPointerForDelegate(_clickImp!), "v@:@");
            AddMethod(cls, Sel("terMenu:"), Marshal.GetFunctionPointerForDelegate(_menuImp!), "v@:@");
            RegisterClassPair(cls);
        }
        return Send(Send(cls, Sel("alloc")), Sel("init"));
    }

    private static void LoadAppKit() => _ = NativeLibrary.Load(LibAppKit);

    private static nint Retain(nint o) => Send(o, Sel("retain"));

    private static nint NSString(string s)
    {
        var p = Marshal.StringToCoTaskMemUTF8(s);
        try
        {
            return Send(GetClass("NSString"), Sel("stringWithUTF8String:"), p);
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }

    private static nint SendBytes(nint cls, nint sel, byte[] bytes)
    {
        var h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return objc_msgSend_bytes(cls, sel, h.AddrOfPinnedObject(), (nuint)bytes.Length);
        }
        finally
        {
            h.Free();
        }
    }

    private static nint GetClass(string n) => objc_getClass(n);

    private static nint Sel(string n) => sel_registerName(n);

    private static nint Send(nint r, nint s) => objc_msgSend(r, s);

    private static nint Send(nint r, nint s, nint a) => objc_msgSend_p(r, s, a);

    private static void SendPtr(nint r, nint s, nint a) => objc_msgSend_p(r, s, a);

    private static nint SendDouble(nint r, nint s, double d) => objc_msgSend_d(r, s, d);

    private static void SendUInt(nint r, nint s, ulong v) => objc_msgSend_u(r, s, v);

    private static void SendBool(nint r, nint s, bool v) => objc_msgSend_b(r, s, v);

    private static void SendSize(nint r, nint s, double w, double h) => objc_msgSend_dd(r, s, w, h);

    private static long SendLong(nint r, nint s) => objc_msgSend_ret_l(r, s);

    private static ulong SendULong(nint r, nint s) => objc_msgSend_ret_u(r, s);

    private static void SendLong2(nint r, nint s, long v) => objc_msgSend_l(r, s, v);

    private static Rect4 SendRect(nint r, nint s) => objc_msgSend_rect(r, s);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect4
    {
        public double X;
        public double Y;
        public double W;
        public double H;
    }

    [DllImport(LibObjC)] private static extern nint objc_getClass(string name);
    [DllImport(LibObjC)] private static extern nint sel_registerName(string name);
    [DllImport(LibObjC)] private static extern nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);
    [DllImport(LibObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(LibObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(nint cls, nint name, nint imp, string types);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend(nint r, nint s);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_p(nint r, nint s, nint a);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_d(nint r, nint s, double a);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_u(nint r, nint s, ulong a);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_l(nint r, nint s, long a);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_b(nint r, nint s, [MarshalAs(UnmanagedType.I1)] bool a);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_dd(nint r, nint s, double a, double b);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern long objc_msgSend_ret_l(nint r, nint s);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern ulong objc_msgSend_ret_u(nint r, nint s);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_bytes(nint r, nint s, nint bytes, nuint len);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern Rect4 objc_msgSend_rect(nint r, nint s);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool objc_msgSend_ret_b(nint r, nint s);

    private static nint AllocateClassPair(nint super, string name, nint extra) => objc_allocateClassPair(super, name, extra);

    private static void RegisterClassPair(nint cls) => objc_registerClassPair(cls);

    private static void AddMethod(nint cls, nint sel, nint imp, string types) => class_addMethod(cls, sel, imp, types);

    #endregion Objective-C plumbing
}

/// <summary>Draws the menu-bar glyph (dome with an arch, as in the app icon) for each state.</summary>
internal static class TermitusTrayIcon
{
    // App-icon geometry (512 box): dome with the arch cut out, plus the warm dot inside the arch.
    private const string DomePath = "F0 M64,400 C64,250 150,116 256,112 C362,116 448,250 448,400 Z M212,400 L212,330 A44,44 0 0 1 300,330 L300,400 Z";
    private const string DotPath = "M240,356 A16,16 0 1 0 272,356 A16,16 0 1 0 240,356 Z";

    public static byte[] RenderPng(string state)
    {
        var color = state switch
        {
            "on" => Color.FromRgb(0x10, 0xB9, 0x81),
            "busy" => Color.FromRgb(0xFF, 0x9A, 0x45),
            "warn" => Color.FromRgb(0xFF, 0x7A, 0x45),
            _ => Colors.Black, // template image: macOS recolours it for light/dark menu bars
        };
        // 36 px at 144 dpi = an 18 pt icon on a Retina menu bar.
        using var rtb = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(144, 144));
        using (var ctx = rtb.CreateDrawingContext())
        {
            // The 36 px bitmap at 144 dpi is an 18 x 18 DIP canvas: the dome (384 x 288) fits in 16 x 12 DIPs.
            const double s = 16.0 / 384.0;
            var m = Matrix.CreateTranslation(-64, -112) * Matrix.CreateScale(s, s) * Matrix.CreateTranslation(1, 3.25);
            using (ctx.PushTransform(m))
            {
                var brush = new SolidColorBrush(color);
                ctx.DrawGeometry(brush, null, StreamGeometry.Parse(DomePath));
                ctx.DrawGeometry(brush, null, StreamGeometry.Parse(DotPath));
            }
        }
        using var ms = new MemoryStream();
#pragma warning disable CS0618
        rtb.Save(ms);
#pragma warning restore CS0618
        return ms.ToArray();
    }
}
