// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Security.Cryptography;
using S9Woa.Installer.Core.Device;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>
/// A Samsung-style Android boot image (header v0 with a trailing DTBH device-tree
/// blob), parsed and re-serialised entirely in memory. The kernel, ramdisk,
/// second stage and device-tree are preserved byte-for-byte unless deliberately
/// replaced, which is what lets the builder assert that only the ramdisk (and the
/// four-byte power-off patch) changed. This mirrors the reference Python
/// <c>bootimg.py</c>; the tests check both round-trip the same official image.
/// </summary>
public sealed class AndroidBootImage
{
    private static readonly byte[] Magic = "ANDROID!"u8.ToArray();

    /// <summary>
    /// RECOVERY on the validated board; the repacked image must fit it. A port's real size comes
    /// from its profile (<see cref="DeviceProfile.RecoveryPartitionBytes"/>), so this is only the
    /// default the builder checks against.
    /// </summary>
    public static long RecoveryPartitionBytes => DeviceCatalog.GalaxyS9Plus.RecoveryPartitionBytes;

    private readonly byte[] _header;

    private AndroidBootImage(byte[] header, byte[] kernel, byte[] ramdisk, byte[] second, byte[] dt, byte[] tail)
    {
        _header = header;
        Kernel = kernel;
        Ramdisk = ramdisk;
        Second = second;
        Dt = dt;
        Tail = tail;
    }

    public byte[] Kernel { get; set; }
    public byte[] Ramdisk { get; set; }
    public byte[] Second { get; }
    public byte[] Dt { get; }
    public byte[] Tail { get; }

    public int PageSize => (int)BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(0x24));

    private static int Pad(int n, int page) => (n + page - 1) / page * page;

    public static AndroidBootImage Parse(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < 0x30 || !blob.AsSpan(0, 8).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not an Android boot image (missing the ANDROID! header).");
        }
        var kernelSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0x08));
        var ramdiskSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0x10));
        var secondSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0x18));
        var pageSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0x24));
        // Header v0's 0x28 word is repurposed by Samsung as dt_size; the blob that
        // follows starts with 'DTBH'. Treating it as header_version would drop the
        // device trees.
        var dtSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(0x28));
        if (pageSize <= 0)
        {
            throw new InvalidDataException("Boot image has an invalid page size.");
        }

        var off = pageSize;
        var kernel = Slice(blob, ref off, kernelSize, pageSize);
        var ramdisk = Slice(blob, ref off, ramdiskSize, pageSize);
        var second = Slice(blob, ref off, secondSize, pageSize);
        var dt = Slice(blob, ref off, dtSize, pageSize);
        var tail = blob.AsSpan(Math.Min(off, blob.Length)).ToArray();

        return new AndroidBootImage(blob.AsSpan(0, pageSize).ToArray(), kernel, ramdisk, second, dt, tail);
    }

    private static byte[] Slice(byte[] blob, ref int off, int size, int page)
    {
        if (off + size > blob.Length)
        {
            throw new InvalidDataException("Boot image sections exceed the file.");
        }
        var part = blob.AsSpan(off, size).ToArray();
        off += Pad(size, page);
        return part;
    }

    public byte[] ComputeId()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (var part in new[] { Kernel, Ramdisk, Second })
        {
            sha.AppendData(part);
            AppendLength(sha, part.Length);
        }
        if (Dt.Length > 0)
        {
            sha.AppendData(Dt);
            AppendLength(sha, Dt.Length);
        }
        return sha.GetHashAndReset();
    }

    private static void AppendLength(IncrementalHash sha, int length)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)length);
        sha.AppendData(b);
    }

    /// <summary>Re-serialise, refreshing the SHA-1 id and dropping the zero tail by default.</summary>
    public byte[] Serialize(bool refreshId = true, bool keepTail = true)
    {
        var page = PageSize;
        var header = (byte[])_header.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x08), (uint)Kernel.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x10), (uint)Ramdisk.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), (uint)Second.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x28), (uint)Dt.Length);
        if (refreshId)
        {
            var id = ComputeId();
            Array.Copy(id, 0, header, 0x240, id.Length);
            Array.Clear(header, 0x240 + id.Length, 0x260 - (0x240 + id.Length));
        }

        using var ms = new MemoryStream();
        ms.Write(header);
        Pad(ms, page - header.Length);
        foreach (var part in new[] { Kernel, Ramdisk, Second, Dt })
        {
            ms.Write(part);
            Pad(ms, Pad(part.Length, page) - part.Length);
        }
        if (keepTail)
        {
            ms.Write(Tail);
        }
        return ms.ToArray();
    }

    private static void Pad(Stream s, int count)
    {
        for (var i = 0; i < count; i++)
        {
            s.WriteByte(0);
        }
    }
}
