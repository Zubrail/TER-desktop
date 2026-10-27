using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ServiceLib.Services;
using TUnit.Core;

namespace ServiceLib.Tests.Termitus;

public class TermitusMacUpdateSafetyTests
{
    private static string Bundle(string root, string name, string value)
    {
        var app = Path.Combine(root, name, "TER.app");
        Directory.CreateDirectory(Path.Combine(app, "Contents"));
        File.WriteAllText(Path.Combine(app, "Contents", "v.txt"), value);
        File.WriteAllText(Path.Combine(app, "Contents", "Info.plist"), "<plist><dict><key>CFBundleIdentifier</key><string>online.termitus.desktop</string><key>CFBundleShortVersionString</key><string>0.6.7</string></dict></plist>");
        return app;
    }

    private static async Task<int> Swap(string root, string staged, string target, int pid, bool open, bool failOpen = false)
    {
        var script = Path.Combine(root, "swap.sh");
        var text = TermitusUpdater.SwapScript.Replace("seq 1 100", "seq 1 2"); // bound live-PID timeout for this test only
        if (failOpen) text = text.Replace("/usr/bin/open \"$TARGET\"", "false"); // LaunchServices failure, no real app launch
        File.WriteAllText(script, text);
        using var p = Process.Start(new ProcessStartInfo("/bin/bash")
        {
            ArgumentList = { script, pid.ToString(), staged, target, open ? "1" : "0", "0.6.7", "" },
            RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await p.WaitForExitAsync(timeout.Token);
        return p.ExitCode;
    }

    [Test]
    public async Task LiveOldProcess_AbortsWithoutRenamingEitherBundle()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("ter-swap-live-").FullName;
        try
        {
            var staged = Bundle(root, "new", "new"); var target = Bundle(root, "installed", "old");
            await Assert.That(await Swap(root, staged, target, Environment.ProcessId, false)).IsEqualTo(6);
            await Assert.That(File.ReadAllText(Path.Combine(target, "Contents", "v.txt"))).IsEqualTo("old");
            await Assert.That(Directory.Exists(staged)).IsTrue();
            await Assert.That(Directory.Exists(target + ".old-ter-update")).IsFalse();
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task FailedLaunch_RestoresOldAndPreservesRejectedNewBundle()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("ter-swap-open-").FullName;
        try
        {
            var staged = Bundle(root, "new", "new"); var target = Bundle(root, "installed", "old");
            using var dead = Process.Start("/bin/sh", ["-c", "exit 0"])!; await dead.WaitForExitAsync();
            await Assert.That(await Swap(root, staged, target, dead.Id, true, true)).IsEqualTo(3);
            await Assert.That(File.ReadAllText(Path.Combine(target, "Contents", "v.txt"))).IsEqualTo("old");
            await Assert.That(Directory.GetDirectories(Path.GetDirectoryName(target)!, "*.failed-ter-update.*").Length).IsEqualTo(1);
            await Assert.That(File.Exists(target + ".pending-ter-update")).IsFalse();
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task BackupRequiresMatchingRenderedVersionConfirmation_AndIsNotOverwritten()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("ter-swap-confirm-").FullName;
        try
        {
            var staged = Bundle(root, "new", "new"); var target = Bundle(root, "installed", "old");
            using var dead = Process.Start("/bin/sh", ["-c", "exit 0"])!; await dead.WaitForExitAsync();
            await Assert.That(await Swap(root, staged, target, dead.Id, false)).IsEqualTo(0);
            await Assert.That(Directory.Exists(target + ".old-ter-update")).IsTrue();
            await Assert.That(TermitusUpdater.ConfirmMacStartup(target, "0.6.6")).IsFalse();
            var next = Bundle(root, "next", "next");
            await Assert.That(await Swap(root, next, target, dead.Id, false)).IsEqualTo(7);
            await Assert.That(File.ReadAllText(Path.Combine(target + ".old-ter-update", "Contents", "v.txt"))).IsEqualTo("old");
            await Assert.That(TermitusUpdater.ConfirmMacStartup(target, "0.6.7")).IsTrue();
            await Assert.That(Directory.Exists(target + ".old-ter-update")).IsFalse();
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task ReadyHelperDoesNotTouchAppsUntilArmed()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("ter-swap-arm-").FullName;
        try
        {
            var staged = Bundle(root, "new", "new"); var target = Bundle(root, "installed", "old");
            using var dead = Process.Start("/bin/sh", ["-c", "exit 0"])!; await dead.WaitForExitAsync();
            var script = Path.Combine(root, "swap.sh"); File.WriteAllText(script, TermitusUpdater.SwapScript);
            var ready = Path.Combine(root, "ready"); var arm = Path.Combine(root, "arm");
            using var process = Process.Start(new ProcessStartInfo("/bin/bash")
            {
                ArgumentList = { script, dead.Id.ToString(), staged, target, "0", "0.6.7", ready, arm },
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                while (!File.Exists(ready)) await Task.Delay(10, timeout.Token);
                await Task.Delay(100, timeout.Token);
                await Assert.That(File.ReadAllText(Path.Combine(target, "Contents", "v.txt"))).IsEqualTo("old");
                await Assert.That(Directory.Exists(staged)).IsTrue();
                File.WriteAllText(arm, "");
                await process.WaitForExitAsync(timeout.Token);
                await Assert.That(process.ExitCode).IsEqualTo(0);
                await Assert.That(File.ReadAllText(Path.Combine(target, "Contents", "v.txt"))).IsEqualTo("new");
            }
            finally { if (!process.HasExited) process.Kill(true); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task StartupCannotDeleteBackupWhileHelperCouldStillRollBack()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("ter-swap-race-").FullName;
        try
        {
            var target = Bundle(root, "installed", "new");
            Directory.CreateDirectory(target + ".old-ter-update");
            var marker = target + ".pending-ter-update";
            File.WriteAllText(marker, "0.6.7\n" + Environment.ProcessId + "\n");
            File.SetUnixFileMode(marker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await Assert.That(TermitusUpdater.ConfirmMacStartup(target, "0.6.7")).IsFalse();
            await Assert.That(Directory.Exists(target + ".old-ter-update")).IsTrue();
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task LinkedConfirmationMarkerCannotDeleteRollback()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("ter-swap-link-").FullName;
        try
        {
            var target = Bundle(root, "installed", "new");
            Directory.CreateDirectory(target + ".old-ter-update");
            var external = Path.Combine(root, "external"); File.WriteAllText(external, "0.6.7");
            File.CreateSymbolicLink(target + ".pending-ter-update", external);
            await Assert.That(TermitusUpdater.ConfirmMacStartup(target, "0.6.7")).IsFalse();
            await Assert.That(Directory.Exists(target + ".old-ter-update")).IsTrue();
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task StalledProxyFallsBackToDirectAndVerifiesDownloadedBytes()
    {
        var root = Directory.CreateTempSubdirectory("ter-fallback-").FullName;
        var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
        var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var bytes = Encoding.UTF8.GetBytes("verified update bytes");
        var proxyAccepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalledProxy = Task.Run(async () =>
        {
            try
            {
                using var peer = await proxy.AcceptTcpClientAsync(stop.Token);
                proxyAccepted.SetResult(true);
                await Task.Delay(Timeout.Infinite, stop.Token);
            }
            catch (OperationCanceledException) { }
        });
        var directOrigin = Task.Run(async () =>
        {
            try
            {
                using var peer = await origin.AcceptTcpClientAsync(stop.Token);
                await using var stream = peer.GetStream();
                var request = new byte[4096];
                if (await stream.ReadAsync(request, stop.Token) == 0) return;
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, stop.Token); await stream.WriteAsync(bytes, stop.Token);
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var url = $"http://127.0.0.1:{((IPEndPoint)origin.LocalEndpoint).Port}/";
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var info = new TermitusUpdateInfo("0.6.7", "", url, hash, "mac-arm64");
            var file = Path.Combine(root, "update.dmg");
            var result = await TermitusUpdater.DownloadAsync(info, file, ((IPEndPoint)proxy.LocalEndpoint).Port, (_, _) => { }, stop.Token,
                inactivityTimeout: TimeSpan.FromMilliseconds(500), attemptTimeout: TimeSpan.FromSeconds(2));
            await Assert.That(result).IsEqualTo(TermitusUpdater.DownloadKind.Ok);
            await Assert.That(proxyAccepted.Task.IsCompletedSuccessfully).IsTrue();
            await Assert.That(File.ReadAllBytes(file).SequenceEqual(bytes)).IsTrue();
            await Assert.That(File.Exists(file + ".part")).IsFalse();
        }
        finally
        {
            stop.Cancel(); proxy.Stop(); origin.Stop();
            await Task.WhenAll(stalledProxy, directOrigin); Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task StalledDownloadBody_TimesOutAndRemovesPartialFile()
    {
        var root = Directory.CreateTempSubdirectory("ter-stall-").FullName;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = Task.Run(async () =>
        {
            try
            {
                using var peer = await listener.AcceptTcpClientAsync(stop.Token);
                await using var stream = peer.GetStream();
                var buffer = new byte[4096];
                if (await stream.ReadAsync(buffer, stop.Token) == 0) return;
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1000\r\nConnection: close\r\n\r\nx"), stop.Token);
                await Task.Delay(Timeout.Infinite, stop.Token);
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            var info = new TermitusUpdateInfo("0.6.7", "", url, new string('0', 64), "mac-arm64");
            var file = Path.Combine(root, "update.dmg");
            var result = await TermitusUpdater.DownloadAsync(info, file, null, (_, _) => { }, stop.Token,
                inactivityTimeout: TimeSpan.FromMilliseconds(150), attemptTimeout: TimeSpan.FromSeconds(2));
            await Assert.That(result).IsEqualTo(TermitusUpdater.DownloadKind.Offline);
            await Assert.That(File.Exists(file + ".part")).IsFalse();
            await Assert.That(File.Exists(file)).IsFalse();
        }
        finally { stop.Cancel(); listener.Stop(); await server; Directory.Delete(root, true); }
    }
}
