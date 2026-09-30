// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace S9Woa.Installer.Core;

/// <summary>
/// Keeps personal identifiers out of the log and on-screen text, so a log can be attached to an
/// issue and the installer can be screen-recorded as is: a phone serial becomes a stable alias
/// (<see cref="Alias"/>), the Windows user folder becomes <c>%USERPROFILE%</c>, and this PC's own
/// Windows version (which can identify a preview or internal build) becomes <see cref="Hidden"/>.
/// </summary>
public sealed partial class Redactor
{
    public const string Hidden = "(hidden)";
    private const int MinimumLiteralLength = 6;
    private readonly object _lock = new();
    private readonly List<(string Literal, string Replacement)> _literals = [];
    private readonly string? _userProfile;

    public Redactor(string? userProfile)
    {
        _userProfile = string.IsNullOrWhiteSpace(userProfile) ? null : Path.TrimEndingDirectorySeparator(userProfile.Trim());
    }

    /// <summary>Hides this serial from now on. Very short values are ignored: they would mangle ordinary words.</summary>
    public void AddSerial(string? serial)
    {
        if (serial?.Trim() is { Length: >= MinimumLiteralLength } s)
        {
            Add(s, Alias(s));
        }
    }

    /// <summary>Hides this literal (e.g. the PC's own Windows version) from now on.</summary>
    public void Hide(string? literal)
    {
        if (literal?.Trim() is { Length: >= MinimumLiteralLength } s)
        {
            Add(s, Hidden);
        }
    }

    private void Add(string literal, string replacement)
    {
        lock (_lock)
        {
            if (!_literals.Any(l => l.Literal.Equals(literal, StringComparison.OrdinalIgnoreCase)))
            {
                _literals.Add((literal, replacement));
                _literals.Sort((a, b) => b.Literal.Length.CompareTo(a.Literal.Length));
            }
        }
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }
        (string Literal, string Replacement)[] literals;
        lock (_lock)
        {
            literals = [.. _literals];
        }
        foreach (var (literal, replacement) in literals)
        {
            text = text.Replace(literal, replacement, StringComparison.OrdinalIgnoreCase);
        }
        // DISM's banner reports the tool's version, which follows the PC's Windows build; the
        // "Image Version:" line below it is the media's and stays.
        text = ToolVersionLine().Replace(text, "${1}" + Hidden);
        return _userProfile is null ? text : text.Replace(_userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A stable stand-in for a phone serial: <c>phone-</c> and 8 hex digits of its SHA-256.</summary>
    public static string Alias(string serial) =>
        "phone-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial.Trim().ToLowerInvariant())))[..8].ToLowerInvariant();

    [GeneratedRegex(@"(?im)^([ \t]*Version:[ \t]*)\d+(?:\.\d+){2,3}")]
    private static partial Regex ToolVersionLine();
}
