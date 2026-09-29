// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Image;

/// <summary>Applies a <see cref="SlimPlan"/> to an offline Windows image mounted or applied at a directory.</summary>
public sealed partial class ImageServicer
{
    private const string Administrators = "*S-1-5-32-544";
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromHours(2);
    private readonly IProcessRunner _runner;
    private readonly string _dism;
    private readonly string _reg;
    private readonly string _takeown;
    private readonly string _icacls;
    private readonly string _cmd;

    public ImageServicer(IProcessRunner runner, string? system32 = null)
    {
        _runner = runner;
        system32 ??= Environment.GetFolderPath(Environment.SpecialFolder.System);
        _dism = Path.Combine(system32, "dism.exe");
        _reg = Path.Combine(system32, "reg.exe");
        _takeown = Path.Combine(system32, "takeown.exe");
        _icacls = Path.Combine(system32, "icacls.exe");
        _cmd = Path.Combine(system32, "cmd.exe");
    }

    internal static string HiveMountName(RegistryHive hive) => hive switch
    {
        RegistryHive.Software => @"HKLM\S9WOA_SOFTWARE",
        RegistryHive.System => @"HKLM\S9WOA_SYSTEM",
        RegistryHive.DefaultUser => @"HKLM\S9WOA_DEFAULT",
        RegistryHive.SystemDefaultUser => @"HKLM\S9WOA_SYSDEFAULT",
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };

    internal static string HiveFile(string imageRoot, RegistryHive hive) => hive switch
    {
        RegistryHive.Software => Path.Combine(imageRoot, @"Windows\System32\config\SOFTWARE"),
        RegistryHive.System => Path.Combine(imageRoot, @"Windows\System32\config\SYSTEM"),
        RegistryHive.DefaultUser => Path.Combine(imageRoot, @"Users\Default\NTUSER.DAT"),
        RegistryHive.SystemDefaultUser => Path.Combine(imageRoot, @"Windows\System32\config\DEFAULT"),
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

    [GeneratedRegex(@"^\s*Package Identity\s*:\s*(\S+)\s*\r?\n\s*State\s*:\s*(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex PackageRegex();

    internal static IReadOnlyList<string> ParseProvisionedPackages(string dismOutput) =>
        PackageNameRegex().Matches(dismOutput).Select(m => m.Groups[1].Value).ToList();

    internal static IReadOnlyList<string> ParseInstalledCapabilities(string dismOutput) =>
        CapabilityRegex().Matches(dismOutput)
            .Where(m => m.Groups[2].Value.Equals("Installed", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups[1].Value).ToList();

    internal static IReadOnlyList<string> ParseInstalledPackages(string dismOutput) =>
        PackageRegex().Matches(dismOutput)
            .Where(m => m.Groups[2].Value.Equals("Installed", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups[1].Value).ToList();

    /// <summary>
    /// A package's main (not satellite) identity: no language and a single architecture,
    /// e.g. Name~31bf3856ad364e35~arm64~~10.0.22621.1. CBS refuses to remove satellites on their own.
    /// </summary>
    internal static bool IsMainPackage(string identity)
    {
        var parts = identity.Split('~');
        return parts.Length >= 5 && parts[3].Length == 0 && !parts[2].Contains('.', StringComparison.Ordinal);
    }

    /// <summary>
    /// Top-level WinSxS entries to keep. Refuses (null) unless the servicing stack and the
    /// manifests would survive, so a store is never trimmed into an unbootable state.
    /// </summary>
    internal static IReadOnlyList<string>? ComponentStoreSurvivors(IEnumerable<string> directoryNames, IReadOnlyList<string> keep)
    {
        var survivors = directoryNames
            .Where(n => keep.Any(p => FileSystemName.MatchesSimpleExpression(p, n, ignoreCase: true)))
            .ToList();
        var hasStack = survivors.Any(n => FileSystemName.MatchesSimpleExpression("*_microsoft-windows-servicingstack_*", n, ignoreCase: true));
        var hasManifests = survivors.Contains("Manifests", StringComparer.OrdinalIgnoreCase);
        return hasStack && hasManifests ? survivors : null;
    }

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

        // A hive left loaded by an interrupted run locks the image's registry, and DISM then
        // fails reading it (sharing violation, error 32).
        foreach (var hive in Enum.GetValues<RegistryHive>())
        {
            await _runner.RunAsync(_reg, ["unload", HiveMountName(hive)], ShortTimeout, ct).ConfigureAwait(false);
        }

        IReadOnlyList<string>? appx = null;
        IReadOnlyList<string>? capabilities = null;
        IReadOnlyList<string>? packages = null;
        var loaded = new List<RegistryHive>();
        try
        {
            foreach (var op in plan)
            {
                ct.ThrowIfCancellationRequested();
                if (op is RemoveProvisionedAppx or RemoveCapability or RemovePackage or DisableFeature or ComponentCleanup)
                {
                    // DISM reads the image's hives; they must not be loaded while it runs.
                    await UnloadAll(loaded).ConfigureAwait(false);
                }
                switch (op)
                {
                    case RemoveProvisionedAppx a:
                        appx ??= ParseProvisionedPackages((await Dism(imageRoot, ShortTimeout, ct, "/Get-ProvisionedAppxPackages").ConfigureAwait(false)).StdOut);
                        foreach (var pkg in appx.Where(p => p.StartsWith(a.NamePrefix, StringComparison.OrdinalIgnoreCase)))
                        {
                            log?.Report($"Removing app {pkg}");
                            await DismChecked(imageRoot, ShortTimeout, ct, "/Remove-ProvisionedAppxPackage", $"/PackageName:{pkg}").ConfigureAwait(false);
                        }
                        break;
                    case RemoveCapability c:
                        capabilities ??= ParseInstalledCapabilities((await Dism(imageRoot, ShortTimeout, ct, "/Get-Capabilities").ConfigureAwait(false)).StdOut);
                        foreach (var cap in capabilities.Where(x => x.StartsWith(c.IdentityPrefix, StringComparison.OrdinalIgnoreCase)))
                        {
                            log?.Report($"Removing capability {cap}");
                            await DismChecked(imageRoot, ShortTimeout, ct, "/Remove-Capability", $"/CapabilityName:{cap}").ConfigureAwait(false);
                        }
                        break;
                    case RemovePackage p:
                        packages ??= await InstalledPackagesAsync(imageRoot, ct).ConfigureAwait(false);
                        packages = await RemovePackagesAsync(imageRoot, p.NamePrefix, packages, log, ct).ConfigureAwait(false);
                        break;
                    case DisableFeature f:
                        log?.Report(op.Description);
                        // Absent features are not an error; the list is shared across builds.
                        await Dism(imageRoot, ShortTimeout, ct, "/Disable-Feature", $"/FeatureName:{f.FeatureName}", "/Remove").ConfigureAwait(false);
                        break;
                    case SetRegistryValue r:
                        await LoadAsync(imageRoot, r.Hive, loaded, ct).ConfigureAwait(false);
                        var key = $@"{HiveMountName(r.Hive)}\{r.Key}";
                        if (r.OnlyIfKeyExists && !(await _runner.RunAsync(_reg, ["query", key], ShortTimeout, ct).ConfigureAwait(false)).Succeeded)
                        {
                            log?.Report($"Skipping {r.Hive}\\{r.Key}: not present in this image.");
                            break;
                        }
                        log?.Report(op.Description);
                        await Run(_reg, RegAddArguments(r), ShortTimeout, ct).ConfigureAwait(false);
                        break;
                    case DeleteRegistryKey k:
                        await LoadAsync(imageRoot, k.Hive, loaded, ct).ConfigureAwait(false);
                        // An absent key is fine: the list is shared across builds.
                        if ((await _runner.RunAsync(_reg, ["delete", $@"{HiveMountName(k.Hive)}\{k.Key}", "/f"], ShortTimeout, ct).ConfigureAwait(false)).Succeeded)
                        {
                            log?.Report(op.Description);
                        }
                        break;
                    case DeleteImagePath d:
                        await DeletePathAsync(imageRoot, d, log, ct).ConfigureAwait(false);
                        break;
                    case TrimComponentStore t:
                        await TrimComponentStoreAsync(imageRoot, t, log, ct).ConfigureAwait(false);
                        break;
                    case ComponentCleanup cc:
                        log?.Report(op.Description + " (this can take a long time)");
                        try
                        {
                            await DismChecked(imageRoot, LongTimeout, ct, cc.ResetBase
                                ? ["/Cleanup-Image", "/StartComponentCleanup", "/ResetBase"]
                                : ["/Cleanup-Image", "/StartComponentCleanup"]).ConfigureAwait(false);
                        }
                        catch (Exception e) when (cc.Optional && e is InvalidOperationException or TimeoutException)
                        {
                            log?.Report($"Component cleanup did not complete; the image is still usable. {e.Message}");
                        }
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

    private async Task<IReadOnlyList<string>> InstalledPackagesAsync(string imageRoot, CancellationToken ct) =>
        ParseInstalledPackages((await Dism(imageRoot, ShortTimeout, ct, "/Get-Packages").ConfigureAwait(false)).StdOut);

    /// <summary>Removes the matching main packages (which takes their satellites along), then any leftovers.</summary>
    private async Task<IReadOnlyList<string>> RemovePackagesAsync(string imageRoot, string prefix, IReadOnlyList<string> installed,
        IProgress<string>? log, CancellationToken ct)
    {
        var matches = installed.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            return installed;
        }
        foreach (var pkg in matches.Where(IsMainPackage))
        {
            log?.Report($"Removing package {pkg}");
            await RemovePackageAsync(imageRoot, pkg, log, ct).ConfigureAwait(false);
        }
        installed = await InstalledPackagesAsync(imageRoot, ct).ConfigureAwait(false);
        foreach (var pkg in installed.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            log?.Report($"Removing package {pkg}");
            await RemovePackageAsync(imageRoot, pkg, log, ct).ConfigureAwait(false);
        }
        return await InstalledPackagesAsync(imageRoot, ct).ConfigureAwait(false);
    }

    private async Task RemovePackageAsync(string imageRoot, string pkg, IProgress<string>? log, CancellationToken ct)
    {
        var r = await Dism(imageRoot, ShortTimeout, ct, "/Remove-Package", $"/PackageName:{pkg}").ConfigureAwait(false);
        if (r.ExitCode is not 0 and not 3010)
        {
            log?.Report($"  kept {pkg} (DISM {r.ExitCode:X8})");
        }
    }

    private async Task LoadAsync(string imageRoot, RegistryHive hive, List<RegistryHive> loaded, CancellationToken ct)
    {
        if (!loaded.Contains(hive))
        {
            await Run(_reg, ["load", HiveMountName(hive), HiveFile(imageRoot, hive)], ShortTimeout, ct).ConfigureAwait(false);
            loaded.Add(hive);
        }
    }

    private async Task DeletePathAsync(string imageRoot, DeleteImagePath d, IProgress<string>? log, CancellationToken ct)
    {
        if (SlimPlan.IsProtectedPath(d.RelativePath))
        {
            throw new InvalidOperationException($"Refusing to delete protected path {d.RelativePath}.");
        }
        var full = Path.Combine(imageRoot, d.RelativePath);
        var isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full))
        {
            return;
        }
        log?.Report(d.Description);
        if (d.TakeOwnership)
        {
            await TakeOwnershipAsync(full, isDirectory, ct).ConfigureAwait(false);
        }
        if (isDirectory)
        {
            await RemoveDirectoryAsync(full, ct).ConfigureAwait(false);
        }
        else
        {
            File.SetAttributes(full, FileAttributes.Normal);
            File.Delete(full);
        }
    }

    private async Task TrimComponentStoreAsync(string imageRoot, TrimComponentStore t, IProgress<string>? log, CancellationToken ct)
    {
        var store = Path.Combine(imageRoot, @"Windows\WinSxS");
        var staging = store + "_keep";
        if (!Directory.Exists(store))
        {
            return;
        }
        if (Directory.Exists(staging))
        {
            throw new InvalidOperationException($"{staging} already exists; the component store was left mid-trim. Rebuild the image.");
        }
        var names = Directory.EnumerateDirectories(store).Select(Path.GetFileName).OfType<string>().ToList();
        var survivors = ComponentStoreSurvivors(names, t.Keep)
            ?? throw new InvalidOperationException("The component store has no servicing stack or manifests matching the keep list; not trimming it.");

        log?.Report($"Taking ownership of the component store ({names.Count} entries). This takes a few minutes...");
        await TakeOwnershipAsync(store, isDirectory: true, ct).ConfigureAwait(false);
        log?.Report($"Keeping {survivors.Count} of {names.Count} component store entries (servicing stack and runtime assemblies)...");
        Directory.CreateDirectory(staging);
        foreach (var name in survivors)
        {
            Directory.Move(Path.Combine(store, name), Path.Combine(staging, name));
        }
        log?.Report("Deleting the rest of the component store...");
        await RemoveDirectoryAsync(store, ct).ConfigureAwait(false);
        Directory.Move(staging, store);
    }

    /// <summary>
    /// Best effort: with /c a single inaccessible entry makes takeown/icacls exit non-zero, so the
    /// deletion that follows is what verifies the result.
    /// </summary>
    private async Task TakeOwnershipAsync(string path, bool isDirectory, CancellationToken ct)
    {
        await _runner.RunAsync(_takeown, isDirectory ? ["/f", path, "/a", "/r"] : ["/f", path, "/a"], LongTimeout, ct).ConfigureAwait(false);
        await _runner.RunAsync(_icacls, isDirectory
            ? [path, "/grant", $"{Administrators}:F", "/t", "/c", "/q"]
            : [path, "/grant", $"{Administrators}:F", "/c", "/q"], LongTimeout, ct).ConfigureAwait(false);
    }

    /// <summary><c>rd /s /q</c> also removes read-only files and does not follow junctions.</summary>
    private async Task RemoveDirectoryAsync(string path, CancellationToken ct)
    {
        await _runner.RunAsync(_cmd, ["/d", "/c", "rd", "/s", "/q", path], LongTimeout, ct).ConfigureAwait(false);
        if (Directory.Exists(path))
        {
            throw new IOException($"Could not delete {path}.");
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