// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Image;

/// <summary>
/// Builds a bootable ARM64 Windows disk inside a VHDX on the PC: an EFI system
/// partition, MSR and an NTFS Windows partition. Windows is applied, the phone
/// drivers are injected, the chosen slim profile runs, and <c>bcdboot</c> lays
/// down the UEFI boot files. The Windows partition can then be exported as the
/// raw image the transfer stage writes to the phone's USERDATA, and the ESP as
/// the boot files. All disk operations go through an injected process runner.
/// </summary>
public sealed class VhdxImageBuilder
{
    private static readonly TimeSpan DiskpartTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan BootTimeout = TimeSpan.FromMinutes(10);

    private readonly IProcessRunner _runner;
    private readonly ImageBuilder _imageBuilder;
    private readonly string _system32;
    private readonly string _diskpart;
    private readonly string _bcdboot;

    public VhdxImageBuilder(IProcessRunner runner, string? system32 = null)
    {
        _runner = runner;
        _system32 = system32 ??= Environment.GetFolderPath(Environment.SpecialFolder.System);
        _imageBuilder = new ImageBuilder(runner, system32);
        _diskpart = Path.Combine(system32, "diskpart.exe");
        _bcdboot = Path.Combine(system32, "bcdboot.exe");
    }

    /// <summary>ESP and Windows drive letters used while the VHDX is attached.</summary>
    public char EspLetter { get; init; } = 'S';
    public char WindowsLetter { get; init; } = 'W';

    internal string CreateScript(string vhdxPath, long maxMegabytes) => string.Join("\r\n",
        $"create vdisk file=\"{vhdxPath}\" maximum={maxMegabytes} type=expandable",
        "select vdisk file=\"" + vhdxPath + "\"",
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

    public async Task BuildAsync(string vhdxPath, long maxMegabytes, string installImage, int index,
        IReadOnlyList<DriverPackage> drivers, SlimProfile profile, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (drivers.Count == 0)
        {
            throw new InvalidOperationException("No phone drivers were found to inject.");
        }
        var esp = $"{EspLetter}:";
        var win = $"{WindowsLetter}:\\";
        var applyDir = $"{WindowsLetter}:\\";

        log?.Report("Creating the VHDX and its partitions...");
        await Diskpart(CreateScript(vhdxPath, maxMegabytes), ct).ConfigureAwait(false);
        try
        {
            await _imageBuilder.BuildAsync(applyDir.TrimEnd('\\'), installImage, index, drivers, profile, log, ct).ConfigureAwait(false);

            log?.Report("Writing UEFI boot files with bcdboot...");
            var r = await _runner.RunAsync(_bcdboot, [$"{win}Windows", "/s", esp, "/f", "UEFI"], BootTimeout, ct).ConfigureAwait(false);
            if (!r.Succeeded)
            {
                throw new InvalidOperationException($"bcdboot failed: {(r.StdErr + r.StdOut).Trim()}");
            }
        }
        finally
        {
            log?.Report("Detaching the VHDX...");
            await Diskpart(DetachScript(vhdxPath), CancellationToken.None).ConfigureAwait(false);
        }
        log?.Report("Bootable Windows VHDX built.");
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
