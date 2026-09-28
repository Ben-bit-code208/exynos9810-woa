// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Text.RegularExpressions;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Device;

public enum AdbState
{
    Device,
    Recovery,
    Sideload,
    Unauthorized,
    Offline,
    Other,
}

public sealed record AdbDevice(string Serial, AdbState State, string? Model, string? Product);

/// <summary>Thin, read-only wrapper over adb.exe.</summary>
public sealed partial class AdbClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly IProcessRunner _runner;

    public AdbClient(string adbPath, IProcessRunner runner)
    {
        AdbPath = adbPath;
        _runner = runner;
    }

    public string AdbPath { get; }

    public async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken ct = default)
    {
        var r = await _runner.RunAsync(AdbPath, ["devices", "-l"], Timeout, ct).ConfigureAwait(false);
        if (!r.Succeeded)
        {
            throw new InvalidOperationException($"adb devices failed: {r.StdErr.Trim()}");
        }
        return ParseDevices(r.StdOut);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetPropertiesAsync(string serial, CancellationToken ct = default)
    {
        var r = await _runner.RunAsync(AdbPath, ["-s", serial, "shell", "getprop"], Timeout, ct).ConfigureAwait(false);
        if (!r.Succeeded)
        {
            throw new InvalidOperationException($"adb getprop failed: {r.StdErr.Trim()}");
        }
        return ParseGetprop(r.StdOut);
    }

    internal static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        var list = new List<AdbDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*'))
            {
                continue;
            }
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }
            var state = parts[1] switch
            {
                "device" => AdbState.Device,
                "recovery" => AdbState.Recovery,
                "sideload" => AdbState.Sideload,
                "unauthorized" => AdbState.Unauthorized,
                "offline" => AdbState.Offline,
                _ => AdbState.Other,
            };
            string? Field(string key) => parts.Skip(2)
                .Where(p => p.StartsWith(key + ":", StringComparison.Ordinal))
                .Select(p => p[(key.Length + 1)..]).FirstOrDefault();
            list.Add(new AdbDevice(parts[0], state, Field("model"), Field("product")));
        }
        return list;
    }

    [GeneratedRegex(@"^\[(?<k>[^\]]+)\]:\s*\[(?<v>.*)\]\s*$")]
    private static partial Regex PropLine();

    internal static IReadOnlyDictionary<string, string> ParseGetprop(string output)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var m = PropLine().Match(raw.TrimEnd('\r'));
            if (m.Success)
            {
                d[m.Groups["k"].Value] = m.Groups["v"].Value;
            }
        }
        return d;
    }
}
