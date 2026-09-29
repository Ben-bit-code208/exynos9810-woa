// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Text.Json;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>What the toolset recorded about the WinRE recovery it flashes.</summary>
public sealed class WinReRecoveryInfo
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public int SchemaVersion { get; set; } = 1;

    /// <summary>Builder version that produced it, or <c>prebuilt</c> for an accepted WinRE image.</summary>
    public string Builder { get; set; } = "";

    /// <summary>SHA-256 of the base TWRP image it was built from (empty for a prebuilt one).</summary>
    public string BaseSha256 { get; set; } = "";

    /// <summary>SHA-256 of the built WinRE image.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary><c>built</c> or <c>prebuilt</c>.</summary>
    public string Source { get; set; } = "built";

    public const string Prebuilt = "prebuilt";
    public const string Built = "built";

    public static WinReRecoveryInfo? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<WinReRecoveryInfo>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

/// <summary>Turns a chosen TWRP image into the WinRE recovery the installer flashes.</summary>
public interface IWinReRecoveryBuilder
{
    /// <summary>Is this file the official TWRP, an existing WinRE build, or neither?</summary>
    BaseImageKind Classify(string imagePath);

    /// <summary>
    /// Build the WinRE recovery from the official TWRP at <paramref name="basePath"/> into
    /// <paramref name="outputPath"/>, using kernel modules from <paramref name="modulesDirectory"/>
    /// if present (else the embedded ones). Throws with a user-facing message if the base is
    /// not the official TWRP or a Windows font is missing.
    /// </summary>
    WinReRecoveryInfo Build(string basePath, string outputPath, string? modulesDirectory, IProgress<string>? log = null);
}

/// <summary>Default builder backed by <see cref="WinReTwrpBuilder"/>.</summary>
public sealed class WinReRecoveryBuilder : IWinReRecoveryBuilder
{
    public BaseImageKind Classify(string imagePath) =>
        File.Exists(imagePath) ? WinReTwrpBuilder.Classify(File.ReadAllBytes(imagePath)) : BaseImageKind.Unknown;

    public WinReRecoveryInfo Build(string basePath, string outputPath, string? modulesDirectory, IProgress<string>? log = null)
    {
        var report = new List<string>();
        var result = new WinReTwrpBuilder().Build(File.ReadAllBytes(basePath), modulesDirectory: modulesDirectory, report: report);
        foreach (var line in report)
        {
            log?.Report(line);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, result.Image);
        return new WinReRecoveryInfo
        {
            Builder = result.BuilderVersion,
            BaseSha256 = result.BaseSha256,
            Sha256 = result.Sha256,
            Source = WinReRecoveryInfo.Built,
        };
    }
}
