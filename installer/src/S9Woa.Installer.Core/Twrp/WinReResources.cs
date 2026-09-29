// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Reflection;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>Loads the embedded WinRE theme, scripts, art and modules from Core.</summary>
internal static class WinReResources
{
    private static readonly Assembly Asm = typeof(WinReResources).Assembly;
    private const string Root = "S9Woa.Installer.Core.Twrp.Assets.";

    public static string ThemeText(string fileName) =>
        System.Text.Encoding.UTF8.GetString(Bytes($"theme.{fileName}"));

    public static byte[] Bytes(string logicalSuffix)
    {
        var name = Root + logicalSuffix;
        using var stream = Asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource missing: {name}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>All resources under a virtual folder, keyed by their file name.</summary>
    public static IReadOnlyDictionary<string, byte[]> Folder(string folder)
    {
        var prefix = Root + folder + ".";
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in Asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var key = name[prefix.Length..];
            using var stream = Asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            result[key] = ms.ToArray();
        }
        return result;
    }

    /// <summary>Tile-icon resource stems (winre_ic_*), sorted.</summary>
    public static IReadOnlyList<string> IconNames() =>
        Folder("images").Keys
            .Where(k => k.StartsWith("winre_ic_", StringComparison.Ordinal) && k.EndsWith(".png", StringComparison.Ordinal))
            .Select(k => k[..^4])
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
}
