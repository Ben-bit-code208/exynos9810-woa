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
    private static readonly TimeSpan DiskTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BootTimeout = TimeSpan.FromMinutes(10);

    private readonly IProcessRunner _runner;
    private readonly ImageBuilder _imageBuilder;
    private readonly BootConfiguration _boot;
    private readonly RawImageExporter _exporter = new();
    private readonly Func<char, IRawDiskSource> _sourceFactory;
    private readonly string _powershell;
    private readonly string _bcdboot;

    public VhdxImageBuilder(IProcessRunner runner, string? system32 = null, Func<char, IRawDiskSource>? sourceFactory = null)
    {
        _runner = runner;
        system32 ??= Environment.GetFolderPath(Environment.SpecialFolder.System);
        _imageBuilder = new ImageBuilder(runner, system32);
        _boot = new BootConfiguration(runner, system32);
        _sourceFactory = sourceFactory ?? (letter => new VolumeDiskSource(letter));
        _powershell = Path.Combine(system32, @"WindowsPowerShell\v1.0\powershell.exe");
        _bcdboot = Path.Combine(system32, "bcdboot.exe");
    }

    public char EspLetter { get; init; } = 'S';
    public char WindowsLetter { get; init; } = 'W';

    /// <summary>
    /// PowerShell that creates a 4Kn VHDX (the phone's UFS is a 4096-byte-sector
    /// device) with an ESP, MSR and NTFS Windows partition. A 512-byte VHDX would
    /// format NTFS for 512-byte sectors, which will not mount when written raw to
    /// the 4Kn phone, so the sector size is explicit and asserted.
    /// </summary>
    internal string CreateScript(string vhdxPath, long maxMegabytes) => string.Join("\n",
        "$ErrorActionPreference = 'Stop'",
        $"$vhd = New-VHD -Path '{vhdxPath}' -Dynamic -SizeBytes ({maxMegabytes}MB) -LogicalSectorSizeBytes 4096 -PhysicalSectorSizeBytes 4096",
        "$disk = Mount-VHD -Path $vhd.Path -NoDriveLetter -Passthru | Get-Disk",
        "if ($disk.LogicalSectorSize -ne 4096) { throw 'VHDX is not a 4Kn disk' }",
        "Initialize-Disk -Number $disk.Number -PartitionStyle GPT | Out-Null",
        "$esp = New-Partition -DiskNumber $disk.Number -Size 260MB -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'",
        "Format-Volume -Partition $esp -FileSystem FAT32 -NewFileSystemLabel 'System' -Confirm:$false | Out-Null",
        $"$esp | Set-Partition -NewDriveLetter {EspLetter}",
        "New-Partition -DiskNumber $disk.Number -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' | Out-Null",
        "$win = New-Partition -DiskNumber $disk.Number -UseMaximumSize -GptType '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'",
        "Format-Volume -Partition $win -FileSystem NTFS -AllocationUnitSize 4096 -NewFileSystemLabel 'Windows' -Confirm:$false | Out-Null",
        $"$win | Set-Partition -NewDriveLetter {WindowsLetter}");

    internal string DetachScript(string vhdxPath) => string.Join("\n",
        "$ErrorActionPreference = 'SilentlyContinue'",
        $"Dismount-VHD -Path '{vhdxPath}'");

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
        await RunScript(CreateScript(vhdxPath, maxMegabytes), ct).ConfigureAwait(false);
        try
        {
            await _imageBuilder.BuildAsync(winRoot, installImage, index, drivers, profile, log, ct).ConfigureAwait(false);

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
            await RunScript(DetachScript(vhdxPath), CancellationToken.None).ConfigureAwait(false);
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

    private async Task RunScript(string script, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"s9woa-vhdx-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(file, script, ct).ConfigureAwait(false);
        try
        {
            var r = await _runner.RunAsync(_powershell,
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", file], DiskTimeout, ct).ConfigureAwait(false);
            if (!r.Succeeded)
            {
                throw new InvalidOperationException($"VHDX preparation failed: {(r.StdErr + r.StdOut).Trim()}");
            }
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}
