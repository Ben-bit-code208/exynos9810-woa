// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Security.Cryptography;
using System.Text.Json;
using S9Woa.Installer.Core.Image;
using S9Woa.Installer.Core.Processes;
using S9Woa.Installer.Core.Twrp;

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
    private readonly IWinReRecoveryBuilder _winre;

    public ToolsetManager(ToolsetPaths paths, IProcessRunner runner, HttpClient http, IRegistryReader registry,
        ISignatureVerifier verifier, string? wingetPath = null, IWinReRecoveryBuilder? winReBuilder = null)
    {
        Paths = paths;
        _runner = runner;
        _registry = registry;
        _verifier = verifier;
        _releases = new ReleaseClient(http);
        _winget = new WingetClient(runner, wingetPath ?? WingetClient.Locate(paths));
        _winre = winReBuilder ?? new WinReRecoveryBuilder();
        Config = ToolsetConfig.Load(paths.DataDirectory);
    }

    public ToolsetPaths Paths { get; }
    public ToolsetConfig Config { get; }
    public bool WingetAvailable => _winget.WingetPath is not null;

    public string TwrpPayload => Path.Combine(Paths.PayloadDirectory, "twrp-star2lte.img");
    public string UefiPayload => Path.Combine(Paths.PayloadDirectory, "uefi.img");
    public string DriversPayload => Path.Combine(Paths.PayloadDirectory, "drivers");

    /// <summary>The WinRE recovery the installer actually flashes, built from the chosen TWRP.</summary>
    public string TwrpWinrePayload => Path.Combine(Paths.PayloadDirectory, "twrp-winre-star2lte.img");
    private string TwrpWinreInfoPath => TwrpWinrePayload + ".json";

    /// <summary>UEFI images with their <c>firmware.json</c> (one build per supported Windows build).</summary>
    public string UefiCatalogDirectory => Path.Combine(Paths.PayloadDirectory, "uefi");

    /// <summary>Kernel modules for the TWRP kernel (e.g. <c>rwd1_ack.ko</c>, which clears the recovery record).</summary>
    public string TwrpModulesDirectory => Path.Combine(Paths.PayloadDirectory, "twrp-modules");

    public FirmwareCatalog? LoadFirmwareCatalog() => FirmwareCatalog.Load(UefiCatalogDirectory);

    private FirmwareCatalog? LoadCatalogOrNull()
    {
        try
        {
            return LoadFirmwareCatalog();
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            return null;
        }
    }

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
                return DetectTwrp();
            case Tools.Uefi:
                if (LoadCatalogOrNull() is { } catalog && catalog.Present(BootImage.BootPartitionBytes) is { Count: > 0 } present)
                {
                    return ToolStatus.Ready(UefiCatalogDirectory,
                        $"{present.Count} UEFI build(s), for Windows {string.Join(", ", present.Select(i => i.Windows))}");
                }
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

    /// <summary>
    /// The installer flashes a WinRE-look recovery it builds from the chosen TWRP, not the raw
    /// TWRP. This is Ready only when that build exists and is current: for a freshly built image
    /// the recorded builder version and base hash must match, and a prebuilt WinRE image the user
    /// supplied directly is accepted as-is.
    /// </summary>
    private ToolStatus DetectTwrp()
    {
        var info = WinReRecoveryInfo.Load(TwrpWinreInfoPath);
        if (info is null || !File.Exists(TwrpWinrePayload) || BootImage.Validate(TwrpWinrePayload, BootImage.RecoveryPartitionBytes) is not null)
        {
            return File.Exists(TwrpPayload)
                ? ToolStatus.Missing("TWRP is selected but the WinRE recovery is not built yet. Press Set up automatically, or choose the TWRP image again.")
                : ToolStatus.Missing("Open the TWRP page, download twrp-3.7.0_9-0-star2lte.img, then choose it here.");
        }
        if (info.Source == WinReRecoveryInfo.Prebuilt)
        {
            return ToolStatus.Ready(TwrpWinrePayload,
                "Using a prebuilt WinRE-look recovery as-is. Choose the official twrp-3.7.0_9-0-star2lte.img to get this "
                + "installer's recovery (repair tools and the install progress screen).");
        }
        if (info.Builder != WinReTwrpBuilder.BuilderVersion || (File.Exists(TwrpPayload) && TwrpBaseSha256() != info.BaseSha256))
        {
            return ToolStatus.Missing("The WinRE recovery is out of date for this installer or TWRP image. Press Set up automatically to rebuild it.");
        }
        return ToolStatus.Ready(TwrpWinrePayload, "WinRE recovery built from TWRP 3.7.0_9-0.");
    }

    /// <summary>Builds (or accepts) the WinRE recovery from the raw TWRP in the payload.</summary>
    public ToolStatus BuildWinReRecovery(IProgress<string>? log = null)
    {
        if (!File.Exists(TwrpPayload))
        {
            return ToolStatus.Missing("Choose the official TWRP image first.");
        }
        try
        {
            var kind = _winre.Classify(TwrpPayload);
            switch (kind)
            {
                case BaseImageKind.OfficialTwrp:
                    log?.Report("Building the WinRE recovery from TWRP 3.7.0_9-0 (about 20 seconds)...");
                    var modules = Directory.Exists(TwrpModulesDirectory)
                                  && Directory.EnumerateFiles(TwrpModulesDirectory, "*.ko").Any()
                        ? TwrpModulesDirectory
                        : null;
                    var info = _winre.Build(TwrpPayload, TwrpWinrePayload, modules, log);
                    info.Save(TwrpWinreInfoPath);
                    return Detect(Tools.Twrp);

                case BaseImageKind.WinReBuild:
                    log?.Report("The chosen image is already a WinRE recovery; using it as-is.");
                    Directory.CreateDirectory(Path.GetDirectoryName(TwrpWinrePayload)!);
                    File.Copy(TwrpPayload, TwrpWinrePayload, overwrite: true);
                    new WinReRecoveryInfo
                    {
                        Builder = WinReRecoveryInfo.Prebuilt,
                        Source = WinReRecoveryInfo.Prebuilt,
                        Sha256 = Sha256File(TwrpWinrePayload),
                    }.Save(TwrpWinreInfoPath);
                    return Detect(Tools.Twrp);

                default:
                    return new ToolStatus(ToolState.Error,
                        "This is not the official TWRP 3.7.0_9-0 for star2lte. Download twrp-3.7.0_9-0-star2lte.img "
                        + "from twrp.me and choose it, or pick a prebuilt WinRE recovery image.");
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or InvalidDataException)
        {
            return new ToolStatus(ToolState.Error, $"Could not build the WinRE recovery: {e.Message}");
        }
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record HashCacheEntry(string Path, long Length, DateTime WriteTimeUtc, string Sha256);

    // Detect runs on the UI thread every time the Setup page refreshes; hashing the 42 MB base
    // image each time is a visible hitch, so the hash is cached until the file changes.
    private volatile HashCacheEntry? _twrpBaseHash;

    private string TwrpBaseSha256()
    {
        var file = new FileInfo(TwrpPayload);
        var cached = _twrpBaseHash;
        if (cached is not null && cached.Path == file.FullName && cached.Length == file.Length
            && cached.WriteTimeUtc == file.LastWriteTimeUtc)
        {
            return cached.Sha256;
        }
        var sha = Sha256File(file.FullName);
        _twrpBaseHash = new HashCacheEntry(file.FullName, file.Length, file.LastWriteTimeUtc, sha);
        return sha;
    }

    /// <summary>
    /// A TWRP image in a build folder: the official TWRP is always preferred; a prebuilt WinRE
    /// recovery is taken only while no usable recovery is chosen, so it never replaces an official
    /// base. UEFI images (which also carry "star2lte" in their names) are skipped.
    /// </summary>
    private string? FindTwrpInFolder(string folder, EnumerationOptions options)
    {
        string? prebuilt = null;
        foreach (var file in Directory.EnumerateFiles(folder, "*.img", options)
                     .Where(f => !Path.GetFileName(f).Contains("uefi", StringComparison.OrdinalIgnoreCase))
                     .Where(f => BootImage.ValidateTwrp(f) is null)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            switch (_winre.Classify(file))
            {
                case BaseImageKind.OfficialTwrp:
                    return file;
                case BaseImageKind.WinReBuild:
                    prebuilt ??= file;
                    break;
            }
        }
        return prebuilt is not null && Detect(Tools.Twrp).State != ToolState.Ready ? prebuilt : null;
    }

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
            {
                var problem = BootImage.ValidateTwrp(file);
                if (problem is not null)
                {
                    return new ToolStatus(ToolState.Error, problem);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(TwrpPayload)!);
                if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(TwrpPayload), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(file, TwrpPayload, overwrite: true);
                }
                return await Task.Run(() => BuildWinReRecovery(log), ct).ConfigureAwait(false);
            }

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
        var root = ResolveBuildRoot(folder);
        if (!string.Equals(root, folder, StringComparison.OrdinalIgnoreCase))
        {
            log?.Report($"Using {root}, the build folder that contains {folder}.");
            folder = root;
        }
        var results = new Dictionary<string, ToolStatus>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        var catalogFile = Directory.EnumerateFiles(folder, FirmwareCatalog.FileName, options).FirstOrDefault();
        var uefi = Directory.EnumerateFiles(folder, "*.img", options)
            .Where(f => Path.GetFileName(f).Contains("uefi", StringComparison.OrdinalIgnoreCase))
            .Where(f => BootImage.ValidateUefi(f) is null)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (catalogFile is not null)
        {
            try
            {
                var catalog = FirmwareCatalog.Load(Path.GetDirectoryName(catalogFile)!)!;
                if (Directory.Exists(UefiCatalogDirectory))
                {
                    Directory.Delete(UefiCatalogDirectory, recursive: true);
                }
                catalog.CopyTo(UefiCatalogDirectory);
                log?.Report($"Using UEFI builds for Windows {catalog.SupportedBuilds} from {catalogFile}");
                results[Tools.Uefi] = Detect(Tools.Uefi);
            }
            catch (InvalidDataException e)
            {
                results[Tools.Uefi] = new ToolStatus(ToolState.Error, e.Message);
            }
        }
        else if (uefi is null)
        {
            results[Tools.Uefi] = new ToolStatus(ToolState.Error, $"No UEFI boot image (*uefi*.img) found under {folder}.");
        }
        else
        {
            log?.Report($"Using UEFI image {uefi}");
            results[Tools.Uefi] = CopyPayload(uefi, UefiPayload, null, Tools.Uefi);
        }

        var modules = Directory.EnumerateFiles(folder, "*.ko", options).ToList();
        if (modules.Count > 0)
        {
            Directory.CreateDirectory(TwrpModulesDirectory);
            foreach (var module in modules)
            {
                File.Copy(module, Path.Combine(TwrpModulesDirectory, Path.GetFileName(module)), overwrite: true);
            }
            log?.Report($"Using {modules.Count} TWRP kernel module(s): {string.Join(", ", modules.Select(Path.GetFileName))}");
        }

        // A TWRP image in the folder becomes the recovery's base; freshly imported modules also
        // need a rebuild so the WinRE recovery carries them. Either way, build once.
        var rebuild = modules.Count > 0 && File.Exists(TwrpPayload)
            && WinReRecoveryInfo.Load(TwrpWinreInfoPath)?.Source != WinReRecoveryInfo.Prebuilt;
        if (FindTwrpInFolder(folder, options) is { } twrpImage
            && !(File.Exists(TwrpPayload) && Sha256File(twrpImage) == TwrpBaseSha256()))
        {
            log?.Report($"Using TWRP image {twrpImage}");
            Directory.CreateDirectory(Path.GetDirectoryName(TwrpPayload)!);
            File.Copy(twrpImage, TwrpPayload, overwrite: true);
            rebuild = true;
        }
        if (rebuild)
        {
            results[Tools.Twrp] = BuildWinReRecovery(log);
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

    /// <summary>
    /// The folder to import from. Picking a subfolder of the build folder (e.g. its <c>drivers</c>
    /// folder from the drivers row) would miss the firmware beside it, so a folder without any UEFI
    /// image falls back to its parent when the parent has one.
    /// </summary>
    internal static string ResolveBuildRoot(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (HasFirmware(full))
        {
            return full;
        }
        var parent = Directory.GetParent(full)?.FullName;
        return parent is not null && HasFirmware(parent) ? parent : full;
    }

    private static bool HasFirmware(string folder) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
            .Select(Path.GetFileName)
            .Any(n => n is not null && (n.Equals(FirmwareCatalog.FileName, StringComparison.OrdinalIgnoreCase)
                || (n.Contains("uefi", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".img", StringComparison.OrdinalIgnoreCase))));

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
    /// and release payloads (unless a build folder is configured), and the WinRE recovery
    /// built from a TWRP image that was already chosen (for example after an installer
    /// update bumps the builder). Tools that need a user decision (Samsung installer,
    /// the TWRP download itself) are left for the Setup page.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ToolStatus>> AutoSetupAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (Config.BuildFolder is { } folder && Directory.Exists(folder)
            && (Detect(Tools.Uefi).State != ToolState.Ready || Detect(Tools.Drivers).State != ToolState.Ready))
        {
            // Off the caller's thread: importing can include building the WinRE recovery.
            await Task.Run(() => UseBuildFolder(folder, log), ct).ConfigureAwait(false);
        }
        if (File.Exists(TwrpPayload) && Detect(Tools.Twrp).State != ToolState.Ready)
        {
            var twrp = await Task.Run(() => BuildWinReRecovery(log), ct).ConfigureAwait(false);
            if (twrp.State != ToolState.Ready)
            {
                log?.Report($"{Tools.Get(Tools.Twrp).Name}: {twrp.Detail}");
            }
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
