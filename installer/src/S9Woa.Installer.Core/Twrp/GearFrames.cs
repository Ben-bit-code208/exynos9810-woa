// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>
/// Minimal PNG encoder for the frames the builder renders on the user's PC. It
/// writes 8-bit RGB with filter None and zlib STORED blocks: TWRP's libpng reads
/// that fine, the ramdisk is LZMA-compressed as a whole anyway, and the bytes are
/// then identical on every machine and runtime (no dependency on the platform
/// zlib), which keeps the build deterministic.
/// </summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] Rgb(int width, int height, byte[] rgb)
    {
        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.Length != width * height * 3)
        {
            throw new ArgumentException("Pixel buffer does not match the size.", nameof(rgb));
        }
        var stride = width * 3;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(rgb, y * stride, raw, (y * (stride + 1)) + 1, stride); // filter byte 0 = None
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // colour type RGB

        using var ms = new MemoryStream();
        ms.Write(Signature);
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", ZlibStored(raw));
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] ZlibStored(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x01);
        var offset = 0;
        do
        {
            var n = Math.Min(65535, data.Length - offset);
            ms.WriteByte((byte)(offset + n >= data.Length ? 1 : 0)); // BFINAL, BTYPE=00
            ms.WriteByte((byte)n);
            ms.WriteByte((byte)(n >> 8));
            ms.WriteByte((byte)~n);
            ms.WriteByte((byte)(~n >> 8));
            ms.Write(data, offset, n);
            offset += n;
        }
        while (offset < data.Length);
        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(data));
        ms.Write(adler);
        return ms.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes)
        {
            crc = Crc(crc, b);
        }
        foreach (var b in data)
        {
            crc = Crc(crc, b);
        }
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        s.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint Crc(uint crc, byte b) => CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);

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
}

/// <summary>
/// Turns the user's own UpdateOS gear GIF into the theme's gear frames at build
/// time, the C# twin of <c>gif_gear_frames</c> in tools/twrp-winre/build.py:
/// every frame is composited over black, reduced to luma (the GIF's near-black
/// background tint, at or below <see cref="Floor"/>, becomes true black), resized
/// to the procedural gears' size with a Lanczos-3 filter and written opaque white
/// on black, which is what TWRP's alpha-unweighted upscaler needs. The GIF and
/// these frames are Microsoft's: they only ever exist inside the image being
/// built on the user's PC.
/// </summary>
public static class GearFrames
{
    /// <summary>Luma at or below this is the GIF's background and becomes black.</summary>
    public const int Floor = 8;

    /// <summary>Side of a gear frame in theme pixels; must match mkassets.COG_PX.</summary>
    public const int Size = 420;

    /// <summary>Frames rendered from a GIF, the mean frame delay, and the TWRP speed to use.</summary>
    public sealed record Result(IReadOnlyList<byte[]> Pngs, double MeanDelayMs, int Fps);

    public static Result FromGif(byte[] gif)
    {
        var anim = GifDecoder.Decode(gif);
        var pngs = new List<byte[]>(anim.Frames.Count);
        foreach (var frame in anim.Frames)
        {
            var luma = new float[anim.Width * anim.Height];
            for (var i = 0; i < luma.Length; i++)
            {
                var o = i * 4;
                var a = frame.Rgba[o + 3];
                // Over black, then ITU-R 601 luma (what PIL's convert("L") computes).
                var r = frame.Rgba[o] * a / 255;
                var g = frame.Rgba[o + 1] * a / 255;
                var b = frame.Rgba[o + 2] * a / 255;
                var l = ((r * 299) + (g * 587) + (b * 114) + 500) / 1000;
                luma[i] = l <= Floor ? 0 : l;
            }
            var scaled = Lanczos(luma, anim.Width, anim.Height, Size, Size);
            var rgb = new byte[Size * Size * 3];
            for (var i = 0; i < scaled.Length; i++)
            {
                var v = (byte)Math.Clamp((int)Math.Round(scaled[i], MidpointRounding.AwayFromZero), 0, 255);
                rgb[i * 3] = rgb[(i * 3) + 1] = rgb[(i * 3) + 2] = v;
            }
            pngs.Add(PngWriter.Rgb(Size, Size, rgb));
        }
        var mean = anim.Frames.Average(f => f.DelayMs);
        return new Result(pngs, mean, SpeedForDelay(mean));
    }

    /// <summary>
    /// The &lt;speed fps&gt; whose TWRP frame period is closest to <paramref name="delayMs"/>:
    /// the GUI loop runs at 30 Hz and an animation advances every floor(30/fps)+1 passes,
    /// so 350 ms maps to fps 3 (367 ms).
    /// </summary>
    public static int SpeedForDelay(double delayMs) =>
        Enumerable.Range(1, 30)
            .OrderBy(f => Math.Abs((((30 / f) + 1) * 1000.0 / 30) - delayMs))
            .ThenBy(f => f)
            .First();

    private static float[] Lanczos(float[] src, int sw, int sh, int dw, int dh)
    {
        var horizontal = new float[dw * sh];
        var (hIdx, hW) = Weights(sw, dw);
        for (var y = 0; y < sh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                double sum = 0;
                for (var k = 0; k < hIdx[x].Length; k++)
                {
                    sum += src[(y * sw) + hIdx[x][k]] * hW[x][k];
                }
                horizontal[(y * dw) + x] = (float)sum;
            }
        }
        var output = new float[dw * dh];
        var (vIdx, vW) = Weights(sh, dh);
        for (var y = 0; y < dh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                double sum = 0;
                for (var k = 0; k < vIdx[y].Length; k++)
                {
                    sum += horizontal[(vIdx[y][k] * dw) + x] * vW[y][k];
                }
                output[(y * dw) + x] = (float)sum;
            }
        }
        return output;
    }

    private static (int[][] Index, double[][] Weight) Weights(int inSize, int outSize)
    {
        const double a = 3.0;
        var scale = (double)inSize / outSize;
        var filterScale = Math.Max(scale, 1.0);
        var support = a * filterScale;
        var index = new int[outSize][];
        var weight = new double[outSize][];
        for (var o = 0; o < outSize; o++)
        {
            var centre = ((o + 0.5) * scale) - 0.5;
            var lo = (int)Math.Floor(centre - support) + 1;
            var hi = (int)Math.Floor(centre + support);
            var idx = new List<int>();
            var w = new List<double>();
            double total = 0;
            for (var i = lo; i <= hi; i++)
            {
                var v = Kernel((i - centre) / filterScale, a);
                if (v == 0)
                {
                    continue;
                }
                idx.Add(Math.Clamp(i, 0, inSize - 1));
                w.Add(v);
                total += v;
            }
            index[o] = [.. idx];
            weight[o] = w.Select(v => v / total).ToArray();
        }
        return (index, weight);
    }

    private static double Kernel(double x, double a)
    {
        if (x == 0)
        {
            return 1;
        }
        if (Math.Abs(x) >= a)
        {
            return 0;
        }
        var px = Math.PI * x;
        return a * Math.Sin(px) * Math.Sin(px / a) / (px * px);
    }
}
