// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S9Woa.Installer.Core.Stages;

public enum StageStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Skipped,
}

public enum StageAvailability
{
    /// <summary>Implemented and validated on the reference device.</summary>
    Ready,

    /// <summary>The user performs this step by hand; the installer guides and verifies.</summary>
    Guided,

    /// <summary>
    /// Automated and unit-tested, but not yet validated end to end on the reference
    /// device. Runs only when the user opts in to experimental stages.
    /// </summary>
    Experimental,

    /// <summary>Planned; the installer stops before this stage.</summary>
    NotImplemented,
}

public sealed record StageDefinition(string Id, string Title, string Summary, StageAvailability Availability,
    bool Destructive = false);

public static class StageCatalog
{
    public static IReadOnlyList<StageDefinition> All { get; } =
    [
        new("host", "Check this PC", "Administrator rights, disk space, adb and Samsung USB driver.", StageAvailability.Ready),
        new("identify", "Identify the phone", "Model, firmware and bootloader state over ADB.", StageAvailability.Ready),
        new("unlock", "Unlock the bootloader", "OEM unlock in Developer options, then the Download-mode unlock. Wipes Android.", StageAvailability.Guided, Destructive: true),
        new("twrp", "Install TWRP", "Flash TWRP to RECOVERY from Download mode and boot it once.", StageAvailability.Experimental, Destructive: true),
        new("backup", "Back up the phone", "Copy EFS, modem calibration and the partition table to this PC before anything else is written.", StageAvailability.Experimental),
        new("partition", "Prepare partitions", "Locate the target partitions by name and verify the validated layout.", StageAvailability.Experimental, Destructive: true),
        new("media", "Get Windows", "Use your ISO/ESD, or download ARM64 media from Microsoft.", StageAvailability.Ready),
        new("image", "Build the Windows image", "Apply, add drivers, slim (optional), configure boot and first-run settings.", StageAvailability.Experimental),
        new("transfer", "Copy Windows to the phone", "Write the image and boot files through TWRP, verifying every block.", StageAvailability.Experimental, Destructive: true),
        new("uefi", "Install UEFI", "Flash the UEFI boot image to BOOT. RECOVERY keeps TWRP.", StageAvailability.Experimental, Destructive: true),
        new("firstboot", "First boot", "Boot Windows and finish setup.", StageAvailability.Experimental),
    ];

    public static StageDefinition Get(string id) => All.First(s => s.Id == id);
}

public sealed class StageRecord
{
    public StageStatus Status { get; set; }
    public DateTimeOffset? Updated { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Resumable installer state, persisted as JSON under %LOCALAPPDATA%\S9WoaInstaller.</summary>
public sealed class InstallState
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public int SchemaVersion { get; set; } = 1;
    public string? DeviceSerial { get; set; }
    public string? DeviceBootloader { get; set; }
    public string? MediaPath { get; set; }
    public string? SlimProfile { get; set; }
    public Dictionary<string, StageRecord> Stages { get; set; } = [];

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "S9WoaInstaller");

    public StageStatus StatusOf(string id) => Stages.TryGetValue(id, out var r) ? r.Status : StageStatus.Pending;

    public void Set(string id, StageStatus status, string? detail = null, TimeProvider? clock = null)
    {
        _ = StageCatalog.Get(id);
        Stages[id] = new StageRecord { Status = status, Detail = detail, Updated = (clock ?? TimeProvider.System).GetUtcNow() };
    }

    /// <summary>First stage that is neither done nor skipped, in catalog order.</summary>
    public StageDefinition? NextStage() =>
        StageCatalog.All.FirstOrDefault(s => StatusOf(s.Id) is not (StageStatus.Done or StageStatus.Skipped));

    public static InstallState Load(string directory)
    {
        var path = Path.Combine(directory, "state.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<InstallState>(File.ReadAllText(path), Json) ?? new InstallState()
            : new InstallState();
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
