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

public sealed partial class InstallPage : Page
{
    private readonly List<StageItem> _items;
    private CancellationTokenSource? _cts;

    public InstallPage()
    {
        InitializeComponent();
        _items = StageCatalog.All.Select(s => new StageItem(s) { Status = AppServices.State.StatusOf(s.Id) }).ToList();
        Timeline.ItemsSource = _items;
        StartText.Text = _items.Any(i => i.Status == StageStatus.Done) ? "Resume" : "Start";
        var dev = AppServices.CurrentDevice;
        ReviewText.Text =
            $"Phone: {(dev is null ? "not connected" : $"{dev.Model} · {dev.Bootloader}")}   ·   " +
            $"Media: {(AppServices.MediaPath is null ? "not selected" : Path.GetFileName(AppServices.MediaPath))}   ·   " +
            $"Image: {AppServices.Profile}";
        AppServices.LogWritten += OnLog;
        Unloaded += (_, _) => AppServices.LogWritten -= OnLog;
    }

    private void OnLog(string line) => DispatcherQueue.TryEnqueue(() =>
    {
        LogText.Text += line + Environment.NewLine;
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null);
    });

    private void SetStatus(StageItem item, StageStatus status, string? detail = null)
    {
        item.Status = status;
        AppServices.State.Set(item.Definition.Id, status, detail);
        AppServices.SaveState();
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ResultBar.IsOpen = false;
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
                        "This step is experimental. Turn on \"Run experimental steps\" below to continue on your own device.");
                    return;
                }
                SetStatus(item, StageStatus.Running);
                AppServices.Log($"stage {item.Definition.Id}: {item.Title}");
                var (ok, message) = await RunStageAsync(item.Definition, _cts.Token);
                SetStatus(item, ok ? StageStatus.Done : StageStatus.Failed, message);
                if (!ok)
                {
                    AppServices.Log($"stage {item.Definition.Id} stopped: {message}");
                    Report(InfoBarSeverity.Error, item.Title, message);
                    return;
                }
            }
            Report(InfoBarSeverity.Success, "Windows is installed", "Unplug the phone and follow Windows setup on the screen.");
        }
        catch (OperationCanceledException)
        {
            foreach (var i in _items.Where(i => i.Status == StageStatus.Running))
            {
                SetStatus(i, StageStatus.Pending);
            }
            Report(InfoBarSeverity.Warning, "Cancelled", "Nothing further was written. You can resume later.");
        }
        finally
        {
            StartButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            StartText.Text = "Resume";
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
                return AppServices.CurrentDevice?.FlashLocked == false
                    ? (true, "Bootloader unlocked.")
                    : (false, "Unlock the bootloader (see the Phone page), finish Android setup, re-enable USB debugging, then press Resume.");

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
                return await RunFirstBootAsync(ct);

            default:
                await Task.Yield();
                return stage.Availability == StageAvailability.NotImplemented
                    ? (false, "This step is not automated in this preview yet. Nothing has been written to the phone. Follow the project README for the manual procedure.")
                    : (false, "Unknown stage.");
        }
    }

    private static async Task<(bool, string)> RunTwrpAsync(IProgress<string> log, CancellationToken ct)
    {
        if (AppServices.TwrpImagePath is null)
        {
            return (false, "Place a TWRP image at payload\\twrp.img next to the installer, then retry.");
        }
        var resolved = await AppServices.TwrpFlasher.ResolveAsync(ct);
        if (resolved is null)
        {
            return (false, "Put the phone in Download mode and install Heimdall (with the Zadig/libusbK driver). "
                + "Power off, hold Volume Down + Bixby + Power, then press Volume Up to enter Download mode.");
        }
        await AppServices.TwrpFlasher.FlashRecoveryAsync(AppServices.TwrpImagePath, log, ct);

        if (AppServices.Device is null || AppServices.CurrentDevice is null)
        {
            return (true, "TWRP flashed. Boot into TWRP now (Volume Up + Bixby + Power).");
        }
        log.Report("Waiting for TWRP. Boot it now: hold Volume Up + Bixby + Power.");
        try
        {
            await AppServices.Device.WaitForModeAsync(AppServices.CurrentDevice.Serial, DeviceMode.Recovery,
                TimeSpan.FromMinutes(3), log, ct);
            return (true, "TWRP is running.");
        }
        catch (TimeoutException)
        {
            return (false, "TWRP did not come up. Flash succeeded; boot TWRP manually, then press Resume.");
        }
    }

    private static async Task<(bool, string)> RunBackupAsync(IProgress<string> log, CancellationToken ct)
    {
        var dev = AppServices.CurrentDevice;
        if (dev is null || dev.Mode != DeviceMode.Recovery)
        {
            return (false, "Boot the phone into TWRP first (the Install TWRP step).");
        }
        var twrp = AppServices.Twrp(dev.Serial);
        if (twrp is null)
        {
            return (false, "adb.exe not found.");
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
        var install = WindowsMedia.FindInstallImage(AppServices.MediaPath);
        if (install is null)
        {
            return (false, "The media does not contain sources\\install.wim or install.esd. For an .iso, extract or mount it first.");
        }
        var editions = await new WindowsMedia(AppServices.Runner).GetEditionsAsync(install, ct);
        var chosen = WindowsMedia.ChooseEdition(editions);
        AppServices.InstallImagePath = install;
        AppServices.EditionIndex = chosen.Index;
        return (true, $"Using {chosen.Name} (index {chosen.Index}) from {Path.GetFileName(install)}.");
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
            return (false, $"No built driver packages found under {AppServices.DriversDirectory}. "
                + "Build the UFS and touch drivers and copy their output there.");
        }
        var outDir = Path.Combine(AppServices.WorkDirectory, "out");
        var vhdx = Path.Combine(AppServices.WorkDirectory, "s9windows.vhdx");
        Directory.CreateDirectory(AppServices.WorkDirectory);
        if (File.Exists(vhdx))
        {
            File.Delete(vhdx);
        }
        var built = await new VhdxImageBuilder(AppServices.Runner).BuildAsync(vhdx, AppServices.WindowsVolumeMib + 300,
            AppServices.InstallImagePath!, AppServices.EditionIndex, drivers, AppServices.Profile,
            AppServices.Unattend, outDir, log, ct);
        AppServices.Built = built;
        return (true, $"Windows image built: {built.WindowsBytes >> 20} MiB volume + boot files, {drivers.Count} drivers, {AppServices.Profile} profile.");
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
        var twrp = RequireTwrp(out _, out var err);
        if (twrp is null)
        {
            return (false, err);
        }
        var parts = await twrp.ListPartitionsAsync(ct);
        var required = new[] { PartitionMap.WindowsTarget, PartitionMap.UefiTarget };
        var missing = required.Where(n => !parts.Keys.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0)
        {
            return (false, $"The phone did not expose the expected partition(s): {string.Join(", ", missing)}.");
        }
        var userdata = parts.Keys.First(k => k.Equals(PartitionMap.WindowsTarget, StringComparison.OrdinalIgnoreCase));
        var size = await twrp.PartitionSizeAsync(userdata, ct);
        return size < 16L << 30
            ? (false, $"{userdata} is only {size >> 30} GiB; Windows needs a larger target partition.")
            : (true, $"Partitions verified. {userdata} is {size >> 30} GiB.");
    }

    private static async Task<(bool, string)> RunTransferAsync(IProgress<string> log, CancellationToken ct)
    {
        var twrp = RequireTwrp(out _, out var err);
        if (twrp is null)
        {
            return (false, err);
        }
        if (!File.Exists(AppServices.WindowsImagePath))
        {
            return (false, $"No Windows image at {AppServices.WindowsImagePath}. Run the Build the Windows image step first.");
        }
        await new TransferService(twrp).WriteRawImageAsync(PartitionMap.WindowsTarget, AppServices.WindowsImagePath,
            windowMiB: 256, mountToEnsureUnmounted: "/data", log: log, ct: ct);

        if (Directory.Exists(AppServices.EspDirectory))
        {
            log.Report("Writing the Windows boot files...");
            await new BootFilesService(twrp).WriteAsync(AppServices.EspDirectory, log, ct);
        }
        return (true, "Windows and boot files written and verified on the phone.");
    }

    private static async Task<(bool, string)> RunUefiAsync(IProgress<string> log, CancellationToken ct)
    {
        var twrp = RequireTwrp(out _, out var err);
        if (twrp is null)
        {
            return (false, err);
        }
        if (AppServices.UefiImagePath is null)
        {
            return (false, $"Place the UEFI image at payload\\uefi.img next to the installer.");
        }
        await new TransferService(twrp).WriteWholePartitionAsync(PartitionMap.UefiTarget, AppServices.UefiImagePath, log, ct);
        return (true, "UEFI installed to BOOT. RECOVERY keeps TWRP.");
    }

    private static async Task<(bool, string)> RunFirstBootAsync(CancellationToken ct)
    {
        var dev = AppServices.CurrentDevice;
        if (dev is null || AppServices.Device is null)
        {
            return (true, "Unplug the phone and power it on to start Windows.");
        }
        await AppServices.Device.RebootAsync(dev.Serial, RebootTarget.System, ct);
        return (true, "Restarting into Windows. Follow the setup on the phone's screen.");
    }

    private void Report(InfoBarSeverity severity, string title, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnExperimentalToggled(object sender, RoutedEventArgs e) =>
        AppServices.ExperimentalEnabled = ExperimentalToggle.IsChecked == true;

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppServices.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", AppServices.LogDirectory) { UseShellExecute = true });
    }
}
