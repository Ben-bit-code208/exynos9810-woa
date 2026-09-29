// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Text.RegularExpressions;

namespace S9Woa.Installer.Core.Device;

/// <summary>
/// A Samsung firmware build string such as <c>G965FXXUHFVG4</c>:
/// model, CSC, update type, binary (anti-rollback) revision, OS letter, year, month, build.
/// </summary>
public sealed partial record SamsungBuild(string Model, string Csc, char UpdateType, int BinaryRevision,
    char OsLetter, char Year, char Month, char Build, string Raw)
{
    [GeneratedRegex(@"^(?<csc>[A-Z]{2,3})(?<type>[US])(?<rev>[1-9A-Z])(?<os>[A-Z])(?<year>[A-Z])(?<month>[A-L])(?<build>[1-9A-Z])$")]
    private static partial Regex Tail();

    public static SamsungBuild? TryParse(string? raw, string modelCode)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith(modelCode, StringComparison.Ordinal))
        {
            return null;
        }
        var m = Tail().Match(raw[modelCode.Length..]);
        if (!m.Success)
        {
            return null;
        }
        return new SamsungBuild(modelCode, m.Groups["csc"].Value, m.Groups["type"].Value[0],
            DecodeRevision(m.Groups["rev"].Value[0]), m.Groups["os"].Value[0], m.Groups["year"].Value[0],
            m.Groups["month"].Value[0], m.Groups["build"].Value[0], raw);
    }

    /// <summary>1-9 then A=10, B=11, ...</summary>
    public static int DecodeRevision(char c) => c is >= '1' and <= '9' ? c - '0' : c - 'A' + 10;

    /// <summary>Chronological key: year, month, build (letters sort after digits).</summary>
    public (char, char, char) ReleaseKey => (Year, Month, Build);
}

public enum DeviceMode
{
    Android,
    Recovery,
    Unauthorized,
    Offline,
    Unknown,
}

public sealed record DeviceSnapshot(
    string Serial,
    DeviceMode Mode,
    string? Model,
    string? Codename,
    string? Hardware,
    string? Bootloader,
    string? AndroidVersion,
    string? RecoveryVersion,
    bool? OemUnlockAllowed,
    bool? FlashLocked,
    string? VerifiedBootState,
    bool? WarrantyTripped)
{
    public static DeviceSnapshot FromAdb(AdbDevice device, IReadOnlyDictionary<string, string>? props)
    {
        string? P(params string[] keys) => props is null ? null
            : keys.Select(k => props.TryGetValue(k, out var v) && v.Length > 0 ? v : null).FirstOrDefault(v => v is not null);
        bool? B(params string[] keys) => P(keys) switch { "1" => true, "0" => false, _ => null };

        var twrp = P("ro.twrp.version");
        var mode = device.State switch
        {
            AdbState.Unauthorized => DeviceMode.Unauthorized,
            AdbState.Offline => DeviceMode.Offline,
            AdbState.Recovery or AdbState.Sideload => DeviceMode.Recovery,
            AdbState.Device when twrp is not null => DeviceMode.Recovery,
            AdbState.Device => DeviceMode.Android,
            _ => DeviceMode.Unknown,
        };
        return new DeviceSnapshot(
            device.Serial,
            mode,
            // S-Boot sets ro.boot.em.model; custom recoveries often report a marketing name in ro.product.model.
            P("ro.boot.em.model", "ro.product.model", "ro.product.system.model") ?? device.Model?.Replace('_', '-'),
            P("ro.product.device", "ro.product.vendor.device") ?? device.Product,
            P("ro.boot.hardware", "ro.hardware"),
            P("ro.boot.bootloader", "ro.bootloader"),
            P("ro.build.version.release"),
            twrp,
            B("sys.oem_unlock_allowed"),
            B("ro.boot.flash.locked"),
            P("ro.boot.verifiedbootstate"),
            B("ro.boot.warranty_bit"));
    }
}
