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
        Assert.DoesNotContain(plan, o => o is DeleteImagePath or RemovePackage or TrimComponentStore);
        Assert.IsType<ComponentCleanup>(plan[^1]);
    }

    [Fact]
    public void CoreEndsWithResetBase()
    {
        var plan = SlimPlan.For(SlimProfile.Core);
        Assert.Equal(new ComponentCleanup(ResetBase: true, Optional: true), plan[^1]);
        Assert.Contains(plan, o => o is DeleteImagePath d && d.RelativePath.EndsWith("winre.wim", StringComparison.Ordinal));
        Assert.All(plan.OfType<DeleteImagePath>(), d => Assert.False(SlimPlan.IsProtectedPath(d.RelativePath)));
    }

    [Fact]
    public void CoreFollowsTiny11Coremaker()
    {
        var plan = SlimPlan.For(SlimProfile.Core);
        Assert.Contains(new RemovePackage("Windows-Defender-Client-Package~"), plan);
        Assert.Contains(new RemoveProvisionedAppx("Microsoft.Windows.Copilot"), plan);
        Assert.Contains(plan, o => o is DeleteImagePath { RelativePath: @"Program Files (x86)\Microsoft\Edge", TakeOwnership: true });
        Assert.Contains(plan, o => o is DeleteImagePath { RelativePath: @"Windows\System32\Microsoft-Edge-Webview" });
        Assert.Contains(plan, o => o is SetRegistryValue { Name: "WUServer", Value: "localhost" });
        Assert.Contains(new DeleteRegistryKey(RegistryHive.System, @"ControlSet001\Services\UsoSvc"), plan);
        Assert.All(plan.OfType<SetRegistryValue>().Where(r => r.Key.StartsWith(@"ControlSet001\Services\", StringComparison.Ordinal)),
            r => Assert.True(r.OnlyIfKeyExists, $"{r.Key} would create a bare service key"));
        Assert.Contains(plan, o => o is SetRegistryValue { Name: "ConfigureStartPins", Kind: RegistryValueKind.String });

        // DISM package work first, then the store trim, then /ResetBase last.
        var ops = plan.ToList();
        var trim = ops.FindIndex(o => o is TrimComponentStore);
        Assert.True(trim > ops.FindLastIndex(o => o is RemovePackage or RemoveProvisionedAppx));
        Assert.True(trim < ops.Count - 1);
    }

    [Theory]
    [InlineData("Windows-Defender-Client-Package~31bf3856ad364e35~arm64~~10.0.22621.7633", true)]
    [InlineData("Microsoft-Windows-WordPad-FoD-Package~31bf3856ad364e35~arm64.arm~~10.0.22621.7633", false)]
    [InlineData("Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~arm64~en-US~10.0.22621.7079", false)]
    public void RecognisesMainPackages(string identity, bool main) => Assert.Equal(main, ImageServicer.IsMainPackage(identity));

    [Fact]
    public void ComponentStoreTrimKeepsTheServicingStackOrRefuses()
    {
        var keep = SlimPlan.For(SlimProfile.Core).OfType<TrimComponentStore>().Single().Keep;
        var survivors = ImageServicer.ComponentStoreSurvivors(
            ["Manifests", "Catalogs", "arm64_microsoft-windows-servicingstack_31bf3856ad364e35_10.0.22621.7635_none_1",
             "arm64_microsoft-windows-notepad_31bf3856ad364e35_10.0.22621.1_none_2",
             "amd64_microsoft.windows.common-controls_6595b64144ccf1df_6.0.22621.1_none_3"], keep);
        Assert.NotNull(survivors);
        Assert.DoesNotContain(survivors, n => n.Contains("notepad", StringComparison.Ordinal));
        Assert.Contains(survivors, n => n.StartsWith("amd64_microsoft.windows.common-controls", StringComparison.Ordinal));

        Assert.Null(ImageServicer.ComponentStoreSurvivors(["Manifests", "arm64_microsoft-windows-notepad_x"], keep));
        Assert.Null(ImageServicer.ComponentStoreSurvivors(["arm64_microsoft-windows-servicingstack_31bf3856ad364e35_1"], keep));
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

        var packages = ImageServicer.ParseInstalledPackages(
            "Package Identity : Windows-Defender-Client-Package~31bf3856ad364e35~arm64~~10.0.1\r\nState : Installed\r\nRelease Type : OnDemand Pack\r\n\r\n" +
            "Package Identity : Package_for_RollupFix~31bf3856ad364e35~arm64~~22621.7633.1.1\r\nState : Superseded\r\n");
        Assert.Equal(["Windows-Defender-Client-Package~31bf3856ad364e35~arm64~~10.0.1"], packages);
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
            if (Path.GetFileName(fileName) == "cmd.exe" && arguments.Contains("rd"))
            {
                Directory.Delete(arguments[^1], recursive: true);
            }
            var stdout = arguments.Contains("/Get-ProvisionedAppxPackages")
                ? "PackageName : Microsoft.BingNews_1_neutral_~_x\r\nPackageName : Microsoft.WindowsCalculator_1_neutral_~_x\r\n"
                : arguments.Contains("/Get-Packages")
                    ? "Package Identity : Windows-Defender-Client-Package~31bf3856ad364e35~arm64~~10.0.1\r\nState : Installed\r\n"
                    : "";
            // Service keys are absent in the fake image.
            var exit = arguments.Count > 0 && arguments[0] == "query" ? 1 : 0;
            return Task.FromResult(new ProcessResult(exit, stdout, ""));
        }
    }

    /// <summary>Every DISM call must happen with no image hive loaded (else error 32 reading SOFTWARE).</summary>
    private static void AssertNoDismWhileHivesLoaded(List<string> calls)
    {
        var loaded = new HashSet<string>();
        foreach (var c in calls)
        {
            var parts = c.Split(' ');
            if (c.StartsWith("reg.exe load", StringComparison.Ordinal))
            {
                loaded.Add(parts[2]);
            }
            else if (c.StartsWith("reg.exe unload", StringComparison.Ordinal))
            {
                loaded.Remove(parts[2]);
            }
            else if (c.StartsWith("dism.exe", StringComparison.Ordinal))
            {
                Assert.True(loaded.Count == 0, $"DISM ran with {string.Join(", ", loaded)} loaded: {c}");
            }
        }
        Assert.Empty(loaded);
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
            Assert.Equal(2, runner.Calls.Count(c => c.StartsWith("reg.exe load", StringComparison.Ordinal)));
            AssertNoDismWhileHivesLoaded(runner.Calls);
            Assert.Contains(runner.Calls, c => c.Contains("/StartComponentCleanup", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CoreRunsPackagesTrimAndCleanupWithoutHiveLocks()
    {
        var root = Directory.CreateTempSubdirectory("s9woa-img").FullName;
        try
        {
            var sxs = Path.Combine(root, @"Windows\WinSxS");
            foreach (var d in new[] { "Manifests", "arm64_microsoft-windows-servicingstack_31bf3856ad364e35_10.0.1_none_1", "arm64_microsoft-windows-notepad_31bf3856ad364e35_10.0.1_none_2" })
            {
                Directory.CreateDirectory(Path.Combine(sxs, d));
                File.WriteAllText(Path.Combine(sxs, d, "f"), "x");
            }
            Directory.CreateDirectory(Path.Combine(root, @"Windows\System32\Recovery"));
            Directory.CreateDirectory(Path.Combine(root, @"Program Files (x86)\Microsoft\Edge\Application"));
            File.WriteAllText(Path.Combine(root, @"Windows\System32\ntoskrnl.exe"), "");
            File.WriteAllText(Path.Combine(root, @"Windows\System32\Recovery\winre.wim"), "x");
            var runner = new FakeRunner();
            var log = new List<string>();
            await new ImageServicer(runner, @"C:\sys").ApplyAsync(root, SlimPlan.For(SlimProfile.Core), new SyncProgress(log.Add));

            Assert.Contains(runner.Calls, c => c.Contains("/Remove-Package /PackageName:Windows-Defender-Client-Package~31bf3856ad364e35~arm64~~10.0.1", StringComparison.Ordinal));
            Assert.Contains(runner.Calls, c => c.Contains("/ResetBase", StringComparison.Ordinal));
            AssertNoDismWhileHivesLoaded(runner.Calls);
            Assert.True(Directory.Exists(Path.Combine(sxs, "Manifests")));
            Assert.True(Directory.Exists(Path.Combine(sxs, "arm64_microsoft-windows-servicingstack_31bf3856ad364e35_10.0.1_none_1")));
            Assert.False(Directory.Exists(Path.Combine(sxs, "arm64_microsoft-windows-notepad_31bf3856ad364e35_10.0.1_none_2")));
            Assert.False(Directory.Exists(sxs + "_keep"));
            Assert.False(Directory.Exists(Path.Combine(root, @"Program Files (x86)\Microsoft\Edge")));
            Assert.False(File.Exists(Path.Combine(root, @"Windows\System32\Recovery\winre.wim")));
            Assert.DoesNotContain(runner.Calls, c => c.StartsWith("reg.exe add", StringComparison.Ordinal) && c.Contains(@"Services\WinDefend", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
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
