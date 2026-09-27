// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Image;

/// <summary>A driver package to inject: a folder containing at least one signed .inf.</summary>
public sealed record DriverPackage(string Name, string Directory);

/// <summary>
/// Builds an offline ARM64 Windows image in a directory: apply the chosen edition,
/// inject the phone drivers, then run the selected <see cref="SlimProfile"/>.
/// The result is a ready-to-transfer Windows tree; writing it to the phone and
/// laying down boot files are separate stages.
/// </summary>
public sealed class ImageBuilder
{
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan DriverTimeout = TimeSpan.FromMinutes(15);

    private readonly IProcessRunner _runner;
    private readonly ImageServicer _servicer;
    private readonly string _dism;

    public ImageBuilder(IProcessRunner runner, string? system32 = null)
    {
        _runner = runner;
        system32 ??= Environment.GetFolderPath(Environment.SpecialFolder.System);
        _servicer = new ImageServicer(runner, system32);
        _dism = Path.Combine(system32, "dism.exe");
    }

    /// <summary>Discovers driver packages (folders with an .inf) under <paramref name="driversRoot"/>.</summary>
    public static IReadOnlyList<DriverPackage> DiscoverDrivers(string driversRoot)
    {
        if (!Directory.Exists(driversRoot))
        {
            return [];
        }
        return Directory.EnumerateFiles(driversRoot, "*.inf", SearchOption.AllDirectories)
            .Select(inf => Path.GetDirectoryName(inf)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(dir => new DriverPackage(Path.GetFileName(dir), dir))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task BuildAsync(string applyDir, string installImage, int index,
        IReadOnlyList<DriverPackage> drivers, SlimProfile profile,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(applyDir);

        log?.Report($"Applying Windows image (index {index}). This takes a while...");
        await Checked(_dism, ["/Apply-Image", $"/ImageFile:{installImage}", $"/Index:{index}", $"/ApplyDir:{applyDir}"],
            ApplyTimeout, ct).ConfigureAwait(false);

        if (drivers.Count == 0)
        {
            throw new InvalidOperationException(
                "No phone drivers were found to inject. Build the UFS and touch drivers first, or point the installer at their output folder.");
        }
        foreach (var driver in drivers)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report($"Adding driver {driver.Name}");
            await Checked(_dism, [$"/Image:{applyDir}", "/Add-Driver", $"/Driver:{driver.Directory}", "/Recurse", "/ForceUnsigned"],
                DriverTimeout, ct).ConfigureAwait(false);
        }

        var plan = SlimPlan.For(profile);
        if (plan.Count > 0)
        {
            log?.Report($"Applying the {profile} image profile...");
            await _servicer.ApplyAsync(applyDir, plan, log, ct).ConfigureAwait(false);
        }
        log?.Report("Windows image built.");
    }

    private async Task Checked(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var r = await _runner.RunAsync(file, [.. args, "/English"], timeout, ct).ConfigureAwait(false);
        if (r.ExitCode is not 0 and not 3010)
        {
            throw new InvalidOperationException($"{Path.GetFileName(file)} {args[0]} failed ({r.ExitCode}): {(r.StdOut + r.StdErr).Trim()}");
        }
    }
}
