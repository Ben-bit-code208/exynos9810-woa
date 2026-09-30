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

    // ---- GIF decoder, gear frames, PNG writer --------------------------------

    [Fact]
    public void GifDecoderCompositesFramesWithTransparencyAndLocalTables()
    {
        var gif = GifDecoder.Decode(TestGifs.Bytes(TestGifs.TwoFrames));
        Assert.Equal((16, 12), (gif.Width, gif.Height));
        Assert.Equal(2, gif.Frames.Count);
        Assert.All(gif.Frames, f => Assert.Equal(350, f.DelayMs));

        // Expected values are Pillow's decode of the same bytes.
        Assert.Equal((0, 0, 0, 0), Pixel(gif, 0, 0, 0));
        Assert.Equal((255, 0, 0, 255), Pixel(gif, 0, 4, 4));
        Assert.Equal((0, 0, 0, 0), Pixel(gif, 0, 11, 5));
        Assert.Equal((255, 0, 0, 255), Pixel(gif, 1, 4, 4));   // left in place by disposal 1
        Assert.Equal((0, 255, 0, 255), Pixel(gif, 1, 11, 5));  // from the sub-rectangle's own table
        Assert.Equal((0, 0, 0, 0), Pixel(gif, 1, 0, 0));
    }

    [Fact]
    public void GifDecoderHandlesInterlacedImages()
    {
        var gif = GifDecoder.Decode(TestGifs.Bytes(TestGifs.Interlaced));
        Assert.Equal((20, 20), (gif.Width, gif.Height));
        Assert.Equal((0, 0, 0, 255), Pixel(gif, 0, 0, 0));
        Assert.Equal((64, 64, 64, 255), Pixel(gif, 0, 7, 2));
        Assert.Equal((128, 128, 128, 255), Pixel(gif, 0, 19, 19));
        Assert.Equal((255, 255, 255, 255), Pixel(gif, 0, 12, 6));
        Assert.Throws<InvalidDataException>(() => GifDecoder.Decode(Encoding.ASCII.GetBytes("not a gif at all")));
    }

    private static (int, int, int, int) Pixel(GifAnimation gif, int frame, int x, int y)
    {
        var o = ((y * gif.Width) + x) * 4;
        var p = gif.Frames[frame].Rgba;
        return (p[o], p[o + 1], p[o + 2], p[o + 3]);
    }

    [Fact]
    public void GearFramesRenderWhiteOnBlackAtTheThemeSizeAndSpeed()
    {
        var gears = GearFrames.FromGif(TestGifs.Bytes(TestGifs.TwoFrames));
        Assert.Equal(2, gears.Pngs.Count);
        Assert.Equal(350, gears.MeanDelayMs);
        Assert.Equal(3, gears.Fps); // TWRP: frame every floor(30/3)+1 = 11 passes of a 30 Hz loop = 367 ms

        var (w, h, colourType, rgb) = ReadRgbPng(gears.Pngs[0]);
        Assert.Equal((GearFrames.Size, GearFrames.Size, 2), (w, h, colourType));
        // Opaque grey (R = G = B); transparent pixels came out black; the red square is lit.
        for (var i = 0; i < rgb.Length; i += 3)
        {
            Assert.True(rgb[i] == rgb[i + 1] && rgb[i] == rgb[i + 2]);
        }
        Assert.Equal(0, rgb[0]);
        var centre = ((5 * GearFrames.Size / 12 * GearFrames.Size) + (6 * GearFrames.Size / 16)) * 3;
        Assert.InRange(rgb[centre], 60, 90); // luma of pure red is 76

        // Deterministic, and the speed maps sensibly across the range: TWRP advances every
        // floor(30/fps)+1 passes, so fps 16..30 all mean 67 ms and the lowest wins a tie.
        Assert.Equal(Convert.ToHexString(gears.Pngs[1]), Convert.ToHexString(GearFrames.FromGif(TestGifs.Bytes(TestGifs.TwoFrames)).Pngs[1]));
        Assert.Equal(16, GearFrames.SpeedForDelay(33));
        Assert.Equal(11, GearFrames.SpeedForDelay(100));
        Assert.Equal(1, GearFrames.SpeedForDelay(2000));
    }

    private static (int W, int H, int ColourType, byte[] Rgb) ReadRgbPng(byte[] png)
    {
        Assert.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        int w = 0, h = 0, type = 0;
        using var idat = new MemoryStream();
        var off = 8;
        while (off + 8 <= png.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(off));
            var kind = Encoding.ASCII.GetString(png, off + 4, 4);
            var data = png.AsSpan(off + 8, len);
            if (kind == "IHDR")
            {
                w = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                h = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                type = data[9];
            }
            else if (kind == "IDAT")
            {
                idat.Write(data);
            }
            off += 12 + len;
        }
        idat.Position = 0;
        using var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var rows = raw.ToArray();
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            Assert.Equal(0, rows[y * ((w * 3) + 1)]); // filter None
            Array.Copy(rows, (y * ((w * 3) + 1)) + 1, rgb, y * w * 3, w * 3);
        }
        return (w, h, type, rgb);
    }

    // ---- embedded theme sanity (the key validate.py checks, in C#) --------

    [Fact]
    public void EmbeddedThemeIsWellFormedAndHasTheWinReContract()
    {
        var winre = ThemeText("winre.xml");
        var doc = XDocument.Parse(winre); // well-formed
        var pages = doc.Descendants("page").Select(p => p.Attribute("name")!.Value).ToHashSet();
        foreach (var required in new[]
                 {
                     "main", "lock", "winre_home", "winre_install", "winre_install_static", "winre_run", "winre_output",
                     "winre_confirm", "winre_troubleshoot", "singleaction_page", "action_page", "action_complete",
                 })
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
        // The old ORS-driven copy screen is gone; an empty compare never appears.
        Assert.DoesNotContain("winre_push", code, StringComparison.Ordinal);
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

        // Gears take their speed from the builder (procedural or the user's GIF).
        foreach (var anim in doc.Descendants("animation").Where(a => a.Element("resource")?.Attribute("name")?.Value == "winre_cogs"))
        {
            Assert.Equal("%winre_cogs_fps%", anim.Element("speed")!.Attribute("fps")!.Value);
        }

        var splash = XDocument.Parse(ThemeText("splash.xml"));
        Assert.Contains(splash.Descendants("page").Select(p => p.Attribute("name")!.Value), n => n == "splash");
        Assert.Contains("%winre_cogs_fps%", ThemeText("splash.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void InstallScreenIsLiveWithoutOpenRecoveryScript()
    {
        var doc = XDocument.Parse(ThemeText("winre.xml"));
        var body = doc.Root!.Element("templates")!.Elements("template").Single(t => t.Attribute("name")?.Value == "winre_install_body");
        XElement Page(string name) => doc.Descendants("page").Single(p => p.Attribute("name")!.Value == name);

        // Live values are Android properties, re-read by TWRP on every draw.
        Assert.Contains(body.Elements("text"), t => t.Element("text")!.Value == "%property.s9woa.label%");
        Assert.Contains(body.Descendants("data"), d => d.Attribute("name")!.Value == "property.s9woa.pct");

        // Both the screen and the ORS page draw the same body, and while it is up each shows an
        // odd number of texts (GUIText's shared refresh counter only reaches all of an odd set).
        foreach (var name in new[] { "winre_install", "singleaction_page" })
        {
            var page = Page(name);
            Assert.Contains(page.Elements("template"), t => t.Attribute("name")!.Value == "winre_install_body");
            var texts = page.Elements("text").Concat(body.Elements("text"))
                .Count(t => !t.Descendants("condition").Any(c =>
                    c.Attribute("var1")!.Value == "property.s9woa.show" && c.Attribute("op")?.Value == "!="));
            Assert.Equal(1, texts % 2);
            Assert.Contains(page.Descendants("action"), a => a.Value == "ui_progress_frames=0");
        }

        // The lock overlay never dismisses itself on load (TWRP runs load actions before the
        // push, which would leave an invisible overlay); a tap dismisses it.
        var lockPage = Page("lock");
        Assert.DoesNotContain(lockPage.Elements("action"), a => a.Element("touch") is null
            && a.Descendants("action").Any(x => x.Attribute("function")?.Value == "overlay"));
        Assert.Contains(lockPage.Elements("button"), b => b.Descendants("action").Any(x => x.Attribute("function")?.Value == "overlay"));

        // The watcher never pokes GUI variables through openrecoveryscript.
        var watcher = System.Text.Encoding.UTF8.GetString(WinReResources.Bytes("sbin.winre-statuswatch.sh"));
        var watcherCode = string.Join('\n', watcher.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
        Assert.DoesNotMatch(@"\btwrp\s+""?set\b", watcherCode);
        Assert.Contains("changepage=", watcherCode, StringComparison.Ordinal);
        foreach (var prop in new[] { "s9woa.show", "s9woa.mode", "s9woa.label", "s9woa.pct", "s9woa.detail" })
        {
            Assert.Contains($"setprop {prop} ", watcherCode, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReskinOperationsApplyExactlyOrStop()
    {
        const string ops = """
            <reskin>
              <file name="t.xml">
                <replace count="2"><find><![CDATA[#0090CA]]></find><with><![CDATA[#0078D4]]></with></replace>
                <element count="1"><start><![CDATA[<template name="page">]]></start><end><![CDATA[</template>]]></end>
                  <with><![CDATA[<template name="page"><background color="#000000"/></template>]]></with></element>
                <word count="2"><find>main2</find><with>twrp_main2</with></word>
              </file>
            </reskin>
            """;
        var input = "<a c=\"#0090CA\"/><template name=\"page\"><fill color=\"#0090CA\"/><image resource=\"logo\"/></template>"
                    + "<page name=\"main2\"/><x>main2</x><y>main2_old</y>";
        var (output, count) = WinReTheme.ApplyReskin(input, "t.xml", ops);
        Assert.Equal(3, count);
        Assert.Equal("<a c=\"#0078D4\"/><template name=\"page\"><background color=\"#000000\"/></template>"
                     + "<page name=\"twrp_main2\"/><x>twrp_main2</x><y>main2_old</y>", output);

        // A base that does not match the declared counts stops the build.
        Assert.Throws<InvalidOperationException>(() => WinReTheme.ApplyReskin(input.Replace("#0090CA", "#123456"), "t.xml", ops));

        // The shipped reskin.xml parses and has blocks for both stock files.
        var reskin = XDocument.Parse(ThemeText("reskin.xml"));
        Assert.Equal(["ui.xml", "portrait.xml"], reskin.Root!.Elements("file").Select(f => f.Attribute("name")!.Value));
        Assert.Equal(57, WinReResources.Folder("stock").Count); // one original per stock TWRP bitmap
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
        // The supervised clearers for the Samsung-logo startup gate are baked in too.
        Assert.NotNull(cpio.Get("sbin/s9woa/rwd1_clear_poc.ko"));
        Assert.NotNull(cpio.Get("sbin/s9woa/pram_smp_clear_poc.ko"));
        // The ntfs-3g FUSE deadlock breaker is baked in and registered as an init service.
        Assert.NotNull(cpio.Get("sbin/winre-ntfs-watchdog.sh"));
        // TWRP's reboot-to-system hook prepares every start of Windows from the recovery.
        Assert.Contains("winre-actions.sh prepare-boot", Encoding.UTF8.GetString(cpio.Get("sbin/rebootsystem.sh")!.Data), StringComparison.Ordinal);
        Assert.Equal(0x1FF, cpio.Get("sbin/rebootsystem.sh")!.Mode & 0x1FF);
        var serviceRc = Encoding.UTF8.GetString(cpio.Get("init.recovery.service.rc")!.Data);
        Assert.Contains("service winre_ntfswd /sbin/winre-ntfs-watchdog.sh", serviceRc, StringComparison.Ordinal);
        Assert.Contains("start winre_ntfswd", serviceRc, StringComparison.Ordinal);
        Assert.NotNull(cpio.Get("twres/fonts/winre-light.ttf"));
        Assert.Contains("setprop sys.usb.config adb", Encoding.UTF8.GetString(cpio.Get("init.recovery.usb.rc")!.Data), StringComparison.Ordinal);
        Assert.Equal($"builder={WinReTwrpBuilder.BuilderVersion}\nbase_sha256={WinReTwrpBuilder.OfficialTwrpSha256}\ngears=builtin\n",
            Encoding.UTF8.GetString(cpio.Get("twres/winre-build.txt")!.Data));
        Assert.Equal(new WinReStamp(WinReTwrpBuilder.BuilderVersion, WinReTwrpBuilder.OfficialTwrpSha256, WinReTwrpBuilder.BuiltinGears),
            WinReTwrpBuilder.ReadStamp(result.Image));
        Assert.Null(WinReTwrpBuilder.ReadStamp(baseBytes)); // the official image carries no stamp

        // Scripts land with LF endings and mode 0755.
        var script = cpio.Get("sbin/winre-actions.sh")!;
        Assert.DoesNotContain("\r\n", Encoding.UTF8.GetString(script.Data), StringComparison.Ordinal);
        Assert.Equal(0x1FF, script.Mode & 0x1FF);

        // The whole stock look is gone: every stock bitmap is our original art, the TWRP palette
        // and header template are replaced, and TWRP's own pages are handed to winre.xml.
        foreach (var (name, png) in WinReResources.Folder("stock"))
        {
            Assert.True(cpio.Get($"twres/images/{name}")!.Data.AsSpan().SequenceEqual(png), name);
        }
        var ui = Encoding.UTF8.GetString(cpio.Get("twres/ui.xml")!.Data);
        var portrait = Encoding.UTF8.GetString(cpio.Get("twres/portrait.xml")!.Data);
        foreach (var stockColour in new[] { "#0090CA", "#1A1A1A", "#EEEEEE", "#111111", "#5b5b5b", "#FF0101" })
        {
            Assert.DoesNotContain(stockColour, ui, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("resource=\"logo\"", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("RobotoCondensed", ui, StringComparison.Ordinal);
        Assert.Contains("<variable name=\"winre_cogs_fps\" value=\"24\"/>", ui, StringComparison.Ordinal);
        foreach (var owned in new[] { "main", "lock", "singleaction_page", "action_page", "action_complete" })
        {
            Assert.DoesNotContain($"<page name=\"{owned}\">", portrait, StringComparison.Ordinal);
            Assert.Contains($"<page name=\"twrp_{owned}\">", portrait, StringComparison.Ordinal);
        }
        Assert.Equal(16, cpio.Entries.Count(e => e.Name.StartsWith("twres/images/winrecog", StringComparison.Ordinal)));

        // Deterministic.
        Assert.Equal(result.Sha256, new WinReTwrpBuilder().Build(baseBytes).Sha256);

        // With a gear GIF (a synthetic one here: the UpdateOS GIF is never in the repo) its
        // frames and speed replace the procedural gears, and the marker records the GIF.
        var gif = TestGifs.Bytes(TestGifs.TwoFrames);
        var withGif = new WinReTwrpBuilder().Build(baseBytes, gearsGif: gif);
        Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(gif)).ToLowerInvariant(), withGif.Gears);
        var gifCpio = CpioArchive.Parse(LzmaAlone.Decompress(AndroidBootImage.Parse(withGif.Image).Ramdisk));
        Assert.Equal(2, gifCpio.Entries.Count(e => e.Name.StartsWith("twres/images/winrecog", StringComparison.Ordinal)));
        Assert.Contains("<variable name=\"winre_cogs_fps\" value=\"3\"/>",
            Encoding.UTF8.GetString(gifCpio.Get("twres/ui.xml")!.Data), StringComparison.Ordinal);
        Assert.Contains("<variable name=\"winre_cogs_fps\" value=\"3\"/>",
            Encoding.UTF8.GetString(gifCpio.Get("twres/splash.xml")!.Data), StringComparison.Ordinal);
        Assert.EndsWith($"gears={withGif.Gears}\n", Encoding.UTF8.GetString(gifCpio.Get("twres/winre-build.txt")!.Data), StringComparison.Ordinal);
        Assert.True(withGif.Bytes <= AndroidBootImage.RecoveryPartitionBytes);

        // The redistributable build: the repository's open fonts (and their license) instead of
        // Segoe UI, recorded in the stamp.
        var openFonts = RepoFontsDirectory();
        var open = new WinReTwrpBuilder().Build(baseBytes, fontsDirectory: openFonts);
        var openCpio = CpioArchive.Parse(LzmaAlone.Decompress(AndroidBootImage.Parse(open.Image).Ramdisk));
        foreach (var face in new[] { "winre-light.ttf", "winre-semilight.ttf", "winre-regular.ttf" })
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(openFonts, face)), openCpio.Get($"twres/fonts/{face}")!.Data);
        }
        Assert.Equal(File.ReadAllBytes(Path.Combine(openFonts, "OFL.txt")), openCpio.Get("twres/fonts/winre-OFL.txt")!.Data);
        Assert.Equal(new WinReStamp(WinReTwrpBuilder.BuilderVersion, WinReTwrpBuilder.OfficialTwrpSha256, WinReTwrpBuilder.BuiltinGears, "open"),
            WinReTwrpBuilder.ReadStamp(open.Image));
    }

    /// <summary>tools/twrp-winre/fonts, found by walking up from the test binaries to the repository.</summary>
    private static string RepoFontsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var fonts = Path.Combine(dir.FullName, "tools", "twrp-winre", "fonts");
            if (File.Exists(Path.Combine(fonts, "winre-regular.ttf")))
            {
                return fonts;
            }
        }
        throw new DirectoryNotFoundException("tools/twrp-winre/fonts not found above the test binaries.");
    }

    [Fact]
    public void FontsComeFromWindowsOrFromAFolderOfOpenFonts()
    {
        var root = Directory.CreateTempSubdirectory("s9woa-fonts-").FullName;
        try
        {
            var windows = Directory.CreateDirectory(Path.Combine(root, "windows")).FullName;
            foreach (var f in new[] { "segoeuil.ttf", "segoeuisl.ttf", "segoeui.ttf" })
            {
                File.WriteAllBytes(Path.Combine(windows, f), [1]);
            }
            var (segoe, segoeOpen) = WinReTwrpBuilder.ResolveFonts(windows);
            Assert.False(segoeOpen);
            Assert.Equal(Path.Combine(windows, "segoeuisl.ttf"), segoe.Single(f => f.Dest == "winre-semilight.ttf").Source);

            var (open, isOpen) = WinReTwrpBuilder.ResolveFonts(RepoFontsDirectory());
            Assert.True(isOpen);
            Assert.Equal(["winre-light.ttf", "winre-semilight.ttf", "winre-regular.ttf"], open.Select(f => f.Dest));

            // Neither complete set: the Segoe message, naming the first missing face.
            File.Delete(Path.Combine(windows, "segoeuisl.ttf"));
            var e = Assert.Throws<InvalidOperationException>(() => WinReTwrpBuilder.ResolveFonts(windows));
            Assert.Contains("segoeuisl.ttf", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
