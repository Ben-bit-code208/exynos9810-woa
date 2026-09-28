// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace S9Woa.Installer.Core.Toolset;

public sealed record ReleaseAsset(string Name, Uri Url, long Size);

public sealed record ReleaseInfo(string Tag, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// Downloads project artefacts (<c>uefi.img</c>, <c>drivers.zip</c>) from the
/// latest GitHub release. A release must publish <c>SHA256SUMS</c>; every file is
/// verified against it and nothing unverified is kept.
/// </summary>
public sealed class ReleaseClient
{
    public const string ChecksumsAsset = "SHA256SUMS";
    private readonly HttpClient _http;

    public ReleaseClient(HttpClient http) => _http = http;

    public async Task<ReleaseInfo> GetLatestAsync(string repo, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
        {
            throw new ArgumentException($"\"{repo}\" is not a GitHub owner/name.", nameof(repo));
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("S9WoaInstaller", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"{repo} has no published release yet. Use a local build folder instead.");
        }
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var assets = root.GetProperty("assets").EnumerateArray()
            .Select(a => new ReleaseAsset(a.GetProperty("name").GetString()!,
                new Uri(a.GetProperty("browser_download_url").GetString()!), a.GetProperty("size").GetInt64()))
            .ToList();
        return new ReleaseInfo(root.GetProperty("tag_name").GetString() ?? "", assets);
    }

    internal static IReadOnlyDictionary<string, string> ParseChecksums(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Length == 64)
            {
                map[parts[1].TrimStart('*').Trim()] = parts[0].ToLowerInvariant();
            }
        }
        return map;
    }

    /// <summary>Downloads one asset to <paramref name="destination"/>, verified against SHA256SUMS.</summary>
    public async Task DownloadVerifiedAsync(ReleaseInfo release, string assetName, string destination,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        var sums = release.Assets.FirstOrDefault(a => a.Name == ChecksumsAsset)
            ?? throw new InvalidOperationException($"Release {release.Tag} has no {ChecksumsAsset}; refusing unverified downloads.");
        var asset = release.Assets.FirstOrDefault(a => a.Name == assetName)
            ?? throw new InvalidOperationException($"Release {release.Tag} does not contain {assetName}.");

        var checksums = ParseChecksums(await _http.GetStringAsync(sums.Url, ct).ConfigureAwait(false));
        if (!checksums.TryGetValue(assetName, out var expected))
        {
            throw new InvalidOperationException($"{ChecksumsAsset} in {release.Tag} has no entry for {assetName}.");
        }

        log?.Report($"Downloading {assetName} ({asset.Size / (1024 * 1024)} MiB) from release {release.Tag}...");
        var temp = destination + ".download";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        string actual;
        await using (var source = await _http.GetStreamAsync(asset.Url, ct).ConfigureAwait(false))
        await using (var file = File.Create(temp))
        {
            using var sha = SHA256.Create();
            await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write, leaveOpen: true))
            {
                await source.CopyToAsync(hashing, ct).ConfigureAwait(false);
            }
            actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            File.Delete(temp);
            throw new InvalidOperationException($"{assetName} failed verification (expected {expected[..12]}…, got {actual[..12]}…).");
        }
        File.Move(temp, destination, overwrite: true);
        log?.Report($"{assetName} verified.");
    }

    /// <summary>Extracts a zip into an empty directory, refusing entries that escape it.</summary>
    public static void ExtractSafely(string zipFile, string destinationDir)
    {
        if (Directory.Exists(destinationDir))
        {
            Directory.Delete(destinationDir, recursive: true);
        }
        Directory.CreateDirectory(destinationDir);
        var root = Path.GetFullPath(destinationDir + Path.DirectorySeparatorChar);
        using var zip = ZipFile.OpenRead(zipFile);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Archive entry {entry.FullName} escapes the destination.");
            }
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
