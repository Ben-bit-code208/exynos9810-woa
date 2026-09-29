// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>
/// The stock-theme transforms that turn TWRP's own <c>ui.xml</c> and
/// <c>portrait.xml</c> into the WinRE look: it splices in the WinRE fonts, image
/// and animation resources plus the shared variable block, recolours the stock
/// palette and swaps its fonts so every untouched stock page is restyled without
/// rewriting page logic, includes <c>winre.xml</c>, and renames the stock
/// <c>main</c>/<c>lock</c> pages out of the way. This mirrors the reference
/// Python <c>build.py</c> so the two builders produce an equivalent theme; the
/// same committed <c>winre.xml</c>/<c>splash.xml</c> back both.
/// </summary>
internal static partial class WinReTheme
{
    private const int Margin = 108;
    private const int TileW = 864;
    private const int TileH = 220;
    private const int RowPitch = 230;
    private const int Row0 = 560;
    private const int IconX = 156;
    private const int TextX = 264;

    // Must match tools/twrp-winre/mkassets.py.
    private const int CogPx = 420;
    private const int BarW = 480;
    private const int BarH = 12;
    private const int ThemeW = 1080, ThemeH = 1920, PanelW = 1440, PanelH = 2960;

    private static readonly IReadOnlyDictionary<string, string> StockColourOverrides = new Dictionary<string, string>
    {
        ["name=\"background_color\" value=\"#1A1A1A\""] = "name=\"background_color\" value=\"#000000\"",
        ["name=\"accent_color\" value=\"#0090CA\""] = "name=\"accent_color\" value=\"#0067C0\"",
        ["name=\"accent_color_semitransparent\" value=\"#0090CA30\""] = "name=\"accent_color_semitransparent\" value=\"#0067C030\"",
        ["name=\"text_color\" value=\"#EEEEEE\""] = "name=\"text_color\" value=\"#FFFFFF\"",
        ["name=\"text_button_color\" value=\"#EEEEEE\""] = "name=\"text_button_color\" value=\"#FFFFFF\"",
        ["name=\"highlight_color\" value=\"#1A1A1A80\""] = "name=\"highlight_color\" value=\"#0067C040\"",
        ["name=\"highlight\" value=\"#0090CA\""] = "name=\"highlight\" value=\"#0067C0\"",
    };

    private static readonly IReadOnlyDictionary<string, string> StockFontOverrides = new Dictionary<string, string>
    {
        ["name=\"font_l\" filename=\"RobotoCondensed-Regular.ttf\" size=\"54\""] = "name=\"font_l\" filename=\"winre-semilight.ttf\" size=\"50\"",
        ["name=\"font_m\" filename=\"RobotoCondensed-Regular.ttf\" size=\"42\""] = "name=\"font_m\" filename=\"winre-regular.ttf\" size=\"40\"",
        ["name=\"font_s\" filename=\"RobotoCondensed-Regular.ttf\" size=\"36\""] = "name=\"font_s\" filename=\"winre-regular.ttf\" size=\"34\"",
    };

    /// <summary>Stock PNGs that carry the teal accent as pixels (recoloured to blue).</summary>
    public static readonly IReadOnlyList<string> TealImages =
        ["progress_fill", "slider_used", "slider_touch", "handle", "checkbox_true", "radio_true"];

    private const string FontResources =
        "\n\t\t<font name=\"winre_h1\" filename=\"winre-light.ttf\" size=\"62\"/>\n" +
        "\t\t<font name=\"winre_h2\" filename=\"winre-semilight.ttf\" size=\"42\"/>\n" +
        "\t\t<font name=\"winre_title\" filename=\"winre-semilight.ttf\" size=\"38\"/>\n" +
        "\t\t<font name=\"winre_body\" filename=\"winre-regular.ttf\" size=\"27\"/>\n" +
        "\t\t<font name=\"winre_small\" filename=\"winre-regular.ttf\" size=\"23\"/>\n" +
        "\t\t<font name=\"winre_mono\" filename=\"DroidSansMono.ttf\" size=\"22\"/>\n";

    private const string AnimResources =
        "\t\t<animation name=\"winre_cogs\" filename=\"winrecog\" retainaspect=\"1\"/>\n" +
        "\t\t<animation name=\"winre_bar\" filename=\"winrebar\" retainaspect=\"1\"/>\n";

    public static IReadOnlyDictionary<string, string> BuildVariables()
    {
        var v = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["winre_bg"] = "#000000",
            ["winre_text"] = "#FFFFFF",
            ["winre_text_dim"] = "#B4B4B4",
            ["winre_stamp"] = "#4A4A4A",
            ["winre_hi"] = "#FFFFFF26",
            ["winre_clear"] = "#00000000",
            ["winre_console_bg"] = "#000000",
            ["winre_btn"] = "#FFFFFF33",
            ["winre_btn_hi"] = "#FFFFFF66",
            ["winre_margin"] = Margin.ToString(CultureInfo.InvariantCulture),
            ["winre_tile_w"] = TileW.ToString(CultureInfo.InvariantCulture),
            ["winre_tile_h"] = TileH.ToString(CultureInfo.InvariantCulture),
            ["winre_icon_x"] = IconX.ToString(CultureInfo.InvariantCulture),
            ["winre_text_x"] = TextX.ToString(CultureInfo.InvariantCulture),
            ["center_x"] = "540",
            ["winre_hdr_y"] = "336",
            ["winre_sub_y"] = "432",
            ["winre_back_hit_y"] = "120",
            ["winre_back_x"] = "120",
            ["winre_back_y"] = "212",
            ["winre_p2_y"] = "638",
            ["winre_p3_y"] = "696",
            ["winre_btn_row_y"] = "900",
            ["winre_btn2_x"] = (Margin + 400 + 40).ToString(CultureInfo.InvariantCulture),
            ["winre_console_y"] = "560",
            ["winre_console_h"] = "1000",
            ["winre_out_btn_y"] = "1620",
            ["winre_wait_y"] = "880",
            ["winre_wait_sub_y"] = "980",
            ["winre_stamp_y"] = "1812",
            ["winre_push_name_y"] = "1180",
        };
        for (var r = 1; r <= 4; r++)
        {
            var y = Row0 + ((r - 1) * RowPitch);
            v[$"winre_r{r}_y"] = y.ToString(CultureInfo.InvariantCulture);
            v[$"winre_r{r}_icon_y"] = (y + (TileH / 2)).ToString(CultureInfo.InvariantCulture);
            v[$"winre_r{r}_title_y"] = (y + 54).ToString(CultureInfo.InvariantCulture);
            v[$"winre_r{r}_desc_y"] = (y + 122).ToString(CultureInfo.InvariantCulture);
            v[$"winre_r{r}_solo_y"] = (y + 86).ToString(CultureInfo.InvariantCulture);
        }

        // Cog / bar placement, solved so the retainaspect-scaled bitmap lands
        // centred on the physical panel (identical maths to build.py).
        double sx = (double)PanelW / ThemeW, sy = (double)PanelH / ThemeH;
        var min = Math.Min(sx, sy);
        var cogDrawn = CogPx * min;
        (v["winre_cogs_x"], v["winre_cogs_y"]) = CentreXy(cogDrawn, cogDrawn, PanelW / 2.0, PanelH / 2.0, sx, sy);
        var (_, waitY) = CentreXy(cogDrawn, cogDrawn, PanelW / 2.0, (880 * sy) - (cogDrawn / 2) - 110, sx, sy);
        v["winre_cogs_wait_y"] = waitY;
        var barW = BarW * min;
        var barH = BarH * min;
        (v["winre_bar_x"], v["winre_bar_y"]) = CentreXy(barW, barH, PanelW / 2.0, 1100 * sy, sx, sy);
        return v;
    }

    private static (string X, string Y) CentreXy(double w, double h, double cx, double cy, double sx, double sy) =>
        (Math.Round((cx - (w / 2)) / sx, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture),
         Math.Round((cy - (h / 2)) / sy, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture));

    public static string PatchUiXml(string xml, IReadOnlyDictionary<string, string> vars, IReadOnlyList<string> iconNames)
    {
        if (xml.Contains("<xmlfile name=\"winre.xml\"/>", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ui.xml is already patched; start from a pristine image.");
        }
        var images = new StringBuilder();
        foreach (var n in iconNames)
        {
            images.Append("\t\t<image name=\"").Append(n).Append("\" filename=\"").Append(n).Append("\" retainaspect=\"1\"/>\n");
        }

        xml = Replace(xml, "<xmlfile name=\"portrait.xml\"/>",
            "<xmlfile name=\"portrait.xml\"/>\n\t\t<xmlfile name=\"winre.xml\"/>");
        xml = Replace(xml, "<resources>", "<resources>\n" + FontResources + images + AnimResources);
        xml = InjectVars(xml, vars, "ui.xml");

        foreach (var (old, @new) in StockColourOverrides)
        {
            xml = Replace(xml, old, @new);
        }
        foreach (var (old, @new) in StockFontOverrides)
        {
            xml = Replace(xml, old, @new);
        }
        xml = Replace(xml, "<description>Default basic theme</description>",
            "<description>Windows Recovery Environment shell</description>");
        return xml;
    }

    public static string PatchSplashXml(string xml, IReadOnlyDictionary<string, string> vars)
    {
        if (!xml.Contains("%winre_bg%", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("splash.xml does not use %winre_bg%; refusing to build.");
        }
        return InjectVars(xml, vars, "splash.xml");
    }

    public static (string Xml, int RefsRewritten) PatchPortraitXml(string xml)
    {
        foreach (var (old, @new) in new[] { ("main", "twrp_main"), ("main2", "twrp_main2"), ("lock", "twrp_lock") })
        {
            var needle = $"<page name=\"{old}\">";
            if (!xml.Contains(needle, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"portrait.xml has no {needle}.");
            }
            xml = Replace(xml, needle, $"<page name=\"{@new}\">");
        }
        var count = Main2Regex().Matches(xml).Count;
        xml = Main2Regex().Replace(xml, "twrp_main2");
        return (xml, count);
    }

    private static string InjectVars(string xml, IReadOnlyDictionary<string, string> vars, string where)
    {
        if (!xml.Contains("<variables>", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{where} has no <variables> block; cannot place WinRE vars.");
        }
        var block = new StringBuilder();
        foreach (var (k, v) in vars)
        {
            block.Append("\t\t<variable name=\"").Append(k).Append("\" value=\"").Append(v).Append("\"/>\n");
        }
        return Replace(xml, "<variables>", "<variables>\n" + block);
    }

    private static string Replace(string xml, string old, string @new)
    {
        var i = xml.IndexOf(old, StringComparison.Ordinal);
        if (i < 0)
        {
            throw new InvalidOperationException($"Reskin token not found: {old}");
        }
        return string.Concat(xml.AsSpan(0, i), @new, xml.AsSpan(i + old.Length));
    }

    [GeneratedRegex(@"(?<![\w-])main2(?![\w-])")]
    private static partial Regex Main2Regex();
}
