// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using S9Woa.Installer.Core;
using S9Woa.Installer.Core.Deploy;
using S9Woa.Installer.Core.Device;
using S9Woa.Installer.Core.Host;
using S9Woa.Installer.Core.Image;
using S9Woa.Installer.Core.Stages;

namespace S9Woa.Installer.App.Pages;

public sealed partial class InstallPage : Page, IWizardStep
{
    private readonly List<StageItem> _items;
    private readonly StageProgress _progress = new();
    private StageItem? _current;
    private CancellationTokenSource? _cts;
    private bool _running;

    public InstallPage()
    {
        InitializeComponent();
        _items = StageCatalog.All.Select(s => new StageItem(s) { Status = AppServices.State.StatusOf(s.Id) }).ToList();
        Timeline.ItemsSource = _items;
        ExperimentalToggle.IsOn = AppServices.ExperimentalEnabled;
        VerifyToggle.IsOn = AppServices.VerifyWrites;
        SkipTwrpToggle.IsOn = AppServices.SkipTwrpFlash;
        var dev = AppServices.CurrentDevice;
        ReviewPhone.Text = dev is null ? "Not connected" : $"{dev.Model} · {dev.Bootloader}";
        ReviewMedia.Text = AppServices.MediaPath is null ? "Not selected" : Path.GetFileName(AppServices.MediaPath);
        ReviewProfile.Text = AppServices.Profile switch
        {
            SlimProfile.None => "Stock",
            SlimProfile.Lite => "Lite",
            _ => "Core",
        };
        ReviewAccount.Text = AppServices.Unattend.Username + (AppServices.Unattend.Password is null ? " · no password" : "");
        AppServices.LogWritten += OnLog;
        Unloaded += (_, _) => AppServices.LogWritten -= OnLog;
        UpdateNow();
    }

    public event EventHandler? StateChanged;

    public bool CanAdvance => !_running;

    public bool CanGoBack => !_running;

    public string? NextLabel => _running ? "Installing…" : _items.Any(i => i.Status == StageStatus.Done) ? "Resume" : "Install";

    public Task<bool> OnAdvanceAsync()
    {
        _ = RunAllAsync();
        return Task.FromResult(false);
    }

    private void OnLog(string line) => DispatcherQueue.TryEnqueue(() =>
    {
        LogText.Text += line + Environment.NewLine;
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null);
        if (_running && _progress.Observe(line))
        {
            UpdateNow();
        }
    });

    /// <summary>The progress card: the running step with its own bar, or what comes next, plus the whole install.</summary>
    private void UpdateNow()
    {
        static bool Finished(StageItem i) => i.Status is StageStatus.Done or StageStatus.Skipped;
        var total = _items.Count;
        var finished = _items.Count(Finished);
        var running = _running ? _current : null;
        OverallProgress.Value = StageProgress.Overall(_items.Select(i => (i.Definition.Id, Finished(i))), running?.Definition.Id, _progress.Fraction);
        OverallText.Text = $"{finished} of {total} steps done";
        if (running is not null)
        {
            NowCaption.Text = $"Step {_items.IndexOf(running) + 1} of {total}";
            NowTitle.Text = running.Title;
            NowDetail.Text = _progress.Detail.Length > 0 ? _progress.Detail : running.Summary;
            StepProgress.Visibility = Visibility.Visible;
            StepProgress.IsIndeterminate = _progress.Fraction is null;
            StepProgress.Value = _progress.Fraction ?? 0;
            return;
        }
        StepProgress.Visibility = Visibility.Collapsed;
        var failed = _items.FirstOrDefault(i => i.Status == StageStatus.Failed);
        var next = failed ?? _items.FirstOrDefault(i => !Finished(i));
        if (next is null)
        {
            NowCaption.Text = "Finished";
            NowTitle.Text = "Windows is installed";
            NowDetail.Text = "Unplug the phone. Windows finishes setting itself up and signs you in.";
            return;
        }
        NowCaption.Text = failed is not null ? "Stopped at" : finished == 0 ? "First step" : "Next step";
        NowTitle.Text = next.Title;
        NowDetail.Text = next.Summary;
    }

    private void SetStatus(StageItem item, StageStatus status, string? detail = null)
    {
        item.Status = status;
        AppServices.State.Set(item.Definition.Id, status, detail);
        AppServices.SaveState();
    }

    private void SetRunning(bool running)
    {
        _running = running;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ExperimentalToggle.IsEnabled = !running;
        VerifyToggle.IsEnabled = !running;
        SkipTwrpToggle.IsEnabled = !running;
        HeadlineText.Text = running ? "Installing Windows" : HeadlineText.Text;
        UpdateNow();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunAllAsync()
    {
        _cts = new CancellationTokenSource();
        ResultBar.IsOpen = false;
        SetRunning(true);
        try
        {
            foreach (var item in _items)
            {
                if (item.Status is StageStatus.Done or StageStatus.Skipped)
                {
                    continue;
                }
                var avail = item.Definition.Availability;
                if (avail == StageAvailability.NotImplemented)
                {
                    Report(InfoBarSeverity.Informational, item.Title,
                        "This step isn't automated yet. Nothing has been written to the phone beyond the finished steps.");
                    return;
                }
                if (avail == StageAvailability.Experimental && !AppServices.ExperimentalEnabled)
                {
                    Report(InfoBarSeverity.Informational, item.Title,
                        "This step is experimental: automated, but not yet validated end to end. Continue only on your own Galaxy S9+ with its backup.",
                        ("Run experimental steps", () =>
                        {
                            ExperimentalToggle.IsOn = true;
                            _ = RunAllAsync();
                        }));
                    return;
                }
                SetStatus(item, StageStatus.Running);
                _current = item;
                _progress.Begin(item.Definition.Id);
                UpdateNow();
                AppServices.Log($"stage {item.Definition.Id}: {item.Title}");
                var (ok, message) = await RunStageAsync(item.Definition, _cts.Token);
                SetStatus(item, ok ? StageStatus.Done : StageStatus.Failed, message);
                UpdateNow();
                if (!ok)
                {
                    AppServices.Log($"stage {item.Definition.Id} stopped: {message}");
                    Report(InfoBarSeverity.Error, item.Title, message);
                    return;
                }
            }
            HeadlineText.Text = "Windows is installed";
            LeadText.Text = "Unplug the phone. Windows finishes setting itself up and signs you in.";
            Report(InfoBarSeverity.Success, "All done", "Windows is installed on your Galaxy S9+.");
        }
        catch (OperationCanceledException)
        {
            foreach (var i in _items.Where(i => i.Status == StageStatus.Running))
            {
                SetStatus(i, StageStatus.Pending);
            }
            Report(InfoBarSeverity.Warning, "Cancelled", "Nothing further was written. You can resume later.");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            foreach (var i in _items.Where(i => i.Status == StageStatus.Running))
            {
                SetStatus(i, StageStatus.Failed, e.Message);
            }
            AppServices.Log($"install stopped: {e.Message}");
            Report(InfoBarSeverity.Error, "Stopped", e.Message);
        }
        finally
        {
            SetRunning(false);
            if (HeadlineText.Text == "Installing Windows")
            {
                HeadlineText.Text = "Ready to install";
            }
        }
    }

    private static async Task<(bool Ok, string Message)> RunStageAsync(StageDefinition stage, CancellationToken ct)
    {
        var log = new Progress<string>(AppServices.Log);
        switch (stage.Id)
        {
            case "host":
                var host = HostPreflight.Evaluate(new LocalHostEnvironment(AppServices.AdbPath), AppServices.WorkDirectory);
                return host.HasBlockers()
                    ? (false, string.Join(" ", host.Where(h => h.Severity == CheckSeverity.Blocker).Select(h => h.Detail)))
                    : (true, "PC ready.");

            case "identify":
                if (AppServices.Adb is null)
                {
                    return (false, "adb.exe not found.");
                }
                var devices = await AppServices.Adb.ListDevicesAsync(ct);
                if (devices.Count == 0 && await AppServices.FindDownloadModeAsync(ct) is (true, var inDownload))
                {
                    if (inDownload is null)
                    {
                        return (false, "The phone is in Download mode but hasn't been identified yet. Start it in Android with USB debugging "
                            + "(or in TWRP) once, then press Resume.");
                    }
                    AppServices.CurrentDevice = inDownload;
                    return (true, $"{inDownload.Model} {inDownload.Bootloader} (in Download mode, identified earlier)");
                }
                if (devices.Count != 1)
                {
                    return (false, devices.Count == 0 ? "No phone connected." : "Connect only one phone.");
                }
                var snap = DeviceSnapshot.FromAdb(devices[0], devices[0].State is AdbState.Device or AdbState.Recovery
                    ? await AppServices.Adb.GetPropertiesAsync(devices[0].Serial, ct) : null);
                AppServices.CurrentDevice = snap;
                if (AppServices.State.DeviceSerial is { } known && known != snap.Serial)
                {
                    return (false, $"This is a different phone ({snap.Serial}) from the one this installation started on ({known}).");
                }
                var checks = DeviceEligibility.Evaluate(snap);
                return checks.HasBlockers()
                    ? (false, string.Join(" ", checks.Where(c => c.Severity == CheckSeverity.Blocker).Select(c => c.Detail)))
                    : (true, $"{snap.Model} {snap.Bootloader}");

            case "unlock":
                var refreshed = await RunStageAsync(StageCatalog.Get("identify"), ct);
                if (!refreshed.Ok)
                {
                    return refreshed;
                }
                return AppServices.CurrentDevice switch
                {
                    { FlashLocked: false } => (true, "Bootloader unlocked."),
                    // Download mode can't report it; a locked bootloader refuses the TWRP flash itself.
                    { Mode: DeviceMode.Download } => (true, "Not reported in Download mode; the TWRP flash checks it."),
                    _ => (false, "Unlock the bootloader (see the Phone page), finish Android setup, re-enable USB debugging, then press Resume."),
                };

            case "twrp":
                return await RunTwrpAsync(log, ct);

            case "backup":
                return await RunBackupAsync(log, ct);

            case "media":
                return await RunMediaAsync(ct);

            case "image":
                return await RunImageAsync(log, ct);

            case "partition":
                return await RunPartitionAsync(ct);

            case "transfer":
                return await RunTransferAsync(log, ct);

            case "uefi":
                return await RunUefiAsync(log, ct);

            case "firstboot":
                return await RunFirstBootAsync(log, ct);

            default:
                await Task.Yield();
                return stage.Availability == StageAvailability.NotImplemented
                    ? (false, "This step is not automated in this preview yet. Nothing has been written to the phone. Follow the project README for the manual procedure.")
                    : (false, "Unknown stage.");
        }
    }

    private static async Task<(bool, string)> RunTwrpAsync(IProgress<string> log, CancellationToken ct)
    {
        if (AppServices.SkipTwrpFlash)
        {
            return await StartExistingTwrpAsync(log, ct);
        }
        if (AppServices.TwrpImagePath is null)
        {
            return (false, "TWRP is not set up. Add it on the Set up page, then press Resume.");
        }
        var resolved = await AppServices.TwrpFlasher.ResolveAsync(ct);
        if (resolved is null && AppServices.Device is not null && await RefreshDeviceAsync(ct) is { Mode: DeviceMode.Recovery })
        {
            // adb only runs in TWRP (stock recovery offers sideload at most), so it is already installed.
            log.Report("The phone is already in TWRP.");
            return await SettleTwrpAsync(log, ct) && await ClearBootRequestAsync(log, ct)
                ? (true, "TWRP is running.")
                : (false, "TWRP started but its connection is not settled yet. Wait a minute, then press Resume.");
        }
        if (resolved is null && AppServices.Device is not null
            && await RefreshDeviceAsync(ct) is { Mode: DeviceMode.Android } phone)
        {
            log.Report("Restarting the phone into Download mode...");
            await AppServices.Device.RebootAsync(phone.Serial, RebootTarget.Download, ct);
            for (var i = 0; i < 60 && resolved is null; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                resolved = await AppServices.TwrpFlasher.ResolveAsync(ct);
                if (i == 20 && resolved is null)
                {
                    log.Report("Still waiting for Download mode. If the phone shows a warning screen, press Volume Up to continue.");
                }
            }
        }
        if (resolved is null)
        {
            return (false, "The phone isn't in Download mode. Power it off, hold Volume Down + Bixby + Power, then press Volume Up "
                + "at the warning screen. Then press Resume.");
        }
        bool restarted;
        try
        {
            restarted = await AppServices.TwrpFlasher.FlashRecoveryAsync(AppServices.TwrpImagePath, log, ct);
        }
        catch (Exception e) when (e is IOException or TimeoutException)
        {
            return (false, $"Flashing TWRP failed: {e.Message} Restart the phone into Download mode (hold Volume Down + Power, "
                + "then Volume Down + Bixby + Power) and press Resume.");
        }

        if (AppServices.Device is null || AppServices.CurrentDevice is null)
        {
            return (true, "TWRP flashed. Boot into TWRP now (Volume Up + Bixby + Power).");
        }
        log.Report(restarted
            ? "The phone is restarting into TWRP by itself..."
            : "Now boot TWRP: hold Volume Down + Power until the screen goes off, then immediately hold Volume Up + Bixby + Power.");
        try
        {
            AppServices.CurrentDevice = await AppServices.Device.WaitForModeAsync(AppServices.CurrentDevice.Serial, DeviceMode.Recovery,
                TimeSpan.FromMinutes(5), log, ct);
            return await SettleTwrpAsync(log, ct) && await ClearBootRequestAsync(log, ct)
                ? (true, "TWRP is running.")
                : (false, "TWRP started but its connection is not settled yet. Wait a minute, then press Resume.");
        }
        catch (TimeoutException)
        {
            return (false, restarted
                ? "TWRP did not come up. If the phone is showing Android, restart it and hold Volume Up + Bixby + Power to open TWRP, then press Resume."
                : "TWRP did not come up. The flash succeeded; boot TWRP manually, then press Resume.");
        }
    }

    /// <summary>
    /// "TWRP is already on the phone": nothing is flashed. A phone in Android is restarted into
    /// recovery; otherwise the user starts TWRP with the key combination.
    /// </summary>
    private static async Task<(bool, string)> StartExistingTwrpAsync(IProgress<string> log, CancellationToken ct)
    {
        if (AppServices.Device is null)
        {
            return (false, "adb.exe not found.");
        }
        var dev = await RefreshDeviceAsync(ct);
        var serial = dev?.Serial ?? AppServices.State.DeviceSerial;
        if (serial is null)
        {
            return (false, "Connect the phone (Android with USB debugging, or TWRP) so the installer can identify it, then press Resume.");
        }
        log.Report("Skipping the TWRP flash: TWRP is already on the phone.");
        if (dev is { Mode: DeviceMode.Android })
        {
            log.Report("Restarting the phone into TWRP...");
            await AppServices.Device.RebootAsync(serial, RebootTarget.Recovery, ct);
        }
        else if (dev is not { Mode: DeviceMode.Recovery })
        {
            log.Report("Start TWRP: hold Volume Down + Power until the screen goes off, then immediately hold Volume Up + Bixby + Power.");
        }
        try
        {
            AppServices.CurrentDevice = dev is { Mode: DeviceMode.Recovery }
                ? dev
                : await AppServices.Device.WaitForModeAsync(serial, DeviceMode.Recovery, TimeSpan.FromMinutes(5), log, ct);
        }
        catch (TimeoutException)
        {
            return (false, "TWRP did not come up. Start it with Volume Up + Bixby + Power, then press Resume. "
                + "If the phone shows Samsung's own recovery, turn off \"TWRP is already on the phone\" so the installer flashes it.");
        }
        return await SettleTwrpAsync(log, ct) && await ClearBootRequestAsync(log, ct)
            ? (true, "TWRP is running (not flashed; it was already on the phone).")
            : (false, "TWRP started but its connection is not settled yet. Wait a minute, then press Resume.");
    }

    /// <summary>Clears the MISC boot-recovery request used to start TWRP, so later restarts are normal.</summary>
    private static async Task<bool> ClearBootRequestAsync(IProgress<string> log, CancellationToken ct)
    {
        if (AppServices.CurrentDevice is not { } dev || AppServices.Twrp(dev.Serial) is not { } twrp)
        {
            return false;
        }
        await new BootRouteService(twrp).ClearBootRequestAsync(log, ct);
        // Neutralise a stale ui.zip theme override for this boot (baked heal covers the next one).
        await twrp.RemoveStaleThemeOverrideAsync(ct);
        return true;
    }

    /// <summary>Re-reads the connected phone so stages act on its current mode (Android, TWRP, ...).</summary>
    private static async Task<DeviceSnapshot?> RefreshDeviceAsync(CancellationToken ct)
    {
        if (AppServices.Adb is null)
        {
            return AppServices.CurrentDevice;
        }
        var devices = await AppServices.Adb.ListDevicesAsync(ct);
        if (devices.Count == 0 && await AppServices.FindDownloadModeAsync(ct) is (true, var inDownload))
        {
            return AppServices.CurrentDevice = inDownload;
        }
        if (devices.Count != 1)
        {
            return AppServices.CurrentDevice = null;
        }
        var d = devices[0];
        return AppServices.CurrentDevice = DeviceSnapshot.FromAdb(d,
            d.State is AdbState.Device or AdbState.Recovery ? await AppServices.Adb.GetPropertiesAsync(d.Serial, ct) : null);
    }

    /// <summary>
    /// TWRP's adb drops and SD access fail during its first minutes; wait until it has been up
    /// for 150 s with the SD card mounted before staging anything through it.
    /// </summary>
    private static async Task<bool> SettleTwrpAsync(IProgress<string> log, CancellationToken ct)
    {
        var dev = AppServices.CurrentDevice;
        var twrp = dev is null ? null : AppServices.Twrp(dev.Serial);
        if (twrp is null)
        {
            return false;
        }
        for (var i = 0; i < 60; i++)
        {
            try
            {
                var up = await twrp.UptimeSecondsAsync(ct);
                if (up >= 150 && await twrp.IsMountedAsync("/external_sd", ct))
                {
                    return true;
                }
                if (i == 0)
                {
                    log.Report($"Letting TWRP settle (up {up:0} s; needs 150 s and the SD card)...");
                }
            }
            catch (InvalidOperationException)
            {
                // adb reconnecting; keep waiting.
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return false;
    }

    private static async Task<(bool, string)> RunBackupAsync(IProgress<string> log, CancellationToken ct)
    {
        var dev = await RefreshDeviceAsync(ct);
        if (dev is null || dev.Mode != DeviceMode.Recovery)
        {
            return (false, "Boot the phone into TWRP first (the Install TWRP step).");
        }
        var twrp = AppServices.Twrp(dev.Serial);
        if (twrp is null)
        {
            return (false, "adb.exe not found.");
        }
        if (!await SettleTwrpAsync(log, ct))
        {
            return (false, "TWRP's connection hasn't settled (it needs about 2.5 minutes and the SD card). Press Resume shortly.");
        }
        var dir = Path.Combine(AppServices.BackupDirectory, dev.Serial);
        var manifest = await new BackupService(twrp).BackupAsync(dir, dev.Model ?? "SM-G965F", dev.Serial, log, ct);
        return (true, $"Backed up {manifest.Partitions.Count} partitions to {dir}. Keep this folder safe.");
    }

    private static async Task<(bool, string)> RunMediaAsync(CancellationToken ct)
    {
        if (AppServices.MediaPath is null || !File.Exists(AppServices.MediaPath))
        {
            return (false, "Choose Windows media on the Windows image page.");
        }
        var media = new WindowsMedia(AppServices.Runner);
        string? install;
        try
        {
            install = await media.ResolveInstallImageAsync(AppServices.MediaPath, ct);
        }
        catch (InvalidOperationException e)
        {
            return (false, e.Message);
        }
        if (install is null)
        {
            return (false, "The media does not contain sources\\install.wim or install.esd. Choose a Windows 11 ARM64 ISO, or its install.wim or install.esd.");
        }
        var editions = await media.GetEditionsAsync(install, ct);
        var chosen = WindowsMedia.ChooseEdition(editions);
        var build = await media.GetBuildAsync(install, chosen.Index, ct);
        if (AppServices.Toolset.LoadFirmwareCatalog() is { } catalog && build is not null && catalog.ForMediaBuild(build) is null)
        {
            return (false, $"This media is Windows {build}. The phone's UEFI starts exactly one Windows build per firmware image, "
                + $"and the available images are for {catalog.SupportedBuilds}. Choose media with one of those builds on the Windows page.");
        }
        AppServices.InstallImagePath = install;
        AppServices.EditionIndex = chosen.Index;
        return (true, $"Using {chosen.Name}{(build is null ? "" : $" {build}")} (index {chosen.Index}) from {Path.GetFileName(install)}.");
    }

    private static async Task<(bool, string)> RunImageAsync(IProgress<string> log, CancellationToken ct)
    {
        if (AppServices.InstallImagePath is null)
        {
            var media = await RunMediaAsync(ct);
            if (!media.Item1)
            {
                return media;
            }
        }
        var drivers = ImageBuilder.DiscoverDrivers(AppServices.DriversDirectory);
        if (drivers.Count == 0)
        {
            return (false, "The phone drivers are not set up. Add them on the Set up page (release download or build folder).");
        }
        var outDir = Path.Combine(AppServices.WorkDirectory, "out");
        var vhdx = Path.Combine(AppServices.WorkDirectory, "s9windows.vhdx");
        Directory.CreateDirectory(AppServices.WorkDirectory);
        if (File.Exists(vhdx))
        {
            File.Delete(vhdx);
        }
        var built = await new VhdxImageBuilder(AppServices.Runner).BuildAsync(vhdx,
            AppServices.InstallImagePath!, AppServices.EditionIndex, drivers, AppServices.Profile,
            AppServices.Unattend, outDir, log, ct);
        AppServices.Built = built;
        var firmwareNote = "";
        if (AppServices.Toolset.LoadFirmwareCatalog() is { } catalog)
        {
            var firmware = catalog.ForBootFiles(built.LoaderSha256, built.KernelSha256);
            if (firmware is null)
            {
                return (false, $"The image's boot loader ({built.LoaderSha256[..12]}…) and kernel ({built.KernelSha256[..12]}…) match none of the "
                    + $"UEFI builds ({catalog.SupportedBuilds}); the phone would stop at the Samsung logo. Use media with a supported build.");
            }
            AppServices.State.FirmwareFile = firmware.File;
            AppServices.SaveState();
            firmwareNote = $", UEFI for Windows {firmware.Windows}";
        }
        return (true, $"Windows image built: {built.WindowsBytes >> 20} MiB volume + boot files, {drivers.Count} drivers, {AppServices.Profile} profile{firmwareNote}.");
    }

    private static TwrpClient? RequireTwrp(out DeviceSnapshot? dev, out string error)
    {
        dev = AppServices.CurrentDevice;
        if (dev is null || dev.Mode != DeviceMode.Recovery)
        {
            error = "Boot the phone into TWRP first (the Install TWRP step).";
            return null;
        }
        var twrp = AppServices.Twrp(dev.Serial);
        if (twrp is null)
        {
            error = "adb.exe not found.";
            return null;
        }
        error = "";
        return twrp;
    }

    private static async Task<(bool, string)> RunPartitionAsync(CancellationToken ct)
    {
        await RefreshDeviceAsync(ct);
        var twrp = RequireTwrp(out _, out var err);
        if (twrp is null)
        {
            return (false, err);
        }
        var parts = await twrp.ListPartitionsAsync(ct);
        var required = new[]
        {
            PartitionMap.WindowsTarget, PartitionMap.UefiTarget, PartitionMap.EfiSystemPartition,
            PartitionMap.SecondaryEfiSystemPartition, PartitionMap.RecoveryTarget, PartitionMap.Misc,
        };
        var missing = required.Where(n => !parts.Keys.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
        {
            return (false, $"The phone did not expose the expected partition(s): {string.Join(", ", missing)}.");
        }
        foreach (var (name, expected) in new[] { (PartitionMap.WindowsTarget, PartitionMap.WindowsBytes), (PartitionMap.EfiSystemPartition, PartitionMap.CacheBytes) })
        {
            var actual = parts.Keys.First(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            var size = await twrp.PartitionSizeAsync(actual, ct);
            if (size != expected)
            {
                return (false, $"{actual} is {size:N0} bytes, not the validated {expected:N0}. This phone's layout differs; stopping before any write.");
            }
        }
        return (true, $"Partitions verified: USERDATA ({PartitionMap.WindowsBytes >> 20} MiB) and the CACHE boot partition match the validated layout.");
    }

    private static async Task<(bool, string)> RunTransferAsync(IProgress<string> log, CancellationToken ct)
    {
        await RefreshDeviceAsync(ct);
        var twrp = RequireTwrp(out _, out var err);
        if (twrp is null)
        {
            return (false, err);
        }
        if (!File.Exists(AppServices.WindowsImagePath))
        {
            return (false, $"No Windows image at {AppServices.WindowsImagePath}. Run the Build the Windows image step first.");
        }
        if (await twrp.IsMountedAsync("/sdcard", ct))
        {
            await twrp.UnmountAsync("/sdcard", ct);
        }
        try
        {
            await new TransferService(twrp).WriteRawImageAsync(PartitionMap.WindowsTarget, AppServices.WindowsImagePath,
                new RawWriteOptions { Verify = AppServices.VerifyWrites }, mountToEnsureUnmounted: "/data", log: log, ct: ct);

            if (Directory.Exists(AppServices.EspDirectory))
            {
                log.Report("Writing the Windows boot files...");
                await new BootFilesService(twrp).WriteAsync(AppServices.EspDirectory, log, ct);
            }
        }
        finally
        {
            // Take the "Installing Windows" screen down once the writing is done.
            await twrp.ClearInstallStatusAsync(CancellationToken.None);
        }
        return (true, AppServices.VerifyWrites
            ? "Windows and boot files written and verified on the phone."
            : "Windows and boot files written to the phone.");
    }

    private static async Task<(bool, string)> RunUefiAsync(IProgress<string> log, CancellationToken ct)
    {
        await RefreshDeviceAsync(ct);
        var twrp = RequireTwrp(out _, out var err);
        if (twrp is null)
        {
            return (false, err);
        }
        string? image;
        var note = "";
        if (AppServices.Toolset.LoadFirmwareCatalog() is { } catalog)
        {
            var firmware = catalog.Images.FirstOrDefault(i => i.File == AppServices.State.FirmwareFile)
                ?? (AppServices.Built is { } b ? catalog.ForBootFiles(b.LoaderSha256, b.KernelSha256) : null);
            if (firmware is null)
            {
                return (false, "The installer doesn't know which UEFI matches the Windows image. Run Build the Windows image again, then press Resume.");
            }
            if (!catalog.Verify(firmware))
            {
                return (false, $"{firmware.File} does not match its checksum. Import the UEFI images again on the Set up page.");
            }
            image = catalog.PathOf(firmware);
            note = $" (for Windows {firmware.Windows})";
            log.Report($"Installing the UEFI build for Windows {firmware.Windows}...");
        }
        else
        {
            image = AppServices.UefiImagePath is { } p && File.Exists(p) ? p : null;
        }
        if (image is null)
        {
            return (false, "The UEFI image is not set up. Add it on the Set up page, then press Resume.");
        }
        await new TransferService(twrp).WriteWholePartitionAsync(PartitionMap.UefiTarget, image, log, ct, verify: AppServices.VerifyWrites);
        await twrp.ClearInstallStatusAsync(CancellationToken.None);
        return (true, $"UEFI installed to BOOT{note}. RECOVERY keeps TWRP.");
    }

    private static async Task<(bool, string)> RunFirstBootAsync(IProgress<string> log, CancellationToken ct)
    {
        var dev = await RefreshDeviceAsync(ct);
        if (dev is null || AppServices.Device is null || AppServices.Adb is null)
        {
            return (true, "Unplug the phone and power it on to start Windows.");
        }
        var modules = AppServices.Toolset.TwrpModulesDirectory;
        for (var attempt = 1; ; attempt++)
        {
            if (attempt > 1)
            {
                dev = await RefreshDeviceAsync(ct) ?? dev;
            }
            if (dev.Mode == DeviceMode.Recovery && AppServices.Twrp(dev.Serial) is { } twrp)
            {
                // A leftover boot-recovery request or pending watchdog record would send the phone straight back to TWRP.
                await new BootRouteService(twrp).ClearAsync(modules, log, ct);
                await twrp.ClearInstallStatusAsync(ct);
                await twrp.ShellAsync("umount /data 2>/dev/null; umount /cache 2>/dev/null; umount /sdcard 2>/dev/null; sync; true", ct);
            }
            log.Report(attempt == 1 ? "Restarting into Windows..." : "Restarting into Windows again...");
            await AppServices.Device.RebootAsync(dev.Serial, RebootTarget.System, ct);

            var (returned, after, record) = await WatchFirstBootAsync(dev.Serial, modules, log, ct);
            if (!returned)
            {
                return (true, "Windows is starting. The first start and setup take several minutes and restart the phone once. "
                    + "If it stays on the Samsung logo for more than 5 minutes, hold Volume Down + Power to restart it.");
            }
            // After a forced restart the record is in a state TWRP can't acknowledge, so the next start only
            // latches: it finds a stale boot owner and returns in ~25 s without trying Windows. That latch can
            // be acknowledged, and then one real attempt is allowed.
            if (attempt == 1 && after < QuickBounce && record is { IsRecoveryPending: true, Reason: RecoveryRecord.StaleBootOwnerReason })
            {
                log.Report("The firmware's recovery latch sent the phone back without trying Windows (expected once after a forced restart). "
                    + "Acknowledging it and trying again...");
                continue;
            }
            return (false, "Windows did not start; the phone returned to TWRP"
                + (record is null ? "." : $" (watchdog record: {record}).")
                + " Press Resume to try again, or open Device tools to collect diagnostics.");
        }
    }

    /// <summary>A return to TWRP faster than this means Windows was never attempted.</summary>
    private static readonly TimeSpan QuickBounce = TimeSpan.FromSeconds(75);

    /// <summary>
    /// Windows exposes no adb, so success is the phone staying away for four minutes; a failed start
    /// comes back to TWRP. Returns whether it came back, how long after the restart, and the record.
    /// </summary>
    private static async Task<(bool Returned, TimeSpan After, RecoveryRecord? Record)> WatchFirstBootAsync(
        string serial, string? modules, IProgress<string> log, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var gone = false;
        while (DateTime.UtcNow - started < TimeSpan.FromMinutes(4))
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var devices = await AppServices.Adb!.ListDevicesAsync(ct);
            var phone = devices.FirstOrDefault(d => d.Serial == serial);
            if (phone is null)
            {
                gone = true;
                continue;
            }
            if (gone && phone.State == AdbState.Recovery)
            {
                var after = DateTime.UtcNow - started;
                log.Report($"The phone came back to TWRP after {after.TotalSeconds:0} s; reading why...");
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                RecoveryRecord? record = null;
                if (AppServices.Twrp(serial) is { } back)
                {
                    record = await new BootRouteService(back).ReadRecordAsync(modules, ct);
                }
                if (record is not null)
                {
                    log.Report($"Watchdog record: {record}.");
                }
                return (true, after, record);
            }
        }
        return (false, DateTime.UtcNow - started, null);
    }

    private void Report(InfoBarSeverity severity, string title, string message, (string Label, Action Run)? action = null)
    {
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.ActionButton = null;
        if (action is { } a)
        {
            var button = new Button { Content = a.Label };
            button.Click += (_, _) =>
            {
                ResultBar.IsOpen = false;
                a.Run();
            };
            ResultBar.ActionButton = button;
        }
        ResultBar.IsOpen = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnExperimentalToggled(object sender, RoutedEventArgs e) =>
        AppServices.ExperimentalEnabled = ExperimentalToggle.IsOn;

    private void OnSkipTwrpToggled(object sender, RoutedEventArgs e)
    {
        if (AppServices.SkipTwrpFlash != SkipTwrpToggle.IsOn)
        {
            AppServices.SkipTwrpFlash = SkipTwrpToggle.IsOn;
            AppServices.SaveState();
        }
    }

    private void OnVerifyToggled(object sender, RoutedEventArgs e)
    {
        if (AppServices.VerifyWrites != VerifyToggle.IsOn)
        {
            AppServices.VerifyWrites = VerifyToggle.IsOn;
            AppServices.SaveState();
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppServices.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", AppServices.LogDirectory) { UseShellExecute = true });
    }
}
