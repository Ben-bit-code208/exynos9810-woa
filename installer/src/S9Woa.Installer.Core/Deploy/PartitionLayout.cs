// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Globalization;
using S9Woa.Installer.Core.Device;

namespace S9Woa.Installer.Core.Deploy;

/// <summary>One partition, as the phone itself reports its place and size on the UFS unit.</summary>
public sealed record PartitionPlacement(
    string Name,
    string Node,
    int PartitionNumber,
    long OffsetBytes,
    long SizeBytes,
    long SectorSize)
{
    /// <summary>Size in the unit's own blocks, which is what <see cref="PartitionLayout.LogicalSectorSize"/> says.</summary>
    public long BlockCount => SizeBytes / SectorSize;

    public string Describe() => $"{Name} ({Node}, {SizeBytes / (1024L * 1024)} MiB at {OffsetBytes})";
}

/// <summary>
/// A phone's real partition layout: the UFS unit it uses, its sector size, and every partition's
/// offset and size. The installer measures this instead of trusting a table of constants, because
/// USERDATA moves and shrinks with the storage option (64 / 128 / 256 GB) and the Exynos 9810 boards
/// do not lay their partitions out identically.
/// </summary>
public sealed record PartitionLayout(
    string Unit,
    int LogicalSectorSize,
    long DiskBytes,
    IReadOnlyDictionary<string, PartitionPlacement> ByName,
    bool NamesAreSemantic = true)
{
    /// <summary>
    /// False when the device has no <c>/dev/block/by-name</c> links, so the keys in
    /// <see cref="ByName"/> are node names (<c>mmcblk0p13</c>) and a lookup by role such as
    /// USERDATA means nothing. The profile says which node holds what instead.
    /// </summary>
    public bool HasRoles => NamesAreSemantic;

    /// <summary>The by-name link the phone uses for a partition name, or null.</summary>
    public PartitionPlacement? this[string name] =>
        ByName.TryGetValue(name, out var extent)
            ? extent
            : ByName.Where(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).FirstOrDefault();

    /// <summary>The partition on a device node such as <c>mmcblk0p13</c>, or null.</summary>
    public PartitionPlacement? Node(string node) => this[node];

    /// <summary>
    /// This layout re-keyed by partition role, for a phone that publishes no names. Each entry of
    /// <see cref="DeviceProfile.PartitionRoles"/> says which node suffix holds which role; the result
    /// is an ordinary named layout, so nothing downstream has to know that the names came from a
    /// profile instead of from the kernel. Roles the profile does not claim are dropped rather than
    /// guessed, which is why a profile has to be measured before it is trusted: claiming
    /// <c>USERDATA = p13</c> for a tablet where p13 is CACHE would overwrite the wrong partition,
    /// and there is no name on the device to catch it.
    /// </summary>
    public PartitionLayout ResolvedByRoles(DeviceProfile profile)
    {
        if (NamesAreSemantic)
        {
            return this;
        }

        var byRole = new Dictionary<string, PartitionPlacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (role, nodeSuffix) in profile.PartitionRoles)
        {
            // The prober reports full node names (mmcblk0p13) when there are no names to report, so
            // accept the suffix either bare or already carrying the unit's prefix.
            var found = ByName.TryGetValue(nodeSuffix, out var extent)
                || ByName.TryGetValue($"{Unit}{nodeSuffix}", out extent);
            if (found)
            {
                byRole[role] = extent! with { Name = role, Node = $"{Unit}{nodeSuffix}" };
            }
        }
        return new PartitionLayout(Unit, LogicalSectorSize, DiskBytes, byRole, NamesAreSemantic: true);
    }

    public bool Has(string name) => this[name] is not null;

    public long SizeOf(string name) => this[name]?.SizeBytes
        ?? throw new KeyNotFoundException($"The phone has no partition named {name}.");

    /// <summary>The Windows NTFS volume is built to land exactly on the phone's USERDATA.</summary>
    public ReferenceGeometry Geometry => new(DiskBytes, OffsetOf("USERDATA"), SizeOf("USERDATA"), SizeOf("CACHE"));

    private long OffsetOf(string name) => this[name]?.OffsetBytes
        ?? throw new KeyNotFoundException($"The phone has no partition named {name}.");

    /// <summary>
    /// Smallest USERDATA Windows can be installed into. The exported image is about 53 GiB, and
    /// Windows itself needs well over 32 GiB, so a phone reporting less than this cannot take the
    /// image and is refused here rather than half-way through writing it.
    /// </summary>
    public const long MinWindowsBytes = 32L * 1024 * 1024 * 1024;

    /// <summary>
    /// Space the Windows image needs in front of Windows itself: a 260 MiB EFI system partition and
    /// a 16 MiB Microsoft reserved partition, with slack (<see cref="VhdxImageBuilder.CreateScript"/>).
    /// </summary>
    public const long ReservedBeforeWindowsBytes = 300L * 1024 * 1024;

    /// <summary>
    /// The layout the Windows image build and the partition checks agree on: what the phone offers,
    /// or a user-facing reason why this layout cannot be used. A USERDATA that sits somewhere else
    /// than the reference phone's is fine - the image is built to match it - so only a layout that
    /// cannot hold the image is refused.
    /// </summary>
    public string? CheckAgainst(DeviceProfile profile)
    {
        foreach (var required in PartitionMap.RequiredPartitions)
        {
            if (!Has(required))
            {
                return $"The phone has no partition named {required}. Its partition table does not look like "
                    + $"{profile.MarketingName}'s ({profile.Id}), so nothing will be written.";
            }
        }
        if (LogicalSectorSize != 4096)
        {
            return $"The phone's storage uses {LogicalSectorSize}-byte sectors; this project only builds "
                + "boot volumes for the 4096-byte sectors of the Exynos 9810 UFS units.";
        }
        var userdata = this["USERDATA"]!;
        if (userdata.SizeBytes < MinWindowsBytes)
        {
            return $"{profile.MarketingName} reports {userdata.SizeBytes / (1024L * 1024 * 1024)} GiB of USERDATA, "
                + $"below the {MinWindowsBytes / (1024L * 1024 * 1024)} GiB Windows needs. Nothing will be written.";
        }
        if (userdata.OffsetBytes < ReservedBeforeWindowsBytes)
        {
            return $"USERDATA starts at {userdata.OffsetBytes / (1024 * 1024)} MiB, leaving no room for the EFI system "
                + $"partition this install needs before it ({ReservedBeforeWindowsBytes / (1024 * 1024)} MiB).";
        }
        if (userdata.OffsetBytes + userdata.SizeBytes > DiskBytes)
        {
            return "USERDATA extends past the end of the phone's storage, so its reported size cannot be trusted.";
        }
        return null;
    }

    /// <summary>A one-line summary for the install log and the phone identification page.</summary>
    public string Summary() =>
        $"{Unit}: {DiskBytes / (1024L * 1024 * 1024)} GB, {LogicalSectorSize}-byte sectors, {ByName.Count} partitions"
        + (this["USERDATA"] is { } u ? $"; USERDATA {u.SizeBytes / (1024 * 1024)} MiB" : "");
}

/// <summary>
/// Turns the phone's <c>/sys/block/sda</c> listing into a <see cref="PartitionLayout"/>. The parsing
/// is pure and separate from the shell so it can be tested against captured output.
/// </summary>
public static class SysfsLayoutParser
{
    /// <summary>
    /// The command the prober runs. sysfs reports every partition's start and size in 512-byte
    /// sectors regardless of the unit's real block size, and <c>/dev/block/by-name</c> says which
    /// name belongs to which node where that exists. Both are world-readable, so this works in
    /// stock Android with no root and in TWRP alike - which is what lets the Windows image be built
    /// before the phone is unlocked or has any recovery on it.
    /// </summary>
    /// <remarks>
    /// The storage unit is found by looking at <c>/sys/block</c> rather than by assuming
    /// <c>sda</c>: the Fire tablet measures as <c>mmcblk0</c>, and a glob for one vendor's name
    /// returns nothing on every other one. Where a device has no <c>by-name</c> links the node names
    /// are reported instead and <c>names=node</c> says so, because then a name means a position on
    /// the disk rather than a role: which node is USERDATA is the profile's business, not the
    /// reader's.
    /// </remarks>
    public const string ProbeCommand =
        // The biggest non-virtual block device with a real block size: the phone's storage.
        "u=; best=0; for d in /sys/block/*; do case ${d##*/} in loop*|ram*|zram*|dm-*) continue ;; esac; "
        + "[ -f \"$d/queue/logical_block_size\" ] && [ -f \"$d/size\" ] || continue; "
        + "s=$(cat \"$d/size\" 2>/dev/null) || continue; case $s in ''|*[!0-9]*) continue ;; esac; "
        + "if [ \"$s\" -gt \"$best\" ]; then best=$s; u=$d; fi; done; [ -n \"$u\" ] || exit 1; "
        + "echo \"unit=${u#/sys/block/}\"; "
        + "echo \"lbs=$(cat \"$u/queue/logical_block_size\")\"; "
        + "echo \"size=$(cat \"$u/size\")\"; "
        // Named partitions where the device has them.
        + "src=node; for dir in /dev/block/by-name /dev/block/bootdevice/by-name; do [ -d \"$dir\" ] || continue; "
        + "src=by-name; for l in \"$dir\"/*; do [ -e \"$l\" ] || continue; n=${l##*/}; "
        + "d=$(readlink -f \"$l\") || continue; b=${d##*/}; [ -f \"$u/$b/start\" ] || continue; "
        + "echo \"p $n $b $(cat \"$u/$b/start\") $(cat \"$u/$b/size\")\"; done; break; done; "
        + "echo \"names=$src\"; "
        // Otherwise the node names, so the layout still carries every partition's place and size.
        + "if [ \"$src\" = node ]; then for p in \"$u\"/*/; do b=${p%/}; b=${b##*/}; "
        + "[ -f \"$u/$b/start\" ] || continue; "
        + "echo \"p $b $b $(cat \"$u/$b/start\") $(cat \"$u/$b/size\")\"; done; fi";

    /// <summary>sysfs always counts in 512-byte sectors, whatever the unit's logical block size is.</summary>
    private const long SysfsSectorBytes = 512;

    /// <summary>
    /// Parses the probe output. Returns null when the output is not a usable layout (no unit, no
    /// sector size or no partitions at all), so a caller can fall back to the profile instead of
    /// failing on a phone that answers differently.
    /// </summary>
    public static PartitionLayout? Parse(string output)
    {
        string? unit = null;
        long? logical = null, sectors = null;
        var semanticNames = true;
        var parts = new List<(int Number, string Name, string Node, long Start, long Size)>();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.StartsWith("unit=", StringComparison.Ordinal))
            {
                unit = line[5..].Trim();
            }
            else if (line.StartsWith("lbs=", StringComparison.Ordinal))
            {
                logical = ParseLong(line[4..]);
            }
            else if (line.StartsWith("size=", StringComparison.Ordinal))
            {
                sectors = ParseLong(line[5..]);
            }
            else if (line.StartsWith("names=", StringComparison.Ordinal))
            {
                semanticNames = line[6..].Trim() == "by-name";
            }
            else if (line.StartsWith("p ", StringComparison.Ordinal))
            {
                var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                // "p <name> <node> <start> <size>" - exactly five fields.
                if (f.Length < 5)
                {
                    continue;
                }
                var number = NodeNumber(f[2]);
                if (number is null)
                {
                    continue;
                }
                if (ParseLong(f[3]) is { } start && ParseLong(f[4]) is { } size)
                {
                    parts.Add((number.Value, f[1], f[2], start, size));
                }
            }
        }

        if (string.IsNullOrEmpty(unit) || logical is not { } sectorBytes || sectorBytes <= 0 || sectors is not { } total)
        {
            return null;
        }
        if (parts.Count == 0)
        {
            return null;
        }

        var byName = new Dictionary<string, PartitionPlacement>(StringComparer.Ordinal);
        foreach (var (number, name, node, start, size) in parts)
        {
            byName[name] = new PartitionPlacement(name, node, number,
                start * SysfsSectorBytes, size * SysfsSectorBytes, sectorBytes);
        }
        return new PartitionLayout(unit, (int)sectorBytes, total * SysfsSectorBytes, byName, semanticNames);
    }

    private static long? ParseLong(string text) =>
        long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// The partition number in a device node name, or null when it has none. The trailing digits are
    /// the partition number on every layout this reads: <c>sda20</c>, <c>mmcblk0p13</c>,
    /// <c>nvme0n1p2</c>. Requiring a vendor's prefix would drop the whole layout on every other
    /// vendor, which is what happened to a Fire HD 8 before this was fixed.
    /// </summary>
    private static int? NodeNumber(string node)
    {
        var end = node.Length;
        while (end > 0 && char.IsAsciiDigit(node[end - 1]))
        {
            end--;
        }
        if (end == node.Length || end == 0)
        {
            return null; // no trailing digits, or nothing but digits (the whole-disk node itself)
        }
        return int.TryParse(node.AsSpan(end), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }
}