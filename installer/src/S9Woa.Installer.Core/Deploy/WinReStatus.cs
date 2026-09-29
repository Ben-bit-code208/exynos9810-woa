// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Globalization;
using System.Text;

namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// The status the installer writes to <c>/tmp/s9woa/status</c> in TWRP so the
/// baked-in <c>winre-statuswatch.sh</c> can raise the "Installing Windows" screen
/// and name the current phase. It is a tiny key=value file, rewritten as the
/// install progresses and deleted at the end; the watcher treats a file older
/// than 30 s as a crashed installer. The <c>label</c> is what appears under the
/// gears, so it is kept short and human ("Copying Windows", "Writing boot files",
/// "Installing firmware").
/// </summary>
public sealed record WinReStatus(string Phase, string Label, int? Percent = null, long? DoneBytes = null, long? TotalBytes = null)
{
    /// <summary>Where the file lives on the phone (inside TWRP's RAM staging dir).</summary>
    public const string DeviceDir = "/tmp/s9woa";
    public const string DevicePath = DeviceDir + "/status";

    public static WinReStatus Copying(int? percent = null, long? done = null, long? total = null) =>
        new("copy", "Copying Windows", percent, done, total);

    public static WinReStatus BootFiles() => new("bootfiles", "Writing boot files");

    public static WinReStatus Firmware() => new("firmware", "Installing firmware");

    /// <summary>Percent from bytes, clamped to 0..100; null when totals are unknown.</summary>
    public static int? PercentOf(long done, long total) =>
        total > 0 ? (int)Math.Clamp(done * 100 / total, 0, 100) : null;

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

    // Values are read by a shell `while IFS='=' read k v` loop, so a newline or a
    // stray '=' in a value would corrupt the file; strip them and keep the label
    // to one clean line.
    private static string Sanitize(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal)
             .Replace("\n", " ", StringComparison.Ordinal)
             .Replace("=", "-", StringComparison.Ordinal)
             .Trim();
}
