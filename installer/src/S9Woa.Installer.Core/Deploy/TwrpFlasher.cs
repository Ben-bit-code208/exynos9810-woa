// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Deploy;

/// <summary>Flashes TWRP to the RECOVERY partition while the phone is in Download mode.</summary>
public interface ITwrpFlasher
{
    string Name { get; }

    /// <summary>True if this flasher is usable (tool present) and a device is in Download mode.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Flashes <paramref name="twrpImage"/> to RECOVERY. Returns true when the flasher also restarted
    /// the phone straight into TWRP; false when the user has to boot it with the key combination.
    /// </summary>
    Task<bool> FlashRecoveryAsync(string twrpImage, IProgress<string>? log = null, CancellationToken ct = default);
}

/// <summary>
/// Drives the open-source Heimdall Odin-protocol flasher. Heimdall locates the
/// RECOVERY partition by name in the device's PIT, so there is no hard-coded
/// partition index. The user provides heimdall.exe (it needs the libusbK/Zadig
/// driver on the Download-mode interface).
/// </summary>
public sealed class HeimdallTwrpFlasher : ITwrpFlasher
{
    private static readonly TimeSpan Detect = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Flash = TimeSpan.FromMinutes(5);
    private readonly string _heimdall;
    private readonly IProcessRunner _runner;

    public HeimdallTwrpFlasher(string heimdallPath, IProcessRunner runner)
    {
        _heimdall = heimdallPath;
        _runner = runner;
    }

    public string Name => "Heimdall";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_heimdall))
        {
            return false;
        }
        try
        {
            var r = await _runner.RunAsync(_heimdall, ["detect"], Detect, ct).ConfigureAwait(false);
            return r.Succeeded;
        }
        catch (Exception e) when (e is TimeoutException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public async Task<bool> FlashRecoveryAsync(string twrpImage, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(twrpImage))
        {
            throw new FileNotFoundException("TWRP image not found.", twrpImage);
        }
        log?.Report("Flashing TWRP to RECOVERY with Heimdall...");
        var r = await _runner.RunAsync(_heimdall, ["flash", "--RECOVERY", twrpImage, "--no-reboot"], Flash, ct).ConfigureAwait(false);
        if (!r.Succeeded)
        {
            throw new InvalidOperationException($"Heimdall failed to flash TWRP: {(r.StdErr + r.StdOut).Trim()}");
        }
        log?.Report("TWRP flashed. Boot into TWRP now (hold Volume Up + Bixby + Power) before Android restarts, "
            + "or the stock recovery will be restored.");
        return false;
    }
}

/// <summary>
/// Chooses a working TWRP flasher: the built-in Download-mode flasher (Samsung USB driver COM
/// port), or Heimdall when the Download-mode interface has been switched to WinUSB.
/// </summary>
public sealed class TwrpFlashService
{
    private readonly IReadOnlyList<ITwrpFlasher> _flashers;

    public TwrpFlashService(IReadOnlyList<ITwrpFlasher> flashersInPreferenceOrder) => _flashers = flashersInPreferenceOrder;

    public async Task<ITwrpFlasher?> ResolveAsync(CancellationToken ct = default)
    {
        foreach (var flasher in _flashers)
        {
            if (await flasher.IsAvailableAsync(ct).ConfigureAwait(false))
            {
                return flasher;
            }
        }
        return null;
    }

    public async Task<bool> FlashRecoveryAsync(string twrpImage, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var flasher = await ResolveAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No phone in Download mode. Power it off, then hold Volume Down + Bixby + Power and press Volume Up at the warning.");
        log?.Report($"Using {flasher.Name} to flash TWRP.");
        return await flasher.FlashRecoveryAsync(twrpImage, log, ct).ConfigureAwait(false);
    }
}
