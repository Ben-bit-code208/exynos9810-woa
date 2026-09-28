// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Deploy;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Image;

/// <summary>Outputs of a full image build, ready for the transfer stage.</summary>
public sealed record BuiltImage(string WindowsImage, long WindowsBytes, string WindowsSha256, string EspDirectory);

/// <summary>
/// Builds a bootable ARM64 Windows disk inside a VHDX on the PC, then exports the
/// pieces the phone needs: an EFI system partition, MSR and an NTFS Windows
/// partition; Windows is applied, phone drivers injected, the slim profile run,
/// the OOBE answer file written and <c>bcdboot</c> laid down. The BCD is then
/// retargeted to boot by locating <c>\Windows</c>, the NTFS partition is exported
/// as the raw image the transfer stage writes to USERDATA, and the ESP files are
/// copied out for the boot-files step. All disk work goes through injected seams.
/// </summary>
public sealed class VhdxImageBuilder
{
    private static readonly TimeSpan DiskpartTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BootTimeout = TimeSpan.FromMinutes(10);

    private readonly IProcessRunner _runner;
    private readonly ImageBuilder _imageBuilder;
    private readonly BootConfiguration _boot;
    private readonly RawImageExporter _exporter = new();
    private readonly Func<char, IRawDiskSource> _sourceFactory;
    private readonly string _diskpart;
    private readonly string _bcdboot;

    public VhdxImageBuilder(IProcessRunner runner, string? system32 = null, Func<char, IRawDiskSource>? sourceFactory = null)
    {
        _runner = runner;
        system32 ??= Environment.GetFolderPath(Environment.SpecialFolder.System);
        _imageBuilder = new ImageBuilder(runner, system32);
        _boot = new BootConfiguration(runner, system32);
        _sourceFactory = sourceFactory ?? (letter => new VolumeDiskSource(letter));
        _diskpart = Path.Combine(system32, "diskpart.exe");
        _bcdboot = Path.Combine(system32, "bcdboot.exe");
    }

    public char EspLetter { get; init; } = 'S';
    public char WindowsLetter { get; init; } = 'W';

    internal string CreateScript(string vhdxPath, long maxMegabytes) => string.Join("\r\n",
        $"create vdisk file=\"{vhdxPath}\" maximum={maxMegabytes} type=expandable",
        $"select vdisk file=\"{vhdxPath}\"",
        "attach vdisk",
        "convert gpt",
        "create partition efi size=260",
        "format fs=fat32 quick label=System",
        $"assign letter={EspLetter}",
        "create partition msr size=16",
        "create partition primary",
        "format fs=ntfs quick label=Windows",
        $"assign letter={WindowsLetter}",
        "exit");

    internal string DetachScript(string vhdxPath) => string.Join("\r\n",
        $"select vdisk file=\"{vhdxPath}\"",
        "detach vdisk",
        "exit");

    public async Task<BuiltImage> BuildAsync(string vhdxPath, long maxMegabytes, string installImage, int index,
        IReadOnlyList<DriverPackage> drivers, SlimProfile profile, UnattendOptions unattend, string outputDir,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (drivers.Count == 0)
        {
            throw new InvalidOperationException("No phone drivers were found to inject.");
        }
        var esp = $"{EspLetter}:";
        var winRoot = $"{WindowsLetter}:\\";
        Directory.CreateDirectory(outputDir);
        var windowsImage = Path.Combine(outputDir, "windows.img");
        var espOut = Path.Combine(outputDir, "esp");

        log?.Report("Creating the VHDX and its partitions...");
        await Diskpart(CreateScript(vhdxPath, maxMegabytes), ct).ConfigureAwait(false);
        try
        {
            await _imageBuilder.BuildAsync(winRoot.TrimEnd('\\'), installImage, index, drivers, profile, log, ct).ConfigureAwait(false);

            log?.Report("Writing the OOBE answer file...");
            var unattendPath = Path.Combine(winRoot, UnattendXml.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(unattendPath)!);
            await File.WriteAllTextAsync(unattendPath, UnattendXml.Build(unattend), ct).ConfigureAwait(false);

            log?.Report("Writing UEFI boot files with bcdboot...");
            var r = await _runner.RunAsync(_bcdboot, [$"{winRoot}Windows", "/s", esp, "/f", "UEFI"], BootTimeout, ct).ConfigureAwait(false);
            if (!r.Succeeded)
            {
                throw new InvalidOperationException($"bcdboot failed: {(r.StdErr + r.StdOut).Trim()}");
            }
            await _boot.RetargetToLocateAsync(Path.Combine(esp + "\\", @"EFI\Microsoft\Boot\BCD"), log, ct).ConfigureAwait(false);

            log?.Report("Exporting the Windows volume to a raw image (this is large)...");
            using var disposable = _sourceFactory(WindowsLetter) as IDisposable;
            var diskSource = (IRawDiskSource)disposable!;
            var (bytes, sha) = await _exporter.ExportAsync(diskSource, new PartitionExtent(0, diskSource.Length),
                windowsImage, log, ct).ConfigureAwait(false);

            log?.Report("Copying the EFI boot files...");
            CopyTree(Path.Combine(esp + "\\", "EFI"), Path.Combine(espOut, "EFI"));

            return new BuiltImage(windowsImage, bytes, sha, espOut);
        }
        finally
        {
            log?.Report("Detaching the VHDX...");
            await Diskpart(DetachScript(vhdxPath), CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, destination, StringComparison.Ordinal));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination, StringComparison.Ordinal), overwrite: true);
        }
    }

    private async Task Diskpart(string script, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"s9woa-dp-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, script, ct).ConfigureAwait(false);
        try
        {
            var r = await _runner.RunAsync(_diskpart, ["/s", file], DiskpartTimeout, ct).ConfigureAwait(false);
            if (!r.Succeeded)
            {
                throw new InvalidOperationException($"diskpart failed: {(r.StdErr + r.StdOut).Trim()}");
            }
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}
