# SPDX-License-Identifier: BSD-2-Clause-Patent
"""Static validation of the built TWRP theme.

A malformed theme does not fail loudly on the phone - TWRP either silently falls
back to its embedded copy or draws a blank screen, and each attempt costs a
reboot. Everything that can be decided from the XML is decided here: XML
well-formedness, resource references, page targets, variable references, the
console-bridge rules, page escapes, the install-screen and lock-screen
invariants, the watcher contract, and - with --release - that no page is left
in the stock TWRP look (the coverage report).

splash.xml gets the same treatment even though it is a separate, tiny package,
because it is drawn before anything else exists to report an error with.

Usage: python validate.py work/out/ui.zip [--release] [--pages]
       --pages prints the per-page coverage table.
"""

from __future__ import annotations

import hashlib
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
WATCHER = HERE / "overlay" / "sbin" / "winre-statuswatch.sh"

# Variables TWRP populates at runtime; they never appear in <variables>.
RUNTIME_VAR_PREFIXES = ("tw_", "winre_busy_title", "winre_done_title",
                        "winre_done_sub", "winre_confirm_")
RUNTIME_VARS = {
    "operation_start_time", "minimum_battery", "battery_level", "current_time",
    "cpu_temp", "simulate_actions", "simulate_fail",
    # Only present in a --test-nav build, which --release rejects.
    "winre_nav", "winre_nav_to",
}

# Android properties the install screen reads (TWRP resolves property.<name>
# with property_get on every draw); the watcher must publish each of them.
INSTALL_PROPS = {"s9woa.show", "s9woa.mode", "s9woa.label", "s9woa.pct", "s9woa.detail"}


def page_animations(root: ET.Element) -> list[ET.Element]:
    declared = {id(a) for res in root.iter("resources") for a in res.findall("animation")}
    return [a for a in root.iter("animation") if id(a) not in declared]


def animation_frames(members: set[str], base: str) -> tuple[int, list[str]]:
    n = 0
    while f"images/{base}{n + 1:03d}.png" in members:
        n += 1
    pattern = re.compile(rf"images/{re.escape(base)}(\d{{3}})\.png$")
    stray = sorted(m for m in members
                   if (mo := pattern.match(m)) and int(mo.group(1)) > n)
    return n, stray


def check_animation_aspect(root: ET.Element, where: str) -> list[str]:
    """Every winre_* <animation> resource must carry retainaspect."""
    out: list[str] = []
    for res in root.iter("resources"):
        for a in res.findall("animation"):
            name = a.get("name") or ""
            if name.startswith("winre_") and a.get("retainaspect") is None:
                out.append(
                    f"{where}: animation '{name}' has no retainaspect; TWRP would "
                    f"scale it {(2960/1920)/(1440/1080) - 1:.1%} too tall (oval gears)")
    return out


def check_animation_frames(anims: dict[str, str], members: set[str], where: str) -> list[str]:
    out: list[str] = []
    for name, base in anims.items():
        n, stray = animation_frames(members, base)
        if n == 0:
            out.append(f"{where}: animation '{name}' -> images/{base}001.png not in theme")
        elif stray:
            out.append(f"{where}: animation '{name}' stops at frame {n:03d} but "
                       f"{len(stray)} later frame(s) exist ({stray[0]}...); numbering gap")
    return out


def check_splash(zf: zipfile.ZipFile, members: set[str]) -> list[str]:
    errors: list[str] = []
    try:
        blob = zf.read("splash.xml").decode("utf-8", "replace")
        root = ET.fromstring(blob)
    except KeyError:
        return ["splash.xml is missing from the theme"]
    except ET.ParseError as exc:
        return [f"splash.xml is not well-formed XML: {exc}"]

    if not any(p.get("name") == "splash"
               for pages in root.iter("pages") for p in pages.findall("page")):
        errors.append("splash.xml has no page named 'splash'")

    declared = {v.get("name") for block in root.iter("variables")
                for v in block.findall("variable")}
    anims = {a.get("name"): a.get("filename") for res in root.iter("resources")
             for a in res.findall("animation")}

    for var in sorted(set(re.findall(r"%([a-zA-Z][\w]*)%", blob))):
        if var not in declared:
            errors.append(f"splash.xml: '%{var}%' is not declared in its own <variables>")
    for anim in page_animations(root):
        res = anim.find("resource")
        if res is None or res.get("name") not in anims:
            errors.append(f"splash.xml: animation resource "
                          f"'{res.get('name') if res is not None else None}' is not declared")
    errors += check_animation_frames(anims, members, "splash.xml")
    errors += check_animation_aspect(root, "splash.xml")
    return errors


GUI_WRAPPER = "/sbin/winre-gui.sh"


def check_command_buttons(winre: ET.Element) -> list[str]:
    """Enforce the console-bridge rules (see winre-gui.sh / winre.xml comments).

    The single most important rule is the ban on openrecoveryscript: that action
    reboots to Android whenever the script it runs succeeds, so it can never be a
    console bridge. Command output must go through terminalcommand, via
    winre-gui.sh, with an absolute tw_terminal_location.
    """
    out: list[str] = []
    for page in winre.iter("page"):
        for act in page.iter("action"):
            fn = (act.get("function") or "")
            if fn == "openrecoveryscript" or "openrecoveryscript" in (act.text or ""):
                out.append(f"winre.xml: page '{page.get('name')}' references openrecoveryscript; "
                           "that reboots to Android on success - use terminalcommand")

    for page in winre.iter("page"):
        name = page.get("name")
        for holder in list(page.findall("button")) + list(page.findall("action")):
            sets, funcs = {}, []
            for act in holder.iter("action"):
                fn = act.get("function")
                if not fn:
                    continue
                funcs.append(fn)
                if fn == "set" and "=" in (act.text or ""):
                    k, _, v = (act.text or "").partition("=")
                    sets[k.strip()] = v.strip()

            if "tw_action" not in sets:
                continue
            action = sets["tw_action"]
            param = sets.get("tw_action_param", "")

            if action == "cmd":
                out.append(f"winre.xml: page '{name}' sets tw_action=cmd; a bare cmd writes only "
                           "to the log - use terminalcommand")
                continue
            elif action != "terminalcommand":
                out.append(f"winre.xml: page '{name}' sets tw_action='{action}', expected terminalcommand")
                continue

            loc = sets.get("tw_terminal_location", "")
            if not loc.startswith("/"):
                out.append(f"winre.xml: page '{name}' runs terminalcommand without an absolute "
                           "tw_terminal_location")
            if not param.startswith("/"):
                out.append(f"winre.xml: page '{name}' runs terminalcommand '{param}' (not absolute)")
            elif not param.startswith(f"{GUI_WRAPPER} /"):
                out.append(f"winre.xml: page '{name}' runs terminalcommand '{param}' directly "
                           f"instead of via {GUI_WRAPPER}, so its output renders under the boot log")
            if sets.get("tw_has_action2") != "0":
                out.append(f"winre.xml: page '{name}' runs terminalcommand but tw_has_action2 is "
                           f"'{sets.get('tw_has_action2')}', not 0")
            if "page" not in funcs:
                out.append(f"winre.xml: page '{name}' prepares a command but never navigates on")

    for page in winre.iter("page"):
        if page.get("name") != "winre_run":
            continue
        if not any("%tw_action%" in [a.get("function") for a in blk.iter("action")]
                   for blk in page.findall("action")):
            out.append("winre.xml: winre_run never invokes %tw_action%")
    return out


def check_page_escapes(winre: ET.Element) -> list[str]:
    """Every winre_* page must be leavable with a hardware back and home key."""
    out: list[str] = []
    for page in winre.iter("page"):
        name = page.get("name") or ""
        if not name.startswith("winre"):
            continue
        keys = {t.get("key") for blk in page.iter("action")
                for t in blk.findall("touch") if t.get("key")}
        for required in ("back", "home"):
            if required not in keys:
                out.append(f"winre.xml: page '{name}' has no hardware '{required}' handler")
    return out


def _templates(*roots: ET.Element) -> dict[str, ET.Element]:
    """Template definitions by name; the first definition wins, as in TWRP."""
    out: dict[str, ET.Element] = {}
    for root in roots:
        for block in root.iter("templates"):
            for t in block.findall("template"):
                out.setdefault(t.get("name"), t)
    return out


def _expanded(node: ET.Element, templates: dict[str, ET.Element]) -> list[ET.Element]:
    """A page's top-level objects with <template name=.../> references inlined."""
    out: list[ET.Element] = []
    for child in node:
        if child.tag == "template":
            t = templates.get(child.get("name"))
            if t is not None:
                out.extend(_expanded(t, templates))
        else:
            out.append(child)
    return out


def _conditions(obj: ET.Element) -> list[ET.Element]:
    found = list(obj.findall("condition"))
    for block in obj.findall("conditions"):
        found += block.findall("condition")
    return found


def _visible(obj: ET.Element, env: dict[str, str]) -> bool:
    """Would obj's conditions pass, given values for the variables in env
    (conditions on any other variable are assumed to pass)?"""
    for c in _conditions(obj):
        var, op, want = c.get("var1"), c.get("op") or "=", c.get("var2") or ""
        if var not in env:
            continue
        if (op == "=" and env[var] != want) or (op == "!=" and env[var] == want):
            return False
    return True


def _is_gui_text(obj: ET.Element) -> bool:
    # Every <text> is a GUIText; a <button> carries one only when it has a font.
    return obj.tag == "text" or (obj.tag == "button" and obj.find("font") is not None)


def _sets(node: ET.Element, assignment: str) -> bool:
    return any(a.get("function") == "set" and (a.text or "").replace(" ", "") == assignment
               for a in node.iter("action"))


def check_install_screen(winre: ET.Element, templates: dict[str, ET.Element]) -> list[str]:
    """Invariants of the live "Installing Windows" screen (and its ORS mirror).

    1. winre_install and singleaction_page both draw the winre_install_body
       template, so a stray openrecoveryscript bounce draws identical pixels.
    2. Only properties the watcher publishes are read (INSTALL_PROPS).
    3. The page shows an ODD number of texts while it is up: TWRP refreshes
       a property-driven <text> only through GUIText's shared counter, which
       lands on every fourth Update call, so with an even count some texts
       would never refresh.
    4. The determinate bar is bound to property.s9woa.pct, the sweep covers the
       busy mode, and the page resets ui_progress_frames (GUIProgressBar's
       uninitialised slide counter would otherwise write into the property).
    5. The screen can always be left by touch: a button to 'main'.
    6. singleaction_page and action_page keep TWRP's action contract.
    """
    out: list[str] = []
    pages = {p.get("name"): p for p in winre.iter("page")}
    if "winre_install_body" not in templates:
        return ["winre.xml: no 'winre_install_body' template"]

    for name in ("winre_install", "singleaction_page"):
        pg = pages.get(name)
        if pg is None:
            out.append(f"winre.xml: no '{name}' page")
            continue
        if not any(t.get("name") == "winre_install_body" for t in pg.findall("template")):
            out.append(f"winre.xml: '{name}' does not draw winre_install_body")
        if not _sets(pg, "ui_progress_frames=0"):
            out.append(f"winre.xml: '{name}' does not reset ui_progress_frames on entry")
        shown = [o for o in _expanded(pg, templates)
                 if _is_gui_text(o) and _visible(o, {"property.s9woa.show": "1"})]
        if len(shown) % 2 == 0:
            out.append(f"winre.xml: '{name}' shows {len(shown)} texts during an install; the "
                       "count must be odd or some property texts never refresh")

    blob = ET.tostring(winre, encoding="unicode")
    used = set(re.findall(r"%property\.([\w.]+)%", blob))
    used |= {c.get("var1")[9:] for c in winre.iter("condition")
             if (c.get("var1") or "").startswith("property.")}
    used |= {d.get("name")[9:] for d in winre.iter("data")
             if (d.get("name") or "").startswith("property.")}
    for prop in sorted(used - INSTALL_PROPS):
        out.append(f"winre.xml: reads property '{prop}', which the watcher never sets")

    body = templates["winre_install_body"]
    bars = [b for b in body.iter("progressbar")
            if b.find("data") is not None and b.find("data").get("name") == "property.s9woa.pct"]
    if not bars or not all(_visible(b, {"property.s9woa.mode": "percent"}) and
                           not _visible(b, {"property.s9woa.mode": "busy"}) for b in bars):
        out.append("winre.xml: no determinate bar bound to property.s9woa.pct for mode=percent")
    if not any(_visible(a, {"property.s9woa.mode": "busy"}) and
               not _visible(a, {"property.s9woa.mode": "percent"}) for a in body.iter("animation")):
        out.append("winre.xml: no sweep animation for mode=busy")

    install = pages.get("winre_install")
    if install is not None and not any(
            (a.text or "").strip() == "main" and a.get("function") == "page"
            for b in install.findall("button") for a in b.iter("action")):
        out.append("winre.xml: 'winre_install' has no touch escape to 'main'")

    for name, done in (("singleaction_page", "tw_page_done=1"), ("action_page", None)):
        pg = pages.get(name)
        if pg is None:
            out.append(f"winre.xml: no '{name}' page (TWRP switches to it by name)")
            continue
        invokes = [blk for blk in pg.findall("action")
                   if any(a.get("function") == "%tw_action%" for a in blk.iter("action"))]
        has2 = {c.get("var2") for blk in invokes for c in _conditions(blk)
                if c.get("var1") == "tw_has_action2"}
        if has2 != {"0", "1"}:
            out.append(f"winre.xml: '{name}' must run %tw_action% for tw_has_action2 0 and 1")
        if done and not _sets(pg, done):
            out.append(f"winre.xml: '{name}' never sets {done} when the command finishes")
    action_page = pages.get("action_page")
    if action_page is not None and not any(
            (a.text or "").strip() == "action_complete" for a in action_page.iter("action")
            if a.get("function") == "page"):
        out.append("winre.xml: 'action_page' never moves on to action_complete")
    return out


def check_lock(winre: ET.Element) -> list[str]:
    """The lock overlay must dismiss itself from a touch, never on load.

    TWRP raises it with ChangeOverlay("lock"), and PageSet::SetOverlay runs the
    page's load actions BEFORE pushing it, so a page-load "overlay" pops
    whatever was below and then the lock is pushed anyway - an invisible
    overlay that eats every touch.
    """
    out: list[str] = []
    lock = next((p for p in winre.iter("page") if p.get("name") == "lock"), None)
    if lock is None:
        return ["winre.xml: no 'lock' page; TWRP would use the stock swipe-to-unlock"]
    for blk in lock.findall("action"):
        if blk.find("touch") is None and any(a.get("function") == "overlay" for a in blk.iter("action")):
            out.append("winre.xml: 'lock' dismisses itself from a page-load action; TWRP runs it "
                       "before pushing the overlay, which leaves an invisible overlay eating input")
    if not any(a.get("function") == "overlay" for b in lock.findall("button") for a in b.iter("action")):
        out.append("winre.xml: 'lock' has no tap-to-dismiss button")
    if not any(t.get("key") == "power" for blk in lock.findall("action") for t in blk.findall("touch")):
        out.append("winre.xml: 'lock' has no power-key handler")
    return out


def check_watcher(path: Path = WATCHER) -> list[str]:
    """The watcher must never poke GUI variables through openrecoveryscript."""
    if not path.exists():
        return [f"{path.name} not found next to validate.py"]
    code = "\n".join(line for line in path.read_text(encoding="utf-8").splitlines()
                     if not line.lstrip().startswith("#"))
    out: list[str] = []
    if re.search(r"\btwrp\s+\"?(set|cmd|print|mount|install|reboot)\b", code):
        out.append(f"{path.name}: calls `twrp <command>` other than changepage; every such ORS "
                   "command flashes singleaction_page on screen")
    if "openrecoveryscript" in code:
        out.append(f"{path.name}: references openrecoveryscript")
    if "changepage=" not in code:
        out.append(f"{path.name}: never raises the screen with `twrp changepage=`")
    for line in code.splitlines():
        if re.search(r"(^|[;&|\s])twrp\s", line) and "timeout -t" not in line:
            out.append(f"{path.name}: `twrp` call without `timeout -t`: {line.strip()}")
    for prop in sorted(INSTALL_PROPS):
        if not re.search(rf"setprop\s+{re.escape(prop)}\s", code):
            out.append(f"{path.name}: never sets {prop}")
    return out


# --------------------------------------------------------------------------
# Coverage: is any page still in the stock TWRP look?
# --------------------------------------------------------------------------

# The stock theme's own colours (TWRP 3.7 ui.xml/splash). Any of them left in
# a template, style, page or variable means a surface was missed.
TWRP_COLOURS = {"0090CA", "0090C9", "03A9F4", "1A1A1A", "EEEEEE", "111111", "5B5B5B",
                "222222", "555555", "76FF03", "F8F8A0", "FF0101", "FFFF00"}
# Stock bitmaps that are the TeamWin look itself.
BANNED_RESOURCES = {"logo", "splashlogo", "splashteamwin", "unlock_icon"}
VISUAL_TAGS = {"background", "fill", "text", "image", "button", "animation", "progressbar",
               "console", "terminal", "listbox", "fileselector", "partitionlist", "slider",
               "checkbox", "input", "keyboard", "slidervalue", "scrolllist", "patternpassword",
               "template"}

# Stock pages with visuals that do not use the page template, with the reason
# they are still WinRE: they draw only reskinned variables and replaced images.
ALLOWED_STOCK = {
    "slideout": "the console overlay behind 'Show details': black fill, white console",
    "select_storage": "storage picker overlay: dimmed backdrop, black dialog, reskinned list",
    "select_language": "language picker overlay: as select_storage",
}
# Stock pages the reskin renames so winre.xml can take the name; nothing
# navigates to them any more.
SHADOWED = {
    "twrp_main": "main", "twrp_lock": "lock", "twrp_singleaction_page": "singleaction_page",
    "twrp_action_page": "action_page", "twrp_action_complete": "action_complete",
}


def _strip_comments(text: str) -> str:
    return re.sub(r"<!--.*?-->", "", text, flags=re.S)


def _twrp_colours(text: str) -> list[str]:
    return sorted({m.group(0) for m in re.finditer(r"#([0-9A-Fa-f]{6})([0-9A-Fa-f]{2})?\b",
                                                   _strip_comments(text))
                   if m.group(1).upper() in TWRP_COLOURS})


def _resource_refs(node: ET.Element) -> set[str]:
    refs = set()
    for el in node.iter():
        for attr in ("resource", "base", "used", "touch", "empty", "full", "handle",
                     "checked", "unchecked", "selected", "unselected", "folder", "file"):
            if el.tag in ("font",) or el.get(attr) is None:
                continue
            refs.add(el.get(attr))
    return refs


def page_coverage(ui: ET.Element, portrait: ET.Element, winre: ET.Element,
                  blobs: dict[str, str], zf: zipfile.ZipFile, members: set[str],
                  release: bool) -> tuple[list[tuple[str, str, str]], list[str], list[str]]:
    """Classify every page and flag anything still in the stock TWRP look.

    Returns (rows, errors, warnings); rows are (page, class, note).
    """
    errors: list[str] = []
    warnings: list[str] = []
    templates = _templates(ui, portrait, winre)
    winre_pages = {p.get("name") for p in winre.iter("page")}

    # Global: variables, templates, fonts.
    for block in ui.iter("variables"):
        for v in block.findall("variable"):
            if _twrp_colours(v.get("value") or ""):
                errors.append(f"coverage: variable '{v.get('name')}' is still the TWRP colour "
                              f"{v.get('value')}")
    for name in ("page", "console", "progress_bar", "tabs_backup", "tabs_settings",
                 "keyboardtemplate", "keyboardterminaltemplate", "keyboardnum", "sort_options"):
        t = templates.get(name)
        if t is None:
            errors.append(f"coverage: template '{name}' is missing")
            continue
        text = ET.tostring(t, encoding="unicode")
        for c in _twrp_colours(text):
            errors.append(f"coverage: template '{name}' still uses the TWRP colour {c}")
        for r in sorted(_resource_refs(t) & BANNED_RESOURCES):
            errors.append(f"coverage: template '{name}' still draws the stock '{r}'")
        if re.search(r'<fill color="%accent_color%"', text):
            errors.append(f"coverage: template '{name}' still fills a blue accent band")
    if templates.get("console") is not None and any(templates["console"].iter("console")):
        errors.append("coverage: the console template still shows a scrolling log")
    for block in portrait.iter("styles"):
        for c in _twrp_colours(ET.tostring(block, encoding="unicode")):
            errors.append(f"coverage: portrait.xml <styles> still use the TWRP colour {c}")
    for res in ui.iter("resources"):
        for f in res.findall("font"):
            if "Roboto" in (f.get("filename") or ""):
                (errors if release else warnings).append(
                    f"coverage: font '{f.get('name')}' is still {f.get('filename')}")
    for name in ("splash.xml",):
        for c in _twrp_colours(zf.read(name).decode("utf-8", "replace")):
            errors.append(f"coverage: {name} still uses the TWRP colour {c}")

    # Images: every bitmap in the theme is ours.
    stock_art = {p.stem: p.read_bytes() for p in (HERE / "assets" / "stock").glob("*.png")}
    own_art = {p.stem: p.read_bytes() for p in (HERE / "assets" / "images").glob("*.png")}
    for member in sorted(m for m in members if m.startswith("images/") and m.endswith(".png")):
        stem = member[7:-4]
        data = zf.read(member)
        if re.fullmatch(r"winrecog\d{3}", stem):
            continue  # procedural frames, or the builder's own UpdateOS GIF render
        expected = stock_art.get(stem, own_art.get(stem))
        if expected is None:
            errors.append(f"coverage: images/{stem}.png is not original art (stock bitmap left?)")
        elif data != expected:
            errors.append(f"coverage: images/{stem}.png differs from assets/"
                          f"{'stock' if stem in stock_art else 'images'}/{stem}.png")

    # Pages.
    rows: list[tuple[str, str, str]] = []
    for source, root in (("portrait.xml", portrait), ("winre.xml", winre)):
        for page in root.iter("page"):
            name = page.get("name")
            objs = _expanded(page, templates)
            visual = [o for o in page if o.tag in VISUAL_TAGS]
            if source == "winre.xml":
                cls, note = "winre", "WinRE page"
            elif name in SHADOWED:
                cls, note = "shadowed", f"renamed; WinRE '{SHADOWED[name]}' is used instead"
            elif not visual:
                cls, note = "logic", "no visible objects"
            elif any(t.get("name") == "page" for t in page.findall("template")):
                cls, note = "template", "WinRE page template (back arrow, Light title, flat navbar)"
            elif name in ALLOWED_STOCK:
                cls, note = "allowed", ALLOWED_STOCK[name]
            else:
                cls, note = "LEFTOVER", "stock page with its own look"
                errors.append(f"coverage: page '{name}' ({source}) draws without the WinRE page "
                              "template and is not allow-listed")
            rows.append((name, cls, note))
            if cls in ("shadowed", "logic"):
                continue
            pageblob = ET.tostring(page, encoding="unicode")
            for c in _twrp_colours(pageblob):
                errors.append(f"coverage: page '{name}' still uses the TWRP colour {c}")
            refs = set()
            for o in objs:
                refs |= _resource_refs(o)
            for r in sorted(refs & BANNED_RESOURCES):
                errors.append(f"coverage: page '{name}' still draws the stock '{r}'")
    for name in winre_pages & {r[0] for r in rows if r[1] != "winre"}:
        errors.append(f"coverage: page '{name}' is defined twice")
    return rows, errors, warnings


def collect(zf: zipfile.ZipFile):
    roots = []
    for name in ("ui.xml", "portrait.xml", "winre.xml"):
        try:
            roots.append(ET.fromstring(zf.read(name)))
        except KeyError:
            sys.exit(f"FAIL  {name} is missing from the theme")
        except ET.ParseError as exc:
            sys.exit(f"FAIL  {name} is not well-formed XML: {exc}")
    return roots, set(zf.namelist())


def main(path: Path, release: bool = False, show_pages: bool = False) -> int:
    zf = zipfile.ZipFile(path)
    (ui, portrait, winre), members = collect(zf)
    errors: list[str] = []
    warnings: list[str] = []

    fonts, images, anims = {}, {}, {}
    for res in ui.iter("resources"):
        for f in res.findall("font"):
            fonts[f.get("name")] = f.get("filename")
        for i in res.findall("image"):
            images[i.get("name")] = i.get("filename")
        for a in res.findall("animation"):
            anims[a.get("name")] = a.get("filename")

    for name, fn in fonts.items():
        if f"fonts/{fn}" not in members:
            errors.append(f"font resource '{name}' -> fonts/{fn} not in theme")
    for name, fn in images.items():
        if f"images/{fn}.png" not in members and f"images/{fn}" not in members:
            errors.append(f"image resource '{name}' -> images/{fn}.png not in theme")
    errors += check_animation_frames(anims, members, "ui.xml")
    errors += check_animation_aspect(ui, "ui.xml")

    variables = set()
    for block in ui.iter("variables"):
        for v in block.findall("variable"):
            variables.add(v.get("name"))

    defined: set[str] = set()
    for root in (portrait, winre):
        for pages in root.iter("pages"):
            for p in pages.findall("page"):
                defined.add(p.get("name"))

    if "main" not in defined:
        errors.append("no page named 'main' - TWRP always boots into 'main'")

    dup = [p for p in defined if sum(
        1 for r in (portrait, winre) for ps in r.iter("pages")
        for q in ps.findall("page") if q.get("name") == p) > 1]
    for p in sorted(set(dup)):
        errors.append(f"page '{p}' is defined more than once")

    blobs = {n: zf.read(n).decode("utf-8", "replace")
             for n in ("ui.xml", "portrait.xml", "winre.xml")}

    for fname, blob in blobs.items():
        for target in re.findall(r'<action function="page">([^<%]+)</action>', blob):
            if target not in defined:
                (errors if fname == "winre.xml" else warnings).append(
                    f"{fname}: page target '{target}' is not defined")
        for target in re.findall(r"tw_clear_destination=([\w]+)", blob):
            if target not in defined:
                (errors if fname == "winre.xml" else warnings).append(
                    f"{fname}: clear_vars destination '{target}' is not defined")

    for res in re.findall(r'<font resource="([^"]+)"', blobs["winre.xml"]):
        if res not in fonts:
            errors.append(f"winre.xml: font resource '{res}' is not declared")
    for res in re.findall(r'<image resource="([^"]+)"', blobs["winre.xml"]):
        if res not in images:
            errors.append(f"winre.xml: image resource '{res}' is not declared")
    for page in page_animations(winre):
        res = page.find("resource")
        rname = res.get("name") if res is not None else None
        if rname not in anims:
            errors.append(f"winre.xml: animation resource '{rname}' is not declared")

    for var in sorted(set(re.findall(r"%([a-zA-Z][\w]*)%", blobs["winre.xml"]))):
        if var in variables or var in RUNTIME_VARS or var.startswith(RUNTIME_VAR_PREFIXES):
            continue
        errors.append(f"winre.xml: '%{var}%' is neither a theme variable nor a runtime var")

    # An empty var2 is evaluated as false by TWRP and silently disables the
    # condition's object or action.
    for fname, root in (("ui.xml", ui), ("winre.xml", winre)):
        for c in root.iter("condition"):
            if c.get("var2") is not None and c.get("var2") == "":
                errors.append(f"{fname}: condition on '{c.get('var1')}' compares against the empty "
                              "string; TWRP evaluates that as false")

    # Animation speeds of the gears come from the builder (GIF or procedural).
    for fname, root in (("winre.xml", winre), ("ui.xml", ui)):
        for anim in page_animations(root):
            res = anim.find("resource")
            speed = anim.find("speed")
            if res is not None and res.get("name") == "winre_cogs" and (
                    speed is None or speed.get("fps") != "%winre_cogs_fps%"):
                errors.append(f"{fname}: a winre_cogs animation does not use fps=\"%winre_cogs_fps%\"")

    for pages in winre.iter("pages"):
        for page in pages.findall("page"):
            name = page.get("name")
            for btn in page.findall("button"):
                if btn.find("action") is None and btn.find("actions") is None:
                    errors.append(f"winre.xml: button on page '{name}' has no action")
                if btn.find("image") is None and btn.find("fill") is None:
                    errors.append(f"winre.xml: button on page '{name}' has neither image nor fill")
                pl = btn.find("placement")
                if pl is None or pl.get("w") is None or pl.get("h") is None:
                    errors.append(f"winre.xml: button on page '{name}' needs placement w/h")

    templates = _templates(ui, portrait, winre)
    errors += check_splash(zf, members)
    errors += check_command_buttons(winre)
    errors += check_page_escapes(winre)
    errors += check_install_screen(winre, templates)
    errors += check_lock(winre)
    errors += check_watcher()
    rows, cov_errors, cov_warnings = page_coverage(ui, portrait, winre, blobs, zf, members, release)
    errors += cov_errors
    warnings += cov_warnings

    nav_hits = sum(blob.count("winre_nav") for blob in blobs.values())
    if release and nav_hits:
        errors.append(f"--release, but 'winre_nav' appears {nav_hits} time(s): this is a --test-nav build")

    n_pages = len(list(winre.iter("page")))
    print(f"theme      {path.name}  ({len(members)} members)")
    print(f"resources  {len(fonts)} fonts, {len(images)} images, {len(anims)} animations, "
          f"{len(variables)} variables")
    for name, base in sorted(anims.items()):
        n, _ = animation_frames(members, base)
        print(f"animation  {name} -> {base}NNN.png, {n} frame(s)")
    print(f"pages      {len(defined)} total, {n_pages} WinRE"
          + ("  [TEST-NAV BUILD]" if nav_hits else ""))
    classes: dict[str, int] = {}
    for _, cls, _ in rows:
        classes[cls] = classes.get(cls, 0) + 1
    print("coverage   " + ", ".join(f"{n} {c}" for c, n in sorted(classes.items()))
          + f"; {sum(1 for r in rows if r[1] == 'LEFTOVER')} leftover(s)")
    if show_pages:
        for name, cls, note in sorted(rows, key=lambda r: (r[1], r[0])):
            print(f"  {cls:<9} {name:<32} {note}")
    for name, cls, note in rows:
        if cls == "allowed":
            print(f"allowed    {name}: {note}")

    for w in warnings:
        print(f"warn  {w}")
    for e in errors:
        print(f"FAIL  {e}")

    if errors:
        print(f"\n{len(errors)} error(s)")
        return 1
    print(f"\nOK ({len(warnings)} pre-existing warning(s) from the stock theme)")
    return 0


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("-")]
    sys.exit(main(Path(args[0]), release="--release" in sys.argv, show_pages="--pages" in sys.argv))
