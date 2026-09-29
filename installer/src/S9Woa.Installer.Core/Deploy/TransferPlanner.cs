// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Deploy;

/// <summary>How <see cref="TransferService.WriteRawImageAsync"/> writes a raw volume image.</summary>
public sealed record RawWriteOptions
{
    /// <summary>Largest piece pushed over USB and written with one dd.</summary>
    public long ChunkMiB { get; init; } = 128;

    /// <summary>Read every written chunk back on the phone and compare SHA-256 with the source.</summary>
    public bool Verify { get; init; } = true;

    /// <summary>Write only what the NTFS allocation bitmap marks as used (falls back to everything).</summary>
    public bool SkipFreeSpace { get; init; } = true;
}

/// <summary>A 1-MiB-aligned piece of the image: <c>[OffsetMiB, OffsetMiB + CountMiB)</c>.</summary>
public readonly record struct TransferChunk(long OffsetMiB, long CountMiB);

public static class TransferPlanner
{
    /// <summary>
    /// Groups the MiBs that must be written into runs of at most <paramref name="chunkMiB"/>.
    /// With no <paramref name="needed"/> filter every MiB is written. The first and last MiB are
    /// always written (boot sector and backup boot sector).
    /// </summary>
    public static IReadOnlyList<TransferChunk> Plan(long totalMiB, long chunkMiB, Func<long, bool>? needed)
    {
        if (totalMiB <= 0 || chunkMiB <= 0)
        {
            throw new ArgumentOutOfRangeException(totalMiB <= 0 ? nameof(totalMiB) : nameof(chunkMiB));
        }
        var chunks = new List<TransferChunk>();
        long runStart = -1;
        for (long mib = 0; mib <= totalMiB; mib++)
        {
            var need = mib < totalMiB && (needed is null || mib == 0 || mib == totalMiB - 1 || needed(mib));
            if (need && runStart < 0)
            {
                runStart = mib;
            }
            else if (!need && runStart >= 0)
            {
                for (var s = runStart; s < mib; s += chunkMiB)
                {
                    chunks.Add(new TransferChunk(s, Math.Min(chunkMiB, mib - s)));
                }
                runStart = -1;
            }
        }
        return chunks;
    }
}
