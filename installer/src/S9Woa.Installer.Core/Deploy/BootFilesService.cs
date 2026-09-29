// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// Puts the Windows boot files on the phone's EFI system partition. On a stock phone
/// that partition (<see cref="PartitionMap.EfiSystemPartition"/>) holds Android's ext4
/// system image, so it is first reformatted as FAT32 with 4096-byte sectors (the UFS
/// block size) — the UEFI mounts any FAT partition and boots bootmgfw.efi from it.
/// The ESP tree itself is produced on the PC by the image build (bcdboot output with
/// the BCD retargeted to locate <c>\Windows</c>).
/// </summary>
public sealed class BootFilesService
{
    private const string Mount = "/s9woa_esp";
    private readonly TwrpClient _twrp;

    public BootFilesService(TwrpClient twrp) => _twrp = twrp;

    /// <summary>Formatter command for FAT32 with 4 KiB sectors, by tool family.</summary>
    internal static string FormatCommand(string tool, string device) =>
        Path.GetFileName(tool) == "newfs_msdos"
            ? $"{tool} -F 32 -S 4096 -L SYSTEM {device}"
            : $"{tool} -F 32 -S 4096 -n SYSTEM {device}";

    public async Task WriteAsync(string espDirectory, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var efi = Path.Combine(espDirectory, "EFI");
        if (!Directory.Exists(efi))
        {
            throw new DirectoryNotFoundException($"No EFI\\ folder under {espDirectory}. Build the Windows image first.");
        }

        var name = await _twrp.ResolvePartitionNameAsync(PartitionMap.EfiSystemPartition, ct).ConfigureAwait(false);
        var device = $"{TwrpClient.ByName}/{name}";
        foreach (var mp in new[] { "/system_root", "/system", Mount })
        {
            if (await _twrp.IsMountedAsync(mp, ct).ConfigureAwait(false))
            {
                await _twrp.UnmountAsync(mp, ct).ConfigureAwait(false);
            }
        }

        if (!await _twrp.IsFat32Async(name, ct).ConfigureAwait(false))
        {
            var tool = await _twrp.FindFatFormatterAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("TWRP has no FAT formatter (mkfs.fat/newfs_msdos); cannot create the EFI system partition.");
            log?.Report($"Formatting {name} as the FAT32 EFI system partition (replaces Android's system partition)...");
            await _twrp.ShellAsync(FormatCommand(tool, device), ct).ConfigureAwait(false);
            if (!await _twrp.IsFat32Async(name, ct).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"{name} did not come out as FAT32 after formatting.");
            }
        }

        log?.Report("Mounting the EFI system partition...");
        await _twrp.MountVfatAsync(device, Mount, ct).ConfigureAwait(false);
        try
        {
            log?.Report("Copying boot files to the EFI system partition...");
            await _twrp.PushTreeAsync(efi, $"{Mount}/", ct).ConfigureAwait(false);
            await _twrp.ShellAsync($"test -f {Mount}/EFI/Microsoft/Boot/bootmgfw.efi && test -f {Mount}/EFI/Microsoft/Boot/BCD && sync", ct)
                .ConfigureAwait(false);
        }
        finally
        {
            await _twrp.UnmountAsync(Mount, ct).ConfigureAwait(false);
        }
        log?.Report("Boot files written.");
    }
}
