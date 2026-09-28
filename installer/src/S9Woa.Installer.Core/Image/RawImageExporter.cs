// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Security.Cryptography;

namespace S9Woa.Installer.Core.Image;

/// <summary>Byte offset and length of one partition on a disk.</summary>
public sealed record PartitionExtent(long Offset, long Length);

/// <summary>Opens a readable stream over a raw disk/partition source. Abstracted for testing.</summary>
public interface IRawDiskSource
{
    /// <summary>Total byte length available from <see cref="OpenRead"/>.</summary>
    long Length { get; }

    /// <summary>Opens a stream positioned at <paramref name="offset"/>.</summary>
    Stream OpenRead(long offset);
}

/// <summary>
/// Exports a partition's raw bytes to a file image, rounding the length up to a
/// whole MiB (padding with zeros) so the transfer stage can write it to the
/// phone in 1-MiB windows. Returns the SHA-256 of the exported image.
/// </summary>
public sealed class RawImageExporter
{
    private const long Mib = 1024 * 1024;
    private const int Buffer = 1 << 20;

    public async Task<(long Bytes, string Sha256)> ExportAsync(IRawDiskSource source, PartitionExtent extent,
        string destinationFile, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (extent.Offset < 0 || extent.Length <= 0 || extent.Offset + extent.Length > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(extent), "Partition extent is outside the disk.");
        }
        var padded = RoundUpToMib(extent.Length);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationFile))!);

        using var sha = SHA256.Create();
        await using (var input = source.OpenRead(extent.Offset))
        await using (var output = File.Create(destinationFile))
        {
            var buffer = new byte[Buffer];
            long copied = 0;
            while (copied < extent.Length)
            {
                ct.ThrowIfCancellationRequested();
                var want = (int)Math.Min(buffer.Length, extent.Length - copied);
                var read = await input.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("The disk source ended before the partition length.");
                }
                sha.TransformBlock(buffer, 0, read, null, 0);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;
                if (copied % (256 * Mib) < Buffer)
                {
                    log?.Report($"  exported {copied / Mib}/{padded / Mib} MiB");
                }
            }
            if (padded > extent.Length)
            {
                var zeros = new byte[Buffer];
                long pad = padded - extent.Length;
                while (pad > 0)
                {
                    var chunk = (int)Math.Min(zeros.Length, pad);
                    sha.TransformBlock(zeros, 0, chunk, null, 0);
                    await output.WriteAsync(zeros.AsMemory(0, chunk), ct).ConfigureAwait(false);
                    pad -= chunk;
                }
            }
        }
        sha.TransformFinalBlock([], 0, 0);
        return (padded, Convert.ToHexString(sha.Hash!).ToLowerInvariant());
    }

    internal static long RoundUpToMib(long bytes) => (bytes + Mib - 1) / Mib * Mib;
}
