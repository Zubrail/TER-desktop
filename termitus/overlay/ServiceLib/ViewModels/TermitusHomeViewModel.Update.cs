using ServiceLib.Services;

namespace ServiceLib.ViewModels;

/// <summary>
/// 0.6 updater. Checks https://termitus.online/app/latest.json on start and every 6 h (switchable in Settings), shows
/// «Доступно обновление X» with «Обновить» / «Позже». Only a manifest signed with the embedded Ed25519 key is ever
/// offered; the downloaded file must match the SIGNED sha256. Installing on macOS: the verified .dmg is mounted
/// read-only, TER.app is copied out, and a tiny helper swaps the bundle after TER quits and starts the new one
/// (a file we download ourselves carries no quarantine flag, so Gatekeeper does not block the unsigned app).
/// If the running app cannot be replaced (not in a bundle / folder not writable) the .dmg is opened for a manual drag.
/// </summary>
public partial class TermitusHomeViewModel
{
    private enum UpdateStage { None, Available, Downloading, Installing, Manual, Failed }

    [Reactive] public partial bool ShowUpdateBanner { get; set; }
    [Reactive] public partial string UpdateBannerText { get; set; }
    [Reactive] public partial bool ShowUpdateSheet { get; set; }
    [Reactive] public partial string UpdateTitle { get; set; }
    [Reactive] public partial string UpdateNotes { get; set; }
    [Reactive] public partial bool UpdateAvailableStage { get; set; }
    [Reactive] public partial bool UpdateBusyStage { get; set; }
    [Reactive] public partial bool UpdateManualStage { get; set; }
    [Reactive] public partial bool UpdateFailedStage { get; set; }
    [Reactive] public partial string UpdateStatusText { get; set; }
    [Reactive] public partial double UpdateProgress { get; set; }
    [Reactive] public partial bool UpdateProgressKnown { get; set; }
    [Reactive] public partial bool AutoUpdate { get; set; }
    [Reactive] public partial string UpdateCheckResult { get; set; }
    [Reactive] public partial bool IsCheckingUpdate { get; set; }

    public ReactiveCommand<RxVoid, RxVoid> UpdateCheckCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> UpdateOpenCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> UpdateNowCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> UpdateLaterCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> UpdateCloseCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> UpdateRevealCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> OpenSourceCmd { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> OpenLicensesCmd { get; private set; } = null!;

    /// <summary>The window asks the app to quit so the helper can swap the bundle.</summary>
    public EventChannel<RxVoid> QuitForUpdateRequested { get; } = new();

    private System.Threading.Timer? _updateTimer;
    private int _updateCheckRunning;
    private TermitusUpdateInfo? _update;
    private string? _stagedDmg;
    private CancellationTokenSource? _updateCts;
    private static readonly TimeSpan UpdateSnooze = TimeSpan.FromHours(24);

    private void InitUpdate()
    {
        UpdateBannerText = string.Empty;
        UpdateTitle = string.Empty;
        UpdateNotes = string.Empty;
        UpdateStatusText = string.Empty;
        UpdateCheckResult = string.Empty;
        AutoUpdate = TermitusSettings.AutoUpdate;
        TermitusUpdater.CleanOld();
        TermitusWinUpdate.AfterStartHousekeeping(); // Windows: log the helper's result, remove .old-* / update\ leftovers

        UpdateCheckCmd = ReactiveCommand.CreateFromTask(() => Bg(() => CheckForUpdateAsync(manual: true)));
        UpdateOpenCmd = ReactiveCommand.CreateFromTask(() =>
        {
            ShowUpdateSheet = _update != null;
            return Task.CompletedTask;
        });
        UpdateLaterCmd = ReactiveCommand.CreateFromTask(() =>
        {
            if (_update != null)
            {
                TermitusSettings.UpdateSnooze = $"{_update.Version}|{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            }
            ShowUpdateBanner = false;
            ShowUpdateSheet = false;
            return Task.CompletedTask;
        });
        UpdateCloseCmd = ReactiveCommand.CreateFromTask(() =>
        {
            if (!UpdateBusyStage)
            {
                ShowUpdateSheet = false;
            }
            return Task.CompletedTask;
        });
        UpdateNowCmd = ReactiveCommand.CreateFromTask(() => Bg(DownloadAndInstallAsync));
        UpdateRevealCmd = ReactiveCommand.CreateFromTask(() =>
        {
            if (_stagedDmg != null)
            {
                TermitusUpdateOpen.Reveal(_stagedDmg);
            }
            return Task.CompletedTask;
        });

        OpenSourceCmd = ReactiveCommand.CreateFromTask(() =>
        {
            TermitusHttp.OpenUrl(TermitusAbout.SourceUrl());
            return Task.CompletedTask;
        });
        OpenLicensesCmd = ReactiveCommand.CreateFromTask(() =>
        {
            // The bundled notices file; without it (dev build) the source repository holds the same texts.
            if (TermitusAbout.LicensesPath() is { } f)
            {
                TermitusUpdateOpen.Reveal(f);
            }
            else
            {
                TermitusHttp.OpenUrl(TermitusAbout.SourceUrl());
            }
            return Task.CompletedTask;
        });

        this.WhenAnyValue(x => x.AutoUpdate).Skip(1).Subscribe(v => TermitusSettings.AutoUpdate = v);

        // First check ~25 s after start (the core and the account refresh go first), then every 6 h.
        // The tick itself decides whether the switch allows it: flipping it on takes effect without a restart.
        _updateTimer = new System.Threading.Timer(_ => _ = CheckForUpdateAsync(manual: false), null, TimeSpan.FromSeconds(25), TermitusUpdater.CheckEvery);
    }

    private void SetUpdateStage(UpdateStage s)
    {
        UpdateAvailableStage = s == UpdateStage.Available;
        UpdateBusyStage = s is UpdateStage.Downloading or UpdateStage.Installing;
        UpdateManualStage = s == UpdateStage.Manual;
        UpdateFailedStage = s == UpdateStage.Failed;
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (!manual && !TermitusSettings.AutoUpdate)
        {
            return;
        }
        if (UpdateBusyStage || Interlocked.Exchange(ref _updateCheckRunning, 1) == 1)
        {
            return;
        }
        try
        {
            if (manual)
            {
                OnUi(() =>
                {
                    IsCheckingUpdate = true;
                    UpdateCheckResult = Loc.UpdateChecking;
                });
            }
            int? port = _connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null;
            var r = await TermitusUpdater.CheckAsync(port, CancellationToken.None);
            switch (r.Status)
            {
                case TermitusUpdateStatus.Available when r.Info != null:
                    _update = r.Info;
                    var snoozed = !manual && IsSnoozed(r.Info.Version);
                    var info = r.Info;
                    OnUi(() =>
                    {
                        UpdateTitle = Loc.UpdateTitle(info.Version);
                        UpdateBannerText = Loc.UpdateBanner(info.Version);
                        UpdateNotes = info.NotesRu;
                        SetUpdateStage(UpdateStage.Available);
                        UpdateStatusText = string.Empty;
                        ShowUpdateBanner = !snoozed;
                        if (manual)
                        {
                            UpdateCheckResult = Loc.UpdateFound(info.Version);
                            ShowUpdateSheet = true;
                        }
                    });
                    break;

                case TermitusUpdateStatus.UpToDate:
                    OnUi(() =>
                    {
                        ShowUpdateBanner = false;
                        UpdateCheckResult = manual ? Loc.UpdateUpToDate : string.Empty;
                    });
                    break;

                default:
                    var text = r.Status switch
                    {
                        TermitusUpdateStatus.NoKey => Loc.UpdateNoKey,
                        TermitusUpdateStatus.Invalid => Loc.UpdateInvalid,
                        TermitusUpdateStatus.Offline => Loc.UpdateOffline,
                        _ => Loc.UpdateServerError,
                    };
                    OnUi(() => UpdateCheckResult = manual ? text : string.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: update check failed ({ex.GetType().Name})");
            if (manual)
            {
                OnUi(() => UpdateCheckResult = Loc.UpdateServerError);
            }
        }
        finally
        {
            OnUi(() => IsCheckingUpdate = false);
            Volatile.Write(ref _updateCheckRunning, 0);
        }
    }

    private static bool IsSnoozed(string version)
    {
        var raw = TermitusSettings.UpdateSnooze;
        var parts = raw.Split('|');
        return parts.Length == 2 && parts[0] == version
            && long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var at)
            && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(at) < UpdateSnooze;
    }

    private async Task DownloadAndInstallAsync()
    {
        var info = _update;
        if (info == null || UpdateBusyStage)
        {
            return;
        }
        _updateCts = new CancellationTokenSource();
        OnUi(() =>
        {
            SetUpdateStage(UpdateStage.Downloading);
            UpdateProgress = 0;
            UpdateProgressKnown = false;
            UpdateStatusText = Loc.UpdateDownloading;
            ShowUpdateSheet = true;
        });
        try
        {
            var dir = TermitusUpdater.UpdatesDir();
            var file = Path.Combine(dir, "TER-update-" + Path.GetFileName(new Uri(info.Url).AbsolutePath).Replace(' ', '_'));
            int? port = _connected ? AppManager.Instance.GetLocalPort(EInboundProtocol.socks) : null;
            var lastUi = DateTime.MinValue;
            var kind = await TermitusUpdater.DownloadAsync(info, file, port, (done, total) =>
            {
                if (DateTime.UtcNow - lastUi < TimeSpan.FromMilliseconds(200))
                {
                    return;
                }
                lastUi = DateTime.UtcNow;
                OnUi(() =>
                {
                    UpdateProgressKnown = total is > 0;
                    UpdateProgress = total is > 0 ? Math.Min(100, done * 100.0 / total.Value) : 0;
                    UpdateStatusText = Loc.UpdateDownloadingMb(done / 1048576, total is > 0 ? total.Value / 1048576 : null);
                });
            }, _updateCts.Token);

            if (kind != TermitusUpdater.DownloadKind.Ok)
            {
                TermitusErrorLog.Add("обновление не скачано: " + kind);
                OnUi(() =>
                {
                    SetUpdateStage(UpdateStage.Failed);
                    UpdateStatusText = kind == TermitusUpdater.DownloadKind.HashMismatch ? Loc.UpdateHashBad : Loc.UpdateDownloadFailed;
                });
                return;
            }

            _stagedDmg = file;
            if (OperatingSystem.IsWindows())
            {
                await InstallWindowsAsync(info, file);
                return;
            }
            if (!OperatingSystem.IsMacOS())
            {
                OnUi(() =>
                {
                    SetUpdateStage(UpdateStage.Manual);
                    UpdateStatusText = Loc.UpdateManualWin(file);
                });
                return;
            }

            OnUi(() =>
            {
                SetUpdateStage(UpdateStage.Installing);
                UpdateProgressKnown = false;
                UpdateStatusText = Loc.UpdateInstalling;
            });
            var target = TermitusUpdater.CurrentBundle();
            var canReplace = target != null && TermitusUpdater.IsWritableDir(Path.GetDirectoryName(target)!);
            var staged = canReplace
                ? await TermitusUpdater.StageFromDmgAsync(file, info.Version, "online.termitus.desktop", info.Sha256)
                : null;
            if (canReplace && staged == null)
            {
                OnUi(() =>
                {
                    SetUpdateStage(UpdateStage.Failed);
                    UpdateStatusText = Loc.UpdateStageFailed;
                });
                return;
            }
            if (staged == null)
            {
                // Not a bundle / folder not writable: hand over the verified dmg.
                TermitusUpdateOpen.Reveal(file);
                OnUi(() =>
                {
                    SetUpdateStage(UpdateStage.Manual);
                    UpdateStatusText = Loc.UpdateManualMac;
                });
                return;
            }

            // The swap helper waits for this process to exit, so leave cleanly.
            await PreUpdateCleanupAsync();
            var open = !(TermitusTestHooks.IsTestInstance && !TermitusTestHooks.UpdateTarget.IsNullOrEmpty());
            if (!TermitusUpdater.StartSwap(staged.AppPath, target!, open, staged.TreeHash))
            {
                OnUi(() =>
                {
                    SetUpdateStage(UpdateStage.Failed);
                    UpdateStatusText = Loc.UpdateStageFailed;
                });
                return;
            }
            Logging.SaveLog($"{_tag}: update {info.Version} staged, restarting");
            QuitForUpdateRequested.Publish();
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}: update failed ({ex.GetType().Name})");
            TermitusErrorLog.Add("обновление: " + ex.GetType().Name);
            OnUi(() =>
            {
                SetUpdateStage(UpdateStage.Failed);
                UpdateStatusText = Loc.UpdateDownloadFailed;
            });
        }
    }

    /// <summary>
    /// Before TER quits for an update (LESSONS #2): protection off, no core may outlive the swap, system proxy cleared.
    /// Shared by the macOS and Windows paths.
    /// </summary>
    private async Task PreUpdateCleanupAsync()
    {
        if (_connected)
        {
            await DisconnectAsync();
        }
        PublishSysProxy(ESysProxyType.ForcedClear);
        await CoreManager.Instance.CoreStop();
        if (OperatingSystem.IsMacOS())
        {
            await TermitusMacCore.ShutdownAsync(); // core and supervisor
        }
    }

    /// <summary>
    /// 0.6.5 Windows install: the zip is verified. If the program folder is writable, unpack it, clean up and hand over to the
    /// NEW TER.exe (helper mode) which swaps the folders after this process exits; otherwise show the file (honest message).
    /// </summary>
    private async Task InstallWindowsAsync(TermitusUpdateInfo info, string zipFile)
    {
        var appDir = TermitusWinUpdate.CurrentAppDir();
        // The helper renames appDir into a sibling, so its PARENT must be writable too (not only appDir itself).
        var parent = appDir == null ? null : Path.GetDirectoryName(appDir);
        if (appDir == null || parent == null || !TermitusUpdater.IsWritableDir(appDir) || !TermitusUpdater.IsWritableDir(parent))
        {
            OnUi(() =>
            {
                SetUpdateStage(UpdateStage.Manual);
                UpdateStatusText = Loc.UpdateManualWinNoWrite;
            });
            return;
        }
        OnUi(() =>
        {
            SetUpdateStage(UpdateStage.Installing);
            UpdateProgressKnown = false;
            UpdateStatusText = Loc.UpdateInstalling;
        });
        TermitusWinUpdate.MarkUpdateStarted(); // from now on the 30 s housekeeping must leave the update folder alone
        var newDir = await Task.Run(() => TermitusWinUpdate.StageFromZip(zipFile, info.Version, TermitusWinUpdate.UpdateRoot(), info.Sha256));
        if (newDir == null)
        {
            TermitusWinUpdate.MarkUpdateAborted();
            TermitusErrorLog.Add("обновление: не удалось распаковать");
            OnUi(() =>
            {
                SetUpdateStage(UpdateStage.Failed);
                UpdateStatusText = Loc.UpdateStageFailed;
            });
            return;
        }
        // Launch the helper FIRST (it waits for this process to exit anyway); only when it runs do we stop the core and quit.
        // If it cannot start, the connection stays as it was.
        if (!TermitusWinUpdate.StartApplyHelper(newDir, appDir))
        {
            TermitusWinUpdate.MarkUpdateAborted();
            OnUi(() =>
            {
                SetUpdateStage(UpdateStage.Failed);
                UpdateStatusText = Loc.UpdateStageFailed;
            });
            return;
        }
        await PreUpdateCleanupAsync();
        Logging.SaveLog($"{_tag}: update {info.Version} staged, restarting");
        QuitForUpdateRequested.Publish();
    }
}
