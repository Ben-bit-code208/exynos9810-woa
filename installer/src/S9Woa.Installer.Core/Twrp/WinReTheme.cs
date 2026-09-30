// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>
/// The theme transforms that turn TWRP's own <c>ui.xml</c> and <c>portrait.xml</c>
/// into the WinRE look. The stock edits themselves are data: the committed
/// <c>theme/reskin.xml</c> (palette, Segoe fonts, the page/console templates, the
/// keyboards, page renames), applied here exactly as tools/twrp-winre/build.py
/// applies it, so both builders produce the same theme. On top of that this splices
/// in the WinRE fonts, image and animation resources and the shared variable block,
/// and includes <c>winre.xml</c>.
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
    private const int CogPx = GearFrames.Size;
    private const int BarW = 480;
    private const int BarH = 12;
    private const int ThemeW = 1080, ThemeH = 1920, PanelW = 1440, PanelH = 2960;

    /// <summary>Extra single images the WinRE pages declare (the determinate install bar).</summary>
    public static readonly IReadOnlyList<string> ExtraImages = ["winre_pbar_empty", "winre_pbar_full"];

    /// <summary>Pages winre.xml must own (TWRP navigates to them by name).</summary>
    public static readonly IReadOnlyList<string> WinReOwnedPages =
        ["main", "lock", "singleaction_page", "action_page", "action_complete"];

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

    /// <summary>The shared variable block (identical to build.py's VARS).</summary>
    public static IReadOnlyDictionary<string, string> BuildVariables(int cogsFps = 24)
    {
        static string S(int n) => n.ToString(CultureInfo.InvariantCulture);
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
            ["winre_accent_text"] = "#60CDFF",
            ["winre_tabbar"] = "#202020",
            ["button_text_color"] = "#FFFFFF",
            ["fileselector_highlight_font_color"] = "#FFFFFF",
            ["winre_margin"] = S(Margin),
            ["winre_tile_w"] = S(TileW),
            ["winre_tile_h"] = S(TileH),
            ["winre_icon_x"] = S(IconX),
            ["winre_text_x"] = S(TextX),
            ["center_x"] = "540",
            ["winre_hdr_y"] = "336",
            ["winre_sub_y"] = "432",
            ["winre_back_hit_y"] = "120",
            ["winre_back_x"] = "120",
            ["winre_back_y"] = "212",
            ["winre_hdr_back_x"] = "92",
            ["winre_hdr_back_y"] = "126",
            ["winre_hdr_hit_y"] = "64",
            ["winre_hdr_hit_w"] = "176",
            ["winre_hdr_hit_h"] = "128",
            ["winre_details_y"] = "752",
            ["winre_details_h"] = "96",
            ["winre_p2_y"] = "638",
            ["winre_p3_y"] = "696",
            ["winre_btn_row_y"] = "900",
            ["winre_btn2_x"] = S(Margin + 400 + 40),
            ["winre_btn_mid_x"] = S((1080 - 400) / 2),
            ["winre_console_y"] = "560",
            ["winre_console_h"] = "1000",
            ["winre_out_btn_y"] = "1620",
            ["winre_wait_y"] = "880",
            ["winre_wait_sub_y"] = "980",
            ["winre_install_detail_y"] = "1135",
            ["winre_install_foot_y"] = "1740",
            ["winre_lock_time_y"] = "760",
            ["winre_lock_sub_y"] = "880",
            ["winre_stamp_y"] = "1812",
            ["winre_cogs_fps"] = S(cogsFps),
        };
        for (var r = 1; r <= 4; r++)
        {
            var y = Row0 + ((r - 1) * RowPitch);
            v[$"winre_r{r}_y"] = S(y);
            v[$"winre_r{r}_icon_y"] = S(y + (TileH / 2));
            v[$"winre_r{r}_title_y"] = S(y + 54);
            v[$"winre_r{r}_desc_y"] = S(y + 122);
            v[$"winre_r{r}_solo_y"] = S(y + 86);
        }

        // Cog / bar placement, solved so the retainaspect-scaled bitmap lands
        // centred on the physical panel (identical maths to build.py).
        double sx = (double)PanelW / ThemeW, sy = (double)PanelH / ThemeH;
        var min = Math.Min(sx, sy);
        var cogDrawn = CogPx * min;
        (v["winre_cogs_x"], v["winre_cogs_y"]) = CentreXy(cogDrawn, cogDrawn, PanelW / 2.0, PanelH / 2.0, sx, sy);
        v["winre_cogs_wait_y"] = CentreXy(cogDrawn, cogDrawn, PanelW / 2.0, (880 * sy) - (cogDrawn / 2) - 110, sx, sy).Y;
        v["winre_cogs_console_y"] = CentreXy(cogDrawn, cogDrawn, PanelW / 2.0, 800 * sy, sx, sy).Y;
        (v["winre_bar_x"], v["winre_bar_y"]) = CentreXy(BarW * min, BarH * min, PanelW / 2.0, 1100 * sy, sx, sy);
        return v;
    }

    private static (string X, string Y) CentreXy(double w, double h, double cx, double cy, double sx, double sy) =>
        (Math.Round((cx - (w / 2)) / sx, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture),
         Math.Round((cy - (h / 2)) / sy, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture));

    /// <summary>Reskin the stock ui.xml and add the WinRE resources, variables and include.</summary>
    public static (string Xml, int Operations) PatchUiXml(string xml, IReadOnlyDictionary<string, string> vars,
        IReadOnlyList<string> iconNames, string reskin)
    {
        if (xml.Contains("<xmlfile name=\"winre.xml\"/>", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ui.xml is already patched; start from a pristine image.");
        }
        (xml, var ops) = ApplyReskin(xml, "ui.xml", reskin);

        var images = new StringBuilder();
        foreach (var n in iconNames.Concat(ExtraImages))
        {
            images.Append("\t\t<image name=\"").Append(n).Append("\" filename=\"").Append(n).Append("\" retainaspect=\"1\"/>\n");
        }
        xml = Replace(xml, "<xmlfile name=\"portrait.xml\"/>",
            "<xmlfile name=\"portrait.xml\"/>\n\t\t<xmlfile name=\"winre.xml\"/>");
        xml = Replace(xml, "<resources>", "<resources>\n" + FontResources + images + AnimResources);
        return (InjectVars(xml, vars, "ui.xml"), ops);
    }

    public static string PatchSplashXml(string xml, IReadOnlyDictionary<string, string> vars)
    {
        if (!xml.Contains("%winre_bg%", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("splash.xml does not use %winre_bg%; refusing to build.");
        }
        return InjectVars(xml, vars, "splash.xml");
    }

    /// <summary>
    /// Reskin the stock portrait.xml (page renames, accent-as-text styles) and check the
    /// renames left no page reference dangling and that winre.xml owns the pages TWRP
    /// navigates to by name.
    /// </summary>
    public static (string Xml, int Operations) PatchPortraitXml(string xml, string winre, string reskin)
    {
        var baseline = Targets(xml).Except(Pages(xml)).ToHashSet(StringComparer.Ordinal);
        var (patched, ops) = ApplyReskin(xml, "portrait.xml", reskin);
        var defined = Pages(patched).Union(Pages(winre)).ToHashSet(StringComparer.Ordinal);
        var dangling = Targets(patched).Union(Targets(winre))
            .Where(t => !defined.Contains(t) && !baseline.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        if (dangling.Count > 0)
        {
            throw new InvalidOperationException($"The reskin left page references dangling: {string.Join(", ", dangling)}.");
        }
        foreach (var name in WinReOwnedPages)
        {
            if (Pages(patched).Contains(name) || !Pages(winre).Contains(name))
            {
                throw new InvalidOperationException($"'{name}' must be defined by winre.xml only.");
            }
        }
        return (patched, ops);
    }

    /// <summary>
    /// Apply the reskin.xml operations for one stock file, in order. Every operation's
    /// match count must equal its declared count, so a base theme other than the one
    /// reskin.xml was written against stops the build instead of half-applying.
    /// </summary>
    public static (string Text, int Operations) ApplyReskin(string text, string fileName, string reskinXml)
    {
        var doc = XDocument.Parse(reskinXml);
        var blocks = doc.Root!.Elements("file").Where(f => (string?)f.Attribute("name") == fileName).ToList();
        if (blocks.Count != 1)
        {
            throw new InvalidOperationException($"reskin.xml has {blocks.Count} blocks for {fileName}.");
        }
        var ops = 0;
        foreach (var op in blocks[0].Elements())
        {
            var want = int.Parse((string?)op.Attribute("count") ?? "1", CultureInfo.InvariantCulture);
            int got;
            string anchor;
            switch (op.Name.LocalName)
            {
                case "replace":
                {
                    anchor = Child(op, "find");
                    var with = Child(op, "with");
                    got = CountOf(text, anchor);
                    if (got == want)
                    {
                        text = text.Replace(anchor, with, StringComparison.Ordinal);
                    }
                    break;
                }
                case "element":
                {
                    anchor = Child(op, "start");
                    var end = Child(op, "end");
                    var with = Child(op, "with");
                    var sb = new StringBuilder();
                    int i = 0, j;
                    got = 0;
                    while ((j = text.IndexOf(anchor, i, StringComparison.Ordinal)) >= 0)
                    {
                        var k = text.IndexOf(end, j + anchor.Length, StringComparison.Ordinal);
                        if (k < 0)
                        {
                            throw new InvalidOperationException($"reskin {fileName}: no '{end}' after '{anchor}'.");
                        }
                        sb.Append(text, i, j - i).Append(with);
                        i = k + end.Length;
                        got++;
                    }
                    if (got == want)
                    {
                        text = sb.Append(text, i, text.Length - i).ToString();
                    }
                    break;
                }
                case "word":
                {
                    anchor = Child(op, "find");
                    var with = Child(op, "with");
                    var rx = new Regex(@"(?<![\w-])" + Regex.Escape(anchor) + @"(?![\w-])", RegexOptions.CultureInvariant);
                    got = rx.Matches(text).Count;
                    if (got == want)
                    {
                        text = rx.Replace(text, with);
                    }
                    break;
                }
                default:
                    throw new InvalidOperationException($"reskin {fileName}: unknown operation <{op.Name.LocalName}>.");
            }
            if (got != want)
            {
                throw new InvalidOperationException(
                    $"reskin {fileName}: {op.Name.LocalName} '{anchor}' matched {got} time(s), expected {want}; "
                    + "the base theme is not the one this installer was written for.");
            }
            ops++;
        }
        return (text, ops);
    }

    private static string Child(XElement op, string name) =>
        op.Element(name)?.Value ?? throw new InvalidOperationException($"reskin operation without <{name}>.");

    private static int CountOf(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += sub.Length;
        }
        return n;
    }

    private static HashSet<string> Pages(string xml) =>
        PageRegex().Matches(xml).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Targets(string xml)
    {
        var set = TargetRegex().Matches(xml).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        set.UnionWith(ClearDestRegex().Matches(xml).Select(m => m.Groups[1].Value));
        return set;
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

    [GeneratedRegex("<page name=\"([\\w-]+)\">")]
    private static partial Regex PageRegex();

    [GeneratedRegex("<action function=\"page\">([\\w-]+)</action>")]
    private static partial Regex TargetRegex();

    [GeneratedRegex("tw_clear_destination=([\\w-]+)")]
    private static partial Regex ClearDestRegex();
}
