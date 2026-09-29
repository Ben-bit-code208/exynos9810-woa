// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Image;

public enum SlimProfile
{
    /// <summary>Stock image, drivers only.</summary>
    None,

    /// <summary>Removes consumer inbox apps and telemetry/ads defaults. Image stays serviceable.</summary>
    Lite,

    /// <summary>
    /// tiny11 Core: Lite plus removal of Edge, WebView2, OneDrive, Defender, WinRE, Windows Update
    /// and optional packages, and a component store cut down to the servicing stack. The resulting
    /// image cannot receive cumulative updates or add features/languages later.
    /// </summary>
    Core,
}

public enum RegistryHive
{
    Software,
    System,
    /// <summary>Users\Default\NTUSER.DAT: the template for new user profiles.</summary>
    DefaultUser,
    /// <summary>Windows\System32\config\DEFAULT: the .DEFAULT (system account) user hive.</summary>
    SystemDefaultUser,
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

/// <summary>
/// Remove every CBS package whose identity starts with <see cref="NamePrefix"/>. Removing a
/// package also removes its satellites, so satellites that refuse removal on their own are skipped.
/// </summary>
public sealed record RemovePackage(string NamePrefix)
    : SlimOperation($"Remove package {NamePrefix}");

/// <param name="OnlyIfKeyExists">Skip when the key is absent instead of creating it (service keys).</param>
public sealed record SetRegistryValue(RegistryHive Hive, string Key, string Name, RegistryValueKind Kind, string Value,
    bool OnlyIfKeyExists = false)
    : SlimOperation($"Set {Hive}\\{Key}\\{Name} = {Value}");

/// <summary>Delete a registry key and its subkeys; an absent key is not an error.</summary>
public sealed record DeleteRegistryKey(RegistryHive Hive, string Key)
    : SlimOperation($"Delete {Hive}\\{Key}");

/// <summary>
/// Delete a file or directory relative to the image root, taking ownership first when
/// <see cref="TakeOwnership"/> is set (TrustedInstaller-owned paths). Absent paths are skipped.
/// Restricted by <see cref="SlimPlan.IsProtectedPath"/>.
/// </summary>
public sealed record DeleteImagePath(string RelativePath, bool TakeOwnership = false)
    : SlimOperation($"Delete {RelativePath}");

/// <summary>
/// Cut Windows\WinSxS down to the entries matching <see cref="Keep"/> (servicing stack, manifests,
/// side-by-side runtime assemblies). Files hard-linked into System32 stay in place.
/// </summary>
public sealed record TrimComponentStore(IReadOnlyList<string> Keep)
    : SlimOperation("Trim the component store (WinSxS) to the servicing stack");

/// <param name="Optional">Log a failure instead of stopping (after the store has been trimmed).</param>
public sealed record ComponentCleanup(bool ResetBase, bool Optional = false)
    : SlimOperation(ResetBase ? "Component store cleanup with /ResetBase" : "Component store cleanup");
