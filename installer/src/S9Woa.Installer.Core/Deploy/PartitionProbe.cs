// SPDX-License-Identifier: BSD-2-Clause-Patent
using S9Woa.Installer.Core.Device;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Deploy;

/// <summary>How the partition layout for the Windows image was arrived at.</summary>
public sealed record LayoutSource(PartitionLayout Layout, bool Measured, string Detail)
{
    /// <summary>The geometry the image build and the partition checks must use.</summary>
    public ReferenceGeometry Geometry => Measured ? Layout.Geometry : PartitionMap.ReferenceLayout;
}

/// <summary>
/// Measures a phone's partition layout over plain <c>adb shell</c>, before anything is written and
/// without root, so the Windows image can be built to land on the phone's real USERDATA instead of a
/// table of constants. Falls back to the profile's validated geometry when the phone will not answer
/// (an older Android that hides sysfs, a recovery without it), which is also what makes a phone with
/// no layout on file installable as soon as it can be probed.
/// </summary>
public sealed class PartitionProbe
{
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(30);
    private readonly string _adb;
    private readonly string _serial;
    private readonly IProcessRunner _runner;

    public PartitionProbe(string adbPath, string serial, IProcessRunner runner) =>
        (_adb, _serial, _runner) = (adbPath, serial, runner);

    /// <summary>adb arguments for the probe, in the same shape the other adb calls use.</summary>
    internal static IReadOnlyList<string> ProbeArguments(string serial) =>
        ["-s", serial, "shell", SysfsLayoutParser.ProbeCommand];

    /// <summary>
    /// The phone's own layout, or null when it cannot be read. Never throws for a phone that simply
    /// does not answer: the caller falls back to the profile.
    /// </summary>
    public async Task<PartitionLayout?> MeasureAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _runner.RunAsync(_adb, ProbeArguments(_serial), Quick, ct).ConfigureAwait(false);
            return r.Succeeded ? SysfsLayoutParser.Parse(r.StdOut) : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The layout to build this phone's Windows image from: what the phone reports when it can be
    /// asked, the profile's validated geometry otherwise. A measured layout that does not hold the
    /// partitions this install needs is refused here rather than after the phone has been touched.
    /// </summary>
    public async Task<LayoutSource> ResolveAsync(DeviceProfile profile, CancellationToken ct = default) =>
        Resolve(profile, await MeasureAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// The same decision without a transport: what the phone answered, or null when it would not.
    /// adb and TWRP both feed their output through this, so both refuse the same phones.
    /// </summary>
    /// <exception cref="InvalidOperationException">the layout cannot be used for this profile.</exception>
    public static LayoutSource Resolve(DeviceProfile profile, PartitionLayout? measured)
    {
        if (measured is null)
        {
            if (profile.Geometry is null)
            {
                throw new InvalidOperationException(
                    $"The phone did not report its partition layout, and {profile.MarketingName} has no validated "
                    + "layout on file. Reconnect the phone in Android or TWRP so the installer can measure it.");
            }
            return new LayoutSource(
                // Not read: Geometry on this layout comes from the profile instead (see LayoutSource).
                new PartitionLayout(profile.Codename, 4096, profile.Geometry.DiskBytes,
                    new Dictionary<string, PartitionPlacement>(StringComparer.Ordinal)),
                false,
                $"The phone did not report its partition layout; using the validated {profile.MarketingName} reference layout.");
        }

        var problem = measured.CheckAgainst(profile);
        if (problem is not null)
        {
            throw new InvalidOperationException(problem);
        }
        return new LayoutSource(measured, true, $"Partition layout measured from the phone: {measured.Summary()}.");
    }

    /// <summary>
    /// The geometry to build the image for, without needing a live phone: the profile's validated
    /// layout. Used by the image build when the phone is not connected yet; once it is, re-resolve
    /// with <see cref="ResolveAsync"/> so the image matches what the phone actually has.
    /// </summary>
    public static ReferenceGeometry OfflineGeometry(DeviceProfile profile) =>
        profile.Geometry ?? PartitionMap.ReferenceLayout;
}