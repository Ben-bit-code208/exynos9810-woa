// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace S9Woa.Installer.Core.Host;

public interface IHostEnvironment
{
    bool IsAdministrator { get; }
    Version OsVersion { get; }
    Architecture OsArchitecture { get; }
    long FreeBytes(string path);

    /// <summary>Bytes of files under <paramref name="directory"/> (0 if it does not exist).</summary>
    long UsedBytes(string directory);
    string? AdbPath { get; }
    bool ServiceExists(string name);
}

public sealed class LocalHostEnvironment(string? adbPath) : IHostEnvironment
{
    public bool IsAdministrator
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public Version OsVersion => Environment.OSVersion.Version;
    public Architecture OsArchitecture => RuntimeInformation.OSArchitecture;
    public long FreeBytes(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;

    public long UsedBytes(string directory) => Directory.Exists(directory)
        ? new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Sum(f => f.Length)
        : 0;

    public string? AdbPath => adbPath;

    public bool ServiceExists(string name) =>
        Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}") is { } k && Dispose(k);

    private static bool Dispose(IDisposable d)
    {
        d.Dispose();
        return true;
    }
}

public static class HostPreflight
{
    public const long RequiredFreeBytes = 80L * 1024 * 1024 * 1024;
    public const int MinimumBuild = 19041;
    public const string SamsungUsbService = "dg_ssudbus";

    /// <param name="workDirectory">
    /// Where the image is built. Files already in it (a previous build that is reused or replaced)
    /// count toward the space requirement.
    /// </param>
    public static IReadOnlyList<CheckResult> Evaluate(IHostEnvironment host, string workDirectory)
    {
        var r = new List<CheckResult>
        {
            host.IsAdministrator
                ? new("admin", "Administrator", CheckSeverity.Pass, "Running elevated.")
                : new("admin", "Administrator", CheckSeverity.Blocker, "DISM and disk tools need an elevated installer. Restart it as administrator."),
            host.OsVersion.Build >= MinimumBuild
                ? new("os", "Windows version", CheckSeverity.Pass, $"Build {host.OsVersion.Build}.")
                : new("os", "Windows version", CheckSeverity.Blocker, $"Windows 10 2004 (build {MinimumBuild}) or newer is required."),
            host.OsArchitecture is Architecture.X64 or Architecture.Arm64
                ? new("arch", "PC architecture", CheckSeverity.Pass, host.OsArchitecture.ToString())
                : new("arch", "PC architecture", CheckSeverity.Blocker, "A 64-bit PC is required."),
        };

        long free;
        long reusable = 0;
        try
        {
            free = host.FreeBytes(workDirectory);
            reusable = host.UsedBytes(workDirectory);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            free = -1;
        }
        r.Add(free >= 0 && free + reusable >= RequiredFreeBytes
            ? new("disk", "Free space", CheckSeverity.Pass, reusable >= 1L << 30
                ? $"{free / (1L << 30)} GB free at {workDirectory}, plus {reusable / (1L << 30)} GB used by the previous image build."
                : $"{free / (1L << 30)} GB free at {workDirectory}.")
            : new("disk", "Free space", CheckSeverity.Blocker,
                $"{RequiredFreeBytes >> 30} GB free is needed at {workDirectory} for the image and phone backups."));

        r.Add(host.AdbPath is not null
            ? new("adb", "Android platform tools", CheckSeverity.Pass, host.AdbPath)
            : new("adb", "Android platform tools", CheckSeverity.Blocker,
                "adb.exe was not found. Install it with: winget install Google.PlatformTools"));

        r.Add(host.ServiceExists(SamsungUsbService)
            ? new("usbdrv", "Samsung USB driver", CheckSeverity.Pass, "Installed.")
            : new("usbdrv", "Samsung USB driver", CheckSeverity.Warning,
                "Samsung USB Driver for Mobile Phones is not installed. It is needed for Download mode (flashing TWRP)."));
        return r;
    }
}
