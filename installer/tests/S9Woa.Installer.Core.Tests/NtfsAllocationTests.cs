// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using S9Woa.Installer.Core.Image;

namespace S9Woa.Installer.Core.Tests;

/// <summary>Builds a tiny, structurally real NTFS image: 4 KiB clusters and records, $Bitmap in MFT record 6.</summary>
internal static class NtfsTestImage
{
    public const int Cluster = 4096;
    private const int MftLcn = 16;
    private const int BitmapLcn = 32;

    /// <summary>
    /// <paramref name="sizeMiB"/> of random junk with NTFS metadata on top; clusters 0-39 (boot, MFT,
    /// bitmap) plus <paramref name="usedClusters"/> are marked in use.
    /// </summary>
    public static byte[] Build(int sizeMiB, IEnumerable<(int First, int Count)> usedClusters, int seed = 1, bool tornRecord = false)
    {
        var image = new byte[sizeMiB << 20];
        new Random(seed).NextBytes(image);
        var clusters = image.Length / Cluster;

        var boot = image.AsSpan(0, 512);
        boot.Clear();
        "NTFS    "u8.CopyTo(boot[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(boot[0x0B..], Cluster);
        boot[0x0D] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(boot[0x28..], clusters - 1); // last sector holds the backup boot sector
        BinaryPrimitives.WriteInt64LittleEndian(boot[0x30..], MftLcn);
        boot[0x40] = 1; // one cluster per MFT record

        var record = image.AsSpan((MftLcn + 6) * Cluster, Cluster);
        record.Clear();
        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], 0x30);   // update sequence array offset
        BinaryPrimitives.WriteUInt16LittleEndian(record[6..], 9);      // 1 + 4096/512
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x14..], 0x48);
        var attr = record[0x48..];
        BinaryPrimitives.WriteUInt32LittleEndian(attr, 0x80);
        BinaryPrimitives.WriteInt32LittleEndian(attr[4..], 0x48);
        attr[8] = 1; // non-resident
        BinaryPrimitives.WriteUInt16LittleEndian(attr[0x20..], 0x40);
        var bitmapBytes = (clusters + 7) / 8;
        BinaryPrimitives.WriteInt64LittleEndian(attr[0x28..], Cluster);
        BinaryPrimitives.WriteInt64LittleEndian(attr[0x30..], bitmapBytes);
        BinaryPrimitives.WriteInt64LittleEndian(attr[0x38..], bitmapBytes);
        attr[0x40] = 0x11; // 1-byte length, 1-byte offset
        attr[0x41] = 1;
        attr[0x42] = BitmapLcn;
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x90..], 0xFFFFFFFF);
        const ushort usn = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(record[0x30..], usn);
        for (var i = 1; i <= 8; i++)
        {
            // The array keeps each sector's real tail (zero here); the tail itself carries the USN.
            BinaryPrimitives.WriteUInt16LittleEndian(record[(i * 512 - 2)..], tornRecord && i == 5 ? (ushort)9 : usn);
        }

        var bitmap = image.AsSpan(BitmapLcn * Cluster, Cluster);
        bitmap.Clear();
        foreach (var (first, count) in usedClusters.Append((0, 40)))
        {
            for (var c = first; c < first + count; c++)
            {
                bitmap[c >> 3] |= (byte)(1 << (c & 7));
            }
        }
        return image;
    }
}

public class NtfsAllocationTests
{
    [Fact]
    public void ReadsTheClusterBitmap()
    {
        var image = NtfsTestImage.Build(8, [(1024, 6)]);
        var a = NtfsAllocation.Read(new MemoryStream(image));

        Assert.Equal(4096, a.ClusterBytes);
        Assert.Equal(2047, a.TotalClusters);
        Assert.Equal(46, a.AllocatedClusters);
        Assert.True(a.IsAllocated(0));
        Assert.True(a.IsAllocated(1029));
        Assert.False(a.IsAllocated(1030));
        Assert.True(a.AnyAllocated(0, 1 << 20));
        Assert.False(a.AnyAllocated(1 << 20, 3 << 20));
        Assert.True(a.AnyAllocated((4 << 20) + 5 * 4096, 1));
        Assert.False(a.AnyAllocated((4 << 20) + 6 * 4096, 1 << 20));
        // The backup boot sector sits past the cluster map and must always be written.
        Assert.True(a.AnyAllocated((8 << 20) - 4096, 4096));
    }

    [Fact]
    public void RefusesATornBitmapRecord() =>
        Assert.Throws<InvalidDataException>(() => NtfsAllocation.Read(new MemoryStream(NtfsTestImage.Build(8, [], tornRecord: true))));

    [Fact]
    public void RefusesSomethingThatIsNotNtfs()
    {
        var junk = new byte[1 << 20];
        new Random(4).NextBytes(junk);
        Assert.Throws<InvalidDataException>(() => NtfsAllocation.Read(new MemoryStream(junk)));
    }
}
