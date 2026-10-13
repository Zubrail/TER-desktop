namespace ServiceLib.Services;

/// <summary>
/// Applies TeRMiTuS security/anonymity defaults on top of a v2rayN config.
/// See SECURITY.md for the full checklist. This method is idempotent and safe
/// to call on every launch; it only tightens defaults, never loosens them.
///
/// What it enforces (re-verified for 0.3 by TermitusSecurityTests, which
/// generate real Xray/sing-box configs and assert on them):
///  1. Local inbound loopback-only: AllowLANConn = false on every launch, so
///     Xray/sing-box "mixed" inbound listens on 127.0.0.1 only. We deliberately
///     do NOT force auth=password on it: the macOS system proxy carries no
///     credentials, so auth would break browsing (SECURITY.md #1).
///  6. No traffic logging: LogEnabled = false → Xray access/error log paths are
///     null and sing-box log has no output file.
///  -  The upstream app self-update feed (2dust/v2rayN) is disabled by a patch
///     in apply-changes.sh (UpdateService.CheckUpdateGuiN returns early).
///  7. (0.3) Data folder readable only by the current user on macOS/Linux:
///     guiConfigs holds the subscription URL and the server credentials
///     (SQLite DB), binConfigs the generated core config with the same
///     credentials. Directories 0700, files in them 0600.
///  TUN is NOT forced on (TER 0.1.1): on macOS the sudo password lives only in
///  memory, a TUN flag at startup made Connect silently fail (0.1.0).
///
/// Leak protection (DNS/IPv6, strict_route) is a one-time migration in
/// TermitusDefaults plus an apply-changes.sh patch of the sing-box TUN inbound;
/// kill switch + auto-reconnect live in TermitusConnectionGuard and the home VM.
/// </summary>
public static class TermitusSecurity
{
    public static async Task ApplyHardenedDefaults(Config config, bool firstRun)
    {
        if (config == null)
        {
            return;
        }

        try
        {
            ApplyCoreDefaults(config);
            HardenDataDirPermissions();
            await ConfigHandler.SaveConfig(config);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TermitusSecurity.ApplyHardenedDefaults", ex);
        }
    }

    /// <summary>The config part of the hardening (pure; unit-tested).</summary>
    public static void ApplyCoreDefaults(Config config)
    {
        // #6 no traffic/access logging by default.
        config.CoreBasicItem ??= new();
        config.CoreBasicItem.LogEnabled = false;
        if (config.CoreBasicItem.Loglevel.IsNullOrEmpty())
        {
            config.CoreBasicItem.Loglevel = "warning";
        }

        // #1 / LAN: keep the local inbound loopback-only (never LAN by default).
        if (config.Inbound is { Count: > 0 })
        {
            config.Inbound.First().AllowLANConn = false;
        }
    }

    /// <summary>chmod 700 on the data folders, 600 on the files directly in them (non-Windows).</summary>
    public static void HardenDataDirPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode dirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            var root = Utils.StartupPath();
            // The data folder itself only when it is a dedicated one (not the .app/exe folder).
            if (Environment.GetEnvironmentVariable(Global.LocalAppData) == "1" && Directory.Exists(root))
            {
                File.SetUnixFileMode(root, dirMode);
            }
            foreach (var dir in new[] { Utils.GetConfigPath(), Utils.GetBinConfigPath(), Utils.GetLogPath(), Utils.GetTempPath() })
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }
                File.SetUnixFileMode(dir, dirMode);
                if (dir == Utils.GetTempPath())
                {
                    continue;
                }
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        // Upstream runs helper scripts from binConfigs (proxy_set_osx_sh.sh,
                        // run_as_sudo.sh, kill_as_sudo.sh): they must stay executable
                        // (0700), or system proxy / TUN silently fail to start (0.3.0 bug).
                        var mode = file.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
                            ? fileMode | UnixFileMode.UserExecute
                            : fileMode;
                        File.SetUnixFileMode(file, mode);
                    }
                    catch (Exception ex)
                    {
                        Logging.SaveLog("TermitusSecurity.HardenDataDirPermissions", ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("TermitusSecurity.HardenDataDirPermissions", ex);
        }
    }
}
