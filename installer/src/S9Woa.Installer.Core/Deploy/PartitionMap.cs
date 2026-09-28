// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// The validated Galaxy S9+ (star2lte) partition layout the installer relies on.
/// Names are the stable <c>/dev/block/by-name</c> links, independent of the
/// underlying sd* node numbering.
/// </summary>
public static class PartitionMap
{
    /// <summary>
    /// Identity- and calibration-critical partitions to back up before anything is
    /// written. <c>efs</c> holds the IMEI, Wi-Fi/Bluetooth MACs and serial on Exynos
    /// and is irreplaceable; the rest are backed up when present. Missing names are skipped.
    /// </summary>
    public static IReadOnlyList<string> IdentityBackup { get; } =
    [
        "efs", "efs2", "sbl1", "param", "steady", "keystorage", "up_param", "cm", "persist",
    ];

    /// <summary>A backup is only trustworthy if at least these partitions were captured.</summary>
    public static IReadOnlyList<string> RequiredBackup { get; } = ["efs"];

    /// <summary>Partition that holds the UEFI boot image. RECOVERY keeps TWRP.</summary>
    public const string UefiTarget = "boot";

    /// <summary>Partition Windows is written to on this device.</summary>
    public const string WindowsTarget = "userdata";

    /// <summary>Partition TWRP is flashed to from Download mode.</summary>
    public const string RecoveryTarget = "recovery";

    /// <summary>
    /// FAT EFI system partition that holds <c>\EFI\Microsoft\Boot\BCD</c> and the
    /// boot manager. This device exposes it by node rather than a stable by-name
    /// link; the value is the validated star2lte EFI system partition.
    /// </summary>
    public const string EfiSystemNode = "/dev/block/sda18";

    /// <summary>A second BCD copy the firmware also consults.</summary>
    public const string CacheBcdDir = "/cache/EFI/Microsoft/Boot";
}
