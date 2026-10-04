// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Text;
using S9Woa.Installer.Core.Device;

namespace S9Woa.Installer.Core.Toolset;

/// <summary>Checks that a file is an Android boot image that fits a phone partition.</summary>
public static class BootImage
{
    private static readonly byte[] Magic = "ANDROID!"u8.ToArray();

    /// <summary>Returns null when valid, otherwise a user-facing reason.</summary>
    public static string? Validate(string file, long maxBytes)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
        {
            return "The file does not exist.";
        }
        if (info.Length < 4096)
        {
            return "The file is too small to be a boot image.";
        }
        if (info.Length > maxBytes)
        {
            return $"The image ({info.Length / (1024 * 1024)} MiB) does not fit the {maxBytes / (1024 * 1024)} MiB partition.";
        }
        Span<byte> head = stackalloc byte[8];
        using (var stream = File.OpenRead(file))
        {
            if (stream.Read(head) != head.Length || !head.SequenceEqual(Magic))
            {
                return "This is not an Android boot image (missing the ANDROID! header).";
            }
        }
        return null;
    }

    /// <summary>
    /// TWRP must be the build for the phone's own board, because the recovery carries that board's
    /// kernel and device tree; official file names end in <c>-&lt;codename&gt;.img</c>. The limits
    /// and the expected name come from the profile, so a port needs no change here.
    /// </summary>
    public static string? ValidateTwrp(string file, DeviceProfile profile)
    {
        var name = Path.GetFileName(file);
        var suffix = profile.TwrpFileSuffix;
        if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            var others = DeviceCatalog.All
                .Where(p => p.Codename != profile.Codename)
                .Select(p => $"{p.Codename} ({p.MarketingName})")
                .ToList();
            return $"This does not look like TWRP for {profile.MarketingName}. Official file names end in "
                + $"{suffix}. The catalog's other boards are: {string.Join("; ", others)}.";
        }
        return Validate(file, profile.RecoveryPartitionBytes);
    }

    /// <summary>The UEFI image has to fit the phone's BOOT partition.</summary>
    public static string? ValidateUefi(string file, DeviceProfile profile) => Validate(file, profile.BootPartitionBytes);

    internal static string Describe(string file) =>
        new StringBuilder(Path.GetFileName(file)).Append(" (").Append(new FileInfo(file).Length / 1024).Append(" KiB)").ToString();
}

/// <summary>
/// Detects whether the Samsung Download-mode USB interface (VID 04E8, PID 685D)
/// is bound to WinUSB/libusbK, which Heimdall needs. The device only appears
/// after the phone has been connected in Download mode at least once.
/// </summary>
public static class DownloadModeDriver
{
    public const string HardwarePrefix = "VID_04E8&PID_685D";
    private const string EnumUsb = @"SYSTEM\CurrentControlSet\Enum\USB";
    private static readonly string[] UsableServices = ["WinUSB", "libusbK", "libusb0"];

    public static ToolStatus Detect(IRegistryReader registry)
    {
        var seen = false;
        foreach (var device in registry.SubKeyNames(EnumUsb)
                     .Where(n => n.StartsWith(HardwarePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var instance in registry.SubKeyNames($@"{EnumUsb}\{device}"))
            {
                seen = true;
                var service = registry.GetString($@"{EnumUsb}\{device}\{instance}", "Service");
                if (service is not null && UsableServices.Contains(service, StringComparer.OrdinalIgnoreCase))
                {
                    return new ToolStatus(ToolState.Ready, $"Download mode uses {service}.");
                }
            }
        }
        return new ToolStatus(ToolState.Deferred, seen
            ? "The phone's Download-mode interface still uses the Samsung driver. Put the phone in Download mode, open Zadig, "
              + "choose Options > List All Devices, select the Samsung device, pick WinUSB and click Replace Driver."
            : "Done during Install TWRP: with the phone in Download mode, open Zadig and replace its driver with WinUSB.");
    }
}
