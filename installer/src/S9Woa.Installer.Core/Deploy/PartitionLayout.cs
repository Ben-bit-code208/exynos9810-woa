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
    IReadOnlyDictionary<string, PartitionPlacement> ByName)
{
    /// <summary>The by-name link the phone uses for a partition name, or null.</summary>
    public PartitionPlacement? this[string name] =>
        ByName.TryGetValue(name, out var extent)
            ? extent
            : ByName.Where(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).FirstOrDefault();

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
    /// name belongs to which node. Both are world-readable, so this works in stock Android with no
    /// root and in TWRP alike - which is what lets the Windows image be built before the phone is
    /// unlocked or has any recovery on it.
    /// </summary>
    public const string ProbeCommand =
        "u=$(for p in /sys/block/sd?; do [ -f \"$p/queue/logical_block_size\" ] && echo \"$p\" && break; done); "
        + "[ -n \"$u\" ] || exit 1; "
        + "echo \"unit=${u#/sys/block/}\"; "
        + "echo \"lbs=$(cat \"$u/queue/logical_block_size\")\"; "
        + "echo \"size=$(cat \"$u/size\")\"; "
        + "for l in /dev/block/by-name/*; do "
        + "n=${l##*/}; d=$(readlink -f \"$l\") || continue; "
        + "b=${d##*/}; [ -e \"$u/$b/start\" ] || continue; "
        + "echo \"p $n $b $(cat \"$u/$b/start\") $(cat \"$u/$b/size\")\"; "
        + "done";

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
        return new PartitionLayout(unit, (int)sectorBytes, total * SysfsSectorBytes, byName);
    }

    private static long? ParseLong(string text) =>
        long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>The partition number in an <c>sdXN</c> node name, or null when it is not one.</summary>
    private static int? NodeNumber(string node)
    {
        if (node.Length < 4 || !node.StartsWith("sd", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var i = 2;
        while (i < node.Length && char.IsAsciiLetterLower(node[i]))
        {
            i++;
        }
        var digits = node.AsSpan(i);
        for (var k = 0; k < digits.Length; k++)
        {
            if (!char.IsAsciiDigit(digits[k]))
            {
                return null;
            }
        }
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}