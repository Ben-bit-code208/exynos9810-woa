// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Image;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Tests;

public class ImageBuilderTests
{
    private sealed class RecordingRunner : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public Func<IReadOnlyList<string>, string>? StdOut { get; init; }
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"{Path.GetFileName(fileName)} {string.Join(' ', arguments)}");
            return Task.FromResult(new ProcessResult(0, StdOut?.Invoke(arguments) ?? "", ""));
        }
    }

    [Fact]
    public void ParsesEditionsAndChooses()
    {
        const string output =
            "Index : 1\r\nName : Windows 11 Home\r\nDescription : Windows 11 Home\r\nSize : 1\r\n\r\n" +
            "Index : 6\r\nName : Windows 11 Pro\r\nDescription : Windows 11 Pro\r\nSize : 1\r\n";
        var editions = WindowsMedia.ParseImageInfo(output);
        Assert.Equal(2, editions.Count);
        Assert.Equal(6, editions[1].Index);
        Assert.Equal("Windows 11 Pro", editions[1].Name);
        Assert.Equal(6, WindowsMedia.ChooseEdition(editions).Index);
        Assert.Equal(1, WindowsMedia.ChooseEdition([editions[0]]).Index);
        Assert.Throws<InvalidOperationException>(() => WindowsMedia.ChooseEdition([]));
    }

    [Fact]
    public void FindsInstallImage()
    {
        var dir = Directory.CreateTempSubdirectory("s9woa-media").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sources"));
            var wim = Path.Combine(dir, "sources", "install.wim");
            File.WriteAllText(wim, "");
            Assert.Equal(wim, WindowsMedia.FindInstallImage(dir));
            Assert.Equal(wim, WindowsMedia.FindInstallImage(wim));
            Assert.Null(WindowsMedia.FindInstallImage(Path.Combine(dir, "nope.wim")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DiscoversDriverFolders()
    {
        var dir = Directory.CreateTempSubdirectory("s9woa-drv").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Ufs"));
            Directory.CreateDirectory(Path.Combine(dir, "Touch"));
            File.WriteAllText(Path.Combine(dir, "Ufs", "Exynos9810Ufs.inf"), "");
            File.WriteAllText(Path.Combine(dir, "Touch", "S6SY761Touch.inf"), "");
            var found = ImageBuilder.DiscoverDrivers(dir);
            Assert.Equal(2, found.Count);
            Assert.Contains(found, p => p.Name == "Ufs");
            Assert.Empty(ImageBuilder.DiscoverDrivers(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAppliesInjectsThenSlims()
    {
        var apply = Directory.CreateTempSubdirectory("s9woa-apply").FullName;
        try
        {
            var runner = new RecordingRunner();
            var drivers = new[] { new DriverPackage("Ufs", @"C:\d\Ufs"), new DriverPackage("Touch", @"C:\d\Touch") };
            await new ImageBuilder(runner, @"C:\sys").BuildAsync(apply, @"C:\m\install.wim", 6, drivers, SlimProfile.None);

            var applyIdx = runner.Calls.FindIndex(c => c.Contains("/Apply-Image", StringComparison.Ordinal));
            var ufsIdx = runner.Calls.FindIndex(c => c.Contains("/Add-Driver", StringComparison.Ordinal) && c.Contains(@"C:\d\Ufs", StringComparison.Ordinal));
            Assert.True(applyIdx >= 0 && ufsIdx > applyIdx);
            Assert.Contains(runner.Calls, c => c.Contains("/Add-Driver", StringComparison.Ordinal) && c.Contains("/ForceUnsigned", StringComparison.Ordinal));
            Assert.Contains(runner.Calls, c => c.Contains(@"/ImageFile:C:\m\install.wim", StringComparison.Ordinal) && c.Contains("/Index:6", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(apply, recursive: true);
        }
    }

    [Fact]
    public void VhdxScriptCreatesEspMsrAndNtfs()
    {
        var builder = new VhdxImageBuilder(new RecordingRunner(), @"C:\sys");
        var script = builder.CreateScript(@"C:\img\s9.vhdx", 60000);
        Assert.Contains("create vdisk file=\"C:\\img\\s9.vhdx\" maximum=60000", script, StringComparison.Ordinal);
        Assert.Contains("convert gpt", script, StringComparison.Ordinal);
        Assert.Contains("create partition efi size=260", script, StringComparison.Ordinal);
        Assert.Contains("format fs=fat32 quick label=System", script, StringComparison.Ordinal);
        Assert.Contains("create partition msr size=16", script, StringComparison.Ordinal);
        Assert.Contains("format fs=ntfs quick label=Windows", script, StringComparison.Ordinal);
        Assert.Contains("assign letter=S", script, StringComparison.Ordinal);
        Assert.Contains("assign letter=W", script, StringComparison.Ordinal);
        Assert.Contains("detach vdisk", builder.DetachScript(@"C:\img\s9.vhdx"), StringComparison.Ordinal);
    }
}
