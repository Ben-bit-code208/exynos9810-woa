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
  python build.py --source ... --gears /path/to/UpdateOS-GearAnimation.gif

--gears takes the builder's own copy of Microsoft's UpdateOS gear animation
(optional, like the Segoe fonts): its frames are rendered into the image being
built, white on black at the size the theme expects, instead of the procedural
gears. Neither the GIF nor any frame of it is ever written into this repository.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import poweroff  # noqa: E402
from bootimg import BootImage, Cpio, lzma_compress, lzma_decompress  # noqa: E402

HERE = Path(__file__).resolve().parent
TWRES = "twres"
THEME = HERE / "theme"
IMAGES = HERE / "assets" / "images"
STOCK_ART = HERE / "assets" / "stock"
SBIN = HERE / "overlay" / "sbin"

# winre-2: installs the live-progress install screen, the reskin.xml page
# template/styles/images and the WinRE singleaction/action pages. The installer
# rebuilds any recovery whose marker names an older builder.
BUILDER_VERSION = "winre-2"
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

# Gears from the user's UpdateOS GIF: luma at or below this is the GIF's
# near-black background tint and becomes true black.
GEAR_FLOOR = 8

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
    # Accent as text on black (Windows' light accent for #0078D4) and the
    # dark band behind the stock tab bars.
    "winre_accent_text": "#60CDFF",
    "winre_tabbar": "#202020",
    # Used by the stock styles but never defined by the stock theme.
    "button_text_color": "#FFFFFF",
    "fileselector_highlight_font_color": "#FFFFFF",

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

    # The back arrow the page template puts beside stock page titles (which
    # sit at x=184, y=90 in the stock header), and its touch target.
    "winre_hdr_back_x": "92",
    "winre_hdr_back_y": "126",
    "winre_hdr_hit_y": "64",
    "winre_hdr_hit_w": "176",
    "winre_hdr_hit_h": "128",
    # "Show details" on stock operation pages, where the log used to be.
    "winre_details_y": "752",
    "winre_details_h": "96",

    "winre_p2_y": "638",
    "winre_p3_y": "696",
    "winre_btn_row_y": "900",
    "winre_btn2_x": str(MARGIN + 400 + 40),
    "winre_btn_mid_x": str((1080 - 400) // 2),

    "winre_console_y": "560",
    "winre_console_h": "1000",
    "winre_out_btn_y": "1620",

    "winre_wait_y": "880",
    "winre_wait_sub_y": "980",
    "winre_install_detail_y": "1135",
    "winre_install_foot_y": "1740",
    "winre_lock_time_y": "760",
    "winre_lock_sub_y": "880",

    "winre_stamp_y": "1812",

    # Set per build from the gear frames actually installed (see gear_speed).
    "winre_cogs_fps": "24",
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
# Centred in the area the stock console used to fill (y 320..1280).
_, VARS["winre_cogs_console_y"] = _cog_xy(PANEL_W / 2, 800 * _SY)

BAR_DRAWN_W = mkassets.BAR_W * min(_SX, _SY)
BAR_DRAWN_H = mkassets.BAR_H * min(_SX, _SY)
VARS["winre_bar_x"], VARS["winre_bar_y"] = _centre_xy(
    BAR_DRAWN_W, BAR_DRAWN_H, PANEL_W / 2, 1100 * _SY)

FONT_RESOURCES = """
		<font name="winre_h1" filename="winre-light.ttf" size="62"/>
		<font name="winre_h2" filename="winre-semilight.ttf" size="42"/>
		<font name="winre_title" filename="winre-semilight.ttf" size="38"/>
		<font name="winre_body" filename="winre-regular.ttf" size="27"/>
		<font name="winre_small" filename="winre-regular.ttf" size="23"/>
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

def _inject_vars(xml: str, where: str, variables: dict[str, str]) -> str:
    if "<variables>" not in xml:
        raise SystemExit(f"{where} has no <variables> block; cannot place WinRE vars")
    block = "".join(f'\t\t<variable name="{k}" value="{v}"/>\n' for k, v in variables.items())
    return xml.replace("<variables>", "<variables>\n" + block, 1)


def _load_icon_names() -> list[str]:
    return sorted(p.stem for p in IMAGES.glob("winre_ic_*.png"))


# Extra single images the WinRE pages declare (determinate install bar).
EXTRA_IMAGES = ["winre_pbar_empty", "winre_pbar_full"]

_WORD = r"(?<![\w-]){}(?![\w-])"


def apply_reskin(text: str, file_name: str, ops_path: Path = THEME / "reskin.xml") -> tuple[str, int]:
    """Apply the reskin.xml operations for one stock file; every count must match."""
    root = ET.parse(ops_path).getroot()
    blocks = [f for f in root.findall("file") if f.get("name") == file_name]
    if len(blocks) != 1:
        raise SystemExit(f"reskin.xml has {len(blocks)} blocks for {file_name}")
    n_ops = 0
    for op in blocks[0]:
        want = int(op.get("count", "1"))
        if op.tag == "replace":
            find, new = op.findtext("find"), op.findtext("with")
            got = text.count(find)
            if got == want:
                text = text.replace(find, new)
        elif op.tag == "element":
            start, end, new = op.findtext("start"), op.findtext("end"), op.findtext("with")
            got, i, out = 0, 0, []
            while (j := text.find(start, i)) >= 0:
                k = text.find(end, j + len(start))
                if k < 0:
                    raise SystemExit(f"reskin {file_name}: no {end!r} after {start!r}")
                out += [text[i:j], new]
                i = k + len(end)
                got += 1
            find = start
            if got == want:
                text = "".join(out) + text[i:]
        elif op.tag == "word":
            find, new = op.findtext("find"), op.findtext("with")
            rx = re.compile(_WORD.format(re.escape(find)))
            got = len(rx.findall(text))
            if got == want:
                text = rx.sub(new, text)
        else:
            raise SystemExit(f"reskin {file_name}: unknown operation <{op.tag}>")
        if got != want:
            raise SystemExit(f"reskin {file_name}: {op.tag} {find!r} matched {got} time(s), "
                             f"expected {want}; the base theme is not the one this was written for")
        n_ops += 1
    return text, n_ops


def patch_ui_xml(xml: str) -> tuple[str, int]:
    """Reskin the stock ui.xml and add the WinRE resources, variables and include."""
    if '<xmlfile name="winre.xml"/>' in xml:
        raise SystemExit("ui.xml already patched - start from a pristine image")
    xml, n = apply_reskin(xml, "ui.xml")

    images = "".join(
        f'\t\t<image name="{n}" filename="{n}" retainaspect="1"/>\n'
        for n in _load_icon_names() + EXTRA_IMAGES)
    xml = xml.replace(
        '<xmlfile name="portrait.xml"/>',
        '<xmlfile name="portrait.xml"/>\n\t\t<xmlfile name="winre.xml"/>', 1)
    xml = xml.replace("<resources>", "<resources>\n" + FONT_RESOURCES + images + ANIM_RESOURCES, 1)
    return _inject_vars(xml, "ui.xml", VARS), n


def patch_splash_xml(xml: str) -> str:
    if "%winre_bg%" not in xml:
        raise SystemExit("splash.xml does not use %winre_bg%; refusing to build")
    return _inject_vars(xml, "splash.xml", VARS)


def _pages(xml: str) -> set[str]:
    return set(re.findall(r'<page name="([\w-]+)">', xml))


def _targets(xml: str) -> set[str]:
    used = set(re.findall(r'<action function="page">([\w-]+)</action>', xml))
    used |= set(re.findall(r"tw_clear_destination=([\w-]+)", xml))
    return used


def patch_portrait_xml(xml: str, winre: str) -> tuple[str, int]:
    """Reskin the stock portrait.xml (page renames, styles) and check no page
    reference was left dangling by the renames."""
    baseline = _targets(xml) - _pages(xml)
    new, n = apply_reskin(xml, "portrait.xml")
    defined = _pages(new) | _pages(winre)
    dangling = (_targets(new) | _targets(winre)) - defined - baseline
    if dangling:
        raise SystemExit(f"the reskin left page references dangling: {sorted(dangling)}")
    for name in ("main", "lock", "singleaction_page", "action_page", "action_complete"):
        if name in _pages(new) or name not in _pages(winre):
            raise SystemExit(f"'{name}' must be defined by winre.xml only")
    return new, n


# --------------------------------------------------------------------------

def resolve_fonts(cpio: Cpio, twres: str) -> dict[str, bytes]:
    """Segoe UI from this PC's Windows (full faces) under the winre-*.ttf names.

    Without Segoe (a non-Windows dev box), the ramdisk's own font is placed
    under the same names so every reference still resolves; the look differs.
    """
    payload: dict[str, bytes] = {}
    for dest, src in FONT_FILES.items():
        p = WINDOWS_FONTS / src
        if p.exists():
            payload[dest] = p.read_bytes()

    if len(payload) == len(FONT_FILES):
        total = sum(len(v) for v in payload.values())
        log(f"  fonts     Segoe UI ({total:,} B, copied from {WINDOWS_FONTS})")
        return payload

    fallback = cpio.get(f"{twres}/fonts/{FONT_FALLBACK}")
    if fallback is None:
        raise SystemExit("neither Segoe nor the fallback font is available")
    log(f"  fonts     FALLBACK {FONT_FALLBACK} (Segoe not found) - look will differ")
    return {dest: fallback.data for dest in FONT_FILES}


def sh(path: Path) -> bytes:
    data = path.read_bytes().replace(b"\r\n", b"\n")
    if not data.startswith(b"#!"):
        raise SystemExit(f"{path} has no shebang")
    return data


def gear_speed(delay_ms: float) -> int:
    """<speed fps> whose TWRP frame period is closest to delay_ms.

    TWRP's GUI loop runs at 30 Hz and an animation advances every
    floor(30/fps) + 1 loop passes, so only these periods exist.
    """
    return min(range(1, 31), key=lambda f: (abs((30 // f + 1) * 1000 / 30 - delay_ms), f))


def gif_gear_frames(path: Path) -> tuple[list[bytes], float]:
    """Render the frames of the user's own gear GIF, white on black, at COG_PX.

    Returns the PNG frames and the mean frame delay in ms. Never persisted
    outside the image being built.
    """
    from PIL import Image, ImageSequence

    im = Image.open(path)
    if im.format != "GIF":
        raise SystemExit(f"{path} is not a GIF")
    frames: list[bytes] = []
    delays: list[float] = []
    for fr in ImageSequence.Iterator(im):
        delays.append(float(fr.info.get("duration") or 100))
        rgba = fr.convert("RGBA")
        flat = Image.alpha_composite(Image.new("RGBA", rgba.size, (0, 0, 0, 255)), rgba)
        luma = flat.convert("RGB").convert("L").point(lambda v: 0 if v <= GEAR_FLOOR else v)
        luma = luma.resize((mkassets.COG_PX, mkassets.COG_PX), Image.LANCZOS)
        buf = io.BytesIO()
        Image.merge("RGB", (luma, luma, luma)).save(buf, "PNG", optimize=True)
        frames.append(buf.getvalue())
    if not frames:
        raise SystemExit(f"{path} has no frames")
    return frames, sum(delays) / len(delays)


def build(source: Path, outdir: Path, gears: Path | None = None) -> None:
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

    font_payload = resolve_fonts(cpio, twres)

    # Gears: the user's UpdateOS GIF if given, else the procedural frames.
    if gears is not None:
        gear_frames, delay = gif_gear_frames(gears)
        fps = gear_speed(delay)
        gears_id = "sha256:" + hashlib.sha256(gears.read_bytes()).hexdigest()
        log(f"  gears     {len(gear_frames)} frame(s) from {gears.name} "
            f"({delay:.0f} ms/frame -> fps {fps}); NOT stored anywhere but this image")
    else:
        gear_frames = [p.read_bytes() for p in sorted(IMAGES.glob("winrecog*.png"))]
        fps = 24
        gears_id = "builtin"
        log(f"  gears     {len(gear_frames)} procedural frame(s)")
    VARS["winre_cogs_fps"] = str(fps)

    ui = cpio.get(f"{twres}/ui.xml").data.decode("utf-8")
    portrait = cpio.get(f"{twres}/portrait.xml").data.decode("utf-8")
    if cpio.get(f"{twres}/splash.xml") is None:
        raise SystemExit("no twres/splash.xml in the ramdisk")

    winre = (THEME / "winre.xml").read_text(encoding="utf-8")
    ui_new, n_ui = patch_ui_xml(ui)
    portrait_new, n_portrait = patch_portrait_xml(portrait, winre)
    splash_new = patch_splash_xml((THEME / "splash.xml").read_text(encoding="utf-8"))

    log(f"  theme     ui.xml {n_ui} reskin op(s), portrait.xml {n_portrait} op(s), "
        f"winre.xml {len(winre):,} B")

    stock_names = {e.name.rsplit("/", 1)[-1][:-4] for e in cpio.entries
                   if e.name.startswith(f"{twres}/images/") and e.name.endswith(".png")}
    stock_art = {p.stem: p.read_bytes() for p in STOCK_ART.glob("*.png")}
    unreplaced = sorted(stock_names - set(stock_art))
    if unreplaced:
        raise SystemExit(f"stock images without an original replacement: {unreplaced}")
    log(f"  images    {len(stock_art)} stock image(s) replaced with original art")

    icons = {p.stem: p.read_bytes() for p in IMAGES.glob("winre_ic_*.png")}
    extras = {n: (IMAGES / f"{n}.png").read_bytes() for n in EXTRA_IMAGES}
    bars = sorted(IMAGES.glob("winrebar*.png"))
    log(f"  assets    {len(icons)} icons, {len(gear_frames)} cog frames, {len(bars)} bar frames")

    written: list[str] = []

    def put(name: str, data: bytes, mode: int = 0o644) -> None:
        cpio.put_file(name, data, mode)
        written.append(f"{name:<44} {len(data):>9,} B")

    put(f"{twres}/ui.xml", ui_new.encode("utf-8"))
    put(f"{twres}/portrait.xml", portrait_new.encode("utf-8"))
    put(f"{twres}/winre.xml", winre.encode("utf-8"))
    put(f"{twres}/splash.xml", splash_new.encode("utf-8"))
    for n, png in sorted(stock_art.items()):
        put(f"{twres}/images/{n}.png", png)
    for n, png in sorted({**icons, **extras}.items()):
        put(f"{twres}/images/{n}.png", png)
    for i, png in enumerate(gear_frames, 1):
        put(f"{twres}/images/winrecog{i:03d}.png", png)
    for p in bars:
        put(f"{twres}/images/{p.name}", p.read_bytes())
    for dest, blob in font_payload.items():
        put(f"{twres}/fonts/{dest}", blob)

    # Helper scripts.
    for script in sorted(SBIN.glob("*.sh")):
        put(f"sbin/{script.name}", sh(script), 0o755)

    # GPL kernel modules (our own).
    n_mod = 0
    for module in ("rwd1_ack.ko", "rwd1_evidence_reader.ko",
                   "rwd1_clear_poc.ko", "pram_smp_clear_poc.ko"):
        mp = _find_module(module)
        if mp is not None:
            put(f"{MODULE_DIR}/{module}", mp.read_bytes(), 0o644)
            n_mod += 1
    log(f"  modules   {n_mod} kernel module(s) baked into /{MODULE_DIR}")

    # USB gadget fix: adb must not depend on the recovery binary surviving.
    if _patch_usb_rc(cpio):
        written.append("init.recovery.usb.rc   (setprop sys.usb.config adb)")

    # ntfs-3g FUSE deadlock breaker: an init service so it runs before the
    # recovery binary mounts /data (where the self-deadlock strikes).
    if _inject_ntfs_watchdog(cpio):
        written.append("init.recovery.service.rc (winre_ntfswd watchdog)")

    # Builder marker.
    marker = (f"builder={BUILDER_VERSION}\n"
              f"base_sha256={hashlib.sha256(raw).hexdigest()}\n"
              f"gears={gears_id}\n").encode()
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
    report.append(f"gears         {gears_id} (fps {fps})")
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


def _inject_ntfs_watchdog(cpio: Cpio) -> bool:
    entry = cpio.get("init.recovery.service.rc")
    if entry is None:
        return False
    text = entry.data.decode("utf-8").replace("\r\n", "\n")
    if "winre_ntfswd" in text:
        return False
    block = (
        "\n"
        "service winre_ntfswd /sbin/winre-ntfs-watchdog.sh\n"
        "    oneshot\n"
        "    seclabel u:r:recovery:s0\n"
        "\n"
        "on boot\n"
        "    start winre_ntfswd\n")
    entry.data = (text.rstrip("\n") + "\n" + block).encode("utf-8")
    return True


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--source", type=Path, required=True)
    ap.add_argument("--outdir", type=Path, default=HERE / "work" / "out")
    ap.add_argument("--gears", type=Path, default=None,
                    help="your own UpdateOS-GearAnimation.gif (optional; never stored)")
    args = ap.parse_args()
    if args.gears is not None and not args.gears.is_file():
        raise SystemExit(f"--gears: {args.gears} not found")
    build(args.source, args.outdir, args.gears)


if __name__ == "__main__":
    main()
