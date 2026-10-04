// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core;
using S9Woa.Installer.Core.Deploy;
using S9Woa.Installer.Core.Device;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Tests;

public class PartitionProbeTests
{
    /// <summary>What the probe command prints on a phone that answers: one line per partition.</summary>
    private const string S9PlusOutput =
        "unit=sda\n" +
        "lbs=4096\n" +
        "size=124928000\n" +
        "p APBOOT sda1 8192 262144\n" +
        "p devinfo sda2 265216 16384\n" +
        "p CACHE sda21 425984 1228800\n" +
        "p SYSTEM sda18 4096 6291456\n" +
        "p EFS sda5 4210688 16384\n" +
        "p BOOT sda2 1357824 786432\n" +
        "p RECOVERY sda6 3407872 786432\n" +
        "p MISC sda7 4194304 16384\n" +
        "p USERDATA sda20 13577216 111337472\n";

    private sealed class StubRunner(string? output) : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"{Path.GetFileName(fileName)} {string.Join(' ', arguments)}");
            return Task.FromResult(output is null
                ? new ProcessResult(1, "", "device offline")
                : new ProcessResult(0, output, ""));
        }
    }

    [Fact]
    public void ParsesTheLayoutAPhoneReports()
    {
        var layout = SysfsLayoutParser.Parse(S9PlusOutput)!;
        Assert.Equal("sda", layout.Unit);
        Assert.Equal(4096, layout.LogicalSectorSize);
        Assert.Equal(124928000L * 512, layout.DiskBytes);
        Assert.Equal(9, layout.ByName.Count);

        var userdata = layout["USERDATA"]!;
        Assert.Equal(20, userdata.PartitionNumber);
        Assert.Equal("sda20", userdata.Node);
        Assert.Equal(13577216L * 512, userdata.OffsetBytes);
        Assert.Equal(111337472L * 512, userdata.SizeBytes);
        Assert.Equal(13917184L, userdata.BlockCount);

        // Byte totals match the validated reference: the same phone, measured rather than assumed.
        Assert.Equal(PartitionMap.ReferenceLayout.DiskBytes, layout.Geometry.DiskBytes);
        Assert.Equal(PartitionMap.ReferenceLayout.WindowsOffset, layout.Geometry.WindowsOffset);
        Assert.Equal(PartitionMap.ReferenceLayout.WindowsBytes, layout.Geometry.WindowsBytes);

        // by-name links are matched case-insensitively, and a missing one is just null.
        Assert.Equal("USERDATA", layout["userdata"]!.Name);
        Assert.Null(layout["NOPE"]);
        Assert.True(layout.Has(PartitionMap.EfiSystemPartition));
        Assert.Equal(629145600, layout.SizeOf(PartitionMap.EfiSystemPartition));
        Assert.Contains("9 partitions", layout.Summary(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Captured from a real Amazon Fire HD 8 (product:lineage_ford, ro.boot.hardware=mt8127), the
    /// first non-Samsung phone the prober was run against. It found the two things the Samsung-only
    /// version of the command got wrong: the storage unit is <c>mmcblk0</c>, not <c>sda</c>, and this
    /// device has no <c>/dev/block/by-name</c> links at all.
    /// </summary>
    private const string FireHd8Output =
        "unit=mmcblk0\n" +
        "lbs=512\n" +
        "size=30535680\n" +
        "names=node\n" +
        "p mmcblk0p1 mmcblk0p1 2048 2048\n" +
        "p mmcblk0p10 mmcblk0p10 127744 10240\n" +
        "p mmcblk0p11 mmcblk0p11 137984 2457600\n" +
        "p mmcblk0p12 mmcblk0p12 2595584 512000\n" +
        "p mmcblk0p13 mmcblk0p13 3107584 27428063\n" +
        "p mmcblk0p2 mmcblk0p2 4096 2048\n" +
        "p mmcblk0p3 mmcblk0p3 6144 35584\n" +
        "p mmcblk0p4 mmcblk0p4 41728 2048\n" +
        "p mmcblk0p5 mmcblk0p5 43776 32768\n" +
        "p mmcblk0p6 mmcblk0p6 76544 32768\n" +
        "p mmcblk0p7 mmcblk0p7 109312 1024\n" +
        "p mmcblk0p8 mmcblk0p8 110336 7168\n" +
        "p mmcblk0p9 mmcblk0p9 117504 10240\n";

    [Fact]
    public void MeasuresAPhoneWithoutSamsungNames()
    {
        var layout = SysfsLayoutParser.Parse(FireHd8Output)!;
        Assert.Equal("mmcblk0", layout.Unit);
        Assert.Equal(512, layout.LogicalSectorSize);
        Assert.Equal(15_634_268_160, layout.DiskBytes);
        Assert.Equal(13, layout.ByName.Count);

        // No by-name links here, so a role lookup must not silently succeed.
        Assert.False(layout.HasRoles);
        Assert.False(layout.Has("USERDATA"));
        Assert.Throws<KeyNotFoundException>(() => layout.SizeOf("USERDATA"));

        // The partitions are still there to build an image for, by node.
        var userdata = layout.Node("mmcblk0p13")!;
        Assert.Equal(1_591_083_008, userdata.OffsetBytes);
        Assert.Equal(14_043_168_256, userdata.SizeBytes);
        Assert.Equal(1_200 * 1024 * 1024, layout.Node("mmcblk0p11")!.SizeBytes);

        // And it is refused on its own terms: this project builds 4Kn images, this is a 512-byte
        // eMMC, and it says so rather than writing something unbootable.
        var problem = layout.CheckAgainst(DeviceCatalog.GalaxyS9Plus)!;
        Assert.Contains("no partition named", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LetsTheProfileSayWhichNodeHoldsWhichRole()
    {
        var phone = DeviceCatalog.Ford;
        var named = SysfsLayoutParser.Parse(FireHd8Output)!;
        Assert.False(named.HasRoles);

        var layout = named.ResolvedByRoles(phone);
        Assert.True(layout.HasRoles);
        Assert.Equal("mmcblk0p13", layout.Node("USERDATA")!.Node);
        Assert.Equal("mmcblk0p11", layout.Node("SYSTEM")!.Node);
        Assert.Equal("mmcblk0p12", layout.Node("CACHE")!.Node);
        Assert.Equal(14_043_168_256, layout.SizeOf("USERDATA"));
        Assert.Equal(1_258_291_200, layout.SizeOf("SYSTEM"));

        // Roles the profile does not claim stay absent: it must not guess BOOT out of thin air.
        Assert.False(layout.Has("MISC"));
        Assert.Equal(5, layout.ByName.Count);
    }

    [Fact]
    public void RefusesToWriteToATabletThatCannotHoldWindows()
    {
        var raw = SysfsLayoutParser.Parse(FireHd8Output)!;

        // As measured, the tablet has no MISC partition at all, and that is what stops it first.
        var missing = raw.ResolvedByRoles(DeviceCatalog.Ford);
        Assert.Contains("no partition named MISC", missing.CheckAgainst(DeviceCatalog.Ford)!, StringComparison.OrdinalIgnoreCase);

        // And even if MISC were among the five partitions not yet identified, the sector size
        // refuses it on its own: this profile builds 4Kn boot volumes, this tablet has 512-byte ones.
        var withMisc = DeviceCatalog.Ford with
        {
            PartitionRoles = new Dictionary<string, string>(DeviceCatalog.Ford.PartitionRoles) { ["MISC"] = "p8" },
        };
        var problem = raw.ResolvedByRoles(withMisc).CheckAgainst(withMisc)!;
        Assert.Contains("512-byte sectors", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepsTheReferencePhoneOnRealPartitionNames()
    {
        // The Samsung path must not be disturbed by role mapping: it has by-name links, so the
        // profile's roles are never consulted.
        var samsung = SysfsLayoutParser.Parse(S9PlusOutput)!;
        Assert.True(samsung.HasRoles);
        Assert.Same(samsung, samsung.ResolvedByRoles(DeviceCatalog.GalaxyS9Plus));
        Assert.Null(samsung.CheckAgainst(DeviceCatalog.GalaxyS9Plus));
    }

    [Fact]
    public void RefusesOutputThatIsNotALayout()
    {
        Assert.Null(SysfsLayoutParser.Parse(""));
        Assert.Null(SysfsLayoutParser.Parse("error: device offline\n"));
        Assert.Null(SysfsLayoutParser.Parse("p BOOT sda2 1357824\n"));            // truncated line
        Assert.Null(SysfsLayoutParser.Parse("p CACHE sda21 425984 notanumber\n")); // unreadable size
        Assert.Null(SysfsLayoutParser.Parse("p CACHE sda21 0 0\n"));              // an empty disk
        Assert.Null(SysfsLayoutParser.Parse("lbs=4096\nsize=124928000\n"));      // no unit
        Assert.Null(SysfsLayoutParser.Parse("unit=sda\nlbs=4096\nsize=124928000\n")); // no partitions
    }

    [Fact]
    public void ChecksAMeasuredLayoutAgainstWhatTheInstallNeeds()
    {
        var good = SysfsLayoutParser.Parse(S9PlusOutput)!;
        Assert.Null(good.CheckAgainst(DeviceCatalog.GalaxyS9Plus));

        var noUserdata = SysfsLayoutParser.Parse(
            "unit=sda\nlbs=4096\nsize=124928000\np BOOT sda2 1357824 786432\n")!;
        Assert.Contains("USERDATA", noUserdata.CheckAgainst(DeviceCatalog.GalaxyS9Plus)!,
            StringComparison.OrdinalIgnoreCase);

        // Windows cannot be fitted into a USERDATA this small.
        var tiny = SysfsLayoutParser.Parse(S9PlusOutput.Replace("13577216 111337472", "13577216 2097152"))!;
        Assert.Contains("GiB of USERDATA", tiny.CheckAgainst(DeviceCatalog.GalaxyS9Plus)!,
            StringComparison.OrdinalIgnoreCase);

        // A moved USERDATA is fine - the image is built to match the phone, not the reference.
        var moved = SysfsLayoutParser.Parse(S9PlusOutput.Replace("13577216 111337472", "13000000 111337472"))!;
        Assert.Null(moved.CheckAgainst(DeviceCatalog.GalaxyS9Plus));
        Assert.NotEqual(PartitionMap.ReferenceLayout.WindowsOffset, moved.Geometry.WindowsOffset);

        // USERDATA starting too early leaves no room for the ESP the install needs before it.
        var cramped = SysfsLayoutParser.Parse(S9PlusOutput.Replace("13577216 111337472", "1024 111337472"))!;
        Assert.Contains("no room", cramped.CheckAgainst(DeviceCatalog.GalaxyS9Plus)!,
            StringComparison.OrdinalIgnoreCase);

        // A phone that reports 512-byte sectors is not one this project builds boot volumes for.
        var wrongSectors = SysfsLayoutParser.Parse(S9PlusOutput.Replace("lbs=4096", "lbs=512"))!;
        Assert.Contains("512-byte sectors", wrongSectors.CheckAgainst(DeviceCatalog.GalaxyS9Plus)!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MeasuresTheConnectedPhoneOverAdb()
    {
        var runner = new StubRunner(S9PlusOutput);
        var probe = new PartitionProbe(@"C:\adb\adb.exe", "aa11bb22cc33dd44", runner);

        var layout = await probe.MeasureAsync();
        Assert.NotNull(layout);
        Assert.Equal(PartitionMap.ReferenceLayout.WindowsBytes, layout.Geometry.WindowsBytes);

        Assert.Single(runner.Calls);
        Assert.Contains("adb.exe -s aa11bb22cc33dd44 shell", runner.Calls[0], StringComparison.Ordinal);
        Assert.Contains("/sys/block/*", runner.Calls[0], StringComparison.Ordinal);
        Assert.Contains("/dev/block/by-name", runner.Calls[0], StringComparison.Ordinal);

        var source = await probe.ResolveAsync(DeviceCatalog.GalaxyS9Plus);
        Assert.True(source.Measured);
        Assert.Equal(PartitionMap.ReferenceLayout.WindowsBytes, source.Geometry.WindowsBytes);
    }

    [Fact]
    public async Task FallsBackToTheProfileWhenThePhoneDoesNotAnswer()
    {
        var probe = new PartitionProbe("adb", "aa11bb22cc33dd44", new StubRunner(null));

        Assert.Null(await probe.MeasureAsync());

        var source = await probe.ResolveAsync(DeviceCatalog.GalaxyS9Plus);
        Assert.False(source.Measured);
        Assert.Equal(PartitionMap.ReferenceLayout.DiskBytes, source.Geometry.DiskBytes);
        Assert.Equal(PartitionProbe.OfflineGeometry(DeviceCatalog.GalaxyS9Plus), source.Geometry);

        // A profile with nothing on file cannot be built blind: the caller is told to reconnect.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => probe.ResolveAsync(DeviceCatalog.GalaxyNote9));
        Assert.Contains("no validated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProbeCommandIsScopedToThePhoneAndNeedsNoRoot()
    {
        var args = PartitionProbe.ProbeArguments("aa11bb22cc33dd44");
        Assert.Equal(["-s", "aa11bb22cc33dd44", "shell", SysfsLayoutParser.ProbeCommand], args);

        // No root, and nothing caller-controlled is embedded in the command.
        Assert.DoesNotContain("su ", args[3], StringComparison.Ordinal);
        Assert.DoesNotContain("aa11bb22", args[3], StringComparison.Ordinal);
        Assert.Contains("/sys/block/*", args[3], StringComparison.Ordinal);
        Assert.Contains("/dev/block/by-name", args[3], StringComparison.Ordinal);
        Assert.DoesNotContain("/sys/block/sd?", args[3], StringComparison.Ordinal); // Samsung-only glob
    }

    [Fact]
    public async Task UsesWhicheverCatalogItIsGiven()
    {
        // An empty catalog must not silently fall back to the shipped one.
        Assert.Null(DeviceSnapshot.InDownloadMode("aa11bb22cc33dd44", "G965FXXUHFVG4", []));

        var injected = new DeviceProfile
        {
            Id = "testlte",
            Codename = "testlte",
            MarketingName = "Test Phone",
            SoC = "Test",
            HardwareToken = "TEST",
            Architecture = TargetArchitecture.Arm64,
            Tier = SupportTier.Experimental,
            Variants = [new DeviceVariant("SM-T0001", "T0001", "TEST")],
        };
        var probe = new PartitionProbe("adb", "aa11bb22cc33dd44", new StubRunner(S9PlusOutput));

        // A profile with no geometry of its own is still buildable, because the phone answered.
        var source = await probe.ResolveAsync(injected);
        Assert.True(source.Measured);
        Assert.Equal(PartitionMap.ReferenceLayout.DiskBytes, source.Geometry.DiskBytes);

        // But a refused phone is never measured for the install: the tier check stays in front.
        Assert.Null(DeviceSnapshot.InDownloadMode("aa11bb22cc33dd44", "G965FXXUHFVG4",
            [DeviceCatalog.GalaxyS9, DeviceCatalog.GalaxyNote9]));
    }
}