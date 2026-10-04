// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Device;

namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// The partition names the installer relies on. They are the stable
/// <c>/dev/block/by-name</c> links (upper case on these phones; lookups are case-insensitive), which
/// Samsung keeps across models of one family, unlike the sd* node numbering and unlike every
/// partition's size and offset - those are read off the phone (see <see cref="PartitionLayout"/>)
/// because USERDATA grows and moves with the storage option.
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
    /// The EFI system partition the UEFI boots from: Android's CACHE (sda21, 600 MiB on the
    /// reference phone). It is reformatted as FAT32 with 4096-byte sectors (the UFS block size) and
    /// holds \EFI\Microsoft\Boot\bootmgfw.efi and the BCD, whose boot-manager device points here.
    /// </summary>
    public const string EfiSystemPartition = "CACHE";

    /// <summary>
    /// Second FAT32 copy of the boot files on Android's SYSTEM partition (sda18). The reference
    /// deployment kept both; firmware builds that only connect the SYSTEM-sized FAT volume use it.
    /// On the reference phone its GPT type is the EFI system partition GUID (a stock flash resets it
    /// to basic data); <see cref="GptTypeService"/> puts that back.
    /// </summary>
    public const string SecondaryEfiSystemPartition = "SYSTEM";

    /// <summary>
    /// Holds the Android bootloader control block. <c>boot-recovery</c> there makes the bootloader
    /// start RECOVERY; the UEFI writes it too when it gives up on Windows.
    /// </summary>
    public const string Misc = "MISC";

    /// <summary>
    /// Every partition a phone must have before the installer writes anything, and which of them
    /// must be exactly the right size. Sizes are compared against the phone's own layout, not against
    /// a fixed table.
    /// </summary>
    public static IReadOnlyList<string> RequiredPartitions { get; } =
        [UefiTarget, WindowsTarget, EfiSystemPartition, RecoveryTarget, Misc];

    /// <summary>
    /// Samsung derives GPT identifiers from partition names ("ANDROID MMC DISK", "ANDROID USERDATA",
    /// ...), so the BCD addresses Windows by the same values on every Exynos 9810 board. They follow
    /// the names, not the model, and are read back from the phone before they are trusted.
    /// </summary>
    public static string DiskGuid { get; } = "{52444e41-494f-2044-4d4d-43204449534b}";
    public static string UserdataGuid { get; } = "{52444e41-494f-2044-5553-455244415441}";
    public static string CacheGuid { get; } = "{52444e41-494f-2044-4341-434845000000}";
    public static string SystemGuid { get; } = "{52444e41-494f-2044-5359-5354454d0000}";

    /// <summary>
    /// The validated Galaxy S9+ (SM-G965F, 128 GB) byte geometry. Kept as the fallback for phones
    /// that cannot be probed (and the expectation the measured layout is checked against), but the
    /// image is built from the phone's own layout whenever that can be read.
    /// </summary>
    public static ReferenceGeometry ReferenceLayout => ReferenceGeometry.GalaxyS9Plus128Gb;
}