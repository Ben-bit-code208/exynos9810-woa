// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Text.RegularExpressions;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// Talks to a device booted into TWRP over ADB: enumerate partitions by name,
/// read their size, dd them to and from the external SD card, hash them, and
/// move files with adb push/pull. Binary payloads always travel as files on the
/// SD card, never as captured stdout, so nothing is corrupted by text decoding.
/// </summary>
public sealed partial class TwrpClient
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Long = TimeSpan.FromHours(1);
    private readonly string _adb;
    private readonly string _serial;
    private readonly IProcessRunner _runner;

    public TwrpClient(string adbPath, string serial, IProcessRunner runner)
    {
        _adb = adbPath;
        _serial = serial;
        _runner = runner;
    }

    /// <summary>Where partition backups and image payloads are staged on the device.</summary>
    public string SdStagingDir { get; init; } = "/external_sd/s9woa";

    public const string ByName = "/dev/block/by-name";

    [GeneratedRegex(@"(?<name>\S+)\s+->\s+(?<node>\S+)\s*$")]
    private static partial Regex LinkRegex();

    internal static IReadOnlyDictionary<string, string> ParsePartitionLinks(string lsOutput)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lsOutput.Split('\n'))
        {
            var m = LinkRegex().Match(raw.TrimEnd('\r'));
            if (m.Success)
            {
                var node = m.Groups["node"].Value;
                map[m.Groups["name"].Value] = node.StartsWith('/') ? node : $"/dev/block/{Path.GetFileName(node)}";
            }
        }
        return map;
    }

    internal static string? ParseSha256(string sumOutput) =>
        sumOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is { Length: >= 1 } parts
            && parts[0].Length == 64 ? parts[0].ToLowerInvariant() : null;

    private async Task<ProcessResult> AdbAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
        await _runner.RunAsync(_adb, ["-s", _serial, .. args], timeout, ct).ConfigureAwait(false);

    private async Task<string> ShellCheckedAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        var r = await AdbAsync(["shell", command], timeout, ct).ConfigureAwait(false);
        if (!r.Succeeded)
        {
            throw new InvalidOperationException($"TWRP command failed: {command}\n{(r.StdErr + r.StdOut).Trim()}");
        }
        return r.StdOut;
    }

    public async Task<IReadOnlyDictionary<string, string>> ListPartitionsAsync(CancellationToken ct = default) =>
        ParsePartitionLinks(await ShellCheckedAsync($"ls -l {ByName}", Quick, ct).ConfigureAwait(false));

    public async Task<long> PartitionSizeAsync(string name, CancellationToken ct = default)
    {
        var text = (await ShellCheckedAsync($"blockdev --getsize64 {ByName}/{name}", Quick, ct).ConfigureAwait(false)).Trim();
        return long.TryParse(text, out var n) ? n
            : throw new InvalidOperationException($"Could not read the size of partition {name}: {text}");
    }

    /// <summary>SHA-256 of the first <paramref name="bytes"/> of a device path (whole thing if null).</summary>
    public async Task<string> Sha256Async(string devicePath, long? bytes = null, CancellationToken ct = default)
    {
        var cmd = bytes is { } n
            ? $"head -c {n} {devicePath} | sha256sum"
            : $"sha256sum {devicePath}";
        var sum = ParseSha256(await ShellCheckedAsync(cmd, Long, ct).ConfigureAwait(false));
        return sum ?? throw new InvalidOperationException($"Could not hash {devicePath}.");
    }

    public Task MakeStagingDirAsync(CancellationToken ct = default) =>
        ShellCheckedAsync($"mkdir -p {SdStagingDir}", Quick, ct);

    public Task DdAsync(string source, string destination, CancellationToken ct = default) =>
        ShellCheckedAsync($"dd if={source} of={destination} bs=4194304 conv=fsync", Long, ct);

    /// <summary>Writes a 1-MiB-aligned window of an SD file into a partition at <paramref name="seekMiB"/>.</summary>
    public Task WritePartitionWindowAsync(string sdFile, string name, long seekMiB, long countMiB, CancellationToken ct = default) =>
        ShellCheckedAsync($"dd if={sdFile} of={ByName}/{name} bs=1048576 seek={seekMiB} count={countMiB} conv=notrunc,fsync", Long, ct);

    /// <summary>SHA-256 of a 1-MiB-aligned window read back from a partition.</summary>
    public async Task<string> HashPartitionWindowAsync(string name, long skipMiB, long countMiB, CancellationToken ct = default)
    {
        var cmd = $"dd if={ByName}/{name} bs=1048576 skip={skipMiB} count={countMiB} 2>/dev/null | sha256sum";
        return ParseSha256(await ShellCheckedAsync(cmd, Long, ct).ConfigureAwait(false))
            ?? throw new InvalidOperationException($"Could not hash a window of {name}.");
    }

    public async Task PullAsync(string devicePath, string hostPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(hostPath)!);
        var r = await AdbAsync(["pull", devicePath, hostPath], Long, ct).ConfigureAwait(false);
        if (!r.Succeeded)
        {
            throw new InvalidOperationException($"adb pull {devicePath} failed: {(r.StdErr + r.StdOut).Trim()}");
        }
    }

    public async Task PushAsync(string hostPath, string devicePath, CancellationToken ct = default)
    {
        var r = await AdbAsync(["push", hostPath, devicePath], Long, ct).ConfigureAwait(false);
        if (!r.Succeeded)
        {
            throw new InvalidOperationException($"adb push {Path.GetFileName(hostPath)} failed: {(r.StdErr + r.StdOut).Trim()}");
        }
    }

    public Task RemoveAsync(string devicePath, CancellationToken ct = default) =>
        ShellCheckedAsync($"rm -f {devicePath}", Quick, ct);

    /// <summary>Runs an arbitrary checked shell command in TWRP and returns stdout.</summary>
    public Task<string> ShellAsync(string command, CancellationToken ct = default) =>
        ShellCheckedAsync(command, Long, ct);

    public Task MakeDirAsync(string devicePath, CancellationToken ct = default) =>
        ShellCheckedAsync($"mkdir -p {devicePath}", Quick, ct);

    /// <summary>Mounts a vfat partition node at a mountpoint (created first).</summary>
    public async Task MountVfatAsync(string node, string mountpoint, CancellationToken ct = default)
    {
        await MakeDirAsync(mountpoint, ct).ConfigureAwait(false);
        await ShellCheckedAsync($"mount -t vfat -o rw {node} {mountpoint}", Quick, ct).ConfigureAwait(false);
    }

    public Task UnmountAsync(string mountpoint, CancellationToken ct = default) =>
        ShellCheckedAsync($"umount {mountpoint}", Quick, ct);

    /// <summary>Recursively pushes a host directory's contents into a device directory.</summary>
    public async Task PushTreeAsync(string hostDir, string deviceDir, CancellationToken ct = default)
    {
        var r = await AdbAsync(["push", hostDir.TrimEnd('\\'), deviceDir], Long, ct).ConfigureAwait(false);
        if (!r.Succeeded)
        {
            throw new InvalidOperationException($"adb push {hostDir} failed: {(r.StdErr + r.StdOut).Trim()}");
        }
    }

    /// <summary>True when <paramref name="mountpoint"/> appears in /proc/mounts.</summary>
    public async Task<bool> IsMountedAsync(string mountpoint, CancellationToken ct = default)
    {
        var r = await AdbAsync(["shell", $"grep -q ' {mountpoint} ' /proc/mounts && echo yes || echo no"], Quick, ct).ConfigureAwait(false);
        return r.StdOut.Contains("yes", StringComparison.Ordinal);
    }
}
