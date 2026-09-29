// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using S9Woa.Installer.Core.Deploy;
using S9Woa.Installer.Core.Twrp;
using S9Woa.Installer.Core.Twrp.Lzma;

namespace S9Woa.Installer.Core.Tests;

public sealed class WinReTwrpTests
{
    // The official TWRP image, if the path is provided via the environment. It
    // lives outside this repo (in the research checkout), so there is no default.
    private static string? ReferenceImage()
    {
        var p = Environment.GetEnvironmentVariable("S9WOA_TWRP_IMAGE");
        return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
    }

    // ---- LZMA alone -------------------------------------------------------

    [Fact]
    public void LzmaAloneRoundTripsAndUsesTheStockHeader()
    {
        var rng = new Random(1234);
        var data = new byte[200_000];
        rng.NextBytes(data.AsSpan(0, 50_000)); // partly random, partly zero: compresses.
        var comp = LzmaAlone.Compress(data);
        // 5D 00 00 80 00 = lc3 lp0 pb2, 8 MiB dict, exactly like the stock ramdisk.
        Assert.Equal("5D-00-00-80-00", BitConverter.ToString(comp, 0, 5));
        Assert.True(LzmaAlone.Decompress(comp).AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void LzmaAloneCompressIsDeterministic()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox ", 500)));
        Assert.Equal(Convert.ToHexString(LzmaAlone.Compress(data)), Convert.ToHexString(LzmaAlone.Compress(data)));
    }

    // ---- cpio -------------------------------------------------------------

    [Fact]
    public void CpioRoundTripsAndPutFileReplacesOrAppends()
    {
        var cpio = BuildCpio(("a.txt", "alpha"), ("dir/b.txt", "beta"));
        var serial = cpio.Serialize();
        var reparsed = CpioArchive.Parse(serial);
        Assert.True(reparsed.Serialize().AsSpan().SequenceEqual(serial));
        Assert.Equal(2, reparsed.Entries.Count);

        reparsed.PutFile("a.txt", Encoding.ASCII.GetBytes("ALPHA")); // replace
        Assert.Equal("ALPHA", Encoding.ASCII.GetString(reparsed.Get("a.txt")!.Data));
        Assert.Equal(2, reparsed.Entries.Count);

        reparsed.PutFile("c.txt", Encoding.ASCII.GetBytes("gamma")); // append
        Assert.Equal(3, reparsed.Entries.Count);
        Assert.True(reparsed.Get("c.txt")!.Ino > reparsed.Get("a.txt")!.Ino);
        Assert.True(CpioArchive.Parse(reparsed.Serialize()).Serialize().AsSpan().SequenceEqual(reparsed.Serialize()));
    }

    private static CpioArchive BuildCpio(params (string Name, string Body)[] files)
    {
        // Serialise a minimal newc archive by hand, then parse it back into a CpioArchive.
        using var ms = new MemoryStream();
        long ino = 300000;
        foreach (var (name, body) in files)
        {
            WriteNewc(ms, name, ino++, Encoding.ASCII.GetBytes(body), 0x81A4);
        }
        WriteNewc(ms, "TRAILER!!!", ino, [], 0x1ED);
        return CpioArchive.Parse(ms.ToArray());
    }

    private static void WriteNewc(Stream s, string name, long ino, byte[] data, int mode)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        long[] f = [ino, mode, 0, 0, 1, 0, data.Length, 0, 0, 0, 0, nameBytes.Length + 1, 0];
        s.Write(Encoding.ASCII.GetBytes("070701"));
        foreach (var v in f)
        {
            s.Write(Encoding.ASCII.GetBytes(((uint)v).ToString("x8")));
        }
        s.Write(nameBytes);
        s.WriteByte(0);
        var headerLen = 6 + (13 * 8) + nameBytes.Length + 1;
        Pad(s, (Align(headerLen, 4)) - headerLen);
        s.Write(data);
        Pad(s, Align(data.Length, 4) - data.Length);
    }

    private static int Align(int n, int a) => (n + a - 1) / a * a;
    private static void Pad(Stream s, int n) { for (var i = 0; i < n; i++) { s.WriteByte(0); } }

    // ---- boot image -------------------------------------------------------

    [Fact]
    public void SyntheticBootImageRoundTripsByteForByte()
    {
        var raw = SyntheticBoot(kernel: RandomBytes(5000), ramdisk: RandomBytes(3000), dt: RandomBytes(1200));
        var boot = AndroidBootImage.Parse(raw);
        Assert.True(boot.Serialize(refreshId: false).AsSpan().SequenceEqual(raw));
        Assert.Equal(5000, boot.Kernel.Length);
        Assert.Equal(1200, boot.Dt.Length);
    }

    [Fact]
    public void BootImageIdRefreshesFromSections()
    {
        var raw = SyntheticBoot(RandomBytes(1000), RandomBytes(800), RandomBytes(600));
        var boot = AndroidBootImage.Parse(raw);
        var refreshed = boot.Serialize(refreshId: true, keepTail: false);
        var id = AndroidBootImage.Parse(refreshed).ComputeId();
        Assert.True(refreshed.AsSpan(0x240, 20).SequenceEqual(id));
    }

    private static byte[] SyntheticBoot(byte[] kernel, byte[] ramdisk, byte[] dt, int page = 2048)
    {
        static int Pad(int n, int p) => (n + p - 1) / p * p;
        using var ms = new MemoryStream();
        var header = new byte[page];
        "ANDROID!"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x08), (uint)kernel.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x10), (uint)ramdisk.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x24), (uint)page);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x28), (uint)dt.Length);
        ms.Write(header);
        foreach (var part in new[] { kernel, ramdisk, Array.Empty<byte>(), dt })
        {
            ms.Write(part);
            for (var i = 0; i < Pad(part.Length, page) - part.Length; i++)
            {
                ms.WriteByte(0);
            }
        }
        return ms.ToArray();
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        new Random(n).NextBytes(b);
        return b;
    }

    // ---- power-off patch --------------------------------------------------

    [Fact]
    public void PowerOffPatchRefusesAnUnknownKernel()
    {
        Assert.False(PowerOffRoutePatch.IsPatchable(RandomBytes(4096)));
        Assert.Throws<InvalidOperationException>(() => PowerOffRoutePatch.Patch(RandomBytes(4096)));
    }

    // ---- status file ------------------------------------------------------

    [Fact]
    public void WinReStatusFormatsKeyValueLines()
    {
        var text = WinReStatus.Copying(percent: 45, done: 4500, total: 10000).ToFileContents();
        Assert.Contains("phase=copy\n", text, StringComparison.Ordinal);
        Assert.Contains("label=Copying Windows\n", text, StringComparison.Ordinal);
        Assert.Contains("percent=45\n", text, StringComparison.Ordinal);
        Assert.Contains("done_bytes=4500\n", text, StringComparison.Ordinal);
        Assert.Contains("total_bytes=10000\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);

        // No percent line when totals are unknown; a stray newline/'=' is sanitised.
        Assert.DoesNotContain("percent=", WinReStatus.BootFiles().ToFileContents(), StringComparison.Ordinal);
        Assert.Equal(100, WinReStatus.PercentOf(50, 50));
        Assert.Null(WinReStatus.PercentOf(1, 0));
        Assert.Contains("label=a b-c\n", new WinReStatus("copy", "a\nb=c").ToFileContents(), StringComparison.Ordinal);
    }

    // ---- PNG recolor ------------------------------------------------------

    [Fact]
    public void PngRecolorShiftsTealToBlue()
    {
        var teal = SolidRgbaPng(4, 4, 0x00, 0x90, 0xC9, 0xFF);
        var blue = PngRecolor.AccentToBlue(teal);
        var (r, g, b) = FirstOpaquePixel(blue);
        Assert.True(r < 40 && g is > 80 and < 130 && b is > 150 and < 210, $"got {r},{g},{b}");
        // A grey image is left alone.
        var grey = SolidRgbaPng(4, 4, 0x60, 0x60, 0x60, 0xFF);
        Assert.Equal(FirstOpaquePixel(grey), FirstOpaquePixel(PngRecolor.AccentToBlue(grey)));
    }

    // ---- embedded theme sanity (the key validate.py checks, in C#) --------

    [Fact]
    public void EmbeddedThemeIsWellFormedAndHasTheWinReContract()
    {
        var winre = ThemeText("winre.xml");
        var doc = XDocument.Parse(winre); // well-formed
        var pages = doc.Descendants("page").Select(p => p.Attribute("name")!.Value).ToHashSet();
        foreach (var required in new[] { "main", "lock", "winre_home", "winre_push", "winre_run", "winre_output", "winre_confirm", "winre_troubleshoot" })
        {
            Assert.Contains(required, pages);
        }

        // Command-bridge rules are checked on the code, not the prose: winre.xml
        // documents why openrecoveryscript is banned, so strip comments first.
        var code = System.Text.RegularExpressions.Regex.Replace(winre, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.DoesNotContain("openrecoveryscript", code, StringComparison.Ordinal);
        Assert.DoesNotContain("tw_action=cmd", code, StringComparison.Ordinal);
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(code, "tw_action_param=([^<]+)").Cast<System.Text.RegularExpressions.Match>())
        {
            Assert.StartsWith("/sbin/winre-gui.sh /", m.Groups[1].Value.Trim(), StringComparison.Ordinal);
        }
        // The copy-screen trigger uses != "0" and never an empty compare (checked
        // on the code, since a comment explains the empty-compare trap).
        Assert.Contains("var1=\"winre_push\" op=\"!=\" var2=\"0\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("var2=\"\"", code, StringComparison.Ordinal);
        // No '--' inside an XML comment (illegal, and rapidxml tolerates it).
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(winre, "<!--(.*?)-->", System.Text.RegularExpressions.RegexOptions.Singleline).Cast<System.Text.RegularExpressions.Match>())
        {
            Assert.DoesNotContain("--", m.Groups[1].Value, StringComparison.Ordinal);
        }

        // Every winre_* page can be left with a hardware back and home key.
        foreach (var page in doc.Descendants("page").Where(p => p.Attribute("name")!.Value.StartsWith("winre", StringComparison.Ordinal)))
        {
            var keys = page.Descendants("touch").Select(t => t.Attribute("key")?.Value).ToHashSet();
            Assert.Contains("back", keys);
            Assert.Contains("home", keys);
        }

        var splash = XDocument.Parse(ThemeText("splash.xml"));
        Assert.Contains(splash.Descendants("page").Select(p => p.Attribute("name")!.Value), n => n == "splash");
    }

    private static string ThemeText(string name)
    {
        var asm = typeof(WinReTwrpBuilder).Assembly;
        using var s = asm.GetManifestResourceStream($"S9Woa.Installer.Core.Twrp.Assets.theme.{name}")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // ---- end-to-end build from the official image (skips when absent) -----

    [Fact]
    public void BuildsWinReFromTheOfficialImage()
    {
        var path = ReferenceImage();
        if (path is null)
        {
            return; // reference image not present; skip gracefully.
        }
        var baseBytes = File.ReadAllBytes(path);
        Assert.True(WinReTwrpBuilder.MatchesOfficial(baseBytes));
        Assert.Equal(BaseImageKind.OfficialTwrp, WinReTwrpBuilder.Classify(baseBytes));

        var boot = AndroidBootImage.Parse(baseBytes);
        Assert.True(boot.Serialize(refreshId: false).AsSpan().SequenceEqual(baseBytes)); // byte-exact round-trip
        var plain = LzmaAlone.Decompress(boot.Ramdisk);
        Assert.True(LzmaAlone.Decompress(LzmaAlone.Compress(plain)).AsSpan().SequenceEqual(plain)); // lzma round-trip
        Assert.True(CpioArchive.Parse(plain).Serialize().AsSpan().SequenceEqual(plain)); // cpio byte-exact

        var report = new List<string>();
        var result = new WinReTwrpBuilder().Build(baseBytes, report: report);

        Assert.True(result.Bytes <= AndroidBootImage.RecoveryPartitionBytes);
        Assert.Equal(WinReTwrpBuilder.BuilderVersion, result.BuilderVersion);

        var built = AndroidBootImage.Parse(result.Image);
        Assert.True(built.Dt.AsSpan().SequenceEqual(boot.Dt));       // device tree unchanged
        Assert.True(built.Second.AsSpan().SequenceEqual(boot.Second)); // second stage unchanged
        Assert.True(built.Kernel.AsSpan().SequenceEqual(PowerOffRoutePatch.Patch(boot.Kernel))); // kernel = base + patch

        var cpio = CpioArchive.Parse(LzmaAlone.Decompress(built.Ramdisk));
        Assert.NotNull(cpio.Get("twres/winre.xml"));
        Assert.NotNull(cpio.Get("twres/winre-build.txt"));
        Assert.NotNull(cpio.Get("sbin/winre-statuswatch.sh"));
        Assert.NotNull(cpio.Get("sbin/winre-actions.sh"));
        Assert.NotNull(cpio.Get("sbin/s9woa/rwd1_ack.ko"));
        Assert.NotNull(cpio.Get("sbin/s9woa/rwd1_evidence_reader.ko"));
        Assert.NotNull(cpio.Get("twres/fonts/winre-light.ttf"));
        Assert.Contains("setprop sys.usb.config adb", Encoding.UTF8.GetString(cpio.Get("init.recovery.usb.rc")!.Data), StringComparison.Ordinal);

        // Scripts land with LF endings and mode 0755.
        var script = cpio.Get("sbin/winre-actions.sh")!;
        Assert.DoesNotContain("\r\n", Encoding.UTF8.GetString(script.Data), StringComparison.Ordinal);
        Assert.Equal(0x1FF, script.Mode & 0x1FF);

        // Deterministic.
        Assert.Equal(result.Sha256, new WinReTwrpBuilder().Build(baseBytes).Sha256);
    }

    // ---- helpers ----------------------------------------------------------

    private static byte[] SolidRgbaPng(int w, int h, byte r, byte g, byte b, byte a)
    {
        // Build a raw RGBA image, filter as None, deflate, wrap as a PNG.
        var stride = w * 4;
        var raw = new byte[(stride + 1) * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * (stride + 1)) + 1 + (x * 4);
                raw[i] = r; raw[i + 1] = g; raw[i + 2] = b; raw[i + 3] = a;
            }
        }
        using var idat = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8; ihdr[9] = 6; // depth 8, RGBA
        WritePngChunk(ms, "IHDR", ihdr);
        WritePngChunk(ms, "IDAT", idat.ToArray());
        WritePngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WritePngChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        Span<byte> crcBytes = stackalloc byte[4];
        var combined = typeBytes.Concat(data).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, Crc32Png(combined));
        s.Write(crcBytes);
    }

    private static uint Crc32Png(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static (byte R, byte G, byte B) FirstOpaquePixel(byte[] png)
    {
        var chunks = new List<(string Type, byte[] Data)>();
        var off = 8;
        while (off + 8 <= png.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(off));
            var type = Encoding.ASCII.GetString(png, off + 4, 4);
            chunks.Add((type, png.AsSpan(off + 8, len).ToArray()));
            off += 12 + len;
            if (type == "IEND")
            {
                break;
            }
        }
        var idat = chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();
        using var input = new MemoryStream(idat);
        using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        var raw = outMs.ToArray();
        // first scanline: [filter][r g b a ...]; filter is None for our test PNGs.
        return (raw[1], raw[2], raw[3]);
    }
}
