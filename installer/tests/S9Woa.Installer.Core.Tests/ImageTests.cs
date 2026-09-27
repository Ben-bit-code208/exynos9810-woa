// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Image;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Tests;

public class ImageTests
{
    [Fact]
    public void NoneProfileIsEmpty() => Assert.Empty(SlimPlan.For(SlimProfile.None));

    [Fact]
    public void LiteKeepsImageServiceable()
    {
        var plan = SlimPlan.For(SlimProfile.Lite);
        Assert.Contains(plan, o => o is RemoveProvisionedAppx);
        Assert.DoesNotContain(plan, o => o is ComponentCleanup { ResetBase: true });
        Assert.DoesNotContain(plan, o => o is DeleteImageFile or RemoveCapability);
        Assert.IsType<ComponentCleanup>(plan[^1]);
    }

    [Fact]
    public void CoreEndsWithResetBase()
    {
        var plan = SlimPlan.For(SlimProfile.Core);
        Assert.Equal(new ComponentCleanup(ResetBase: true), plan[^1]);
        Assert.Contains(plan, o => o is DeleteImageFile d && d.RelativePath.EndsWith("winre.wim", StringComparison.Ordinal));
        Assert.All(plan.OfType<DeleteImageFile>(), d => Assert.False(SlimPlan.IsProtectedPath(d.RelativePath)));
    }

    [Theory]
    [InlineData(@"Windows\System32\ntoskrnl.exe")]
    [InlineData(@"windows\system32\DRIVERS\Exynos9810Ufs.sys")]
    [InlineData(@"Windows\System32\DriverStore\FileRepository\x")]
    [InlineData(@"EFI\Microsoft\Boot\bootmgfw.efi")]
    [InlineData(@"Windows\..\bootmgr")]
    [InlineData(@"C:\Windows\notepad.exe")]
    public void ProtectsBootCriticalPaths(string path) => Assert.True(SlimPlan.IsProtectedPath(path));

    [Fact]
    public void ParsesDismListings()
    {
        var pkgs = ImageServicer.ParseProvisionedPackages(
            "Displayname : Microsoft.BingNews\r\nVersion : 1\r\nPackageName : Microsoft.BingNews_4.1.0.0_neutral_~_8wekyb3d8bbwe\r\n\r\n" +
            "PackageName : Microsoft.WindowsTerminal_1.0_neutral_~_8wekyb3d8bbwe\r\n");
        Assert.Equal(["Microsoft.BingNews_4.1.0.0_neutral_~_8wekyb3d8bbwe", "Microsoft.WindowsTerminal_1.0_neutral_~_8wekyb3d8bbwe"], pkgs);

        var caps = ImageServicer.ParseInstalledCapabilities(
            "Capability Identity : Language.OCR~~~en-US~0.0.1.0\r\nState : Installed\r\n\r\n" +
            "Capability Identity : Media.WindowsMediaPlayer~~~~0.0.12.0\r\nState : Not Present\r\n");
        Assert.Equal(["Language.OCR~~~en-US~0.0.1.0"], caps);
    }

    [Fact]
    public void BuildsRegAddArguments()
    {
        var args = ImageServicer.RegAddArguments(new SetRegistryValue(RegistryHive.DefaultUser, @"Software\X", "Y", RegistryValueKind.DWord, "0"));
        Assert.Equal(@"add HKLM\S9WOA_DEFAULT\Software\X /v Y /t REG_DWORD /d 0 /f", string.Join(' ', args));
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"{Path.GetFileName(fileName)} {string.Join(' ', arguments)}");
            var stdout = arguments.Contains("/Get-ProvisionedAppxPackages")
                ? "PackageName : Microsoft.BingNews_1_neutral_~_x\r\nPackageName : Microsoft.WindowsCalculator_1_neutral_~_x\r\n"
                : "";
            return Task.FromResult(new ProcessResult(0, stdout, ""));
        }
    }

    [Fact]
    public async Task ServicerRemovesOnlyMatchingAppsAndUnloadsHives()
    {
        var root = Directory.CreateTempSubdirectory("s9woa-img").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, @"Windows\System32"));
            File.WriteAllText(Path.Combine(root, @"Windows\System32\ntoskrnl.exe"), "");
            var runner = new FakeRunner();
            await new ImageServicer(runner, @"C:\sys").ApplyAsync(root, SlimPlan.For(SlimProfile.Lite));

            Assert.Contains(runner.Calls, c => c.Contains("/PackageName:Microsoft.BingNews_1_neutral_~_x", StringComparison.Ordinal));
            Assert.DoesNotContain(runner.Calls, c => c.Contains("WindowsCalculator", StringComparison.Ordinal) && c.Contains("/Remove", StringComparison.Ordinal));
            var loads = runner.Calls.Count(c => c.StartsWith("reg.exe load", StringComparison.Ordinal));
            var unloads = runner.Calls.Count(c => c.StartsWith("reg.exe unload", StringComparison.Ordinal));
            Assert.Equal(2, loads);
            Assert.Equal(loads, unloads);
            var lastUnload = runner.Calls.FindLastIndex(c => c.StartsWith("reg.exe unload", StringComparison.Ordinal));
            var cleanup = runner.Calls.FindIndex(c => c.Contains("/StartComponentCleanup", StringComparison.Ordinal));
            Assert.True(lastUnload < cleanup, "hives must be unloaded before component cleanup");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ServicerRejectsNonWindowsDirectory()
    {
        var root = Directory.CreateTempSubdirectory("s9woa-img").FullName;
        try
        {
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
                new ImageServicer(new FakeRunner(), @"C:\sys").ApplyAsync(root, SlimPlan.For(SlimProfile.Lite)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
