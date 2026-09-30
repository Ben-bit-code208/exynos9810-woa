// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Globalization;
using System.Text;

namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// The status the installer writes to <c>/tmp/s9woa/status</c> in TWRP so the
/// baked-in <c>winre-statuswatch.sh</c> can raise the "Installing Windows" screen,
/// name the current phase and move its progress bar. It is a tiny key=value file,
/// rewritten as the install progresses (appended to shell commands the installer
/// runs anyway, see <see cref="TwrpClient.QueueInstallStatus"/>) and deleted at the
/// end of a phase. The watcher copies <c>label</c>, <c>percent</c> and
/// <c>detail</c> into Android properties the screen reads live, so the values are
/// kept short (a property value holds at most 91 bytes).
/// </summary>
public sealed record WinReStatus(string Phase, string Label, int? Percent = null, long? DoneBytes = null, long? TotalBytes = null)
{
    /// <summary>Where the file lives on the phone (inside TWRP's RAM staging dir).</summary>
    public const string DeviceDir = "/tmp/s9woa";
    public const string DevicePath = DeviceDir + "/status";

    /// <summary>Longest value the screen shows (Android's PROP_VALUE_MAX is 92 including the NUL).</summary>
    public const int MaxValueBytes = 90;

    public static WinReStatus Copying(int? percent = null, long? done = null, long? total = null) =>
        new("copy", "Copying Windows", percent, done, total);

    public static WinReStatus BootFiles() => new("bootfiles", "Writing boot files");

    public static WinReStatus Firmware() => new("firmware", "Installing firmware");

    /// <summary>Percent from bytes, clamped to 0..100; null when totals are unknown.</summary>
    public static int? PercentOf(long done, long total) =>
        total > 0 ? (int)Math.Clamp(done * 100 / total, 0, 100) : null;

    /// <summary>The line under the progress bar, e.g. "37% complete · 7.4 of 20.0 GB".</summary>
    public string? Detail
    {
        get
        {
            if (Percent is not { } p)
            {
                return null;
            }
            var text = string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(p, 0, 100)}% complete");
            if (DoneBytes is { } d && TotalBytes is { } t && t > 0)
            {
                text += string.Create(CultureInfo.InvariantCulture, $" \u00B7 {d / GiB:0.0} of {t / GiB:0.0} GB");
            }
            return text;
        }
    }

    private const double GiB = 1024.0 * 1024 * 1024;

    /// <summary>Serialises to the LF-terminated key=value form the watcher parses.</summary>
    public string ToFileContents()
    {
        var sb = new StringBuilder();
        sb.Append("phase=").Append(Sanitize(Phase)).Append('\n');
        sb.Append("label=").Append(Sanitize(Label)).Append('\n');
        if (Percent is { } p)
        {
            sb.Append("percent=").Append(Math.Clamp(p, 0, 100).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        if (Detail is { } detail)
        {
            sb.Append("detail=").Append(Sanitize(detail)).Append('\n');
        }
        if (DoneBytes is { } d)
        {
            sb.Append("done_bytes=").Append(d.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        if (TotalBytes is { } t)
        {
            sb.Append("total_bytes=").Append(t.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    // Values are read by a shell `while IFS='=' read k v` loop and end up in an
    // Android property, so a newline or a stray '=' would corrupt the file and a
    // long value would be refused: strip them and keep each value short.
    private static string Sanitize(string value)
    {
        var clean = value.Replace("\r", " ", StringComparison.Ordinal)
                         .Replace("\n", " ", StringComparison.Ordinal)
                         .Replace("=", "-", StringComparison.Ordinal)
                         .Trim();
        while (Encoding.UTF8.GetByteCount(clean) > MaxValueBytes)
        {
            clean = clean[..^1];
        }
        return clean;
    }
}
