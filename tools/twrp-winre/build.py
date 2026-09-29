# SPDX-License-Identifier: BSD-2-Clause-Patent
"""Build the WinRE-look TWRP recovery for star2lte.

Re-skins the existing, known-good recovery instead of rebuilding TWRP: the
kernel (bar a four-byte power-off route patch), the DTBH blob, the boot header
and every ramdisk entry we do not touch are carried across untouched. That keeps
the blast radius small - if the phone fails to boot the recovery, it is one of
the handful of files listed in the build report.

This is the reference/dev-time tool. End users never run it: the C# installer
(S9Woa.Installer.Core.Twrp.WinReTwrpBuilder) does the identical build on their
own PC from the same committed theme, scripts and assets, and copies the fonts
from their own Windows. Keeping both in step is what the tests on both sides are
for.

Outputs (in --outdir):
  star2lte-winre-recovery.img   flashable RECOVERY image
  ui.zip                        the same theme as a TWRP custom theme, for
                                iterating without flashing anything
  build-report.txt              what changed, with sizes

Usage:
  python build.py --source /path/to/twrp-3.7.0_9-0-star2lte.img --outdir work/out
"""

from __future__ import annotations

import argparse
import hashlib
import io
import re
import sys
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import poweroff  # noqa: E402
from bootimg import BootImage, Cpio, lzma_compress, lzma_decompress  # noqa: E402

HERE = Path(__file__).resolve().parent
TWRES = "twres"
THEME = HERE / "theme"
IMAGES = HERE / "assets" / "images"
SBIN = HERE / "overlay" / "sbin"

BUILDER_VERSION = "winre-1"
# The two GPL kernel modules (our own) baked into the ramdisk so the recovery can
# clear the retained recovery record and read it back with no host help.
MODULE_DIR = "sbin/s9woa"

WINDOWS_FONTS = Path("C:/Windows/Fonts")
# Segoe is what makes the screen read as Windows rather than as a blue TWRP.
# Copied from the builder's own licensed Windows; NOT redistributed (see README).
# The recovery partition has ~25 MiB of slack, so no subsetting is needed - the
# full faces are copied, which keeps the Python and C# builds byte-identical in
# structure.
FONT_FILES = {
    "winre-light.ttf": "segoeuil.ttf",
    "winre-semilight.ttf": "segoeuisl.ttf",
    "winre-regular.ttf": "segoeui.ttf",
}
FONT_FALLBACK = "RobotoCondensed-Regular.ttf"

# --------------------------------------------------------------------------
# Reskin of the STOCK theme: recolour its global variables and swap its fonts so
# every untouched stock page (install, backup, wipe, mount, settings, terminal,
# file manager, ...) renders in the WinRE palette without rewriting page logic.
# These are in-place edits of the base image's own ui.xml, never committed.
# --------------------------------------------------------------------------
STOCK_COLOUR_OVERRIDES = {
    'name="background_color" value="#1A1A1A"': 'name="background_color" value="#000000"',
    'name="accent_color" value="#0090CA"': 'name="accent_color" value="#0067C0"',
    'name="accent_color_semitransparent" value="#0090CA30"': 'name="accent_color_semitransparent" value="#0067C030"',
    'name="text_color" value="#EEEEEE"': 'name="text_color" value="#FFFFFF"',
    'name="text_button_color" value="#EEEEEE"': 'name="text_button_color" value="#FFFFFF"',
    'name="highlight_color" value="#1A1A1A80"': 'name="highlight_color" value="#0067C040"',
    'name="highlight" value="#0090CA"': 'name="highlight" value="#0067C0"',
}
# Point the stock fonts at the Segoe faces (kept: fixed = DroidSansMono for the
# console/terminal, and the keyboard labels).
STOCK_FONT_OVERRIDES = {
    'name="font_l" filename="RobotoCondensed-Regular.ttf" size="54"': 'name="font_l" filename="winre-semilight.ttf" size="50"',
    'name="font_m" filename="RobotoCondensed-Regular.ttf" size="42"': 'name="font_m" filename="winre-regular.ttf" size="40"',
    'name="font_s" filename="RobotoCondensed-Regular.ttf" size="36"': 'name="font_s" filename="winre-regular.ttf" size="34"',
}
# Stock PNGs that carry the teal accent as pixels; recoloured to Windows blue at
# build time from the base image's own copies (not committed).
TEAL_IMAGES = ["progress_fill", "slider_used", "slider_touch", "handle",
               "checkbox_true", "radio_true"]

# --------------------------------------------------------------------------
# Layout, in the stock theme's 1080x1920 logical space.
# --------------------------------------------------------------------------
MARGIN = 108
TILE_W = 864
TILE_H = 220
ROW_PITCH = 230
ROW0 = 560
ICON_X = 156
TEXT_X = 264

VARS: dict[str, str] = {
    "winre_bg": "#000000",
    "winre_text": "#FFFFFF",
    "winre_text_dim": "#B4B4B4",
    "winre_stamp": "#4A4A4A",
    "winre_hi": "#FFFFFF26",
    "winre_clear": "#00000000",
    "winre_console_bg": "#000000",
    "winre_btn": "#FFFFFF33",
    "winre_btn_hi": "#FFFFFF66",

    "winre_margin": str(MARGIN),
    "winre_tile_w": str(TILE_W),
    "winre_tile_h": str(TILE_H),
    "winre_icon_x": str(ICON_X),
    "winre_text_x": str(TEXT_X),
    "center_x": "540",

    "winre_hdr_y": "336",
    "winre_sub_y": "432",

    "winre_back_hit_y": "120",
    "winre_back_x": "120",
    "winre_back_y": "212",

    "winre_p2_y": "638",
    "winre_p3_y": "696",
    "winre_btn_row_y": "900",
    "winre_btn2_x": str(MARGIN + 400 + 40),

    "winre_console_y": "560",
    "winre_console_h": "1000",
    "winre_out_btn_y": "1620",

    "winre_wait_y": "880",
    "winre_wait_sub_y": "980",

    "winre_stamp_y": "1812",
}

for _r in range(1, 5):
    _y = ROW0 + (_r - 1) * ROW_PITCH
    VARS[f"winre_r{_r}_y"] = str(_y)
    VARS[f"winre_r{_r}_icon_y"] = str(_y + TILE_H // 2)
    VARS[f"winre_r{_r}_title_y"] = str(_y + 54)
    VARS[f"winre_r{_r}_desc_y"] = str(_y + 122)
    VARS[f"winre_r{_r}_solo_y"] = str(_y + 86)

# Cog / bar placement, solved so the retainaspect-scaled bitmap lands where we
# want on the physical panel (see the ANIM_RESOURCES note below).
import mkassets  # noqa: E402

THEME_W, THEME_H = 1080, 1920
PANEL_W, PANEL_H = 1440, 2960
_SX, _SY = PANEL_W / THEME_W, PANEL_H / THEME_H
COG_DRAWN = mkassets.COG_PX * min(_SX, _SY)


def _centre_xy(w_px: float, h_px: float, cx_px: float, cy_px: float) -> tuple[str, str]:
    return (str(round((cx_px - w_px / 2) / _SX)),
            str(round((cy_px - h_px / 2) / _SY)))


def _cog_xy(cx_px: float, cy_px: float) -> tuple[str, str]:
    return _centre_xy(COG_DRAWN, COG_DRAWN, cx_px, cy_px)


VARS["winre_cogs_x"], VARS["winre_cogs_y"] = _cog_xy(PANEL_W / 2, PANEL_H / 2)
_, VARS["winre_cogs_wait_y"] = _cog_xy(PANEL_W / 2, 880 * _SY - COG_DRAWN / 2 - 110)

BAR_DRAWN_W = mkassets.BAR_W * min(_SX, _SY)
BAR_DRAWN_H = mkassets.BAR_H * min(_SX, _SY)
VARS["winre_bar_x"], VARS["winre_bar_y"] = _centre_xy(
    BAR_DRAWN_W, BAR_DRAWN_H, PANEL_W / 2, 1100 * _SY)
VARS["winre_push_name_y"] = "1180"

FONT_RESOURCES = """
		<font name="winre_h1" filename="{light}" size="62"/>
		<font name="winre_h2" filename="{semilight}" size="42"/>
		<font name="winre_title" filename="{semilight}" size="38"/>
		<font name="winre_body" filename="{regular}" size="27"/>
		<font name="winre_small" filename="{regular}" size="23"/>
		<font name="winre_mono" filename="DroidSansMono.ttf" size="22"/>
"""

# retainaspect is not cosmetic: TWRP scales every theme bitmap at load time by
# scale_theme_w in X and scale_theme_h in Y independently, and this panel's
# aspect does not match the theme's (1.3333 across, 1.5417 down), so an
# unqualified resource comes out ~15.6% too tall and the gears go oval. The
# attribute makes TWRP use min(scale_w, scale_h) on both axes. It does not appear
# in `strings /sbin/recovery`, so it can only be confirmed on device.
ANIM_RESOURCES = ('\t\t<animation name="winre_cogs" filename="winrecog"'
                  ' retainaspect="1"/>\n'
                  '\t\t<animation name="winre_bar" filename="winrebar"'
                  ' retainaspect="1"/>\n')


def log(msg: str) -> None:
    print(msg, flush=True)


# --------------------------------------------------------------------------
# Theme patching
# --------------------------------------------------------------------------

def _inject_vars(xml: str, where: str) -> str:
    if "<variables>" not in xml:
        raise SystemExit(f"{where} has no <variables> block; cannot place WinRE vars")
    block = "".join(f'\t\t<variable name="{k}" value="{v}"/>\n' for k, v in VARS.items())
    return xml.replace("<variables>", "<variables>\n" + block, 1)


def _load_icon_names() -> list[str]:
    return sorted(p.stem for p in IMAGES.glob("winre_ic_*.png"))


def patch_ui_xml(xml: str, font_map: dict[str, str]) -> str:
    """Extend and reskin the stock ui.xml with the WinRE resources and palette."""
    if '<xmlfile name="winre.xml"/>' in xml:
        raise SystemExit("ui.xml already patched - start from a pristine image")

    fonts = FONT_RESOURCES.format(
        light=font_map["light"], semilight=font_map["semilight"], regular=font_map["regular"])
    images = "".join(
        f'\t\t<image name="{n}" filename="{n}" retainaspect="1"/>\n'
        for n in _load_icon_names())

    xml = xml.replace(
        '<xmlfile name="portrait.xml"/>',
        '<xmlfile name="portrait.xml"/>\n\t\t<xmlfile name="winre.xml"/>', 1)
    xml = xml.replace("<resources>", "<resources>\n" + fonts + images + ANIM_RESOURCES, 1)
    xml = _inject_vars(xml, "ui.xml")

    # Reskin the stock palette and fonts in place.
    for old, new in {**STOCK_COLOUR_OVERRIDES, **STOCK_FONT_OVERRIDES}.items():
        if old not in xml:
            raise SystemExit(f"reskin token not found in ui.xml: {old}")
        xml = xml.replace(old, new, 1)

    xml = xml.replace(
        "<description>Default basic theme</description>",
        "<description>Windows Recovery Environment shell</description>", 1)
    return xml


def patch_splash_xml(xml: str) -> str:
    if "%winre_bg%" not in xml:
        raise SystemExit("splash.xml does not use %winre_bg%; refusing to build")
    return _inject_vars(xml, "splash.xml")


def _page_refs(xml: str) -> set[str]:
    defined = set(re.findall(r'<page name="(\w+)">', xml))
    used = set(re.findall(r"<action function=\"page\">(\w+)</action>", xml))
    used |= set(re.findall(r"tw_clear_destination=(\w+)", xml))
    return used - defined


def patch_portrait_xml(xml: str) -> tuple[str, int]:
    """Move the stock TWRP menu out of the way of the WinRE 'main' page.

    Only the page definitions are renamed; references to `main` are left alone so
    they resolve to the WinRE screen, and every stock Home button lands back on
    WinRE instead of the TWRP grid.
    """
    baseline = _page_refs(xml)
    for old, new in (("main", "twrp_main"), ("main2", "twrp_main2"),
                     ("lock", "twrp_lock")):
        needle = f'<page name="{old}">'
        if needle not in xml:
            raise SystemExit(f"portrait.xml has no {needle}")
        xml = xml.replace(needle, f'<page name="{new}">', 1)

    xml, refs = re.subn(r"(?<![\w-])main2(?![\w-])", "twrp_main2", xml)

    introduced = _page_refs(xml) - baseline - {"main", "lock"}
    if introduced:
        raise SystemExit(f"renaming broke page references: {sorted(introduced)}")
    return xml, refs


# --------------------------------------------------------------------------

def resolve_fonts(cpio: Cpio, twres: str) -> tuple[dict[str, str], dict[str, bytes]]:
    """Prefer real Segoe (full faces); fall back to the ramdisk's own font."""
    payload: dict[str, bytes] = {}
    for dest, src in FONT_FILES.items():
        p = WINDOWS_FONTS / src
        if p.exists():
            payload[dest] = p.read_bytes()

    if len(payload) == len(FONT_FILES):
        total = sum(len(v) for v in payload.values())
        log(f"  fonts     Segoe UI ({total:,} B, copied from {WINDOWS_FONTS})")
        return ({"light": "winre-light.ttf", "semilight": "winre-semilight.ttf",
                 "regular": "winre-regular.ttf"}, payload)

    if cpio.get(f"{twres}/fonts/{FONT_FALLBACK}") is None:
        raise SystemExit("neither Segoe nor the fallback font is available")
    log(f"  fonts     FALLBACK {FONT_FALLBACK} (Segoe not found) - look will differ")
    # With the fallback the stock font swaps would point at missing files, so
    # only the winre_* resources use it and the stock swaps are skipped.
    return ({"light": FONT_FALLBACK, "semilight": FONT_FALLBACK, "regular": FONT_FALLBACK}, {})


def sh(path: Path) -> bytes:
    data = path.read_bytes().replace(b"\r\n", b"\n")
    if not data.startswith(b"#!"):
        raise SystemExit(f"{path} has no shebang")
    return data


def recolour_stock_images(cpio: Cpio, twres: str) -> list[str]:
    """Recolour the teal-carrying stock PNGs to Windows blue, in memory."""
    done = []
    for name in TEAL_IMAGES:
        e = cpio.get(f"{twres}/images/{name}.png")
        if e is None:
            continue
        e.data = mkassets.recolour_accent(e.data)
        done.append(name)
    return done


def build(source: Path, outdir: Path) -> None:
    outdir.mkdir(parents=True, exist_ok=True)
    report: list[str] = []

    log(f"reading {source} ({source.stat().st_size:,} B)")
    raw = source.read_bytes()
    boot = BootImage.parse(raw)
    log(f"  kernel    {len(boot.kernel):,} B")
    log(f"  ramdisk   {len(boot.ramdisk):,} B (lzma)")
    log(f"  dtbh      {len(boot.dt):,} B")

    if boot.serialize(refresh_id=False) != raw:
        raise SystemExit("boot image does not round-trip; refusing to build")

    # Power-off route patch (kernel, hash/pattern gated).
    if poweroff.is_patchable(boot.kernel):
        boot.kernel = poweroff.patch_kernel(boot.kernel)
        log("  kernel    power-off route patched (4 bytes)")
    else:
        log("  kernel    NOT patched (unknown kernel or already patched)")

    plain = lzma_decompress(boot.ramdisk)
    cpio = Cpio.parse(plain)
    if cpio.serialize() != plain:
        raise SystemExit("cpio does not round-trip; refusing to build")
    log(f"  cpio      {len(cpio.entries):,} entries, {len(plain):,} B")

    twres = TWRES if cpio.get(f"{TWRES}/ui.xml") else "/" + TWRES
    if cpio.get(f"{twres}/ui.xml") is None:
        raise SystemExit("no twres/ui.xml in the ramdisk")

    font_map, font_payload = resolve_fonts(cpio, twres)

    ui = cpio.get(f"{twres}/ui.xml").data.decode("utf-8")
    portrait = cpio.get(f"{twres}/portrait.xml").data.decode("utf-8")
    if cpio.get(f"{twres}/splash.xml") is None:
        raise SystemExit("no twres/splash.xml in the ramdisk")

    ui_new = patch_ui_xml(ui, font_map)
    if not font_payload:
        # Fallback: undo the stock font swaps so they do not point at missing files.
        for old, new in STOCK_FONT_OVERRIDES.items():
            ui_new = ui_new.replace(new, old)
    portrait_new, refs = patch_portrait_xml(portrait)
    winre = (THEME / "winre.xml").read_text(encoding="utf-8")
    splash_new = patch_splash_xml((THEME / "splash.xml").read_text(encoding="utf-8"))

    log(f"  theme     ui.xml +{len(ui_new) - len(ui):,} B (reskinned), "
        f"portrait.xml 3 pages renamed / {refs} refs rewritten, "
        f"winre.xml {len(winre):,} B")

    recoloured = recolour_stock_images(cpio, twres)
    log(f"  images    recoloured {len(recoloured)} teal stock image(s) to blue")

    icons = {p.stem: p.read_bytes() for p in IMAGES.glob("winre_ic_*.png")}
    cogs = sorted(IMAGES.glob("winrecog*.png"))
    bars = sorted(IMAGES.glob("winrebar*.png"))
    log(f"  assets    {len(icons)} icons, {len(cogs)} cog frames, {len(bars)} bar frames")

    written: list[str] = []

    def put(name: str, data: bytes, mode: int = 0o644) -> None:
        cpio.put_file(name, data, mode)
        written.append(f"{name:<44} {len(data):>9,} B")

    put(f"{twres}/ui.xml", ui_new.encode("utf-8"))
    put(f"{twres}/portrait.xml", portrait_new.encode("utf-8"))
    put(f"{twres}/winre.xml", winre.encode("utf-8"))
    put(f"{twres}/splash.xml", splash_new.encode("utf-8"))
    for n, png in sorted(icons.items()):
        put(f"{twres}/images/{n}.png", png)
    for p in cogs:
        put(f"{twres}/images/{p.name}", p.read_bytes())
    for p in bars:
        put(f"{twres}/images/{p.name}", p.read_bytes())
    for dest, blob in font_payload.items():
        put(f"{twres}/fonts/{dest}", blob)

    # Helper scripts.
    for script in sorted(SBIN.glob("*.sh")):
        put(f"sbin/{script.name}", sh(script), 0o755)

    # GPL kernel modules (our own).
    n_mod = 0
    for module in ("rwd1_ack.ko", "rwd1_evidence_reader.ko"):
        mp = _find_module(module)
        if mp is not None:
            put(f"{MODULE_DIR}/{module}", mp.read_bytes(), 0o644)
            n_mod += 1
    log(f"  modules   {n_mod} kernel module(s) baked into /{MODULE_DIR}")

    # USB gadget fix: adb must not depend on the recovery binary surviving.
    if _patch_usb_rc(cpio):
        written.append("init.recovery.usb.rc   (setprop sys.usb.config adb)")

    # Builder marker.
    marker = (f"builder={BUILDER_VERSION}\n"
              f"base_sha256={hashlib.sha256(raw).hexdigest()}\n").encode()
    put(f"{twres}/winre-build.txt", marker)

    # ---- repack ----------------------------------------------------------
    stock_lzma = len(boot.ramdisk)
    plain_new = cpio.serialize()
    boot.ramdisk = lzma_compress(plain_new)
    log(f"  repack    cpio {len(plain_new):,} B -> lzma {len(boot.ramdisk):,} B "
        f"({len(boot.ramdisk) - stock_lzma:+,} vs stock)")

    if lzma_decompress(boot.ramdisk) != plain_new:
        raise SystemExit("recompressed ramdisk does not decompress back; aborting")

    if boot.tail.strip(b"\0"):
        raise SystemExit("recovery partition tail is not zero fill; not safe to re-pad")

    img = boot.serialize(refresh_id=True, keep_tail=False)
    if len(img) > RECOVERY_PARTITION_BYTES:
        raise SystemExit(
            f"image does not fit the partition: {len(img):,} > {RECOVERY_PARTITION_BYTES:,} B")

    out_img = outdir / "star2lte-winre-recovery.img"
    out_img.write_bytes(img)
    log(f"  image     {out_img} ({len(img):,} B, "
        f"sha256 {hashlib.sha256(img).hexdigest()[:16]}...)")

    # ---- theme zip for the no-flash dev loop -----------------------------
    buf = io.BytesIO()
    n_zip = 0
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        prefix = twres.lstrip("/") + "/"
        for e in cpio.entries:
            rel = e.name.lstrip("/")
            if not rel.startswith(prefix) or e.is_dir or e.is_symlink:
                continue
            z.writestr(rel[len(prefix):], e.data)
            n_zip += 1
    out_zip = outdir / "ui.zip"
    out_zip.write_bytes(buf.getvalue())
    log(f"  theme zip {out_zip} ({len(buf.getvalue()):,} B, {n_zip} files)")

    report.append(f"source        {source}")
    report.append(f"builder       {BUILDER_VERSION}")
    report.append(f"image         {out_img.name}  {len(img):,} B")
    report.append(f"image sha256  {hashlib.sha256(img).hexdigest()}")
    report.append(f"theme zip     {out_zip.name}  {len(buf.getvalue()):,} B")
    report.append(f"cpio entries  {len(cpio.entries):,}")
    report.append("")
    report.append("files changed or added:")
    report.extend("  " + w for w in written)
    (outdir / "build-report.txt").write_text("\n".join(report) + "\n", encoding="utf-8")

    log("")
    log(f"{len(written)} file(s) changed in the ramdisk:")
    for w in written:
        log("  " + w)


RECOVERY_PARTITION_BYTES = 68_149_248  # RECOVERY on star2lte


def _find_module(name: str) -> Path | None:
    for base in (HERE / "modules", HERE.parent.parent / "installer" / "payload" / "twrp-modules"):
        p = base / name
        if p.exists():
            return p
    return None


USB_ANCHOR = "    setprop sys.usb.controller 10c00000.dwc3\n"


def _patch_usb_rc(cpio: Cpio) -> bool:
    entry = cpio.get("init.recovery.usb.rc")
    if entry is None:
        return False
    text = entry.data.decode("utf-8")
    if "setprop sys.usb.config adb" in text or text.count(USB_ANCHOR) != 1:
        return False
    patch = USB_ANCHOR + (
        "    # adb must not depend on the recovery binary surviving: nothing else\n"
        "    # sets sys.usb.config, and TWRP skips its USB setup after a crash.\n"
        "    setprop sys.usb.config adb\n")
    entry.data = text.replace(USB_ANCHOR, patch).encode("utf-8")
    return True


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--source", type=Path, required=True)
    ap.add_argument("--outdir", type=Path, default=HERE / "work" / "out")
    args = ap.parse_args()
    build(args.source, args.outdir)


if __name__ == "__main__":
    main()
