// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>One fully composited GIF frame: RGBA, row-major, plus its display time.</summary>
public sealed record GifFrame(byte[] Rgba, int DelayMs);

/// <summary>A decoded GIF: the logical screen size and every frame, composited.</summary>
public sealed record GifAnimation(int Width, int Height, IReadOnlyList<GifFrame> Frames);

/// <summary>
/// A small GIF87a/GIF89a decoder (no System.Drawing: Core is plain net8.0). It handles
/// global and local colour tables, LZW with deferred clear codes, interlacing,
/// the graphic control extension (delay, transparency, disposal 0-3) and composites
/// every frame onto the logical screen the way browsers do (the canvas starts
/// transparent). It exists to read the user's own UpdateOS gear animation at build
/// time; see <see cref="GearFrames"/>.
/// </summary>
public static class GifDecoder
{
    private const int MaxSide = 4096;
    private const int MaxFrames = 512;

    public static GifAnimation Decode(byte[] gif)
    {
        ArgumentNullException.ThrowIfNull(gif);
        var r = new Reader(gif);
        var sig = r.Bytes(6);
        if (sig[0] != 'G' || sig[1] != 'I' || sig[2] != 'F' || sig[3] != '8' || (sig[4] != '7' && sig[4] != '9') || sig[5] != 'a')
        {
            throw new InvalidDataException("Not a GIF file.");
        }
        var width = r.U16();
        var height = r.U16();
        if (width == 0 || height == 0 || width > MaxSide || height > MaxSide)
        {
            throw new InvalidDataException($"Unsupported GIF size {width}x{height}.");
        }
        var packed = r.U8();
        r.U8(); // background colour index: the canvas starts transparent, as in browsers.
        r.U8(); // pixel aspect ratio
        byte[]? globalTable = (packed & 0x80) != 0 ? r.Bytes(3 * (2 << (packed & 7))) : null;

        var canvas = new byte[width * height * 4];
        var frames = new List<GifFrame>();
        int delayMs = 0, disposal = 0, transparent = -1;

        while (true)
        {
            var block = r.U8();
            if (block == 0x3B)
            {
                break; // trailer
            }
            if (block == 0x21)
            {
                var label = r.U8();
                if (label == 0xF9)
                {
                    var size = r.U8();
                    var ext = r.Bytes(size);
                    SkipSubBlocks(r);
                    if (size >= 4)
                    {
                        disposal = (ext[0] >> 2) & 7;
                        delayMs = BinaryPrimitives.ReadUInt16LittleEndian(ext.AsSpan(1)) * 10;
                        transparent = (ext[0] & 1) != 0 ? ext[3] : -1;
                    }
                }
                else
                {
                    SkipSubBlocks(r);
                }
                continue;
            }
            if (block != 0x2C)
            {
                throw new InvalidDataException($"Unexpected GIF block 0x{block:X2}.");
            }

            int left = r.U16(), top = r.U16(), w = r.U16(), h = r.U16();
            var ipacked = r.U8();
            var table = (ipacked & 0x80) != 0 ? r.Bytes(3 * (2 << (ipacked & 7))) : globalTable
                ?? throw new InvalidDataException("GIF frame without a colour table.");
            var interlaced = (ipacked & 0x40) != 0;
            var minCode = r.U8();
            var indices = Lzw(ReadSubBlocks(r), minCode, w * h);

            var before = disposal == 3 ? (byte[])canvas.Clone() : null;
            for (var row = 0; row < h; row++)
            {
                var y = top + (interlaced ? InterlacedRow(row, h) : row);
                if (y >= height)
                {
                    continue;
                }
                for (var col = 0; col < w; col++)
                {
                    var x = left + col;
                    var index = indices[(row * w) + col];
                    if (x >= width || index == transparent || (3 * index) + 2 >= table.Length)
                    {
                        continue;
                    }
                    var o = ((y * width) + x) * 4;
                    canvas[o] = table[3 * index];
                    canvas[o + 1] = table[(3 * index) + 1];
                    canvas[o + 2] = table[(3 * index) + 2];
                    canvas[o + 3] = 255;
                }
            }
            frames.Add(new GifFrame((byte[])canvas.Clone(), delayMs > 0 ? delayMs : 100));
            if (frames.Count > MaxFrames)
            {
                throw new InvalidDataException("Too many GIF frames.");
            }

            if (disposal == 2 && left < width)
            {
                for (var y = top; y < Math.Min(height, top + h); y++)
                {
                    Array.Clear(canvas, ((y * width) + left) * 4, Math.Min(w, width - left) * 4);
                }
            }
            else if (disposal == 3 && before is not null)
            {
                canvas = before;
            }
            delayMs = 0;
            disposal = 0;
            transparent = -1;
        }
        if (frames.Count == 0)
        {
            throw new InvalidDataException("The GIF has no frames.");
        }
        return new GifAnimation(width, height, frames);
    }

    /// <summary>Row in the image for the n-th row stored in an interlaced frame.</summary>
    private static int InterlacedRow(int n, int height)
    {
        foreach (var (start, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
        {
            var count = start >= height ? 0 : ((height - start - 1) / step) + 1;
            if (n < count)
            {
                return start + (n * step);
            }
            n -= count;
        }
        return height - 1;
    }

    private static byte[] Lzw(byte[] data, int minCodeSize, int pixels)
    {
        if (minCodeSize is < 2 or > 11)
        {
            throw new InvalidDataException($"Bad GIF LZW code size {minCodeSize}.");
        }
        var clear = 1 << minCodeSize;
        var end = clear + 1;
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var first = new byte[4096];
        var stack = new byte[4097];
        for (var i = 0; i < clear; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            first[i] = (byte)i;
        }

        var output = new byte[pixels];
        int written = 0, codeSize = minCodeSize + 1, next = end + 1, previous = -1;
        int bits = 0, bitCount = 0, pos = 0;
        while (written < pixels)
        {
            while (bitCount < codeSize)
            {
                if (pos >= data.Length)
                {
                    return output; // truncated stream: keep what decoded
                }
                bits |= data[pos++] << bitCount;
                bitCount += 8;
            }
            var code = bits & ((1 << codeSize) - 1);
            bits >>= codeSize;
            bitCount -= codeSize;

            if (code == clear)
            {
                codeSize = minCodeSize + 1;
                next = end + 1;
                previous = -1;
                continue;
            }
            if (code == end)
            {
                break;
            }
            if (previous == -1)
            {
                if (code >= clear)
                {
                    throw new InvalidDataException("Bad first GIF LZW code.");
                }
                output[written++] = (byte)code;
                previous = code;
                continue;
            }

            int top = 0, cur = code;
            byte head;
            if (code < next)
            {
                head = first[code];
            }
            else if (code == next)
            {
                head = first[previous];
                stack[top++] = head;
                cur = previous;
            }
            else
            {
                throw new InvalidDataException("Bad GIF LZW code.");
            }
            while (cur >= 0 && top < stack.Length)
            {
                stack[top++] = suffix[cur];
                cur = prefix[cur];
            }
            while (top > 0 && written < pixels)
            {
                output[written++] = stack[--top];
            }

            if (next < 4096)
            {
                prefix[next] = (short)previous;
                suffix[next] = head;
                first[next] = first[previous];
                next++;
                if (next == (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }
            previous = code;
        }
        return output;
    }

    private static byte[] ReadSubBlocks(Reader r)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var n = r.U8();
            if (n == 0)
            {
                return ms.ToArray();
            }
            ms.Write(r.Bytes(n));
        }
    }

    private static void SkipSubBlocks(Reader r)
    {
        while (true)
        {
            var n = r.U8();
            if (n == 0)
            {
                return;
            }
            r.Bytes(n);
        }
    }

    private sealed class Reader(byte[] data)
    {
        private int _pos;

        public byte U8() =>
            _pos < data.Length ? data[_pos++] : throw new InvalidDataException("The GIF is truncated.");

        public int U16()
        {
            var lo = U8();
            return lo | (U8() << 8);
        }

        public byte[] Bytes(int n)
        {
            if (n < 0 || _pos + n > data.Length)
            {
                throw new InvalidDataException("The GIF is truncated.");
            }
            var b = data.AsSpan(_pos, n).ToArray();
            _pos += n;
            return b;
        }
    }
}
