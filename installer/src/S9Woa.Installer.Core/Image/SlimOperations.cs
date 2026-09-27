// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Image;

public enum SlimProfile
{
    /// <summary>Stock image, drivers only.</summary>
    None,

    /// <summary>Removes consumer inbox apps and telemetry/ads defaults. Image stays serviceable.</summary>
    Lite,

    /// <summary>
    /// Lite plus removal of heavyweight components (Defender, WinRE image, optional capabilities)
    /// and a /ResetBase component cleanup. The resulting image cannot receive cumulative updates
    /// or add features/languages later.
    /// </summary>
    Core,
}

public enum RegistryHive
{
    Software,
    System,
    DefaultUser,
}

public enum RegistryValueKind
{
    DWord,
    String,
}

public abstract record SlimOperation(string Description);

/// <summary>Remove every provisioned appx whose PackageName starts with <see cref="NamePrefix"/>.</summary>
public sealed record RemoveProvisionedAppx(string NamePrefix)
    : SlimOperation($"Remove provisioned app {NamePrefix}");

/// <summary>Remove every installed capability whose identity starts with <see cref="IdentityPrefix"/>.</summary>
public sealed record RemoveCapability(string IdentityPrefix)
    : SlimOperation($"Remove capability {IdentityPrefix}");

public sealed record DisableFeature(string FeatureName)
    : SlimOperation($"Disable optional feature {FeatureName}");

public sealed record SetRegistryValue(RegistryHive Hive, string Key, string Name, RegistryValueKind Kind, string Value)
    : SlimOperation($"Set {Hive}\\{Key}\\{Name} = {Value}");

/// <summary>Delete one file relative to the image root. Restricted by <see cref="SlimPlan.IsProtectedPath"/>.</summary>
public sealed record DeleteImageFile(string RelativePath)
    : SlimOperation($"Delete {RelativePath}");

public sealed record ComponentCleanup(bool ResetBase)
    : SlimOperation(ResetBase ? "Component store cleanup with /ResetBase" : "Component store cleanup");
