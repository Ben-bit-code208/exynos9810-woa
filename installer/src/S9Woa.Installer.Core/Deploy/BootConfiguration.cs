// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// Retargets a freshly created BCD store so Windows boots from the phone's
/// Windows volume regardless of its partition GUID. bcdboot writes a store that
/// points at the volume it was built on; on the phone the raw NTFS volume has no
/// matching GUID, so the boot manager and loader are switched to a
/// <c>locate</c> device, which makes the firmware find the partition that holds
/// <c>\Windows</c>. Uses only public bcdedit features.
/// </summary>
public sealed class BootConfiguration
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private readonly IProcessRunner _runner;
    private readonly string _bcdedit;

    public BootConfiguration(IProcessRunner runner, string? system32 = null) =>
        (_runner, _bcdedit) = (runner, Path.Combine(system32 ?? Environment.GetFolderPath(Environment.SpecialFolder.System), "bcdedit.exe"));

    /// <summary>
    /// The bcdedit invocations that make <paramref name="storePath"/> boot by locating \Windows,
    /// with the loader settings of the phone's validated full-OS entry: test signing (the phone
    /// drivers are test-signed), four processors, no VSM or hypervisor, HAL detection, and
    /// ignoring boot-status failures so an unclean shutdown does not stop at a recovery prompt.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<string>> RetargetCommands(string storePath) =>
    [
        ["/store", storePath, "/set", "{default}", "device", "locate=\\Windows"],
        ["/store", storePath, "/set", "{default}", "osdevice", "locate=\\Windows"],
        ["/store", storePath, "/set", "{default}", "path", "\\Windows\\System32\\winload.efi"],
        ["/store", storePath, "/set", "{default}", "systemroot", "\\Windows"],
        ["/store", storePath, "/set", "{default}", "testsigning", "on"],
        ["/store", storePath, "/set", "{default}", "nointegritychecks", "off"],
        ["/store", storePath, "/set", "{default}", "numproc", "4"],
        ["/store", storePath, "/set", "{default}", "vsmlaunchtype", "off"],
        ["/store", storePath, "/set", "{default}", "hypervisorlaunchtype", "off"],
        ["/store", storePath, "/set", "{default}", "detecthal", "on"],
        ["/store", storePath, "/set", "{default}", "bootstatuspolicy", "IgnoreAllFailures"],
        ["/store", storePath, "/set", "{bootmgr}", "device", "locate=\\EFI\\Microsoft\\Boot\\bootmgfw.efi"],
    ];

    public async Task RetargetToLocateAsync(string storePath, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(storePath))
        {
            throw new FileNotFoundException("BCD store not found.", storePath);
        }
        log?.Report("Retargeting BCD to boot the phone's Windows volume by locate...");
        foreach (var args in RetargetCommands(storePath))
        {
            ct.ThrowIfCancellationRequested();
            var r = await _runner.RunAsync(_bcdedit, args, Timeout, ct).ConfigureAwait(false);
            if (!r.Succeeded)
            {
                throw new InvalidOperationException($"bcdedit {string.Join(' ', args)} failed: {(r.StdErr + r.StdOut).Trim()}");
            }
        }
    }
}
