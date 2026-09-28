// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Deploy;
using S9Woa.Installer.Core.Device;
using S9Woa.Installer.Core.Image;
using S9Woa.Installer.Core.Processes;
using S9Woa.Installer.Core.Recovery;
using S9Woa.Installer.Core.Stages;

namespace S9Woa.Installer.App;

/// <summary>Process-wide services and wizard selections shared by the pages.</summary>
internal static class AppServices
{
    private static readonly object LogLock = new();

    public static IProcessRunner Runner { get; } = new ProcessRunner();
    public static string DataDirectory { get; } = InstallState.DefaultDirectory;
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");
    public static string LogFile { get; } = Path.Combine(LogDirectory, $"installer-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    public static string WorkDirectory { get; set; } = Path.Combine(DataDirectory, "work");
    public static string BackupDirectory { get; set; } = Path.Combine(DataDirectory, "backups");
    public static InstallState State { get; } = InstallState.Load(DataDirectory);

    /// <summary>Optional build artefacts placed next to the app: drivers/, twrp.img, uefi.img.</summary>
    public static string PayloadDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "payload");
    public static string DriversDirectory { get; set; } = Path.Combine(PayloadDirectory, "drivers");
    public static string? TwrpImagePath => FirstExisting(Path.Combine(PayloadDirectory, "twrp.img"), Path.Combine(PayloadDirectory, "recovery.img"));
    public static string? UefiImagePath => FirstExisting(Path.Combine(PayloadDirectory, "uefi.img"), Path.Combine(PayloadDirectory, "boot.img"));

    /// <summary>Raw Windows volume image (produced by the VHDX build + export) written to USERDATA.</summary>
    public static string WindowsImagePath => Path.Combine(WorkDirectory, "out", "windows.img");

    /// <summary>ESP boot files (produced by the VHDX build) copied to the phone's EFI partition.</summary>
    public static string EspDirectory => Path.Combine(WorkDirectory, "out", "esp");

    /// <summary>NTFS Windows volume size inside the built VHDX, in MiB (fits the phone's USERDATA).</summary>
    public static long WindowsVolumeMib { get; } = 52000;

    /// <summary>OOBE answer-file choices for the built image.</summary>
    public static Core.Image.UnattendOptions Unattend { get; set; } = new();

    /// <summary>Last successful image build, if any.</summary>
    public static Core.Image.BuiltImage? Built { get; set; }

    public static string? AdbPath { get; } = AdbClient.Locate(AppContext.BaseDirectory);
    public static AdbClient? Adb { get; } = AdbPath is null ? null : new AdbClient(AdbPath, Runner);
    public static DeviceActions? Device { get; } = Adb is null ? null : new DeviceActions(Adb, Runner);

    public static string? HeimdallPath { get; } = HeimdallTwrpFlasher.Locate(AppContext.BaseDirectory);

    /// <summary>TWRP flasher preference order: native (when ported and validated) then Heimdall.</summary>
    public static TwrpFlashService TwrpFlasher { get; } = new(
        HeimdallPath is null ? [] : [new HeimdallTwrpFlasher(HeimdallPath, Runner)]);

    public static TwrpClient? Twrp(string serial) => AdbPath is null ? null : new TwrpClient(AdbPath, serial, Runner);

    public static DeviceSnapshot? CurrentDevice { get; set; }
    public static bool RisksAccepted { get; set; }

    /// <summary>Opt-in to run stages that are automated but not yet validated on the reference device.</summary>
    public static bool ExperimentalEnabled { get; set; }

    /// <summary>In-Windows (on the phone) Restart-to-TWRP over the UFS vendor ticket.</summary>
    public static RecoveryTicketService RecoveryTickets { get; } =
        new(new ScsiPassThroughTransportFactory(), new ShutdownExeRestart());

    /// <summary>Chosen Windows edition index inside the media, resolved by the media stage.</summary>
    public static string? InstallImagePath { get; set; }
    public static int EditionIndex { get; set; } = 1;

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    public static string? MediaPath { get; set; } = State.MediaPath;
    public static SlimProfile Profile { get; set; } =
        Enum.TryParse<SlimProfile>(State.SlimProfile, out var p) ? p : SlimProfile.Lite;

    public static event Action<string>? LogWritten;

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (LogLock)
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogFile, line + Environment.NewLine);
        }
        LogWritten?.Invoke(line);
    }

    public static void SaveState()
    {
        State.MediaPath = MediaPath;
        State.SlimProfile = Profile.ToString();
        State.DeviceSerial = CurrentDevice?.Serial ?? State.DeviceSerial;
        State.DeviceBootloader = CurrentDevice?.Bootloader ?? State.DeviceBootloader;
        State.Save(DataDirectory);
    }
}
