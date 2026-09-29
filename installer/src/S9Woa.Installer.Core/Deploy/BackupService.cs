// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S9Woa.Installer.Core.Deploy;

public sealed record BackupEntry(string Name, string Node, long Bytes, string Sha256);

public sealed record BackupManifest(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    string Device,
    string Serial,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<BackupEntry> Partitions);

/// <summary>
/// Backs up the identity-critical partitions to the PC before any destructive
/// step. Each partition is dd'd to the SD card, hashed on the device, pulled to
/// the PC, and re-hashed here; a mismatch fails the backup. Keeping this backup
/// is how a phone returns to stock, so a partial or unverified backup is refused.
/// </summary>
public sealed class BackupService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TwrpClient _twrp;

    public BackupService(TwrpClient twrp) => _twrp = twrp;

    public async Task<BackupManifest> BackupAsync(string hostBackupDir, string deviceModel, string serial,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var partitions = new Dictionary<string, string>(await _twrp.ListPartitionsAsync(ct).ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase);
        var missingRequired = PartitionMap.RequiredBackup.Where(n => !partitions.ContainsKey(n)).ToList();
        if (missingRequired.Count > 0)
        {
            throw new InvalidOperationException(
                $"The phone did not expose the required partition(s) {string.Join(", ", missingRequired)}. "
                + "Make sure it is fully booted into TWRP, then try again.");
        }

        await _twrp.MakeStagingDirAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(hostBackupDir);

        var entries = new List<BackupEntry>();
        var present = PartitionMap.IdentityBackup.Where(partitions.ContainsKey).ToList();
        foreach (var wanted in present)
        {
            ct.ThrowIfCancellationRequested();
            var node = partitions[wanted];
            // Use the phone's own spelling of the link (by-name is case-sensitive on the device).
            var name = partitions.Keys.First(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase));
            log?.Report($"Backing up {name} ({present.IndexOf(wanted) + 1} of {present.Count})...");
            var size = await _twrp.PartitionSizeAsync(name, ct).ConfigureAwait(false);
            var staged = $"{_twrp.SdStagingDir}/{name}.img";
            await _twrp.DdAsync($"{TwrpClient.ByName}/{name}", staged, ct).ConfigureAwait(false);

            var deviceHash = await _twrp.Sha256Async(staged, ct: ct).ConfigureAwait(false);
            var hostFile = Path.Combine(hostBackupDir, $"{name}.img");
            await _twrp.PullAsync(staged, hostFile, ct).ConfigureAwait(false);
            var hostHash = await HashFileAsync(hostFile, ct).ConfigureAwait(false);
            await _twrp.RemoveAsync(staged, ct).ConfigureAwait(false);

            if (!string.Equals(deviceHash, hostHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Backup of {name} did not verify (device {deviceHash[..12]}… vs PC {hostHash[..12]}…). Nothing was written to the phone.");
            }
            log?.Report($"  {name}: {size / 1024} KiB, verified {hostHash[..12]}…");
            entries.Add(new BackupEntry(name, node, size, hostHash));
        }

        var manifest = new BackupManifest(1, deviceModel, serial, DateTimeOffset.UtcNow, entries);
        await File.WriteAllTextAsync(Path.Combine(hostBackupDir, "backup-manifest.json"),
            JsonSerializer.Serialize(manifest, Json), ct).ConfigureAwait(false);
        return manifest;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
