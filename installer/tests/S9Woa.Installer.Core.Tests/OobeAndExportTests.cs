// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Security.Cryptography;
using System.Xml.Linq;
using S9Woa.Installer.Core.Deploy;
using S9Woa.Installer.Core.Image;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Tests;

public class OobeAndExportTests
{
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";

    [Fact]
    public void UnattendCreatesLocalAdminAndSkipsOobe()
    {
        var xml = UnattendXml.Build(new UnattendOptions { Username = "Alex", Locale = "en-GB", TimeZone = "GMT Standard Time" });
        var doc = XDocument.Parse(xml);

        var settings = doc.Root!.Elements(Ns + "settings").Single();
        Assert.Equal("oobeSystem", settings.Attribute("pass")!.Value);

        var shell = settings.Elements(Ns + "component").Single(c => c.Attribute("name")!.Value == "Microsoft-Windows-Shell-Setup");
        Assert.All(settings.Elements(Ns + "component").Attributes("processorArchitecture"), a => Assert.Equal("arm64", a.Value));

        var account = shell.Descendants(Ns + "LocalAccount").Single();
        Assert.Equal("Alex", account.Element(Ns + "Name")!.Value);
        Assert.Equal("Administrators", account.Element(Ns + "Group")!.Value);

        var oobe = shell.Element(Ns + "OOBE")!;
        Assert.Equal("true", oobe.Element(Ns + "HideOnlineAccountScreens")!.Value);
        Assert.Equal("true", oobe.Element(Ns + "HideEULAPage")!.Value);

        var intl = settings.Elements(Ns + "component").Single(c => c.Attribute("name")!.Value == "Microsoft-Windows-International-Core");
        Assert.Equal("en-GB", intl.Element(Ns + "UILanguage")!.Value);
        Assert.Equal(@"Windows\Panther\unattend.xml", UnattendXml.RelativePath);
    }

    [Fact]
    public void UnattendRejectsEmptyUsername() =>
        Assert.Throws<ArgumentException>(() => UnattendXml.Build(new UnattendOptions { Username = " " }));

    /// <summary>An in-memory disk source over a fixed byte buffer.</summary>
    private sealed class MemoryDiskSource : IRawDiskSource, IDisposable
    {
        private readonly byte[] _data;
        public MemoryDiskSource(byte[] data) => _data = data;
        public long Length => _data.Length;
        public Stream OpenRead(long offset) => new MemoryStream(_data, (int)offset, _data.Length - (int)offset, writable: false);
        public void Dispose() { }
    }

    [Fact]
    public async Task ExporterCopiesExtentAndPadsToMib()
    {
        var disk = new byte[3 * 1024 * 1024 + 500];
        new Random(11).NextBytes(disk);
        var dst = Path.GetTempFileName();
        try
        {
            var extent = new PartitionExtent(1024 * 1024, 2 * 1024 * 1024 + 100);
            var (bytes, sha) = await new RawImageExporter().ExportAsync(new MemoryDiskSource(disk), extent, dst);

            Assert.Equal(3 * 1024 * 1024, bytes); // rounded up to whole MiB
            var written = await File.ReadAllBytesAsync(dst);
            Assert.Equal(bytes, written.Length);
            Assert.Equal(disk.AsSpan((int)extent.Offset, (int)extent.Length).ToArray(), written.AsSpan(0, (int)extent.Length).ToArray());
            Assert.All(written.AsSpan((int)extent.Length).ToArray(), b => Assert.Equal(0, b));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(written)).ToLowerInvariant(), sha);
        }
        finally
        {
            File.Delete(dst);
        }
    }

    [Fact]
    public async Task ExporterRejectsOutOfRangeExtent()
    {
        var dst = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                new RawImageExporter().ExportAsync(new MemoryDiskSource(new byte[1024]), new PartitionExtent(0, 4096), dst));
        }
        finally
        {
            File.Delete(dst);
        }
    }

    [Theory]
    [InlineData(1, 1048576)]
    [InlineData(1048576, 1048576)]
    [InlineData(1048577, 2097152)]
    public void RoundsUpToMib(long input, long expected) => Assert.Equal(expected, RawImageExporter.RoundUpToMib(input));

    [Fact]
    public void BcdRetargetUsesLocateDevice()
    {
        var cmds = BootConfiguration.RetargetCommands(@"S:\EFI\Microsoft\Boot\BCD");
        var flat = cmds.Select(c => string.Join(' ', c)).ToList();
        Assert.Contains(flat, c => c.Contains("{default} device locate=\\Windows", StringComparison.Ordinal));
        Assert.Contains(flat, c => c.Contains("{default} osdevice locate=\\Windows", StringComparison.Ordinal));
        Assert.Contains(flat, c => c.Contains("path \\Windows\\System32\\winload.efi", StringComparison.Ordinal));
        Assert.Contains(flat, c => c.EndsWith("{default} testsigning on", StringComparison.Ordinal));
        Assert.Contains(flat, c => c.EndsWith("{default} numproc 4", StringComparison.Ordinal));
        Assert.Contains(flat, c => c.EndsWith("{default} vsmlaunchtype off", StringComparison.Ordinal));
        Assert.Contains(flat, c => c.EndsWith("{default} hypervisorlaunchtype off", StringComparison.Ordinal));
        Assert.Contains(flat, c => c.EndsWith("{default} bootstatuspolicy IgnoreAllFailures", StringComparison.Ordinal));
        Assert.All(flat, c => Assert.Contains(@"/store S:\EFI\Microsoft\Boot\BCD", c, StringComparison.Ordinal));
    }
}
