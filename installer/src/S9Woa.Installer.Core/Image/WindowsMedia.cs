// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Text.RegularExpressions;

namespace S9Woa.Installer.Core.Image;

/// <summary>One Windows edition inside a WIM/ESD, as reported by <c>dism /Get-ImageInfo</c>.</summary>
public sealed record WindowsImageEdition(int Index, string Name);

/// <summary>Locates the install image inside user media and lists its editions.</summary>
public sealed partial class WindowsMedia
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private readonly Processes.IProcessRunner _runner;
    private readonly string _dism;

    public WindowsMedia(Processes.IProcessRunner runner, string? system32 = null)
    {
        _runner = runner;
        _dism = Path.Combine(system32 ?? Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe");
    }

    /// <summary>An .esd/.wim is the image itself; inside a mounted ISO it is under <c>sources\install.*</c>.</summary>
    public static string? FindInstallImage(string mediaPathOrMountRoot)
    {
        var ext = Path.GetExtension(mediaPathOrMountRoot);
        if (ext.Equals(".wim", StringComparison.OrdinalIgnoreCase) || ext.Equals(".esd", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(mediaPathOrMountRoot) ? mediaPathOrMountRoot : null;
        }
        foreach (var name in new[] { "install.wim", "install.esd" })
        {
            var candidate = Path.Combine(mediaPathOrMountRoot, "sources", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    [GeneratedRegex(@"Index\s*:\s*(\d+)\s*[\r\n]+\s*Name\s*:\s*(.+?)\s*[\r\n]", RegexOptions.Multiline)]
    private static partial Regex IndexNameRegex();

    internal static IReadOnlyList<WindowsImageEdition> ParseImageInfo(string dismOutput) =>
        IndexNameRegex().Matches(dismOutput)
            .Select(m => new WindowsImageEdition(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value.Trim()))
            .ToList();

    public async Task<IReadOnlyList<WindowsImageEdition>> GetEditionsAsync(string installImage, CancellationToken ct = default)
    {
        var r = await _runner.RunAsync(_dism, ["/Get-ImageInfo", $"/ImageFile:{installImage}", "/English"], Timeout, ct).ConfigureAwait(false);
        if (!r.Succeeded)
        {
            throw new InvalidOperationException($"Could not read {Path.GetFileName(installImage)}: {r.StdOut.Trim()}");
        }
        return ParseImageInfo(r.StdOut);
    }

    /// <summary>Prefer Pro, then Home, else the first edition.</summary>
    public static WindowsImageEdition ChooseEdition(IReadOnlyList<WindowsImageEdition> editions)
    {
        if (editions.Count == 0)
        {
            throw new InvalidOperationException("The media contains no Windows editions.");
        }
        return editions.FirstOrDefault(e => e.Name.Contains("Pro", StringComparison.OrdinalIgnoreCase) && !e.Name.Contains("Education", StringComparison.OrdinalIgnoreCase))
            ?? editions.FirstOrDefault(e => e.Name.Contains("Home", StringComparison.OrdinalIgnoreCase))
            ?? editions[0];
    }
}
