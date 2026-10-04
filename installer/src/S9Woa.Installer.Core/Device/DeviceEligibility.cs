// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Device;

/// <summary>
/// Decides whether the connected phone may be installed on, from what the catalog
/// (<see cref="DeviceCatalog"/>) says about its model. A phone that is not in the catalog, or is in
/// it as a candidate or excluded board, is refused with the reason and the blockers that apply to it
/// rather than a single "not supported".
/// </summary>
public static class DeviceEligibility
{
    public static IReadOnlyList<CheckResult> Evaluate(DeviceSnapshot d, IReadOnlyList<DeviceProfile>? profiles = null)
    {
        var catalog = profiles ?? DeviceCatalog.All;
        var results = new List<CheckResult>();

        if (d.Mode == DeviceMode.Unauthorized)
        {
            results.Add(new("usb-auth", "USB debugging authorization", CheckSeverity.Blocker,
                "The phone has not authorized this PC.",
                "Unlock the phone and tap Allow on the \"Allow USB debugging?\" prompt."));
            return results;
        }
        if (d.Mode is DeviceMode.Offline or DeviceMode.Unknown)
        {
            results.Add(new("usb-state", "Connection", CheckSeverity.Blocker,
                "The phone is connected but not responding over ADB.",
                "Reconnect the USB cable, or restart the phone."));
            return results;
        }

        var profile = Resolve(d, catalog);
        if (profile is null)
        {
            results.Add(UnknownModel(d));
            return results;
        }

        if (!profile.Installable)
        {
            results.Add(Refused(profile, d.Model));
            return results;
        }

        results.Add(new("model", "Phone model", CheckSeverity.Pass, $"{profile.ModelList} ({profile.Id})"));
        if (profile.Tier == SupportTier.Experimental)
        {
            results.Add(new("proven", "Proven", CheckSeverity.Warning,
                $"{profile.MarketingName} is supported but nobody has installed on this exact phone yet.",
                "Everything the installer does is implemented for this board, but an untested step can still need a fix. "
                + "Back up the phone and be ready to return it to stock."));
        }

        if (d.Hardware is not null && !d.Hardware.Contains(profile.HardwareToken, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new("soc", "Chipset", CheckSeverity.Blocker,
                $"Reported hardware \"{d.Hardware}\" is not {profile.SoC}."));
            return results;
        }
        results.Add(new("soc", "Chipset", CheckSeverity.Pass, profile.SoC));

        results.AddRange(CheckFirmware(d, profile));
        results.AddRange(CheckUnlock(d));
        results.Add(Knox(d));
        results.Add(CurrentMode(d));
        results.Add(Geometry(profile));
        return results;
    }

    /// <summary>The profile for this phone: by model, else by board codename, else by bootloader build.</summary>
    public static DeviceProfile? Resolve(DeviceSnapshot d, IReadOnlyList<DeviceProfile>? profiles = null)
    {
        var catalog = profiles ?? DeviceCatalog.All;
        return Match(catalog, p => DeviceCatalog.ForModel(d.Model) == p, d.Model)
               ?? Match(catalog, p => DeviceCatalog.ForCodename(d.Codename) == p, d.Codename)
               ?? Match(catalog, p => DeviceCatalog.ForBootloader(d.Bootloader) == p, d.Bootloader);
    }

    private static DeviceProfile? Match(IReadOnlyList<DeviceProfile> catalog,
        Func<DeviceProfile, bool> matches, string? reported) =>
        reported is null ? null : catalog.FirstOrDefault(matches);

    private static CheckResult UnknownModel(DeviceSnapshot d)
    {
        var model = d.Model ?? "Unknown model";
        var known = string.Join(", ", DeviceCatalog.InstallableProfiles.SelectMany(p => p.Models));
        var detail = $"{model} is not in this installer's catalog.";
        var action = model.Contains("G965", StringComparison.OrdinalIgnoreCase)
                     || model.Contains("G960", StringComparison.OrdinalIgnoreCase)
                     || model.Contains("N960", StringComparison.OrdinalIgnoreCase)
            ? "It looks like a Galaxy S9-family phone. The Snapdragon variants (SM-G965U/U1/W/0, SM-G960U/U1/W/0, SM-N960U/U1/W/0) "
              + "cannot be installed on: they are not Exynos 9810 phones."
            : $"Supported phones: {known}. If this phone is right, its model code is missing from DeviceCatalog.";
        return new("model", "Phone model", CheckSeverity.Blocker, detail, action);
    }

    private static CheckResult Refused(DeviceProfile profile, string? model) =>
        profile.Tier switch
        {
            SupportTier.Excluded => new("model", "Phone model", CheckSeverity.Blocker,
                $"{model ?? profile.MarketingName} ({profile.MarketingName}) cannot be installed on: {Describe(profile)}",
                string.Join(" ", profile.Blockers)),
            _ => new("model", "Phone model", CheckSeverity.Blocker,
                $"{model ?? profile.MarketingName} ({profile.MarketingName}) is known, but not ready to install on.",
                string.Join(" ", profile.Blockers)),
        };

    private static string Describe(DeviceProfile profile) =>
        profile.Tier == SupportTier.Excluded && profile.Blockers.Count > 0
            ? $"it is a {profile.SoC} board ({profile.Id}). {profile.Blockers[0]}"
            : string.Join(" ", profile.Blockers);

    /// <summary>
    /// Compares the reported bootloader with the build this profile was validated on. A variant
    /// without its own validated build falls back to the earliest build known for the profile, which
    /// keeps the anti-rollback rule ("never install on an older firmware") without inventing a
    /// version nobody has used.
    /// </summary>
    private static IEnumerable<CheckResult> CheckFirmware(DeviceSnapshot d, DeviceProfile profile)
    {
        var modelCode = DeviceCatalog.VariantOf(profile, ModelCodeOf(d))?.ModelCode ?? profile.ModelCodes.First();
        var validatedRaw = DeviceCatalog.VariantOf(profile, modelCode)?.ValidatedBootloader
                           ?? profile.Variants.Select(v => v.ValidatedBootloader).FirstOrDefault(v => v is not null);
        if (validatedRaw is null)
        {
            yield return new("bootloader", "Firmware version", CheckSeverity.Warning,
                $"Could not read the bootloader version ({d.Bootloader ?? "not reported"}).",
                $"This phone's firmware has not been validated; make sure it is Samsung's latest official build.");
            yield break;
        }

        var validated = SamsungBuild.TryParse(validatedRaw, modelCode)!;
        var build = SamsungBuild.TryParse(d.Bootloader, modelCode);
        if (build is null)
        {
            yield return new("bootloader", "Firmware version", CheckSeverity.Warning,
                $"Could not read the bootloader version ({d.Bootloader ?? "not reported"}).",
                $"The installer is validated on {validatedRaw}.");
        }
        else if (build.Raw == validated.Raw)
        {
            yield return new("bootloader", "Firmware version", CheckSeverity.Pass, $"{build.Raw} (validated)");
        }
        else if (build.BinaryRevision < validated.BinaryRevision || build.ReleaseKey.CompareTo(validated.ReleaseKey) < 0)
        {
            yield return new("bootloader", "Firmware version", CheckSeverity.Blocker,
                $"{build.Raw} is older than the validated firmware {validated.Raw}.",
                "Update the phone to the latest official Samsung firmware (Settings > Software update) before installing.");
        }
        else
        {
            yield return new("bootloader", "Firmware version", CheckSeverity.Warning,
                $"{build.Raw} differs from the validated firmware {validated.Raw}.",
                "Installation may work but has not been tested on this firmware.");
        }
    }

    /// <summary>The model code a firmware build string starts with, or null when it is not a build.</summary>
    private static string? ModelCodeOf(DeviceSnapshot d) =>
        DeviceCatalog.All.SelectMany(p => p.ModelCodes)
            .FirstOrDefault(code => SamsungBuild.TryParse(d.Bootloader, code) is not null);

    private static IEnumerable<CheckResult> CheckUnlock(DeviceSnapshot d)
    {
        if (d.FlashLocked == false)
        {
            yield return new("unlock", "Bootloader", CheckSeverity.Pass, "Unlocked");
        }
        else if (d.Mode == DeviceMode.Android && d.OemUnlockAllowed == true)
        {
            yield return new("unlock", "Bootloader", CheckSeverity.Info, "Locked; OEM unlocking is enabled.",
                "The installer will guide you through unlocking in Download mode. This erases the phone.");
        }
        else if (d.Mode == DeviceMode.Android)
        {
            yield return new("unlock", "Bootloader", CheckSeverity.Blocker, "Locked; OEM unlocking is not enabled.",
                "Enable Developer options, then turn on Settings > Developer options > OEM unlocking. "
                + "If the switch is missing, connect to the internet, check for software updates, and wait (the phone may need to be online for several days).");
        }
        else if (d.Mode == DeviceMode.Download)
        {
            yield return new("unlock", "Bootloader", CheckSeverity.Info, "Not reported in Download mode.",
                "The phone refuses TWRP if the bootloader is still locked.");
        }
        else
        {
            yield return new("unlock", "Bootloader", CheckSeverity.Warning, "Lock state not reported by recovery.");
        }
    }

    private static CheckResult Knox(DeviceSnapshot d) => d.WarrantyTripped switch
    {
        true => new("knox", "Knox warranty bit", CheckSeverity.Info, "Already tripped (0x1)."),
        null when d.Mode == DeviceMode.Download => new("knox", "Knox warranty bit", CheckSeverity.Info, "Not reported in Download mode."),
        _ => new("knox", "Knox warranty bit", CheckSeverity.Warning, "Intact. Installing will permanently trip it.",
            "Samsung Pay, Secure Folder and Knox-based features stop working permanently."),
    };

    private static CheckResult CurrentMode(DeviceSnapshot d) => d.Mode switch
    {
        DeviceMode.Recovery => new("mode", "Current mode", CheckSeverity.Info, $"Recovery{(d.RecoveryVersion is null ? "" : $" (TWRP {d.RecoveryVersion})")}"),
        DeviceMode.Download => new("mode", "Current mode", CheckSeverity.Info, "Download mode (the phone identified earlier)"),
        _ => new("mode", "Current mode", CheckSeverity.Info, $"Android {d.AndroidVersion}"),
    };

    private static CheckResult Geometry(DeviceProfile profile) =>
        profile.Geometry is { } known
            ? new("layout", "Partition layout", CheckSeverity.Pass,
                $"Validated for a {known.DiskBytes / (1024L * 1024 * 1024)} GB reference unit.")
            : new("layout", "Partition layout", CheckSeverity.Info,
                "Will be read off this phone.",
                "This board has no reference layout on file, so the installer measures the partition table before it builds the Windows image.");
}