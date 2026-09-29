// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Twrp.Lzma.SevenZip;
using LzmaEncoder = S9Woa.Installer.Core.Twrp.Lzma.SevenZip.Compression.LZMA.Encoder;
using LzmaDecoder = S9Woa.Installer.Core.Twrp.Lzma.SevenZip.Compression.LZMA.Decoder;

namespace S9Woa.Installer.Core.Twrp.Lzma;

/// <summary>
/// Reads and writes the "LZMA alone" stream framing the Android/TWRP ramdisk
/// uses (5 property bytes + an 8-byte little-endian uncompressed length, then the
/// coded stream), matching Python's <c>lzma.FORMAT_ALONE</c>. The coder is
/// configured to reproduce the stock ramdisk's header exactly: lc=3, lp=0, pb=2,
/// 8 MiB dictionary (property byte 0x5D, dictionary 0x00800000), so the first
/// five bytes of the output are <c>5D 00 00 80 00</c>. The kernel's standard LZMA
/// initramfs decompressor reads this without change.
/// </summary>
public static class LzmaAlone
{
    private const int DictionarySize = 1 << 23; // 8 MiB, matches the stock header.
    private const int PosStateBits = 2;         // pb
    private const int LitContextBits = 3;       // lc
    private const int LitPosBits = 0;           // lp
    private const int Algorithm = 2;
    private const int NumFastBytes = 273;
    private const string MatchFinder = "BT4";

    public static byte[] Compress(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var encoder = new LzmaEncoder();
        encoder.SetCoderProperties(
            [
                CoderPropID.DictionarySize, CoderPropID.PosStateBits, CoderPropID.LitContextBits,
                CoderPropID.LitPosBits, CoderPropID.Algorithm, CoderPropID.NumFastBytes,
                CoderPropID.MatchFinder, CoderPropID.EndMarker,
            ],
            [DictionarySize, PosStateBits, LitContextBits, LitPosBits, Algorithm, NumFastBytes, MatchFinder, false]);

        using var output = new MemoryStream();
        encoder.WriteCoderProperties(output);
        for (var i = 0; i < 8; i++)
        {
            output.WriteByte((byte)(data.LongLength >> (8 * i)));
        }
        using var input = new MemoryStream(data);
        encoder.Code(input, output, data.LongLength, -1, null);
        return output.ToArray();
    }

    public static byte[] Decompress(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length < 13)
        {
            throw new InvalidDataException("LZMA-alone stream is too short for its header.");
        }
        var properties = new byte[5];
        Array.Copy(blob, 0, properties, 0, 5);
        // The 8-byte length field is either the exact size or the "unknown size"
        // marker (all 0xFF), in which case an end-of-stream marker terminates the
        // stream. The stock TWRP ramdisk uses the marker form.
        var unknown = true;
        long size = 0;
        for (var i = 0; i < 8; i++)
        {
            size |= (long)blob[5 + i] << (8 * i);
            unknown &= blob[5 + i] == 0xFF;
        }
        var outSize = unknown ? -1L : size;
        if (!unknown && size < 0)
        {
            throw new InvalidDataException("LZMA-alone stream declares a negative length.");
        }
        var decoder = new LzmaDecoder();
        decoder.SetDecoderProperties(properties);
        using var input = new MemoryStream(blob, 13, blob.Length - 13);
        using var output = new MemoryStream(outSize > 0 && outSize < int.MaxValue ? (int)outSize : 0);
        decoder.Code(input, output, blob.Length - 13, outSize, null);
        return output.ToArray();
    }

    /// <summary>The five-byte property header a freshly configured encoder writes.</summary>
    internal static string PropertyHeaderHex()
    {
        var encoder = new LzmaEncoder();
        encoder.SetCoderProperties(
            [
                CoderPropID.DictionarySize, CoderPropID.PosStateBits, CoderPropID.LitContextBits,
                CoderPropID.LitPosBits, CoderPropID.Algorithm, CoderPropID.NumFastBytes,
                CoderPropID.MatchFinder, CoderPropID.EndMarker,
            ],
            [DictionarySize, PosStateBits, LitContextBits, LitPosBits, Algorithm, NumFastBytes, MatchFinder, false]);
        using var ms = new MemoryStream();
        encoder.WriteCoderProperties(ms);
        var sb = new System.Text.StringBuilder();
        foreach (var b in ms.ToArray())
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
