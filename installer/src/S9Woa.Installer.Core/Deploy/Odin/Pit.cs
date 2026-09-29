// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Text;

namespace S9Woa.Installer.Core.Deploy.Odin;

/// <summary>One partition in a Samsung partition information table (PIT).</summary>
public sealed record PitEntry(
    int BinaryType,
    int DeviceType,
    int Identifier,
    int Attributes,
    int BlockStart,
    int BlockCount,
    string PartitionName,
    string FlashFileName)
{
    /// <summary>Application-processor binary (as opposed to the modem, 1).</summary>
    public bool IsApplicationProcessor => BinaryType == 0;
}

/// <summary>
/// Parses the PIT a phone reports in Download mode: a 28-byte header (magic 0x12349876,
/// entry count) followed by 132-byte little-endian entries.
/// </summary>
public static class Pit
{
    public const uint Magic = 0x12349876;
    private const int HeaderSize = 28;
    private const int EntrySize = 132;

    public static IReadOnlyList<PitEntry> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            throw new InvalidDataException("Not a Samsung PIT (bad magic).");
        }
        var count = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (count <= 0 || count > 512 || HeaderSize + (long)count * EntrySize > data.Length)
        {
            throw new InvalidDataException($"PIT reports {count} entries but holds {data.Length} bytes.");
        }
        var entries = new List<PitEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var e = data.Slice(HeaderSize + i * EntrySize, EntrySize);
            entries.Add(new PitEntry(
                BinaryType: BinaryPrimitives.ReadInt32LittleEndian(e),
                DeviceType: BinaryPrimitives.ReadInt32LittleEndian(e[4..]),
                Identifier: BinaryPrimitives.ReadInt32LittleEndian(e[8..]),
                Attributes: BinaryPrimitives.ReadInt32LittleEndian(e[12..]),
                BlockStart: BinaryPrimitives.ReadInt32LittleEndian(e[20..]),
                BlockCount: BinaryPrimitives.ReadInt32LittleEndian(e[24..]),
                PartitionName: CString(e.Slice(36, 32)),
                FlashFileName: CString(e.Slice(68, 32))));
        }
        return entries;
    }

    public static PitEntry? Find(IEnumerable<PitEntry> entries, string partitionName) =>
        entries.FirstOrDefault(e => e.PartitionName.Equals(partitionName, StringComparison.OrdinalIgnoreCase));

    private static string CString(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }
}
