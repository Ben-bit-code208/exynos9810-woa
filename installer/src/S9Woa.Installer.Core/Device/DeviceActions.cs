// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Device;

public enum RebootTarget
{
    System,
    Recovery,
    Download,
}

/// <summary>Quality-of-life device actions available while the phone is reachable over ADB.</summary>
public sealed class DeviceActions
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly AdbClient _adb;
    private readonly IProcessRunner _runner;

    public DeviceActions(AdbClient adb, IProcessRunner runner)
    {
        _adb = adb;
        _runner = runner;
    }

    internal static IReadOnlyList<string> RebootArguments(string serial, RebootTarget target) => target switch
    {
        RebootTarget.System => ["-s", serial, "reboot"],
        RebootTarget.Recovery => ["-s", serial, "reboot", "recovery"],
        RebootTarget.Download => ["-s", serial, "reboot", "download"],
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    public async Task RebootAsync(string serial, RebootTarget target, CancellationToken ct = default)
    {
        var r = await _runner.RunAsync(_adb.AdbPath, RebootArguments(serial, target), Timeout, ct).ConfigureAwait(false);
        if (!r.Succeeded)
        {
            throw new InvalidOperationException($"adb reboot failed: {(r.StdErr + r.StdOut).Trim()}");
        }
    }

    /// <summary>Waits until a device with <paramref name="serial"/> reports the wanted mode.</summary>
    public async Task<DeviceSnapshot> WaitForModeAsync(string serial, DeviceMode mode, TimeSpan timeout,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var device = (await _adb.ListDevicesAsync(ct).ConfigureAwait(false)).FirstOrDefault(d => d.Serial == serial);
            if (device is not null && device.State is AdbState.Device or AdbState.Recovery)
            {
                var snap = DeviceSnapshot.FromAdb(device, await _adb.GetPropertiesAsync(serial, ct).ConfigureAwait(false));
                if (snap.Mode == mode)
                {
                    return snap;
                }
            }
            progress?.Report($"Waiting for the phone to reach {mode}...");
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"The phone did not reach {mode} within {timeout.TotalMinutes:0} minutes.");
    }
}
