using System.Diagnostics;
using System.Runtime.InteropServices;

namespace v2rayN.Desktop.Common;

/// <summary>
/// "Reduce motion" (TER 0.4): page transitions are skipped when the OS asks for less
/// motion. Avalonia has no API for it, so: macOS = System Settings → Accessibility →
/// Display → Reduce motion (read once with `defaults`, off the UI thread); Windows =
/// "Show animations in Windows" off (SPI_GETCLIENTAREAANIMATION). Env TER_REDUCE_MOTION=1
/// forces it anywhere. Until detection finishes, animations stay on (they are subtle).
/// </summary>
internal static class TermitusMotion
{
    private static int _detected;

    public static volatile bool Reduced = Environment.GetEnvironmentVariable("TER_REDUCE_MOTION") == "1";

    public static void DetectOnce()
    {
        if (Reduced || Interlocked.Exchange(ref _detected, 1) == 1)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    Reduced = MacReduceMotion();
                }
                else if (OperatingSystem.IsWindows())
                {
                    Reduced = WindowsAnimationsOff();
                }
            }
            catch
            {
                // Unknown = keep animations (they are 140 ms and subtle).
            }
        });
    }

    private static bool MacReduceMotion()
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/defaults",
            ArgumentList = { "read", "com.apple.universalaccess", "reduceMotion" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (p == null)
        {
            return false;
        }
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(2000))
        {
            try
            {
                p.Kill();
            }
            catch
            {
                // ignore
            }
            return false;
        }
        return output.Trim() == "1";
    }

    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    private static bool WindowsAnimationsOff()
    {
        var on = true;
        return SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref on, 0) && !on;
    }
}
