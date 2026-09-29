// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// The validated Galaxy S9+ (star2lte) partition layout the installer relies on.
/// Names are the stable <c>/dev/block/by-name</c> links (upper case on this phone;
/// lookups are case-insensitive), independent of the sd* node numbering.
/// </summary>
public static class PartitionMap
{
    /// <summary>
    /// Identity- and calibration-critical partitions to back up before anything is
    /// written. <c>EFS</c> holds the IMEI, Wi-Fi/Bluetooth MACs and serial on Exynos
    /// and is irreplaceable; the rest are backed up when present. Missing names are skipped.
    /// </summary>
    public static IReadOnlyList<string> IdentityBackup { get; } =
    [
        "EFS", "CPEFS", "PARAM", "UP_PARAM", "STEADY", "KEYSTORAGE", "PERSISTENT",
    ];

    /// <summary>A backup is only trustworthy if at least these partitions were captured.</summary>
    public static IReadOnlyList<string> RequiredBackup { get; } = ["EFS"];

    /// <summary>Partition that holds the UEFI boot image. RECOVERY keeps TWRP.</summary>
    public const string UefiTarget = "BOOT";

    /// <summary>Partition Windows is written to on this device.</summary>
    public const string WindowsTarget = "USERDATA";

    /// <summary>Partition TWRP is flashed to from Download mode.</summary>
    public const string RecoveryTarget = "RECOVERY";

    /// <summary>
    /// Partition reformatted as the FAT32 EFI system partition (4096-byte sectors, matching
    /// the UFS block size). It is Android's system partition on a stock phone; the UEFI
    /// mounts any FAT partition and boots \EFI\Microsoft\Boot\bootmgfw.efi from it.
    /// </summary>
    public const string EfiSystemPartition = "SYSTEM";

    /// <summary>
    /// Byte geometry of the phone's main UFS unit and of USERDATA on it. The Windows image is
    /// built on a virtual disk with exactly this layout so the NTFS volume matches USERDATA.
    /// </summary>
    public const long DiskBytes = 63_963_136_000;
    public const long WindowsOffset = 6_951_534_592;
    public const long WindowsBytes = 57_004_785_664;
}
