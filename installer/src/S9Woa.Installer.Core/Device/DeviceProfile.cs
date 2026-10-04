// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Device;

/// <summary>How far a phone is from a working installation. Only the tiers up to
/// <see cref="SupportTier.Experimental"/> are allowed to write anything to a device.</summary>
public enum SupportTier
{
    /// <summary>
    /// The installer runs on this phone, every stage, to the Windows desktop. Exactly one
    /// configuration per tier is validated; others of the same family are near-certain but unproven.
    /// </summary>
    Validated,

    /// <summary>
    /// The whole install path is implemented and the firmware exists, but nobody has run this exact
    /// phone through it end to end. Allowed, with a warning on the identification step.
    /// </summary>
    Experimental,

    /// <summary>
    /// Known hardware with the right ideas, but something required is missing - a UEFI build, an
    /// unlock path, a known partition layout. Refused, and the phone is told exactly what is missing.
    /// </summary>
    Candidate,

    /// <summary>
    /// Refused on purpose: a different chipset or a bootloader that cannot be unlocked. Kept in the
    /// catalog so the installer can say <em>why</em> instead of "not supported".
    /// </summary>
    Excluded,
}

/// <summary>Instruction set the Windows build for a phone has to be.</summary>
public enum TargetArchitecture
{
    Arm64,
    X86,
    X64,
}

/// <summary>Byte geometry of one phone's UFS unit, as validated for a reference unit.</summary>
public sealed record ReferenceGeometry(
    long DiskBytes,
    long WindowsOffset,
    long WindowsBytes,
    long CacheBytes)
{
    /// <summary>
    /// The validated Galaxy S9+ (SM-G965F, 128 GB) layout. Every other model is measured from the
    /// phone itself instead - see <see cref="Deploy.PartitionLayout"/> - because USERDATA shrinks as
    /// the storage grows and its offset moves with it.
    /// </summary>
    public static ReferenceGeometry GalaxyS9Plus128Gb { get; } =
        new(63_963_136_000, 6_951_534_592, 57_004_785_664, 629_145_600);
}

/// <summary>One Samsung model code inside a profile.</summary>
public sealed record DeviceVariant(
    string Model,
    string ModelCode,
    string Region,
    string? ValidatedBootloader = null);

/// <summary>
/// Everything that differs between phones, in one place: identity, the firmware and recovery that
/// go with it, the partitions the installer writes, and how far it has been proven. The engine asks
/// the catalog instead of hard-coding a model name, a TWRP page or a partition size.
/// </summary>
public sealed record DeviceProfile
{
    public required string Id { get; init; }

    /// <summary>Samsung board codename, e.g. <c>star2lte</c>. Also the TWRP file name's suffix.</summary>
    public required string Codename { get; init; }

    public required string MarketingName { get; init; }
    public required string SoC { get; init; }

    /// <summary>Substring <c>ro.boot.hardware</c> must hold for this profile (Exynos reports it).</summary>
    public required string HardwareToken { get; init; }

    public required TargetArchitecture Architecture { get; init; }
    public required SupportTier Tier { get; init; }
    public required IReadOnlyList<DeviceVariant> Variants { get; init; }

    /// <summary>Where the official recovery for this board is downloaded from.</summary>
    public string? TwrpPage { get; init; }

    /// <summary>Official recovery file name, e.g. <c>twrp-3.7.0_9-0-star2lte.img</c>.</summary>
    public string? TwrpFileName { get; init; }

    /// <summary>
    /// Role name to partition-node suffix, for phones whose kernel exposes no
    /// <c>/dev/block/by-name</c> links and therefore no names at all. A Fire HD 8 reports
    /// <c>mmcblk0p11</c> and nothing says that this one is SYSTEM; this is where that fact lives,
    /// and it was read off the phone's own <c>/proc/mounts</c> and the contents of each partition
    /// rather than off a vendor file, because the vendor file describes a different tablet.
    /// Empty for phones that have real partition names.
    /// </summary>
    public IReadOnlyDictionary<string, string> PartitionRoles { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Size the BOOT partition must hold for the UEFI image to fit.</summary>
    public long BootPartitionBytes { get; init; }

    /// <summary>Size the RECOVERY partition must hold for the re-skinned recovery to fit.</summary>
    public long RecoveryPartitionBytes { get; init; }

    /// <summary>Prefix of this board's UEFI image files, e.g. <c>star2lte-uefi-</c>.</summary>
    public string? UefiImagePrefix { get; init; }

    /// <summary>Display scaling the OOBE answer file asks for on this screen (about 100 % = 96).</summary>
    public int Dpi { get; init; } = 275;

    /// <summary>
    /// Measured geometry, present only where a unit was validated. Null means the installer must
    /// read the layout off the phone (<see cref="Deploy.PartitionProbe"/>) and refuses without it.
    /// </summary>
    public ReferenceGeometry? Geometry { get; init; }

    /// <summary>Why this profile is not usable yet; empty for the tiers that can install.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    /// <summary>Anything a user of this phone should know before trying.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The tiers that may write to the phone.</summary>
    public bool Installable => Tier is SupportTier.Validated or SupportTier.Experimental;

    /// <summary>True when this board's profile can carry the install all the way to Windows.</summary>
    public bool HasFirmware => UefiImagePrefix is not null && Blockers.Count == 0;

    public IEnumerable<string> ModelCodes => Variants.Select(v => v.ModelCode);

    public IEnumerable<string> Models => Variants.Select(v => v.Model);

    /// <summary>The model names and codes of this profile, for error messages.</summary>
    public string ModelList => string.Join(", ", Variants.Select(v => v.Model).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>The recovery file official downloads end in for this board, e.g. <c>-star2lte.img</c>.</summary>
    public string TwrpFileSuffix => $"-{Codename}.img";

    public override string ToString() => $"{MarketingName} ({string.Join("/", ModelCodes)})";
}

/// <summary>
/// The phones this installer knows. Adding a model means adding an entry here (or to
/// <see cref="Excluded"/>), never changing code elsewhere: the eligibility checks, the toolset, the
/// image build and the partition writes all read what they need from the matched profile.
/// </summary>
public static class DeviceCatalog
{
    // An assumption, not a measurement: the Exynos 9810 boards are expected to use the same 55 MiB
    // BOOT / 65 MiB RECOVERY windows and the same partition names, so a port starts from these. The
    // partition probe measures the real sizes per phone, and a board that disagrees fails the check
    // instead of being written to.
    private const long BootBytes = 14080L * 4096;
    private const long RecoveryBytes = 16638L * 4096;

    /// <summary>
    /// Galaxy S9+ (Exynos 9810, "star2lte"). The one board this project is validated on. The N is
    /// the Korean Exynos variant; the U/U1/W/0 are Snapdragon and are listed as excluded below.
    /// </summary>
    public static DeviceProfile GalaxyS9Plus { get; } = new()
    {
        Id = "star2lte",
        Codename = "star2lte",
        MarketingName = "Samsung Galaxy S9+ (Exynos 9810)",
        SoC = "Exynos 9810",
        HardwareToken = "exynos9810",
        Architecture = TargetArchitecture.Arm64,
        Tier = SupportTier.Validated,
        Variants =
        [
            new("SM-G965F", "G965F", "Global", "G965FXXUHFVG4"),
            new("SM-G965F/DS", "G965F", "Global dual-SIM"),
            // No validated bootloader: nothing has run the Korean variant through this installer yet.
            new("SM-G965N", "G965N", "Korea"),
        ],
        TwrpPage = "https://twrp.me/samsung/samsunggalaxys9plus.html",
        TwrpFileName = "twrp-3.7.0_9-0-star2lte.img",
        BootPartitionBytes = BootBytes,
        RecoveryPartitionBytes = RecoveryBytes,
        UefiImagePrefix = "star2lte-uefi-",
        Dpi = 275,
        Geometry = ReferenceGeometry.GalaxyS9Plus128Gb,
        Notes =
        [
            "The SM-G965F layout is measured by hand and used as the reference; other storage sizes and models are read off the phone.",
        ],
    };

    /// <summary>
    /// Galaxy S9 (Exynos 9810, "starlte"). Same SoC as the S9+ and the same families of drivers, but
    /// a different board: its own device tree, its own recovery, its own partition layout and a
    /// smaller panel. It needs a UEFI build of its own - see docs/porting.md.
    /// </summary>
    public static DeviceProfile GalaxyS9 { get; } = new()
    {
        Id = "starlte",
        Codename = "starlte",
        MarketingName = "Samsung Galaxy S9 (Exynos 9810)",
        SoC = "Exynos 9810",
        HardwareToken = "exynos9810",
        Architecture = TargetArchitecture.Arm64,
        Tier = SupportTier.Candidate,
        Variants =
        [
            new("SM-G960F", "G960F", "Global"),
            new("SM-G960F/DS", "G960F", "Global dual-SIM"),
            new("SM-G960N", "G960N", "Korea"),
        ],
        TwrpPage = "https://twrp.me/samsung/samsunggalaxys9.html",
        TwrpFileName = "twrp-3.7.0_9-0-starlte.img",
        BootPartitionBytes = BootBytes,
        RecoveryPartitionBytes = RecoveryBytes,
        UefiImagePrefix = "starlte-uefi-",
        Dpi = 300,
        Blockers =
        [
            "No UEFI image has been built for starlte. The firmware in firmware/ is the star2lte build and its device tree does not describe this board.",
            "The partition layout of this phone has not been measured, so the Windows image cannot be built for it yet.",
            "The UEFI build is what most of the work is: a star2lte port needs its own Platform package (see docs/porting.md).",
        ],
        Notes =
        [
            "4 GB of RAM rather than the S9+'s 6 GB, which is tight but workable for ARM64 Windows.",
        ],
    };

    /// <summary>
    /// Galaxy Note9 (Exynos 9810, "crownlte"). Also its own board, and its memory size differs from
    /// both other Exynos 9810 phones, so even a copied star2lte firmware would not be correct.
    /// </summary>
    public static DeviceProfile GalaxyNote9 { get; } = new()
    {
        Id = "crownlte",
        Codename = "crownlte",
        MarketingName = "Samsung Galaxy Note9 (Exynos 9810)",
        SoC = "Exynos 9810",
        HardwareToken = "exynos9810",
        Architecture = TargetArchitecture.Arm64,
        Tier = SupportTier.Candidate,
        Variants =
        [
            new("SM-N960F", "N960F", "Global"),
            new("SM-N960F/DS", "N960F", "Global dual-SIM"),
            new("SM-N960N", "N960N", "Korea"),
        ],
        TwrpPage = "https://twrp.me/samsung/samsunggalaxyNote9.html",
        TwrpFileName = "twrp-3.7.0_9-0-crownlte.img",
        BootPartitionBytes = BootBytes,
        RecoveryPartitionBytes = RecoveryBytes,
        UefiImagePrefix = "crownlte-uefi-",
        Dpi = 275,
        Blockers =
        [
            "No UEFI image has been built for crownlte; the firmware in firmware/ is the star2lte build.",
            "The partition layout of this phone has not been measured.",
            "The S Pen and the Note9's larger memory mean even a copied firmware would need its own RAM manager and handoff table.",
        ],
    };

    /// <summary>
    /// Boards that are deliberately out of reach, kept so the installer can explain itself. These
    /// are the Snapdragon counterparts of the three Exynos boards above: the same phone, a different
    /// SoC, a storage stack this project has no drivers for and a bootloader that cannot be unlocked.
    /// </summary>
    public static IReadOnlyList<DeviceProfile> Excluded { get; } =
    [
        new()
        {
            Id = "star2qlte",
            Codename = "star2qlte",
            MarketingName = "Samsung Galaxy S9+ (Snapdragon 845)",
            SoC = "Snapdragon 845",
            HardwareToken = "qcom",
            Architecture = TargetArchitecture.Arm64,
            Tier = SupportTier.Excluded,
            Variants =
            [
                new("SM-G965U", "G965U", "USA"),
                new("SM-G965U1", "G965U1", "USA"),
                new("SM-G965W", "G965W", "Canada"),
                new("SM-G9650", "G9650", "China / LATAM"),
            ],
            Blockers = ["It is not an Exynos 9810: it has a Snapdragon 845, which this project has no drivers, firmware or boot path for."],
        },
        new()
        {
            Id = "starqlte",
            Codename = "starqlte",
            MarketingName = "Samsung Galaxy S9 (Snapdragon 845)",
            SoC = "Snapdragon 845",
            HardwareToken = "qcom",
            Architecture = TargetArchitecture.Arm64,
            Tier = SupportTier.Excluded,
            Variants =
            [
                new("SM-G960U", "G960U", "USA"),
                new("SM-G960U1", "G960U1", "USA"),
                new("SM-G960W", "G960W", "Canada"),
                new("SM-G9600", "G9600", "China / LATAM"),
            ],
            Blockers = ["It is not an Exynos 9810: it has a Snapdragon 845, which this project has no drivers, firmware or boot path for."],
        },
        new()
        {
            Id = "crownqlte",
            Codename = "crownqlte",
            MarketingName = "Samsung Galaxy Note9 (Snapdragon 845)",
            SoC = "Snapdragon 845",
            HardwareToken = "qcom",
            Architecture = TargetArchitecture.Arm64,
            Tier = SupportTier.Excluded,
            Variants =
            [
                new("SM-N960U", "N960U", "USA"),
                new("SM-N960U1", "N960U1", "USA"),
                new("SM-N960W", "N960W", "Canada"),
                new("SM-N9600", "N9600", "China"),
            ],
            Blockers = ["It is not an Exynos 9810: it has a Snapdragon 845, which this project has no drivers, firmware or boot path for."],
        },
        new()
        {
            Id = "e9810",
            Codename = "e9810",
            MarketingName = "Exynos 9810 devices without a Samsung phone board",
            SoC = "Exynos 9810",
            HardwareToken = "exynos9810",
            Architecture = TargetArchitecture.Arm64,
            Tier = SupportTier.Candidate,
            Variants =
            [
                new("SM-T865", "T865", "Galaxy Tab Active2"),
                new("SM-T870", "T870", "Galaxy Tab Active3"),
                new("SM-N970F", "N970F", "Galaxy Note10 Lite"),
            ],
            Blockers =
            [
                "No board of this family has a UEFI build here; each one needs its own device tree and Platform package.",
                "No partition layout has been measured for these tablets, and their storage is a different layout to the phones'.",
            ],
            Notes = ["Listed so the installer recognises them and says what is missing, instead of failing as an unknown model."],
        },
        new()
        {
            Id = "qcom-arm64",
            Codename = "snapdragon",
            MarketingName = "Other Snapdragon phones (Motorola, Google, Huawei and the rest)",
            SoC = "Qualcomm Snapdragon",
            HardwareToken = "qcom",
            Architecture = TargetArchitecture.Arm64,
            Tier = SupportTier.Excluded,
            Variants = [],
            Blockers =
            [
                "Snapdragon devices are out of scope for this project: the firmware here targets Exynos 9810's UFS, HSI2C and boot chain, none of which a Snapdragon phone has.",
                "Most current Snapdragon phones cannot have their bootloader unlocked at all, and Windows on ARM on them would have no storage driver to boot from.",
            ],
            Notes = ["Kept as a named refusal so the installer can explain the chipset instead of only listing one supported model."],
        },
        new()
        {
            Id = "ford",
            Codename = "ford",
            MarketingName = "Amazon Fire HD 8 (8th generation)",
            SoC = "MediaTek MT8127",
            HardwareToken = "mt8127",
            Architecture = TargetArchitecture.Arm64,
            Tier = SupportTier.Candidate,
            Variants =
            [
                new("Fire", "ford", "all regions"),
            ],
            // No names exist on this device, so these are facts rather than defaults. Every entry was
            // read off the tablet: SYSTEM, CACHE and USERDATA from its own /etc/fstab and
            // /proc/mounts, BOOT and RECOVERY from the "ANDROID!" boot-image header at offset 0 of
            // p5 and p6, TEE1/TEE2 from the "TEE" tag in p9 and p10.
            PartitionRoles = new Dictionary<string, string>
            {
                ["BOOT"] = "p5",
                ["RECOVERY"] = "p6",
                ["SYSTEM"] = "p11",
                ["CACHE"] = "p12",
                ["USERDATA"] = "p13",
            },
            BootPartitionBytes = 16L * 1024 * 1024,
            RecoveryPartitionBytes = 16L * 1024 * 1024,
            Blockers =
            [
                "It is a MediaTek MT8127, not an Exynos 9810: the LK bootloader, the TrustZone firmware and the boot chain here are different parts, and none of them are in this repository.",
                "Its storage is 512-byte sectors. The Windows image build lays out a 4Kn volume, so it would have to be rebuilt for this device before it could be written.",
                "No UEFI build for this board exists here. Windows on ARM needs an EDK2 Platform package for the MT8127, and none has been written.",
                "USERDATA is about 13 GiB, and Windows on ARM needs well over 32 GiB to install. The tablet cannot hold it as it is partitioned.",
            ],
            Notes =
            [
                "The roles above were confirmed on the tablet; the MTK scatter file shipped with the Fire tooling describes a different storage layout and must not be used to fill this in.",
                "A MediaTek tablet of this class is unlocked through the boot ROM rather than through a fused flag: a BROM payload opens the LK bootloader, and fastboot follows. No copy of those payloads is intact on this machine.",
            ],
        },
    ];

    /// <summary>Every profile the catalog knows, installable ones first.</summary>
    public static IReadOnlyList<DeviceProfile> All { get; } =
        [GalaxyS9Plus, GalaxyS9, GalaxyNote9, .. Excluded];

    /// <summary>
    /// The Fire HD 8 profile, named because its partition roles were measured on a real tablet and
    /// are worth referring to directly. It stays in <see cref="Excluded"/>: measured is not the same
    /// as able to install.
    /// </summary>
    public static DeviceProfile Ford { get; } = Excluded.Single(p => p.Id == "ford");

    /// <summary>Profiles the installer will carry through an installation.</summary>
    public static IReadOnlyList<DeviceProfile> InstallableProfiles { get; } =
        All.Where(p => p.Installable).ToList();

    /// <summary>Profiles that are known but refused, with their blockers.</summary>
    public static IReadOnlyList<DeviceProfile> KnownButRefused { get; } =
        All.Where(p => !p.Installable).ToList();

    /// <summary>The profile a reported model belongs to, or null when it is not in the catalog.</summary>
    /// <remarks>
    /// Matching is exact per model code, then by model name, so <c>SM-G965U</c> (Snapdragon) never
    /// falls into <c>SM-G965F</c> (Exynos) and a dual-SIM name resolves to its single-SIM profile.
    /// </remarks>
    public static DeviceProfile? ForModel(string? model, IReadOnlyList<DeviceProfile>? profiles = null)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }
        var wanted = model.Trim();
        return Search(profiles).FirstOrDefault(p => p.Variants.Any(v => v.Model.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
               ?? Search(profiles).FirstOrDefault(p => p.Variants.Any(v =>
                   wanted.StartsWith(v.Model, StringComparison.OrdinalIgnoreCase)
                   && (wanted.Length == v.Model.Length
                       || !char.IsLetterOrDigit(wanted[v.Model.Length]))));
    }

    /// <summary>The profile for a board codename, as reported by a recovery's ro.product.device.</summary>
    public static DeviceProfile? ForCodename(string? codename, IReadOnlyList<DeviceProfile>? profiles = null) =>
        Search(profiles).FirstOrDefault(p => p.Codename.Equals(codename?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The profile for the model code at the start of a Samsung firmware build string.</summary>
    public static DeviceProfile? ForBootloader(string? bootloader, IReadOnlyList<DeviceProfile>? profiles = null) =>
        Search(profiles).FirstOrDefault(p => p.ModelCodes.Any(code =>
            SamsungBuild.TryParse(bootloader, code) is not null));

    /// <summary>
    /// The single variant a firmware build string identifies, so a phone that reported a whole
    /// profile's model list before (a download-mode string carries only the bootloader version) is
    /// pinned to the exact variant that string belongs to - <c>G965FXXUHFVG4</c> is an
    /// <c>SM-G965F</c>, not just "some Galaxy S9+".
    /// </summary>
    public static DeviceVariant? VariantForBootloader(string? bootloader, IReadOnlyList<DeviceProfile>? profiles = null) =>
        Search(profiles).SelectMany(p => p.Variants)
            .FirstOrDefault(v => SamsungBuild.TryParse(bootloader, v.ModelCode) is not null);

    /// <summary>
    /// The installable profile whose board a firmware build belongs to, used to recognise the phone
    /// again in Download mode, where it reports nothing but the bootloader version saved earlier.
    /// </summary>
    public static DeviceProfile? InstallableForBootloader(string? bootloader,
        IReadOnlyList<DeviceProfile>? profiles = null) =>
        (profiles ?? InstallableProfiles).FirstOrDefault(p => p.ModelCodes.Any(code =>
            SamsungBuild.TryParse(bootloader, code) is not null));

    /// <summary>A profile's variant for a model code, for comparing bootloader versions.</summary>
    public static DeviceVariant? VariantOf(DeviceProfile profile, string? modelCode) =>
        profile.Variants.FirstOrDefault(v => v.ModelCode.Equals(modelCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>The profiles to search, defaulting to the full catalog.</summary>
    private static IReadOnlyList<DeviceProfile> Search(IReadOnlyList<DeviceProfile>? profiles) => profiles ?? All;
}