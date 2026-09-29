# SPDX-License-Identifier: BSD-2-Clause-Patent
"""Static validation of the built TWRP theme.

A malformed theme does not fail loudly on the phone - TWRP either silently falls
back to its embedded copy or draws a blank screen, and each attempt costs a
reboot. Everything that can be decided from the XML is decided here: XML
well-formedness, resource references, page targets, variable references, the
console-bridge rules, page escapes and the copy-screen invariants.

splash.xml gets the same treatment even though it is a separate, tiny package,
because it is drawn before anything else exists to report an error with.

Usage: python validate.py work/out/ui.zip [--release]
"""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

# Variables TWRP populates at runtime, or the installer/watcher sets over ORS;
# they never appear in <variables>.
RUNTIME_VAR_PREFIXES = ("tw_", "winre_busy_title", "winre_done_title",
                        "winre_done_sub", "winre_confirm_")
RUNTIME_VARS = {
    "operation_start_time", "minimum_battery", "battery_level", "current_time",
    "cpu_temp", "simulate_actions", "simulate_fail",
    # Set over ORS by /sbin/winre-statuswatch.sh: winre_push is the raise/clear
    # flag, winre_push_sub is the phase caption, winre_push_back is where the
    # copy screen returns to. 'main' initialises all three.
    "winre_push", "winre_push_sub", "winre_push_back",
    # Only present in a --test-nav build, which --release rejects.
    "winre_nav", "winre_nav_to",
}


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


def check_push_screen(winre: ET.Element) -> list[str]:
    """Invariants that keep the copy screen from trapping the UI.

    1. 'main' resets winre_push and initialises winre_push_back.
    2. Every page carrying the trigger tests winre_push != "0" and never uses an
       empty var2 (TWRP evaluates an empty compare as false, silently disabling
       the trigger). winre_home must be one of them.
    3. The copy page exits on winre_push = "0".
    4. The copy page's back/home land on 'main', which resets the flag.
    """
    out: list[str] = []
    pages = {p.get("name"): p for p in winre.iter("page")}

    push = pages.get("winre_push")
    if push is None:
        return ["winre.xml: no 'winre_push' page, but the ramdisk ships winre-statuswatch.sh"]

    main = pages.get("main")
    if main is None or not any(
            (a.get("function") == "set" and (a.text or "").replace(" ", "").startswith("winre_push="))
            for a in main.iter("action")):
        out.append("winre.xml: page 'main' does not reset winre_push")
    if main is None or not any(
            (a.get("function") == "set" and (a.text or "").replace(" ", "").startswith("winre_push_back="))
            for a in main.iter("action")):
        out.append("winre.xml: page 'main' does not initialise winre_push_back")

    trigger_pages = []
    for name, pg in pages.items():
        for blk in pg.iter("action"):
            if not any((a.text or "").strip() == "winre_push"
                       for a in blk.iter("action") if a.get("function") == "page"):
                continue
            conds = [c for c in blk.iter("condition") if c.get("var1") == "winre_push"]
            if not conds:
                continue
            trigger_pages.append(name)
            vals = {(c.get("op") or "=", c.get("var2") or "") for c in conds}
            if ("!=", "0") not in vals:
                out.append(f"winre.xml: the winre_push trigger on '{name}' is not guarded by "
                           "winre_push!=0")
            if any(v == "" for _, v in vals):
                out.append(f"winre.xml: the winre_push trigger on '{name}' compares winre_push "
                           "against the empty string; TWRP evaluates that as false")
    if "winre_home" not in trigger_pages:
        out.append("winre.xml: page 'winre_home' has no winre_push trigger")

    if not any(c.get("var1") == "winre_push" and (c.get("op") or "=") == "="
               and (c.get("var2") or "") == "0" for c in push.iter("condition")):
        out.append("winre.xml: page 'winre_push' has no exit conditioned on winre_push=0")

    for blk in push.iter("action"):
        keys = {t.get("key") for t in blk.findall("touch")}
        if not keys & {"back", "home"}:
            continue
        for a in blk.iter("action"):
            if a.get("function") == "page" and (a.text or "").strip() != "main":
                out.append(f"winre.xml: page 'winre_push' {'/'.join(sorted(k for k in keys if k))} "
                           f"goes to '{(a.text or '').strip()}' instead of 'main'")
    return out


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


def main(path: Path, release: bool = False) -> int:
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

    errors += check_splash(zf, members)
    errors += check_command_buttons(winre)
    errors += check_page_escapes(winre)
    errors += check_push_screen(winre)

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
    sys.exit(main(Path(args[0]), release="--release" in sys.argv))
