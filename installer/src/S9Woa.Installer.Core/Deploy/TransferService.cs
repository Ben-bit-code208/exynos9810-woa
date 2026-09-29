// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using S9Woa.Installer.Core.Image;

namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// Writes prepared images to the phone through TWRP. The raw Windows volume goes to USERDATA:
/// only the clusters NTFS uses (unless disabled), in chunks staged in TWRP's RAM (/tmp) rather
/// than on the SD card, with the next chunk crossing USB while the current one is written.
/// Each chunk can be read back and hashed against the source. A wrong-size destination and a
/// mounted target are refused before any write.
/// </summary>
public sealed class TransferService
{
    public const long Mib = 1024 * 1024;

    private readonly TwrpClient _twrp;

    public TransferService(TwrpClient twrp) => _twrp = twrp;

    private sealed record Staged(TransferChunk Chunk, string? Sha256, bool IsZero, string? DeviceFile);

    /// <summary>Writes a 1-MiB-aligned raw volume image to the start of <paramref name="partitionName"/>.</summary>
    public async Task WriteRawImageAsync(string partitionName, string hostImageFile, RawWriteOptions options,
        string? mountToEnsureUnmounted = null, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var size = new FileInfo(hostImageFile).Length;
        if (size <= 0 || size % Mib != 0)
        {
            throw new InvalidOperationException($"The image {Path.GetFileName(hostImageFile)} must be a nonzero multiple of 1 MiB.");
        }

        partitionName = await _twrp.ResolvePartitionNameAsync(partitionName, ct).ConfigureAwait(false);
        var partSize = await _twrp.PartitionSizeAsync(partitionName, ct).ConfigureAwait(false);
        if (size > partSize)
        {
            throw new InvalidOperationException(
                $"Refusing to write: the image ({size / Mib} MiB) is larger than partition {partitionName} ({partSize / Mib} MiB).");
        }
        if (mountToEnsureUnmounted is not null)
        {
            await EnsureUnmountedAsync(mountToEnsureUnmounted, partitionName, log, ct).ConfigureAwait(false);
        }

        var totalMiB = size / Mib;
        var chunks = TransferPlanner.Plan(totalMiB, options.ChunkMiB, NeededFilter(hostImageFile, options, log));
        var plannedMiB = chunks.Sum(c => c.CountMiB);
        log?.Report(plannedMiB < totalMiB
            ? $"Writing {plannedMiB} MiB of {totalMiB} MiB to {partitionName}: NTFS free space is skipped."
            : $"Writing {totalMiB} MiB to {partitionName}.");

        var deviceDir = _twrp.RamStagingDir;
        var hostDir = Path.Combine(Path.GetTempPath(), $"s9woa-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hostDir);
        await _twrp.MakeDirAsync(deviceDir, ct).ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        long doneMiB = 0, zeroMiB = 0, lastReportMiB = 0;
        var lastStatusMs = -10_000L;
        await _twrp.SetInstallStatusAsync(WinReStatus.Copying(0, 0, plannedMiB * Mib), ct).ConfigureAwait(false);
        Task<Staged>? pending = null;
        await using var image = new FileStream(hostImageFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous);
        try
        {
            if (chunks.Count > 0)
            {
                pending = StageAsync(image, chunks[0], 0, deviceDir, hostDir, options.Verify, ct);
            }
            for (var i = 0; i < chunks.Count; i++)
            {
                var staged = await pending!.ConfigureAwait(false);
                // The next chunk crosses USB while this one is written and checked on the phone.
                pending = i + 1 < chunks.Count ? StageAsync(image, chunks[i + 1], (i + 1) % 2, deviceDir, hostDir, options.Verify, ct) : null;

                var c = staged.Chunk;
                if (staged.IsZero)
                {
                    await _twrp.ZeroPartitionWindowAsync(partitionName, c.OffsetMiB, c.CountMiB, ct).ConfigureAwait(false);
                    zeroMiB += c.CountMiB;
                }
                else
                {
                    await _twrp.WritePartitionWindowAsync(staged.DeviceFile!, partitionName, c.OffsetMiB, c.CountMiB, ct).ConfigureAwait(false);
                }
                if (options.Verify)
                {
                    var actual = await _twrp.HashPartitionWindowAsync(partitionName, c.OffsetMiB, c.CountMiB, ct).ConfigureAwait(false);
                    if (!string.Equals(staged.Sha256, actual, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Verification failed writing {partitionName} at {c.OffsetMiB} MiB (wrote {staged.Sha256![..12]}…, read {actual[..12]}…).");
                    }
                }

                doneMiB += c.CountMiB;
                // Refresh the on-phone "Installing Windows" status at most every ~5 s so the
                // watcher can show a live phase caption without pushing a poke on every chunk.
                if (clock.ElapsedMilliseconds - lastStatusMs >= 5000)
                {
                    lastStatusMs = clock.ElapsedMilliseconds;
                    await _twrp.SetInstallStatusAsync(
                        WinReStatus.Copying(WinReStatus.PercentOf(doneMiB, plannedMiB), doneMiB * Mib, plannedMiB * Mib), ct)
                        .ConfigureAwait(false);
                }
                if (doneMiB - lastReportMiB >= 1024 || doneMiB == plannedMiB)
                {
                    lastReportMiB = doneMiB;
                    log?.Report("  " + Progress(partitionName, doneMiB, plannedMiB, zeroMiB, options.Verify, clock.Elapsed));
                }
            }
        }
        finally
        {
            if (pending is not null)
            {
                try { await pending.ConfigureAwait(false); } catch (Exception) when (ct.IsCancellationRequested || pending.IsFaulted) { }
            }
            try { await _twrp.ShellAsync($"rm -rf {deviceDir}", CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
            try { Directory.Delete(hostDir, recursive: true); } catch (IOException) { }
        }
    }

    internal static string Progress(string partition, long doneMiB, long plannedMiB, long zeroMiB, bool verified, TimeSpan elapsed)
    {
        var rate = elapsed.TotalSeconds > 0 ? doneMiB * Mib / elapsed.TotalSeconds / 1e6 : 0;
        var left = rate > 0 ? TimeSpan.FromSeconds((plannedMiB - doneMiB) * Mib / 1e6 / rate) : TimeSpan.Zero;
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{partition}: {doneMiB / 1024.0:0.0} of {plannedMiB / 1024.0:0.0} GiB written{(verified ? " and verified" : "")}, {rate:0} MB/s");
        if (doneMiB < plannedMiB)
        {
            text += left.TotalMinutes >= 1 ? $", about {Math.Ceiling(left.TotalMinutes):0} min left" : ", under a minute left";
        }
        return zeroMiB > 0 ? text + $" ({zeroMiB} MiB zero-filled on the phone)" : text;
    }

    private static Func<long, bool>? NeededFilter(string hostImageFile, RawWriteOptions options, IProgress<string>? log)
    {
        if (!options.SkipFreeSpace)
        {
            return null;
        }
        try
        {
            var allocation = NtfsAllocation.Read(hostImageFile);
            return mib => allocation.AnyAllocated(mib * Mib, Mib);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            log?.Report($"Writing the whole image: its NTFS allocation could not be read ({e.Message}).");
            return null;
        }
    }

    private async Task EnsureUnmountedAsync(string mountpoint, string partitionName, IProgress<string>? log, CancellationToken ct)
    {
        if (!await _twrp.IsMountedAsync(mountpoint, ct).ConfigureAwait(false))
        {
            return;
        }
        log?.Report($"Unmounting {mountpoint} before writing {partitionName}...");
        try
        {
            await _twrp.UnmountAsync(mountpoint, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Fall through to the recheck, which produces the actionable error.
        }
        if (await _twrp.IsMountedAsync(mountpoint, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"{mountpoint} is still mounted. In TWRP, open Mount and uncheck Data, then retry writing {partitionName}.");
        }
    }

    /// <summary>Copies one chunk to a host file (hashing it if needed) and pushes it unless it is all zero.</summary>
    private async Task<Staged> StageAsync(FileStream image, TransferChunk chunk, int slot, string deviceDir, string hostDir,
        bool hash, CancellationToken ct)
    {
        var hostFile = Path.Combine(hostDir, $"slot{slot}.bin");
        var (sha, isZero) = await CopyChunkAsync(image, chunk, hostFile, hash, ct).ConfigureAwait(false);
        if (isZero)
        {
            return new Staged(chunk, sha, true, null);
        }
        var deviceFile = $"{deviceDir}/slot{slot}.bin";
        await _twrp.PushAsync(hostFile, deviceFile, ct).ConfigureAwait(false);
        return new Staged(chunk, sha, false, deviceFile);
    }

    private static async Task<(string? Sha256, bool IsZero)> CopyChunkAsync(FileStream image, TransferChunk chunk, string hostFile,
        bool hash, CancellationToken ct)
    {
        using var sha = hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var isZero = true;
        image.Position = chunk.OffsetMiB * Mib;
        await using (var dst = new FileStream(hostFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
        {
            var buffer = new byte[4 << 20];
            var remaining = chunk.CountMiB * Mib;
            while (remaining > 0)
            {
                var read = await image.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("The image ended before the expected size.");
                }
                sha?.AppendData(buffer, 0, read);
                isZero = isZero && !buffer.AsSpan(0, read).ContainsAnyExcept((byte)0);
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
            }
        }
        return (sha is null ? null : Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), isZero);
    }

    /// <summary>Writes a whole small image (e.g. UEFI) to the start of a partition, verifying it unless told not to.</summary>
    public async Task WriteWholePartitionAsync(string partitionName, string hostImageFile,
        IProgress<string>? log = null, CancellationToken ct = default, bool verify = true)
    {
        var size = new FileInfo(hostImageFile).Length;
        if (size <= 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(hostImageFile)} is empty.");
        }
        partitionName = await _twrp.ResolvePartitionNameAsync(partitionName, ct).ConfigureAwait(false);
        var partSize = await _twrp.PartitionSizeAsync(partitionName, ct).ConfigureAwait(false);
        if (size > partSize)
        {
            throw new InvalidOperationException(
                $"Refusing to write: {Path.GetFileName(hostImageFile)} ({size} B) is larger than partition {partitionName} ({partSize} B).");
        }

        await _twrp.SetInstallStatusAsync(WinReStatus.Firmware(), ct).ConfigureAwait(false);
        await _twrp.MakeDirAsync(_twrp.RamStagingDir, ct).ConfigureAwait(false);
        var staged = $"{_twrp.RamStagingDir}/{partitionName}.img";
        await _twrp.PushAsync(hostImageFile, staged, ct).ConfigureAwait(false);
        await _twrp.DdAsync(staged, $"{TwrpClient.ByName}/{partitionName}", ct).ConfigureAwait(false);
        await _twrp.RemoveAsync(staged, ct).ConfigureAwait(false);
        if (!verify)
        {
            log?.Report($"  {partitionName}: {size} B written.");
            return;
        }

        var expected = await HashFileAsync(hostImageFile, ct).ConfigureAwait(false);
        var actual = await _twrp.Sha256Async($"{TwrpClient.ByName}/{partitionName}", size, ct).ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Verification failed writing {partitionName} ({expected[..12]}… vs {actual[..12]}…).");
        }
        log?.Report($"  {partitionName}: {size} B written and verified.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
    }
}