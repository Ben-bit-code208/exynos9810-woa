// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;

namespace S9Woa.Installer.Core.Image;

/// <summary>
/// Which clusters of a raw NTFS volume image are in use, read from the volume's own
/// <c>$Bitmap</c> (MFT record 6). Free clusters hold nothing NTFS will ever read, so a
/// writer can skip them, the way ntfsclone does.
/// </summary>
public sealed class NtfsAllocation
{
    private const int BitmapRecord = 6;
    private readonly byte[] _bitmap;

    private NtfsAllocation(int clusterBytes, long totalClusters, byte[] bitmap)
    {
        ClusterBytes = clusterBytes;
        TotalClusters = totalClusters;
        _bitmap = bitmap;
    }

    public int ClusterBytes { get; }

    public long TotalClusters { get; }

    /// <summary>Bytes covered by the cluster map; anything past it (the backup boot sector) counts as used.</summary>
    public long VolumeBytes => TotalClusters * ClusterBytes;

    public long AllocatedClusters
    {
        get
        {
            long n = 0;
            for (long c = 0; c < TotalClusters; c++)
            {
                if (IsAllocated(c))
                {
                    n++;
                }
            }
            return n;
        }
    }

    public bool IsAllocated(long cluster) =>
        cluster >= TotalClusters || (_bitmap[cluster >> 3] & (1 << (int)(cluster & 7))) != 0;

    /// <summary>True if any byte of <c>[offset, offset + length)</c> lies in a used cluster or past the cluster map.</summary>
    public bool AnyAllocated(long offset, long length)
    {
        if (length <= 0)
        {
            return false;
        }
        if (offset + length > VolumeBytes)
        {
            return true;
        }
        var first = offset / ClusterBytes;
        var last = (offset + length - 1) / ClusterBytes;
        var c = first;
        while (c <= last && (c & 7) != 0)
        {
            if (IsAllocated(c++))
            {
                return true;
            }
        }
        var wholeBytes = (last + 1 - c) >> 3;
        if (wholeBytes > 0)
        {
            if (_bitmap.AsSpan((int)(c >> 3), (int)wholeBytes).ContainsAnyExcept((byte)0))
            {
                return true;
            }
            c += wholeBytes << 3;
        }
        while (c <= last)
        {
            if (IsAllocated(c++))
            {
                return true;
            }
        }
        return false;
    }

    public static NtfsAllocation Read(string imagePath)
    {
        using var stream = File.OpenRead(imagePath);
        return Read(stream);
    }

    /// <exception cref="InvalidDataException">Not an NTFS volume, or its $Bitmap cannot be read safely.</exception>
    public static NtfsAllocation Read(Stream volume)
    {
        var boot = ReadAt(volume, 0, 512);
        if (!boot.AsSpan(3, 8).SequenceEqual("NTFS    "u8))
        {
            throw new InvalidDataException("Not an NTFS volume.");
        }
        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        int spcRaw = boot[0x0D];
        var sectorsPerCluster = spcRaw <= 0x80 ? spcRaw : 1 << (256 - spcRaw);
        var clusterBytes = bytesPerSector * sectorsPerCluster;
        var totalSectors = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x28));
        var mftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
        var cpr = (sbyte)boot[0x40];
        var recordBytes = cpr > 0 ? cpr * clusterBytes : 1 << -cpr;
        if (bytesPerSector is < 512 or > 4096 || clusterBytes is < 512 or > 2 * 1024 * 1024
            || recordBytes is < 1024 or > 65536 || totalSectors <= 0 || mftLcn <= 0)
        {
            throw new InvalidDataException("The NTFS boot sector has implausible geometry.");
        }
        var totalClusters = totalSectors * bytesPerSector / clusterBytes;

        // The first 16 MFT records are always in $MFT's first extent.
        var record = ReadAt(volume, mftLcn * clusterBytes + (long)BitmapRecord * recordBytes, recordBytes);
        ApplyFixups(record);
        var data = FindUnnamedData(record);

        var needed = (totalClusters + 7) / 8;
        var bitmap = data.Resident is { } resident
            ? resident
            : ReadRuns(volume, record, data.Offset, clusterBytes, data.RealSize);
        if (bitmap.LongLength < needed)
        {
            throw new InvalidDataException($"$Bitmap holds {bitmap.LongLength} bytes; the volume needs {needed}.");
        }

        var allocation = new NtfsAllocation(clusterBytes, totalClusters, bitmap);
        if (!allocation.IsAllocated(0) || !allocation.IsAllocated(mftLcn))
        {
            throw new InvalidDataException("$Bitmap does not mark the boot sector and $MFT as used.");
        }
        return allocation;
    }

    private static void ApplyFixups(byte[] record)
    {
        if (!record.AsSpan(0, 4).SequenceEqual("FILE"u8))
        {
            throw new InvalidDataException("MFT record 6 ($Bitmap) is not a FILE record.");
        }
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length || record.Length % (usaCount - 1) != 0)
        {
            throw new InvalidDataException("MFT record 6 has a malformed update sequence array.");
        }
        var stride = record.Length / (usaCount - 1);
        var usn = record.AsSpan(usaOffset, 2);
        for (var i = 1; i < usaCount; i++)
        {
            var tail = record.AsSpan(i * stride - 2, 2);
            if (!tail.SequenceEqual(usn))
            {
                throw new InvalidDataException("MFT record 6 failed its update sequence check (torn write).");
            }
            record.AsSpan(usaOffset + i * 2, 2).CopyTo(tail);
        }
    }

    private readonly record struct DataAttribute(int Offset, long RealSize, byte[]? Resident);

    private static DataAttribute FindUnnamedData(byte[] record)
    {
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));
        while (offset + 16 <= record.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset));
            if (type == 0xFFFFFFFF)
            {
                break;
            }
            var length = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(offset + 4));
            if (length <= 0 || offset + length > record.Length)
            {
                break;
            }
            if (type == 0x20)
            {
                throw new InvalidDataException("$Bitmap has an attribute list; not reading it.");
            }
            if (type == 0x80 && record[offset + 9] == 0)
            {
                if (record[offset + 8] == 0)
                {
                    var valueLength = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(offset + 0x10));
                    int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(offset + 0x14));
                    return new DataAttribute(offset, valueLength, record.AsSpan(offset + valueOffset, valueLength).ToArray());
                }
                if (BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(offset + 0x10)) != 0)
                {
                    throw new InvalidDataException("$Bitmap's data is split across records; not reading it.");
                }
                return new DataAttribute(offset, BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(offset + 0x30)), null);
            }
            offset += length;
        }
        throw new InvalidDataException("$Bitmap has no data attribute.");
    }

    private static byte[] ReadRuns(Stream volume, byte[] record, int attributeOffset, int clusterBytes, long realSize)
    {
        if (realSize is <= 0 or > 1L << 30)
        {
            throw new InvalidDataException($"$Bitmap reports {realSize} bytes.");
        }
        var result = new byte[realSize];
        var pos = attributeOffset + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attributeOffset + 0x20));
        var attributeEnd = attributeOffset + BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(attributeOffset + 4));
        long lcn = 0;
        long written = 0;
        while (pos < attributeEnd && record[pos] != 0 && written < realSize)
        {
            var header = record[pos++];
            int lengthSize = header & 0x0F, offsetSize = header >> 4;
            if (lengthSize is 0 or > 8 || offsetSize > 8 || pos + lengthSize + offsetSize > attributeEnd)
            {
                throw new InvalidDataException("$Bitmap has a malformed data run.");
            }
            var clusters = (long)ReadLittleEndian(record.AsSpan(pos, lengthSize), signed: false);
            pos += lengthSize;
            var take = (int)Math.Min(clusters * clusterBytes, realSize - written);
            if (offsetSize == 0)
            {
                written += take; // sparse: already zero
                continue;
            }
            lcn += ReadLittleEndian(record.AsSpan(pos, offsetSize), signed: true);
            pos += offsetSize;
            ReadAt(volume, lcn * clusterBytes, take).CopyTo(result, written);
            written += take;
        }
        if (written < realSize)
        {
            throw new InvalidDataException("$Bitmap's data runs end before its size.");
        }
        return result;
    }

    private static long ReadLittleEndian(ReadOnlySpan<byte> bytes, bool signed)
    {
        long value = 0;
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }
        if (signed && bytes.Length < 8 && (bytes[^1] & 0x80) != 0)
        {
            value -= 1L << (bytes.Length * 8);
        }
        return value;
    }

    private static byte[] ReadAt(Stream volume, long offset, int count)
    {
        if (offset < 0 || offset + count > volume.Length)
        {
            throw new InvalidDataException("NTFS metadata points outside the image.");
        }
        var buffer = new byte[count];
        volume.Position = offset;
        volume.ReadExactly(buffer);
        return buffer;
    }
}
