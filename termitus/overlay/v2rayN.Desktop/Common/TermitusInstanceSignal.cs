using System.Diagnostics;
using System.Runtime.InteropServices;
using ServiceLib.Services;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Common;

/// <summary>
/// Desktop half of TermitusInstanceChannel (hooks added by apply-changes.sh):
///  - Program.OnStartup (macOS/Linux, mutex already taken): ask the running
///    instance to show itself; if it does not answer (an old TER without the
///    listener, a hung instance, or stock v2rayN which shares the mutex name),
///    tell the user in plain words instead of exiting silently.
///  - MainWindow (macOS/Linux): listen and bring the window to the front.
/// </summary>
internal static class TermitusInstanceSignal
{
    private static IDisposable? _listener;

    public static void OnSecondLaunch()
    {
        if (TermitusInstanceChannel.TrySignalExisting())
        {
            return;
        }

        Logging.SaveLog("TER: another instance holds the mutex but did not answer");
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        try
        {
            // osascript's own "display alert" needs no Automation permission.
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(
                "display alert \"TER уже запущен\" message \"Работает другая копия TER (возможно, старая или скрытая) — "
                + "поэтому новое окно не открылось. Закройте её: значок TER в строке меню вверху → «Выход», "
                + "или «Мониторинг системы» → процесс TER → Завершить. Затем откройте TER снова.\" as warning");
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TermitusInstanceSignal", ex);
        }
    }

    public static void Listen(MainWindow window)
    {
        if (_listener != null || Utils.IsWindows() || Design.IsDesignMode)
        {
            return;
        }
        _listener = TermitusInstanceChannel.StartListener(() => Dispatcher.UIThread.Post(() =>
        {
            window.ShowHideWindow(true);
            if (OperatingSystem.IsMacOS())
            {
                ActivateMacApp();
            }
        }));
    }

    /// <summary>[NSApp activateIgnoringOtherApps:YES] — Window.Activate() alone does not
    /// pull an inactive macOS app to the front.</summary>
    private static void ActivateMacApp()
    {
        try
        {
            var app = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            objc_msgSend_bool(app, sel_registerName("activateIgnoringOtherApps:"), true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TermitusInstanceSignal", ex);
        }
    }

    private const string LibObjC = "/usr/lib/libobjc.dylib";

    [DllImport(LibObjC)]
    private static extern nint objc_getClass(string name);

    [DllImport(LibObjC)]
    private static extern nint sel_registerName(string name);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend(nint receiver, nint selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_bool(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool arg);
}
