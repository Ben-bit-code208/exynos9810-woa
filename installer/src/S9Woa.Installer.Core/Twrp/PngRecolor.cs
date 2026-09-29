// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.IO.Compression;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>
/// Recolours an 8-bit RGBA PNG in place, used to shift the stock TWRP theme's
/// teal accent (baked as pixels into the progress bar, slider, handle and
/// checkbox/radio ticks) to Windows blue without an image library. Only the
/// colour changes; the exact shape and anti-aliasing are preserved so the
/// recoloured image still lines up with the stock pages that overlay it. Uses
/// only <see cref="System.IO.Compression"/>; the transform matches the reference
/// Python <c>mkassets.recolour_accent</c>.
/// </summary>
internal static class PngRecolor
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly (byte R, byte G, byte B) WinBlue = (0x00, 0x67, 0xC0);
    private static readonly (byte R, byte G, byte B) TwrpTeal = (0x00, 0x90, 0xC9);

    /// <summary>Recolour the teal accent to blue, or return the original if it is not 8-bit RGBA.</summary>
    public static byte[] AccentToBlue(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature))
        {
            return png;
        }

        var chunks = ReadChunks(png);
        var ihdr = chunks.FirstOrDefault(c => c.Type == "IHDR");
        if (ihdr.Data is null || ihdr.Data.Length < 13)
        {
            return png;
        }
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.Data.AsSpan(0));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.Data.AsSpan(4));
        var bitDepth = ihdr.Data[8];
        var colorType = ihdr.Data[9];
        var interlace = ihdr.Data[12];
        if (bitDepth != 8 || colorType != 6 || interlace != 0)
        {
            return png; // only 8-bit RGBA, non-interlaced is handled.
        }

        var idat = Concat(chunks.Where(c => c.Type == "IDAT").Select(c => c.Data!));
        var raw = Inflate(idat);
        var pixels = Unfilter(raw, width, height, bytesPerPixel: 4);
        Recolor(pixels);
        var refiltered = FilterNone(pixels, width, height, bytesPerPixel: 4);
        var newIdat = Deflate(refiltered);

        using var ms = new MemoryStream();
        ms.Write(Signature);
        foreach (var c in chunks)
        {
            if (c.Type == "IDAT")
            {
                continue; // replaced below, all at once, before IEND.
            }
            if (c.Type == "IEND")
            {
                WriteChunk(ms, "IDAT", newIdat);
            }
            WriteChunk(ms, c.Type, c.Data!);
        }
        return ms.ToArray();
    }

    private static void Recolor(byte[] rgba)
    {
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            var r = rgba[i];
            var g = rgba[i + 1];
            var b = rgba[i + 2];
            var a = rgba[i + 3];
            if (a == 0)
            {
                continue;
            }
            // Teal pixels are blue-dominant with mid green and near-zero red;
            // scale toward the target while keeping each pixel's brightness.
            if (b > 90 && g > 60 && r < 90 && b >= g)
            {
                var scale = b / (double)TwrpTeal.B;
                rgba[i] = (byte)Math.Min(255, (int)(WinBlue.R * scale));
                rgba[i + 1] = (byte)Math.Min(255, (int)(WinBlue.G * scale));
                rgba[i + 2] = (byte)Math.Min(255, (int)(WinBlue.B * scale));
            }
        }
    }

    private readonly record struct Chunk(string Type, byte[]? Data);

    private static List<Chunk> ReadChunks(byte[] png)
    {
        var list = new List<Chunk>();
        var off = 8;
        while (off + 8 <= png.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(off));
            var type = System.Text.Encoding.ASCII.GetString(png, off + 4, 4);
            var data = png.AsSpan(off + 8, len).ToArray();
            list.Add(new Chunk(type, data));
            off += 12 + len;
            if (type == "IEND")
            {
                break;
            }
        }
        return list;
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static byte[] Concat(IEnumerable<byte[]> parts)
    {
        using var ms = new MemoryStream();
        foreach (var p in parts)
        {
            ms.Write(p);
        }
        return ms.ToArray();
    }

    private static byte[] Inflate(byte[] zlib)
    {
        using var input = new MemoryStream(zlib);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }
        return output.ToArray();
    }

    private static byte[] Unfilter(byte[] raw, int width, int height, int bytesPerPixel)
    {
        var stride = width * bytesPerPixel;
        var outPixels = new byte[stride * height];
        var pos = 0;
        for (var y = 0; y < height; y++)
        {
            var filter = raw[pos++];
            for (var x = 0; x < stride; x++)
            {
                int cur = raw[pos++];
                var a = x >= bytesPerPixel ? outPixels[(y * stride) + x - bytesPerPixel] : 0;
                var b = y > 0 ? outPixels[((y - 1) * stride) + x] : 0;
                var c = x >= bytesPerPixel && y > 0 ? outPixels[((y - 1) * stride) + x - bytesPerPixel] : 0;
                var value = filter switch
                {
                    0 => cur,
                    1 => cur + a,
                    2 => cur + b,
                    3 => cur + ((a + b) / 2),
                    4 => cur + Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Unknown PNG filter {filter}."),
                };
                outPixels[(y * stride) + x] = (byte)(value & 0xFF);
            }
        }
        return outPixels;
    }

    private static byte[] FilterNone(byte[] pixels, int width, int height, int bytesPerPixel)
    {
        var stride = width * bytesPerPixel;
        var outRaw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            outRaw[y * (stride + 1)] = 0; // filter type None
            Array.Copy(pixels, y * stride, outRaw, (y * (stride + 1)) + 1, stride);
        }
        return outRaw;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }
        return pb <= pc ? b : c;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var bt in type)
        {
            crc = CrcTable[(crc ^ bt) & 0xFF] ^ (crc >> 8);
        }
        foreach (var bt in data)
        {
            crc = CrcTable[(crc ^ bt) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }
}
