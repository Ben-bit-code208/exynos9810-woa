// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Security.Cryptography;

namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// Writes prepared images to the phone through TWRP, verifying every window it
/// writes. The raw Windows volume goes to USERDATA in aligned windows; the UEFI
/// image goes to BOOT. A wrong-size destination is refused before any write, and
/// each window is read back and hashed against the source so a bad write never
/// passes silently.
/// </summary>
public sealed class TransferService
{
    public const long Mib = 1024 * 1024;

    private readonly TwrpClient _twrp;

    public TransferService(TwrpClient twrp) => _twrp = twrp;

    /// <summary>
    /// Writes <paramref name="hostImageFile"/> (1-MiB aligned) to <paramref name="partitionName"/>
    /// in windows of <paramref name="windowMiB"/> MiB, verifying each window.
    /// </summary>
    public async Task WriteRawImageAsync(string partitionName, string hostImageFile, long windowMiB,
        string? mountToEnsureUnmounted = null, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var size = new FileInfo(hostImageFile).Length;
        if (size <= 0 || size % Mib != 0)
        {
            throw new InvalidOperationException($"The image {Path.GetFileName(hostImageFile)} must be a nonzero multiple of 1 MiB.");
        }
        if (windowMiB <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowMiB));
        }

        var partSize = await _twrp.PartitionSizeAsync(partitionName, ct).ConfigureAwait(false);
        if (size > partSize)
        {
            throw new InvalidOperationException(
                $"Refusing to write: the image ({size / Mib} MiB) is larger than partition {partitionName} ({partSize / Mib} MiB).");
        }
        if (mountToEnsureUnmounted is not null && await _twrp.IsMountedAsync(mountToEnsureUnmounted, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"{mountToEnsureUnmounted} is mounted. Unmount it in TWRP before writing {partitionName}.");
        }

        await _twrp.MakeStagingDirAsync(ct).ConfigureAwait(false);
        var window = $"{_twrp.SdStagingDir}/window.bin";
        var totalMib = size / Mib;
        var tmp = Path.Combine(Path.GetTempPath(), $"s9woa-window-{Guid.NewGuid():N}.bin");

        await using (var stream = File.OpenRead(hostImageFile))
        {
            for (long offsetMib = 0; offsetMib < totalMib; offsetMib += windowMiB)
            {
                ct.ThrowIfCancellationRequested();
                var countMib = Math.Min(windowMiB, totalMib - offsetMib);
                var expected = await StageWindowAsync(stream, tmp, countMib * Mib, ct).ConfigureAwait(false);

                await _twrp.PushAsync(tmp, window, ct).ConfigureAwait(false);
                await _twrp.WritePartitionWindowAsync(window, partitionName, offsetMib, countMib, ct).ConfigureAwait(false);
                var actual = await _twrp.HashPartitionWindowAsync(partitionName, offsetMib, countMib, ct).ConfigureAwait(false);
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Verification failed writing {partitionName} at {offsetMib} MiB (wrote {expected[..12]}…, read {actual[..12]}…).");
                }
                log?.Report($"  {partitionName}: {offsetMib + countMib}/{totalMib} MiB verified");
            }
        }
        await _twrp.RemoveAsync(window, ct).ConfigureAwait(false);
        try { File.Delete(tmp); } catch (IOException) { }
    }

    /// <summary>Writes a whole small image (e.g. UEFI) to the start of a partition and verifies it.</summary>
    public async Task WriteWholePartitionAsync(string partitionName, string hostImageFile,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var size = new FileInfo(hostImageFile).Length;
        if (size <= 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(hostImageFile)} is empty.");
        }
        var partSize = await _twrp.PartitionSizeAsync(partitionName, ct).ConfigureAwait(false);
        if (size > partSize)
        {
            throw new InvalidOperationException(
                $"Refusing to write: {Path.GetFileName(hostImageFile)} ({size} B) is larger than partition {partitionName} ({partSize} B).");
        }

        await _twrp.MakeStagingDirAsync(ct).ConfigureAwait(false);
        var staged = $"{_twrp.SdStagingDir}/{partitionName}.img";
        await _twrp.PushAsync(hostImageFile, staged, ct).ConfigureAwait(false);
        await _twrp.DdAsync(staged, $"{TwrpClient.ByName}/{partitionName}", ct).ConfigureAwait(false);

        var expected = await HashFileAsync(hostImageFile, ct).ConfigureAwait(false);
        var actual = await _twrp.Sha256Async($"{TwrpClient.ByName}/{partitionName}", size, ct).ConfigureAwait(false);
        await _twrp.RemoveAsync(staged, ct).ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Verification failed writing {partitionName} ({expected[..12]}… vs {actual[..12]}…).");
        }
        log?.Report($"  {partitionName}: {size} B written and verified.");
    }

    private static async Task<string> StageWindowAsync(Stream source, string tmpFile, long length, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using (var dst = File.Create(tmpFile))
        {
            var buffer = new byte[1 << 20];
            long remaining = length;
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("The image ended before the expected size.");
                }
                sha.TransformBlock(buffer, 0, read, null, 0);
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
            }
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}
