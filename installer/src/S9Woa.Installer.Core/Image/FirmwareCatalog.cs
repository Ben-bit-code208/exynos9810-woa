// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S9Woa.Installer.Core.Image;

/// <summary>One UEFI build and the single Windows build its boot-time adapters accept.</summary>
public sealed record FirmwareImage
{
    [JsonPropertyName("file")] public required string File { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
    /// <summary>Kernel build the adapters target, e.g. <c>22621.2428</c>.</summary>
    [JsonPropertyName("windows")] public required string Windows { get; init; }
    /// <summary>Builds DISM reports for media carrying that kernel (22621.x and 22631.x share it).</summary>
    [JsonPropertyName("mediaBuilds")] public IReadOnlyList<string> MediaBuilds { get; init; } = [];
    [JsonPropertyName("loaderSha256")] public required string LoaderSha256 { get; init; }
    [JsonPropertyName("kernelSha256")] public required string KernelSha256 { get; init; }
}

/// <summary>
/// The UEFI images available for this phone. Each build patches the Windows boot loader and
/// kernel in memory for exactly one Windows build and halts on any other (the phone stays on
/// the Samsung logo), so the firmware is chosen by the loader/kernel the image actually holds.
/// Stored as <c>firmware.json</c> next to the images.
/// </summary>
public sealed class FirmwareCatalog
{
    public const string FileName = "firmware.json";
    public const string Schema = "s9woa.firmware-catalog.v1";

    private sealed record Document
    {
        [JsonPropertyName("schema")] public string? Schema { get; init; }
        [JsonPropertyName("images")] public List<FirmwareImage> Images { get; init; } = [];
    }

    private FirmwareCatalog(string directory, IReadOnlyList<FirmwareImage> images) => (Directory, Images) = (directory, images);

    public string Directory { get; }
    public IReadOnlyList<FirmwareImage> Images { get; }

    public string PathOf(FirmwareImage image) => System.IO.Path.Combine(Directory, image.File);

    /// <summary>Reads <c>firmware.json</c> in <paramref name="directory"/>; null if there is none.</summary>
    public static FirmwareCatalog? Load(string directory)
    {
        var path = System.IO.Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }
        var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"{path} is empty.");
        if (doc.Schema != Schema)
        {
            throw new InvalidDataException($"{path} is not a {Schema} catalog.");
        }
        foreach (var image in doc.Images)
        {
            if (image.File.Contains('/') || image.File.Contains('\\') || image.File.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{path} names a file outside its folder: {image.File}");
            }
        }
        return new FirmwareCatalog(directory, doc.Images);
    }

    /// <summary>Images whose file is present with the expected size of a BOOT image.</summary>
    public IReadOnlyList<FirmwareImage> Present(long expectedBytes) =>
        Images.Where(i => File.Exists(PathOf(i)) && new FileInfo(PathOf(i)).Length <= expectedBytes).ToList();

    /// <summary>The image built for exactly this boot loader and kernel, or null.</summary>
    public FirmwareImage? ForBootFiles(string loaderSha256, string kernelSha256) =>
        Images.FirstOrDefault(i => i.LoaderSha256.Equals(loaderSha256, StringComparison.OrdinalIgnoreCase)
                                   && i.KernelSha256.Equals(kernelSha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>The image for a media build as DISM reports it (e.g. <c>22631.2428</c>), or null.</summary>
    public FirmwareImage? ForMediaBuild(string build) =>
        Images.FirstOrDefault(i => i.Windows == build || i.MediaBuilds.Contains(build));

    /// <summary>Every media build (as DISM reports it) that some image can start.</summary>
    public string SupportedBuilds => string.Join(", ",
        Images.SelectMany(i => i.MediaBuilds.Prepend(i.Windows)).Distinct(StringComparer.Ordinal));

    /// <summary>True when the file on disk still has the catalogued SHA-256.</summary>
    public bool Verify(FirmwareImage image)
    {
        using var s = File.OpenRead(PathOf(image));
        return Convert.ToHexString(SHA256.HashData(s)).Equals(image.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Copies a catalog and its images into <paramref name="destination"/>, verifying every hash.</summary>
    public FirmwareCatalog CopyTo(string destination)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (var image in Images)
        {
            if (!Verify(image))
            {
                throw new InvalidDataException($"{image.File} does not match the SHA-256 in {FileName}.");
            }
            File.Copy(PathOf(image), System.IO.Path.Combine(destination, image.File), overwrite: true);
        }
        File.Copy(System.IO.Path.Combine(Directory, FileName), System.IO.Path.Combine(destination, FileName), overwrite: true);
        return Load(destination)!;
    }
}
