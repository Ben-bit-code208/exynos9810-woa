// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Globalization;
using System.Text.RegularExpressions;

namespace S9Woa.Installer.Core.Stages;

/// <summary>
/// Reads progress out of the log lines the stages already write, so the install page can show a
/// real bar: exact where a stage reports counts (copying to the phone, exporting the image,
/// flashing TWRP, backing up) and phase-based for the image build, whose DISM steps report none.
/// </summary>
public sealed partial class StageProgress
{
    /// <summary>Rough share of a typical install's time, for the overall bar. Unlisted stages count 1.</summary>
    private static readonly Dictionary<string, double> Weights = new()
    {
        ["image"] = 40, ["twrp"] = 4, ["backup"] = 8, ["transfer"] = 25, ["uefi"] = 2, ["firstboot"] = 12,
    };

    /// <summary>
    /// Where the image build is when a line with this prefix appears, measured on a Core build of
    /// 22631.2428 (apply ~1/3, app and package removal to ~70 %, component store to ~87 %).
    /// Other profiles skip phases, which only makes the bar jump ahead.
    /// </summary>
    private static readonly (string Prefix, double Fraction)[] ImagePhases =
    [
        ("Creating a virtual disk", 0.01),
        ("Applying Windows image", 0.02),
        ("Adding driver", 0.33),
        ("Applying the ", 0.35),
        ("Removing app", 0.35),
        ("Removing package", 0.50),
        ("Delete Program Files", 0.70),
        ("Delete Windows", 0.70),
        ("Taking ownership", 0.72),
        ("Keeping ", 0.87),
        ("Component store cleanup", 0.89),
        ("Writing the OOBE answer file", 0.90),
        ("Writing UEFI boot files", 0.90),
        ("Copying the EFI boot files", 0.91),
    ];

    private const double ExportStart = 0.91;

    [GeneratedRegex(@"^[A-Za-z0-9_]+: (\d+(?:\.\d+)?) of (\d+(?:\.\d+)?) GiB")]
    private static partial Regex CopiedGiB();

    [GeneratedRegex(@"^exported (\d+)/(\d+) MiB")]
    private static partial Regex ExportedMiB();

    [GeneratedRegex(@"^(\d{1,3})%$")]
    private static partial Regex Percent();

    [GeneratedRegex(@"^Backing up \S+ \((\d+) of (\d+)\)")]
    private static partial Regex BackingUp();

    [GeneratedRegex(@"^\d\d:\d\d:\d\d ")]
    private static partial Regex Timestamp();

    public string? Stage { get; private set; }

    /// <summary>0..1 through the current stage, or null while it can't be measured.</summary>
    public double? Fraction { get; private set; }

    /// <summary>The latest line worth showing under the stage title.</summary>
    public string Detail { get; private set; } = "";

    public void Begin(string stageId)
    {
        Stage = stageId;
        Fraction = null;
        Detail = "";
    }

    public static double WeightOf(string stageId) => Weights.TryGetValue(stageId, out var w) ? w : 1;

    /// <summary>0..1 through the whole install: finished stages plus the running one's share.</summary>
    public static double Overall(IEnumerable<(string Id, bool Finished)> stages, string? running, double? runningFraction)
    {
        double total = 0, done = 0;
        foreach (var (id, finished) in stages)
        {
            var w = WeightOf(id);
            total += w;
            if (finished)
            {
                done += w;
            }
            else if (id == running)
            {
                done += w * (runningFraction ?? 0);
            }
        }
        return total == 0 ? 0 : Math.Clamp(done / total, 0, 1);
    }

    /// <summary>Feeds one log line (a leading HH:mm:ss is ignored). Returns true when anything changed.</summary>
    public bool Observe(string line)
    {
        var text = Timestamp().Replace(line, "", 1).Trim();
        if (text.Length == 0 || text.StartsWith("stage ", StringComparison.Ordinal))
        {
            return false;
        }

        double? fraction = null;
        var detail = text;
        if (CopiedGiB().Match(text) is { Success: true } copied && Ratio(copied) is { } c)
        {
            fraction = c;
        }
        else if (ExportedMiB().Match(text) is { Success: true } exported && Ratio(exported) is { } e)
        {
            fraction = Stage == "image" ? ExportStart + (1 - ExportStart) * e : e;
            detail = $"Exporting the Windows volume: {Math.Round(e * 100).ToString(CultureInfo.InvariantCulture)}%";
        }
        else if (Percent().Match(text) is { Success: true } pct)
        {
            fraction = Math.Min(100, int.Parse(pct.Groups[1].Value, CultureInfo.InvariantCulture)) / 100.0;
            detail = $"Flashing: {pct.Groups[1].Value}%";
        }
        else if (BackingUp().Match(text) is { Success: true } backup && Ratio(backup) is { } b)
        {
            var count = int.Parse(backup.Groups[2].Value, CultureInfo.InvariantCulture);
            fraction = b - 1.0 / Math.Max(1, count);
        }
        else if (Stage == "image")
        {
            fraction = ImagePhases.Where(p => text.StartsWith(p.Prefix, StringComparison.Ordinal))
                .Select(p => (double?)p.Fraction).FirstOrDefault();
        }

        var changed = detail != Detail;
        Detail = detail;
        if (fraction is { } f && (Fraction is null || f > Fraction))
        {
            Fraction = Math.Clamp(f, 0, 1);
            changed = true;
        }
        return changed;
    }

    private static double? Ratio(Match m)
    {
        var done = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var total = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return total > 0 ? Math.Clamp(done / total, 0, 1) : null;
    }
}
