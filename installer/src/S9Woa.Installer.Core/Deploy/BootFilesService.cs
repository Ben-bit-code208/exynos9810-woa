// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// Writes the Windows boot files onto the phone's EFI system partition (and a
/// second copy under /cache that the firmware also consults). The ESP tree is
/// produced on the PC by the image build (bcdboot output with the BCD retargeted
/// to locate <c>\Windows</c>), so here it is only mounted and copied.
/// </summary>
public sealed class BootFilesService
{
    private const string Mount = "/s9woa_esp";
    private readonly TwrpClient _twrp;

    public BootFilesService(TwrpClient twrp) => _twrp = twrp;

    public async Task WriteAsync(string espDirectory, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var efi = Path.Combine(espDirectory, "EFI");
        if (!Directory.Exists(efi))
        {
            throw new DirectoryNotFoundException($"No EFI\\ folder under {espDirectory}. Build the Windows image first.");
        }

        log?.Report("Mounting the EFI system partition...");
        await _twrp.MountVfatAsync(PartitionMap.EfiSystemNode, Mount, ct).ConfigureAwait(false);
        try
        {
            await _twrp.MakeDirAsync($"{Mount}/EFI", ct).ConfigureAwait(false);
            log?.Report("Copying boot files to the EFI system partition...");
            await _twrp.PushTreeAsync(efi, $"{Mount}/", ct).ConfigureAwait(false);
        }
        finally
        {
            await _twrp.UnmountAsync(Mount, ct).ConfigureAwait(false);
        }

        var bcd = Path.Combine(espDirectory, @"EFI\Microsoft\Boot\BCD");
        if (File.Exists(bcd))
        {
            log?.Report("Writing the /cache BCD copy...");
            await _twrp.MakeDirAsync(PartitionMap.CacheBcdDir, ct).ConfigureAwait(false);
            await _twrp.PushAsync(bcd, $"{PartitionMap.CacheBcdDir}/BCD", ct).ConfigureAwait(false);
        }
        log?.Report("Boot files written.");
    }
}
