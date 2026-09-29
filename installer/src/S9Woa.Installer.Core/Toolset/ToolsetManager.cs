// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Text.Json;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Toolset;

/// <summary>Toolset choices remembered across runs (<c>toolset.json</c> in the data folder).</summary>
public sealed class ToolsetConfig
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public int SchemaVersion { get; set; } = 1;
    public bool SetupCompleted { get; set; }
    public string ReleaseRepo { get; set; } = "ntdevlabs/exynos9810-woa";
    public string? BuildFolder { get; set; }

    /// <summary>User-chosen program paths that take precedence over detection, by tool id.</summary>
    public Dictionary<string, string> Overrides { get; set; } = [];

    public static ToolsetConfig Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "toolset.json");
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ToolsetConfig>(File.ReadAllText(path), Json) ?? new ToolsetConfig()
                : new ToolsetConfig();
        }
        catch (JsonException)
        {
            return new ToolsetConfig();
        }
    }

    public void Save(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "toolset.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

/// <summary>
/// Detects, acquires and remembers everything the installer needs: programs
/// (adb, Heimdall, Zadig) from winget or a chosen file, the Samsung USB driver
/// from Samsung's signed installer, and the phone payloads (TWRP, UEFI, drivers)
/// from a verified GitHub release, a local build folder, or a chosen file.
/// </summary>
public sealed class ToolsetManager
{
    public const string SamsungUsbService = "dg_ssudbus";
    public const string SamsungSigner = "Samsung Electronics";
    public const string UfsInf = "Exynos9810Ufs.inf";
    private static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(30);

    private readonly IProcessRunner _runner;
    private readonly IRegistryReader _registry;
    private readonly ISignatureVerifier _verifier;
    private readonly ReleaseClient _releases;
    private readonly WingetClient _winget;

    public ToolsetManager(ToolsetPaths paths, IProcessRunner runner, HttpClient http, IRegistryReader registry,
        ISignatureVerifier verifier, string? wingetPath = null)
    {
        Paths = paths;
        _runner = runner;
        _registry = registry;
        _verifier = verifier;
        _releases = new ReleaseClient(http);
        _winget = new WingetClient(runner, wingetPath ?? WingetClient.Locate(paths));
        Config = ToolsetConfig.Load(paths.DataDirectory);
    }

    public ToolsetPaths Paths { get; }
    public ToolsetConfig Config { get; }
    public bool WingetAvailable => _winget.WingetPath is not null;

    public string TwrpPayload => Path.Combine(Paths.PayloadDirectory, "twrp-star2lte.img");
    public string UefiPayload => Path.Combine(Paths.PayloadDirectory, "uefi.img");
    public string DriversPayload => Path.Combine(Paths.PayloadDirectory, "drivers");

    public void SaveConfig() => Config.Save(Paths.DataDirectory);

    public IReadOnlyDictionary<string, ToolStatus> DetectAll() =>
        Tools.All.ToDictionary(t => t.Id, t => Detect(t.Id));

    /// <summary>Every required tool is ready; optional ones (the Heimdall fallback) never block.</summary>
    public static bool IsComplete(IReadOnlyDictionary<string, ToolStatus> statuses) =>
        Tools.All.All(t => !t.Required || (statuses.TryGetValue(t.Id, out var s) && s.State == ToolState.Ready));

    public string? ResolvePath(string id) => Detect(id) is { State: ToolState.Ready, Path: { } p } ? p : null;

    public ToolStatus Detect(string id)
    {
        var def = Tools.Get(id);
        Config.Overrides.TryGetValue(id, out var overridePath);
        switch (id)
        {
            case Tools.Adb:
                return Program(def, ToolLocator.Find(Paths, "adb.exe", def.WingetId, overridePath, @"tools\platform-tools"));
            case Tools.Heimdall:
                return Program(def, ToolLocator.Find(Paths, "heimdall.exe", def.WingetId, overridePath, @"tools\heimdall"));
            case Tools.Zadig:
                return Program(def, ToolLocator.Find(Paths, "zadig*.exe", def.WingetId, overridePath, @"tools\zadig"));
            case Tools.SamsungUsb:
                return _registry.KeyExists($@"SYSTEM\CurrentControlSet\Services\{SamsungUsbService}")
                    ? new ToolStatus(ToolState.Ready, "Installed.")
                    : ToolStatus.Missing("Download \"Samsung USB Driver for Mobile Phones\" from Samsung, then choose the installer here.");
            case Tools.DownloadModeDriver:
                return DownloadModeDriver.Detect(_registry);
            case Tools.Twrp:
                return Payload(
                    (TwrpPayload, BootImage.RecoveryPartitionBytes),
                    (Path.Combine(Paths.BundledPayloadDirectory, "twrp.img"), BootImage.RecoveryPartitionBytes),
                    (Path.Combine(Paths.BundledPayloadDirectory, "recovery.img"), BootImage.RecoveryPartitionBytes))
                    ?? ToolStatus.Missing("Open the TWRP page, download the latest twrp-*-star2lte.img, then choose it here.");
            case Tools.Uefi:
                return Payload(
                    (UefiPayload, BootImage.BootPartitionBytes),
                    (Path.Combine(Paths.BundledPayloadDirectory, "uefi.img"), BootImage.BootPartitionBytes))
                    ?? ToolStatus.Missing("Download it from the project release, or choose your build folder.");
            case Tools.Drivers:
                foreach (var dir in new[] { DriversPayload, Path.Combine(Paths.BundledPayloadDirectory, "drivers") })
                {
                    var packages = BuiltDriverPackages(dir);
                    if (packages.Any(p => File.Exists(Path.Combine(p, UfsInf))))
                    {
                        return ToolStatus.Ready(dir, $"{packages.Count} driver package(s) in {dir}");
                    }
                }
                return ToolStatus.Missing("Download them from the project release, or choose your build folder.");
            default:
                throw new ArgumentOutOfRangeException(nameof(id));
        }
    }

    private static ToolStatus Program(ToolDefinition def, string? path) =>
        path is null
            ? ToolStatus.Missing(def.WingetId is null ? "Not found." : $"Not found. Install it with winget ({def.WingetId}) or choose the file.")
            : ToolStatus.Ready(path);

    private static ToolStatus? Payload(params (string File, long Max)[] candidates)
    {
        foreach (var (file, max) in candidates)
        {
            if (File.Exists(file) && BootImage.Validate(file, max) is null)
            {
                return ToolStatus.Ready(file, BootImage.Describe(file));
            }
        }
        return null;
    }

    /// <summary>Folders under <paramref name="root"/> holding a built driver package (an .inf next to a .sys).</summary>
    public static IReadOnlyList<string> BuiltDriverPackages(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(root, "*.inf", options)
            .Select(f => Path.GetDirectoryName(f)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => Directory.EnumerateFiles(d, "*.sys").Any())
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ToolStatus> InstallWingetAsync(string id, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var def = Tools.Get(id);
        if (def.WingetId is null)
        {
            throw new InvalidOperationException($"{def.Name} is not installed with winget.");
        }
        log?.Report($"Installing {def.Name} with winget ({def.WingetId})...");
        var r = await _winget.InstallAsync(def.WingetId, ct).ConfigureAwait(false);
        var status = Detect(id);
        if (status.State == ToolState.Ready)
        {
            log?.Report($"{def.Name}: {status.Path}");
            return status;
        }
        var tail = string.Join(' ', (r.StdOut + r.StdErr).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3)).Trim();
        return new ToolStatus(ToolState.Error, $"winget could not install {def.WingetId} (exit {r.ExitCode}). {tail}");
    }

    public async Task<ToolStatus> UseFileAsync(string id, string file, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(file))
        {
            return new ToolStatus(ToolState.Error, "The file does not exist.");
        }
        var def = Tools.Get(id);
        switch (id)
        {
            case Tools.Adb or Tools.Heimdall or Tools.Zadig:
                if (!FileSystemName(def.FilePattern!, Path.GetFileName(file)))
                {
                    return new ToolStatus(ToolState.Error, $"Choose {def.FilePattern}.");
                }
                Config.Overrides[id] = file;
                SaveConfig();
                return Detect(id);

            case Tools.SamsungUsb:
                return await RunSamsungInstallerAsync(file, log, ct).ConfigureAwait(false);

            case Tools.Twrp:
                return CopyPayload(file, TwrpPayload, BootImage.ValidateTwrp(file), id);

            case Tools.Uefi:
                return CopyPayload(file, UefiPayload, BootImage.ValidateUefi(file), id);

            default:
                return new ToolStatus(ToolState.Error, $"{def.Name} cannot be provided as a single file.");
        }
    }

    private static bool FileSystemName(string pattern, string name) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);

    private ToolStatus CopyPayload(string source, string destination, string? problem, string id)
    {
        if (problem is not null)
        {
            return new ToolStatus(ToolState.Error, problem);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, destination, overwrite: true);
        }
        return Detect(id);
    }

    private async Task<ToolStatus> RunSamsungInstallerAsync(string installer, IProgress<string>? log, CancellationToken ct)
    {
        var signature = _verifier.Verify(installer);
        if (!signature.Trusted || signature.Subject?.Contains(SamsungSigner, StringComparison.OrdinalIgnoreCase) != true)
        {
            return new ToolStatus(ToolState.Error,
                $"Refusing to run {Path.GetFileName(installer)}: it is not validly signed by {SamsungSigner} "
                + $"(signer: {signature.Subject ?? "none"}). Download it again from Samsung's site.");
        }
        log?.Report("Running the Samsung USB driver installer. Follow its prompts...");
        await _runner.RunAsync(installer, [], InstallerTimeout, ct).ConfigureAwait(false);
        var status = Detect(Tools.SamsungUsb);
        return status.State == ToolState.Ready
            ? status
            : new ToolStatus(ToolState.Error, "The installer finished, but the Samsung USB driver is not registered. Run it again, or restart the PC.");
    }

    /// <summary>Imports the UEFI image and built driver packages from a local build folder and remembers it.</summary>
    public IReadOnlyDictionary<string, ToolStatus> UseBuildFolder(string folder, IProgress<string>? log = null)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(folder);
        }
        var results = new Dictionary<string, ToolStatus>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        var uefi = Directory.EnumerateFiles(folder, "*.img", options)
            .Where(f => Path.GetFileName(f).Contains("uefi", StringComparison.OrdinalIgnoreCase))
            .Where(f => BootImage.ValidateUefi(f) is null)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (uefi is null)
        {
            results[Tools.Uefi] = new ToolStatus(ToolState.Error, $"No UEFI boot image (*uefi*.img) found under {folder}.");
        }
        else
        {
            log?.Report($"Using UEFI image {uefi}");
            results[Tools.Uefi] = CopyPayload(uefi, UefiPayload, null, Tools.Uefi);
        }

        var packages = BuiltDriverPackages(folder);
        if (!packages.Any(p => File.Exists(Path.Combine(p, UfsInf))))
        {
            results[Tools.Drivers] = new ToolStatus(ToolState.Error,
                $"No built UFS driver package ({UfsInf} next to a .sys) found under {folder}.");
        }
        else
        {
            if (Directory.Exists(DriversPayload))
            {
                Directory.Delete(DriversPayload, recursive: true);
            }
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in packages)
            {
                var name = Path.GetFileName(package);
                for (var i = 2; !used.Add(name); i++)
                {
                    name = $"{Path.GetFileName(package)}-{i}";
                }
                log?.Report($"Using driver package {package}");
                CopyDirectory(package, Path.Combine(DriversPayload, name));
            }
            results[Tools.Drivers] = Detect(Tools.Drivers);
        }

        Config.BuildFolder = folder;
        SaveConfig();
        return results;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }

    public async Task<ToolStatus> DownloadReleaseAsync(string id, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var release = await _releases.GetLatestAsync(Config.ReleaseRepo, ct).ConfigureAwait(false);
        switch (id)
        {
            case Tools.Uefi:
            {
                var temp = UefiPayload + ".new";
                await _releases.DownloadVerifiedAsync(release, "uefi.img", temp, log, ct).ConfigureAwait(false);
                var problem = BootImage.ValidateUefi(temp);
                if (problem is not null)
                {
                    File.Delete(temp);
                    return new ToolStatus(ToolState.Error, $"Release uefi.img: {problem}");
                }
                File.Move(temp, UefiPayload, overwrite: true);
                return Detect(id);
            }
            case Tools.Drivers:
            {
                var zip = Path.Combine(Paths.ToolsetDirectory, "drivers.zip");
                await _releases.DownloadVerifiedAsync(release, "drivers.zip", zip, log, ct).ConfigureAwait(false);
                try
                {
                    ReleaseClient.ExtractSafely(zip, DriversPayload);
                }
                finally
                {
                    File.Delete(zip);
                }
                var status = Detect(id);
                return status.State == ToolState.Ready
                    ? status
                    : new ToolStatus(ToolState.Error, $"Release drivers.zip does not contain a built {UfsInf} package.");
            }
            default:
                throw new InvalidOperationException($"{Tools.Get(id).Name} is not published in releases.");
        }
    }

    /// <summary>
    /// Provides everything that can be provided without the user: winget programs
    /// and release payloads (unless a build folder is configured). Tools that need a
    /// user decision (Samsung installer, TWRP file) are left for the Setup page.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ToolStatus>> AutoSetupAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (Config.BuildFolder is { } folder && Directory.Exists(folder)
            && (Detect(Tools.Uefi).State != ToolState.Ready || Detect(Tools.Drivers).State != ToolState.Ready))
        {
            UseBuildFolder(folder, log);
        }
        foreach (var def in Tools.All)
        {
            ct.ThrowIfCancellationRequested();
            if (Detect(def.Id).State is ToolState.Ready or ToolState.Deferred)
            {
                continue;
            }
            try
            {
                if (def.Sources.HasFlag(ToolSource.Winget) && WingetAvailable)
                {
                    await InstallWingetAsync(def.Id, log, ct).ConfigureAwait(false);
                }
                else if (def.Sources.HasFlag(ToolSource.Release) && Config.BuildFolder is null)
                {
                    await DownloadReleaseAsync(def.Id, log, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException or TimeoutException)
            {
                log?.Report($"{def.Name}: {e.Message}");
            }
        }
        return DetectAll();
    }
}
