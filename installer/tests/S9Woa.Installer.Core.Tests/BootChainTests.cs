// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using S9Woa.Installer.Core.Deploy;
using S9Woa.Installer.Core.Image;

namespace S9Woa.Installer.Core.Tests;

public class BootChainTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("s9woa-boot").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteCatalog(string dir, params (string File, string Windows, string[] Media, string Loader, string Kernel)[] images)
    {
        Directory.CreateDirectory(dir);
        var entries = new List<object>();
        foreach (var (file, windows, media, loader, kernel) in images)
        {
            var bytes = new byte[4096];
            new Random(file.Length).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(dir, file), bytes);
            entries.Add(new { file, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), windows, mediaBuilds = media, loaderSha256 = loader, kernelSha256 = kernel });
        }
        File.WriteAllText(Path.Combine(dir, FirmwareCatalog.FileName),
            JsonSerializer.Serialize(new { schema = FirmwareCatalog.Schema, images = entries }));
        return dir;
    }

    [Fact]
    public void FirmwareIsChosenByTheExactLoaderAndKernel()
    {
        var dir = WriteCatalog(Path.Combine(_root, "uefi"),
            ("a.img", "22621.2428", ["22631.2428"], "aa", "ka"),
            ("b.img", "22621.7582", ["22631.7584"], "bb", "kb"));
        var catalog = FirmwareCatalog.Load(dir)!;

        Assert.Equal("a.img", catalog.ForBootFiles("AA", "KA")!.File);
        Assert.Null(catalog.ForBootFiles("aa", "kb"));
        Assert.Equal("b.img", catalog.ForMediaBuild("22631.7584")!.File);
        Assert.Equal("a.img", catalog.ForMediaBuild("22621.2428")!.File);
        Assert.Null(catalog.ForMediaBuild("22631.7633"));
        Assert.Equal("22621.2428, 22621.7582", catalog.SupportedBuilds);
        Assert.All(catalog.Images, i => Assert.True(catalog.Verify(i)));

        var copy = catalog.CopyTo(Path.Combine(_root, "copy"));
        Assert.Equal(2, copy.Present(1 << 20).Count);
    }

    [Fact]
    public void CatalogRefusesTamperedImagesAndEscapes()
    {
        var dir = WriteCatalog(Path.Combine(_root, "t"), ("a.img", "22621.2428", [], "aa", "ka"));
        File.WriteAllBytes(Path.Combine(dir, "a.img"), new byte[4096]);
        var catalog = FirmwareCatalog.Load(dir)!;
        Assert.False(catalog.Verify(catalog.Images[0]));
        Assert.Throws<InvalidDataException>(() => catalog.CopyTo(Path.Combine(_root, "out")));

        File.WriteAllText(Path.Combine(dir, FirmwareCatalog.FileName), JsonSerializer.Serialize(new
        {
            schema = FirmwareCatalog.Schema,
            images = new[] { new { file = @"..\evil.img", sha256 = "", windows = "", loaderSha256 = "", kernelSha256 = "" } },
        }));
        Assert.Throws<InvalidDataException>(() => FirmwareCatalog.Load(dir));
    }

    [Theory]
    [InlineData("Deployment Image Servicing and Management tool\r\nVersion: 10.0.26100.28089\r\n\r\nDetails for image\r\nIndex : 2\r\nName : Windows 11 IoT Enterprise\r\nArchitecture : arm64\r\nVersion : 10.0.22621\r\nServicePack Build : 2428\r\n", "22621.2428")]
    [InlineData("Version: 10.0.26100.1\r\nVersion : 10.0.22631\r\nServicePack Build : 7633\r\n", "22631.7633")]
    [InlineData("Version: 10.0.26100.1\r\n", null)]
    public void ReadsTheMediaBuild(string dism, string? expected) => Assert.Equal(expected, WindowsMedia.ParseBuild(dism));

    private static byte[] Record(uint state, uint owner, uint phase, uint reason, bool corrupt = false)
    {
        var w = new uint[16];
        w[0] = RecoveryRecord.Magic;
        w[1] = (64u << 16) | 1;
        w[2] = 0x044eec33;
        w[3] = ~w[2];
        w[4] = state;
        w[5] = owner;
        w[6] = phase;
        w[7] = reason;
        w[8] = 0x20000000;
        w[9] = 0x20;
        w[11] = 0xFFFFFFFF;
        var ck = 0xA5A55A5Au;
        for (var i = 0; i < 12; i++)
        {
            ck ^= w[i];
        }
        w[12] = corrupt ? ck ^ 1 : ck;
        w[13] = ~w[12];
        var b = new byte[64];
        for (var i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), w[i]);
        }
        return b;
    }

    [Fact]
    public void DecodesTheWatchdogRecordCapturedOnThePhone()
    {
        // The record read on the phone after the first failed start (2026-09-29).
        var r = RecoveryRecord.Parse(Record(0xA0, 2, 0x0E, 2))!;
        Assert.True(r.ChecksumOk);
        Assert.True(r.IsRecoveryPending);
        Assert.Equal("recovery pending (phase: recovery route; reason: stale boot owner)", r.ToString());

        Assert.False(RecoveryRecord.Parse(Record(0xA0, 2, 0x0E, 2, corrupt: true))!.ChecksumOk);
        Assert.Null(RecoveryRecord.Parse(Enumerable.Repeat((byte)0xFF, 64).ToArray()));
        Assert.Null(RecoveryRecord.Parse(new byte[64]));
    }

    [Theory]
    [InlineData("rwd1_ack status=0 empty=0 valid=1 cleared=1 state_before=0x000000a0 reason=0x00000002 generation=0x044eec33\n", 0, true, 0xA0u)]
    [InlineData("rwd1_ack status=-1 empty=0 valid=1 cleared=0 state_before=0x00000090 reason=0x00000009", -1, false, 0x90u)]
    [InlineData("rwd1_ack status=-117 empty=1 valid=0 cleared=0 state=00000000", -117, false, 0u)]
    public void ReadsTheAckModuleResult(string text, int status, bool cleared, uint before)
    {
        var ack = RecoveryAck.Parse(text)!;
        Assert.Equal(status, ack.Status);
        Assert.Equal(cleared, ack.Cleared);
        Assert.Equal(before, ack.StateBefore);
    }

    [Fact]
    public void BootRecoveryMessageIsAndroidsBootloaderMessage()
    {
        var bcb = BootRouteService.BootRecoveryMessage();
        Assert.Equal(4096, bcb.Length);
        Assert.Equal("boot-recovery", System.Text.Encoding.ASCII.GetString(bcb, 0, 13));
        Assert.Equal(0, bcb[13]);
        Assert.Equal("recovery\n", System.Text.Encoding.ASCII.GetString(bcb, 64, 9));
        Assert.All(bcb.Skip(73), b => Assert.Equal(0, b));
    }
}
