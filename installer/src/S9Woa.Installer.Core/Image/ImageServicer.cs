// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Image;

/// <summary>Applies a <see cref="SlimPlan"/> to an offline Windows image mounted or applied at a directory.</summary>
public sealed partial class ImageServicer
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromHours(2);
    private readonly IProcessRunner _runner;
    private readonly string _dism;
    private readonly string _reg;
    private readonly bool _canServicePackages;

    /// <param name="hostArchitecture">
    /// Architecture of this PC. DISM can only run component-based servicing (capabilities,
    /// features, component cleanup) with the image's own servicing stack, which an x64 PC cannot
    /// load from an ARM64 image; those operations are skipped there. App removal, registry
    /// defaults and file deletion work from any host.
    /// </param>
    public ImageServicer(IProcessRunner runner, string? system32 = null, Architecture? hostArchitecture = null)
    {
        _runner = runner;
        system32 ??= Environment.GetFolderPath(Environment.SpecialFolder.System);
        _dism = Path.Combine(system32, "dism.exe");
        _reg = Path.Combine(system32, "reg.exe");
        _canServicePackages = (hostArchitecture ?? RuntimeInformation.OSArchitecture) == Architecture.Arm64;
    }

    /// <summary>Operations that need the image's own (ARM64) servicing stack.</summary>
    internal static bool NeedsImageServicingStack(SlimOperation op) => op is RemoveCapability or DisableFeature or ComponentCleanup;

    internal static string HiveMountName(RegistryHive hive) => hive switch
    {
        RegistryHive.Software => @"HKLM\S9WOA_SOFTWARE",
        RegistryHive.System => @"HKLM\S9WOA_SYSTEM",
        RegistryHive.DefaultUser => @"HKLM\S9WOA_DEFAULT",
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };

    internal static string HiveFile(string imageRoot, RegistryHive hive) => hive switch
    {
        RegistryHive.Software => Path.Combine(imageRoot, @"Windows\System32\config\SOFTWARE"),
        RegistryHive.System => Path.Combine(imageRoot, @"Windows\System32\config\SYSTEM"),
        RegistryHive.DefaultUser => Path.Combine(imageRoot, @"Users\Default\NTUSER.DAT"),
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };

    internal static IReadOnlyList<string> RegAddArguments(SetRegistryValue op) =>
    [
        "add", $@"{HiveMountName(op.Hive)}\{op.Key}", "/v", op.Name,
        "/t", op.Kind == RegistryValueKind.DWord ? "REG_DWORD" : "REG_SZ",
        "/d", op.Value, "/f",
    ];

    [GeneratedRegex(@"^\s*PackageName\s*:\s*(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex PackageNameRegex();

    [GeneratedRegex(@"^\s*Capability Identity\s*:\s*(\S+)\s*\r?\n\s*State\s*:\s*(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex CapabilityRegex();

    internal static IReadOnlyList<string> ParseProvisionedPackages(string dismOutput) =>
        PackageNameRegex().Matches(dismOutput).Select(m => m.Groups[1].Value).ToList();

    internal static IReadOnlyList<string> ParseInstalledCapabilities(string dismOutput) =>
        CapabilityRegex().Matches(dismOutput)
            .Where(m => m.Groups[2].Value.Equals("Installed", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups[1].Value).ToList();

    public async Task ApplyAsync(string imageRoot, IReadOnlyList<SlimOperation> plan,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(Path.Combine(imageRoot, @"Windows\System32\ntoskrnl.exe")))
        {
            throw new DirectoryNotFoundException($"{imageRoot} does not contain an offline Windows image.");
        }
        if (plan.Count == 0)
        {
            return;
        }
        if (!_canServicePackages)
        {
            var skipped = plan.Where(NeedsImageServicingStack).ToList();
            if (skipped.Count > 0)
            {
                log?.Report($"Skipping {skipped.Count} component-servicing step(s) (capabilities, features, component cleanup): "
                    + "they need an ARM64 PC to service an ARM64 image. Everything else in the profile still applies.");
                plan = plan.Where(op => !NeedsImageServicingStack(op)).ToList();
            }
        }

        var packages = plan.OfType<RemoveProvisionedAppx>().Any()
            ? ParseProvisionedPackages((await Dism(imageRoot, ShortTimeout, ct, "/Get-ProvisionedAppxPackages").ConfigureAwait(false)).StdOut)
            : [];
        var capabilities = plan.OfType<RemoveCapability>().Any()
            ? ParseInstalledCapabilities((await Dism(imageRoot, ShortTimeout, ct, "/Get-Capabilities").ConfigureAwait(false)).StdOut)
            : [];

        var loaded = new List<RegistryHive>();
        try
        {
            foreach (var op in plan)
            {
                ct.ThrowIfCancellationRequested();
                switch (op)
                {
                    case RemoveProvisionedAppx a:
                        foreach (var pkg in packages.Where(p => p.StartsWith(a.NamePrefix, StringComparison.OrdinalIgnoreCase)))
                        {
                            log?.Report($"Removing app {pkg}");
                            await DismChecked(imageRoot, ShortTimeout, ct, "/Remove-ProvisionedAppxPackage", $"/PackageName:{pkg}").ConfigureAwait(false);
                        }
                        break;
                    case RemoveCapability c:
                        foreach (var cap in capabilities.Where(x => x.StartsWith(c.IdentityPrefix, StringComparison.OrdinalIgnoreCase)))
                        {
                            log?.Report($"Removing capability {cap}");
                            await DismChecked(imageRoot, ShortTimeout, ct, "/Remove-Capability", $"/CapabilityName:{cap}").ConfigureAwait(false);
                        }
                        break;
                    case DisableFeature f:
                        log?.Report(op.Description);
                        // Absent features are not an error; the list is shared across builds.
                        await Dism(imageRoot, ShortTimeout, ct, "/Disable-Feature", $"/FeatureName:{f.FeatureName}", "/Remove").ConfigureAwait(false);
                        break;
                    case SetRegistryValue r:
                        if (!loaded.Contains(r.Hive))
                        {
                            await Run(_reg, ["load", HiveMountName(r.Hive), HiveFile(imageRoot, r.Hive)], ShortTimeout, ct).ConfigureAwait(false);
                            loaded.Add(r.Hive);
                        }
                        log?.Report(op.Description);
                        await Run(_reg, RegAddArguments(r), ShortTimeout, ct).ConfigureAwait(false);
                        break;
                    case DeleteImageFile d:
                        if (SlimPlan.IsProtectedPath(d.RelativePath))
                        {
                            throw new InvalidOperationException($"Refusing to delete protected path {d.RelativePath}.");
                        }
                        var full = Path.Combine(imageRoot, d.RelativePath);
                        if (File.Exists(full))
                        {
                            log?.Report(op.Description);
                            File.SetAttributes(full, FileAttributes.Normal);
                            File.Delete(full);
                        }
                        break;
                    case ComponentCleanup cc:
                        await UnloadAll(loaded).ConfigureAwait(false);
                        log?.Report(op.Description + " (this can take a long time)");
                        await DismChecked(imageRoot, CleanupTimeout, ct, cc.ResetBase
                            ? ["/Cleanup-Image", "/StartComponentCleanup", "/ResetBase"]
                            : ["/Cleanup-Image", "/StartComponentCleanup"]).ConfigureAwait(false);
                        break;
                    default:
                        throw new NotSupportedException(op.GetType().Name);
                }
            }
        }
        finally
        {
            await UnloadAll(loaded).ConfigureAwait(false);
        }
    }

    private async Task UnloadAll(List<RegistryHive> loaded)
    {
        foreach (var hive in loaded)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await _runner.RunAsync(_reg, ["unload", HiveMountName(hive)], ShortTimeout).ConfigureAwait(false);
        }
        loaded.Clear();
    }

    private Task<ProcessResult> Dism(string imageRoot, TimeSpan timeout, CancellationToken ct, params string[] args) =>
        _runner.RunAsync(_dism, [$"/Image:{imageRoot}", "/English", .. args], timeout, ct);

    private async Task DismChecked(string imageRoot, TimeSpan timeout, CancellationToken ct, params string[] args)
    {
        var r = await Dism(imageRoot, timeout, ct, args).ConfigureAwait(false);
        // 3010 = success, reboot required (irrelevant offline).
        if (r.ExitCode is not 0 and not 3010)
        {
            throw new InvalidOperationException($"DISM {string.Join(' ', args)} failed ({r.ExitCode}): {r.StdOut.Trim()}");
        }
    }

    private async Task Run(string file, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var r = await _runner.RunAsync(file, args, timeout, ct).ConfigureAwait(false);
        if (!r.Succeeded)
        {
            throw new InvalidOperationException($"{Path.GetFileName(file)} {string.Join(' ', args)} failed: {(r.StdErr + r.StdOut).Trim()}");
        }
    }
}
