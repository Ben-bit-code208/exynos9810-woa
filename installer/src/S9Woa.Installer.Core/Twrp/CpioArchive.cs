// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Globalization;
using System.Text;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>One entry in a newc cpio archive.</summary>
public sealed class CpioEntry
{
    public const int RegularFile = 0x8000;
    public const int Directory = 0x4000;
    public const int Symlink = 0xA000;
    private const int FormatMask = 0xF000;

    public required string Name { get; init; }
    public int Mode { get; set; }
    public long Ino { get; set; }
    public int Uid { get; set; }
    public int Gid { get; set; }
    public int Nlink { get; set; } = 1;
    public long Mtime { get; set; }
    public int DevMajor { get; set; }
    public int DevMinor { get; set; }
    public int RdevMajor { get; set; }
    public int RdevMinor { get; set; }
    public byte[] Data { get; set; } = [];

    public bool IsDir => (Mode & FormatMask) == Directory;
    public bool IsSymlink => (Mode & FormatMask) == Symlink;
}

/// <summary>
/// A newc ("070701") cpio archive read and written byte-for-byte: lowercase hex
/// fields, the original inode order and per-entry uid/gid/mode/mtime, the
/// verbatim trailer, and the 512-byte tail padding the kernel's initramfs
/// unpacker expects. Preserving all of that is what lets the builder prove it
/// only touched the files it meant to. Port of the reference Python
/// <c>bootimg.py</c> Cpio.
/// </summary>
public sealed class CpioArchive
{
    private const string Magic = "070701";
    private const string TrailerName = "TRAILER!!!";

    private CpioArchive(List<CpioEntry> entries, CpioEntry? trailer)
    {
        Entries = entries;
        Trailer = trailer;
    }

    public List<CpioEntry> Entries { get; }
    public CpioEntry? Trailer { get; }

    private static int Pad(int n, int align) => (n + align - 1) / align * align;

    public int IndexOf(string name) => Entries.FindIndex(e => e.Name == name);

    public CpioEntry? Get(string name)
    {
        var i = IndexOf(name);
        return i >= 0 ? Entries[i] : null;
    }

    /// <summary>Replace an existing file's payload, or append a new regular file.</summary>
    public void PutFile(string name, byte[] data, int mode = 0x1A4 /* 0644 */)
    {
        var i = IndexOf(name);
        if (i >= 0)
        {
            Entries[i].Data = data;
            return;
        }
        var template = Entries.Count > 0 ? Entries[0] : null;
        var nextIno = Entries.Count > 0 ? Entries.Max(e => e.Ino) + 1 : 1;
        Entries.Add(new CpioEntry
        {
            Name = name,
            Mode = CpioEntry.RegularFile | mode,
            Ino = nextIno,
            Uid = template?.Uid ?? 0,
            Gid = template?.Gid ?? 0,
            Mtime = template?.Mtime ?? 0,
            Data = data,
        });
    }

    public static CpioArchive Parse(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var entries = new List<CpioEntry>();
        CpioEntry? trailer = null;
        var off = 0;
        while (off < blob.Length)
        {
            if (!Encoding.ASCII.GetString(blob, off, Math.Min(6, blob.Length - off)).StartsWith(Magic, StringComparison.Ordinal))
            {
                // Some builders pad the tail with NULs to a block boundary; anything
                // else is a real parse failure.
                if (AllZero(blob, off))
                {
                    break;
                }
                throw new InvalidDataException($"Bad cpio magic at 0x{off:x}.");
            }
            var f = new long[13];
            for (var i = 0; i < 13; i++)
            {
                f[i] = long.Parse(Encoding.ASCII.GetString(blob, off + 6 + (i * 8), 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            var fileSize = (int)f[6];
            var nameSize = (int)f[11];
            var nameOff = off + 110;
            var name = Encoding.UTF8.GetString(blob, nameOff, nameSize - 1);
            var dataOff = Pad(nameOff + nameSize, 4);
            var data = blob.AsSpan(dataOff, fileSize).ToArray();
            off = Pad(dataOff + fileSize, 4);
            var entry = new CpioEntry
            {
                Name = name, Ino = f[0], Mode = (int)f[1], Uid = (int)f[2], Gid = (int)f[3],
                Nlink = (int)f[4], Mtime = f[5], DevMajor = (int)f[7], DevMinor = (int)f[8],
                RdevMajor = (int)f[9], RdevMinor = (int)f[10], Data = data,
            };
            if (name == TrailerName)
            {
                trailer = entry;
                break;
            }
            entries.Add(entry);
        }
        return new CpioArchive(entries, trailer);
    }

    private static bool AllZero(byte[] blob, int from)
    {
        for (var i = from; i < blob.Length; i++)
        {
            if (blob[i] != 0)
            {
                return false;
            }
        }
        return true;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        foreach (var e in Entries)
        {
            Write(ms, e, e.Ino);
        }
        var trailer = Trailer ?? new CpioEntry { Name = TrailerName, Mode = 0x1ED /* 0755 */, Nlink = 1 };
        Write(ms, trailer, trailer.Ino);
        // The kernel's initramfs unpacker wants the archive to end on a 512-byte
        // boundary; every stock Android ramdisk is padded this way.
        PadStream(ms, Pad((int)ms.Length, 512) - (int)ms.Length);
        return ms.ToArray();
    }

    private static void Write(Stream s, CpioEntry e, long ino)
    {
        var name = Encoding.UTF8.GetBytes(e.Name);
        long[] fields =
        [
            ino, e.Mode, e.Uid, e.Gid, e.Nlink, e.Mtime, e.Data.Length,
            e.DevMajor, e.DevMinor, e.RdevMajor, e.RdevMinor, name.Length + 1, 0,
        ];
        s.Write(Encoding.ASCII.GetBytes(Magic));
        foreach (var v in fields)
        {
            // Lowercase hex: what this ramdisk's builder emitted; uppercase would
            // change thousands of bytes and break the byte-exactness check.
            s.Write(Encoding.ASCII.GetBytes(((uint)(v & 0xFFFFFFFF)).ToString("x8", CultureInfo.InvariantCulture)));
        }
        s.Write(name);
        s.WriteByte(0);
        var headerLen = 6 + (13 * 8) + name.Length + 1;
        PadStream(s, Pad(headerLen, 4) - headerLen);
        s.Write(e.Data);
        PadStream(s, Pad(e.Data.Length, 4) - e.Data.Length);
    }

    private static void PadStream(Stream s, int count)
    {
        for (var i = 0; i < count; i++)
        {
            s.WriteByte(0);
        }
    }
}
