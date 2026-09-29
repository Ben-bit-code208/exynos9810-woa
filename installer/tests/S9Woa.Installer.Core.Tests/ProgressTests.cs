// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Image;
using S9Woa.Installer.Core.Stages;

namespace S9Woa.Installer.Core.Tests;

public class ProgressTests
{
    [Fact]
    public void ReadsCopyExportFlashAndBackupCounts()
    {
        var p = new StageProgress();
        p.Begin("transfer");
        Assert.Null(p.Fraction);
        Assert.True(p.Observe("20:00:17   USERDATA: 3.2 of 8.8 GiB written and verified, 36 MB/s, about 3 min left"));
        Assert.Equal(3.2 / 8.8, p.Fraction!.Value, 3);
        Assert.StartsWith("USERDATA: 3.2 of 8.8 GiB", p.Detail, StringComparison.Ordinal);

        p.Begin("twrp");
        p.Observe("  30%");
        Assert.Equal(0.3, p.Fraction!.Value, 3);

        p.Begin("backup");
        p.Observe("Backing up EFS (1 of 4)...");
        Assert.Equal(0, p.Fraction!.Value, 3);
        p.Observe("Backing up PARAM (3 of 4)...");
        Assert.Equal(0.5, p.Fraction!.Value, 3);

        // A stage-start line is not a detail.
        Assert.False(p.Observe("20:00:00 stage uefi: Install UEFI"));
    }

    [Fact]
    public void ImageBuildMovesByPhaseAndNeverBackwards()
    {
        var p = new StageProgress();
        p.Begin("image");
        p.Observe("19:48:47 Applying Windows image (index 2). This takes a while...");
        Assert.Equal(0.02, p.Fraction!.Value, 3);
        p.Observe("Removing package Microsoft-Windows-InternetExplorer-Optional-Package");
        Assert.Equal(0.50, p.Fraction!.Value, 3);
        p.Observe("Delete DefaultUser\\Software\\Something");     // registry deletes don't move it
        p.Observe("Adding driver Exynos9810Ufs");                  // an earlier phase can't pull it back
        Assert.Equal(0.50, p.Fraction!.Value, 3);
        Assert.Equal("Adding driver Exynos9810Ufs", p.Detail);
        p.Observe("  exported 27182/54364 MiB");
        Assert.Equal(0.955, p.Fraction!.Value, 3);
        Assert.Equal("Exporting the Windows volume: 50%", p.Detail);

        // Other stages ignore image phases.
        p.Begin("transfer");
        p.Observe("Applying Windows image");
        Assert.Null(p.Fraction);
    }

    [Fact]
    public void OverallWeighsLongStages()
    {
        string[] ids = ["host", "image", "transfer"];
        Assert.Equal(0, StageProgress.Overall(ids.Select(i => (i, false)), null, null));
        var halfImage = StageProgress.Overall(ids.Select(i => (i, i == "host")), "image", 0.5);
        Assert.Equal((1 + 20.0) / 66, halfImage, 3);
        Assert.Equal(1, StageProgress.Overall(ids.Select(i => (i, true)), null, null));
    }

    [Theory]
    [InlineData("E 1\r\n", 'E', true)]
    [InlineData("warning\nf 0\n", 'F', false)]
    [InlineData("\r\n", null, false)]
    [InlineData("Error text", null, false)]
    public void ParsesTheIsoMountResult(string stdout, char? letter, bool mine)
    {
        var r = WindowsMedia.ParseMount(stdout);
        Assert.Equal(letter, r?.Letter);
        if (r is { } v)
        {
            Assert.Equal(mine, v.AttachedHere);
        }
        Assert.Contains("Dismount-DiskImage -ImagePath 'D:\\a''b.iso'", WindowsMedia.DismountIsoScript(@"D:\a'b.iso"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopiesTheInstallImageWithProgressAndReusesAFinishedCopy()
    {
        var dir = Directory.CreateTempSubdirectory("s9woa-extract").FullName;
        try
        {
            var source = Path.Combine(dir, "install.wim");
            var data = new byte[9 << 20];
            new Random(1).NextBytes(data);
            await File.WriteAllBytesAsync(source, data);
            var target = Path.Combine(dir, "out", "install.wim");
            var seen = new List<double>();
            await WindowsMedia.CopyWithProgressAsync(source, target, new SyncProgress(seen.Add), CancellationToken.None);
            Assert.Equal(data, await File.ReadAllBytesAsync(target));
            Assert.False(File.Exists(target + ".partial"));
            Assert.Equal(1, seen[^1]);
            Assert.True(seen.Count >= 3);

            seen.Clear();
            await WindowsMedia.CopyWithProgressAsync(source, target, new SyncProgress(seen.Add), CancellationToken.None);
            Assert.Equal([1.0], seen);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
