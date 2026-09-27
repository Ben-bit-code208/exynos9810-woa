// SPDX-License-Identifier: BSD-2-Clause-Patent
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
    public static InstallState State { get; } = InstallState.Load(DataDirectory);

    public static string? AdbPath { get; } = AdbClient.Locate(AppContext.BaseDirectory);
    public static AdbClient? Adb { get; } = AdbPath is null ? null : new AdbClient(AdbPath, Runner);
    public static DeviceActions? Device { get; } = Adb is null ? null : new DeviceActions(Adb, Runner);

    public static DeviceSnapshot? CurrentDevice { get; set; }
    public static bool RisksAccepted { get; set; }

    /// <summary>In-Windows (on the phone) Restart-to-TWRP over the UFS vendor ticket.</summary>
    public static RecoveryTicketService RecoveryTickets { get; } =
        new(new ScsiPassThroughTransportFactory(), new ShutdownExeRestart());

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
