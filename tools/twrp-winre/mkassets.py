# SPDX-License-Identifier: BSD-2-Clause-Patent
"""Original procedural art for the WinRE-look TWRP theme.

Everything this file draws is generated from geometry in code: there are no
Microsoft glyphs, no Segoe MDL2 icons and no frames lifted from Microsoft's
UpdateOS gear GIF. That is deliberate and load-bearing for the licence story
(see README): the *fonts* are copied from the builder's own licensed Windows at
build time and are never committed, and the *icons and gears* are these original
line drawings, committed as ordinary project art.

Two rendering rules come straight from the device and are not style choices:

  * Everything is emitted OPAQUE WHITE-ON-BLACK, with the shape carried in the
    RGB channels, not in alpha. TWRP authors this theme at 1080 wide and draws
    it at 1440, so every bitmap is resampled, and TWRP's upscaler blends RGB
    without weighting by alpha - a shape carried only in the alpha channel bleeds
    into a grey smear. On a #000000 page an opaque black background is
    indistinguishable from a transparent one, so white-on-black is both correct
    and invisible where it should be.

  * Icons are square and the theme declares retainaspect="1", so TWRP scales
    both axes by the same factor and a square source stays square on the panel.

Run this file to (re)generate the committed PNGs under assets/, plus a preview
contact sheet:

    python mkassets.py            # writes assets/images/*.png and preview
    python mkassets.py --preview  # only the contact sheet

build.py loads the committed PNGs; it does not regenerate them, so the bytes the
Python tool and the C# installer ship are identical.
"""

from __future__ import annotations

import io
import math
from pathlib import Path

from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
IMAGES = HERE / "assets" / "images"

SS = 4              # supersample factor: draw big, shrink with LANCZOS
WHITE = 255
ICON_PX = 96        # tile icons
BACK_PX = 56        # the small top-left back chevron

# name -> drawing function; every icon is white line-art in a unit square.
# The list is intentionally broad so a new troubleshoot tile never has to fall
# back to a Segoe glyph.


def _canvas(px: int) -> tuple[Image.Image, ImageDraw.ImageDraw, int]:
    n = px * SS
    im = Image.new("L", (n, n), 0)
    return im, ImageDraw.Draw(im), n


def _finish(im: Image.Image, px: int) -> bytes:
    small = im.resize((px, px), Image.LANCZOS)
    rgb = Image.merge("RGB", (small, small, small))
    buf = io.BytesIO()
    rgb.save(buf, "PNG", optimize=True)
    return buf.getvalue()


def _stroke(n: int) -> int:
    return max(2, round(n * 0.070))


def _arrow_head(d: ImageDraw.ImageDraw, x: float, y: float, dx: float, dy: float,
                size: float, w: int) -> None:
    """Two strokes forming a chevron head pointing along (dx, dy)."""
    ang = math.atan2(dy, dx)
    for da in (math.radians(148), math.radians(-148)):
        ex = x + size * math.cos(ang + da)
        ey = y + size * math.sin(ang + da)
        d.line([(x, y), (ex, ey)], fill=WHITE, width=w, joint="curve")


def _line(d, pts, w):
    d.line(pts, fill=WHITE, width=w, joint="curve")


def _ring(d, cx, cy, r, w):
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=WHITE, width=w)


def _disc(d, cx, cy, r):
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=WHITE)


def _hole(d, cx, cy, r):
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=0)


# --------------------------------------------------------------------------
# individual icons (each draws into an n x n canvas of colour L)
# --------------------------------------------------------------------------

def ic_arrow_right(d, n):
    w = _stroke(n)
    y = n * 0.5
    _line(d, [(n * 0.16, y), (n * 0.80, y)], w)
    _arrow_head(d, n * 0.80, y, 1, 0, n * 0.22, w)


def ic_arrow_left(d, n):
    w = _stroke(n)
    y = n * 0.5
    _line(d, [(n * 0.84, y), (n * 0.20, y)], w)
    _arrow_head(d, n * 0.20, y, -1, 0, n * 0.22, w)


def ic_restart(d, n):
    """A circular arrow: open ring with an arrowhead on the open end."""
    w = _stroke(n)
    cx, cy, r = n * 0.5, n * 0.5, n * 0.30
    # arc from ~ -60 deg sweeping clockwise most of the way round
    d.arc([cx - r, cy - r, cx + r, cy + r], start=-40, end=250, fill=WHITE, width=w)
    # arrowhead at the start of the arc (top-right), pointing tangentially
    a = math.radians(-40)
    hx, hy = cx + r * math.cos(a), cy + r * math.sin(a)
    tang = a + math.pi / 2  # clockwise tangent
    _arrow_head(d, hx, hy, math.cos(tang), math.sin(tang), n * 0.20, w)


def ic_power(d, n):
    w = _stroke(n)
    cx, cy, r = n * 0.5, n * 0.54, n * 0.28
    # broken ring (gap at the top)
    d.arc([cx - r, cy - r, cx + r, cy + r], start=-62, end=242, fill=WHITE, width=w)
    _line(d, [(cx, cy - r - n * 0.10), (cx, cy - n * 0.02)], w)


def ic_wrench_tools(d, n):
    """Crossed wrench + screwdriver = 'troubleshoot'."""
    w = _stroke(n)
    # wrench: a thick diagonal bar with an open-C head
    _line(d, [(n * 0.30, n * 0.72), (n * 0.66, n * 0.36)], w)
    _ring(d, n * 0.72, n * 0.30, n * 0.10, w)
    d.rectangle([n * 0.66, n * 0.20, n * 0.80, n * 0.30], fill=0)  # open the C
    # screwdriver: other diagonal, with a handle
    _line(d, [(n * 0.70, n * 0.72), (n * 0.42, n * 0.44)], w)
    d.line([(n * 0.66, n * 0.78), (n * 0.78, n * 0.66)], fill=WHITE, width=w * 2)


def ic_magnifier(d, n):
    w = _stroke(n)
    cx, cy, r = n * 0.44, n * 0.44, n * 0.22
    _ring(d, cx, cy, r, w)
    a = math.radians(45)
    _line(d, [(cx + r * math.cos(a), cy + r * math.sin(a)),
              (n * 0.80, n * 0.80)], round(w * 1.3))


def ic_trash(d, n):
    w = _stroke(n)
    # lid
    _line(d, [(n * 0.26, n * 0.30), (n * 0.74, n * 0.30)], w)
    _line(d, [(n * 0.42, n * 0.30), (n * 0.44, n * 0.22)], w)
    _line(d, [(n * 0.44, n * 0.22), (n * 0.56, n * 0.22)], w)
    _line(d, [(n * 0.56, n * 0.22), (n * 0.58, n * 0.30)], w)
    # can
    _line(d, [(n * 0.32, n * 0.32), (n * 0.36, n * 0.78)], w)
    _line(d, [(n * 0.68, n * 0.32), (n * 0.64, n * 0.78)], w)
    _line(d, [(n * 0.36, n * 0.78), (n * 0.64, n * 0.78)], w)
    for x in (0.44, 0.50, 0.56):
        _line(d, [(n * x, n * 0.40), (n * x, n * 0.70)], max(2, w - SS))


def ic_save(d, n):
    """A floppy-disk 'save' glyph."""
    w = _stroke(n)
    m = n * 0.24
    d.rectangle([m, m, n - m, n - m], outline=WHITE, width=w)
    # cut corner
    d.rectangle([n - m - n * 0.14, m, n - m, m + n * 0.14], fill=0)
    _line(d, [(n - m - n * 0.14, m), (n - m, m + n * 0.14)], w)
    # label + shutter
    d.rectangle([n * 0.38, n * 0.52, n * 0.62, n - m], outline=WHITE, width=max(2, w - SS))
    d.rectangle([n * 0.44, m, n * 0.56, n * 0.36], fill=WHITE)


def ic_terminal(d, n):
    """Command-prompt box with a >_ prompt."""
    w = _stroke(n)
    m = n * 0.20
    d.rectangle([m, m + n * 0.04, n - m, n - m - n * 0.04], outline=WHITE, width=w)
    _line(d, [(n * 0.32, n * 0.42), (n * 0.42, n * 0.50), (n * 0.32, n * 0.58)], w)
    _line(d, [(n * 0.48, n * 0.58), (n * 0.66, n * 0.58)], w)


def ic_bug(d, n):
    w = _stroke(n)
    cx = n * 0.5
    d.ellipse([cx - n * 0.16, n * 0.34, cx + n * 0.16, n * 0.76], outline=WHITE, width=w)
    _ring(d, cx, n * 0.30, n * 0.09, w)  # head
    for sy in (0.44, 0.55, 0.66):        # legs
        _line(d, [(cx - n * 0.16, n * sy), (cx - n * 0.30, n * (sy - 0.05))], w)
        _line(d, [(cx + n * 0.16, n * sy), (cx + n * 0.30, n * (sy - 0.05))], w)


def ic_download(d, n):
    w = _stroke(n)
    cx = n * 0.5
    _line(d, [(cx, n * 0.18), (cx, n * 0.58)], w)
    _arrow_head(d, cx, n * 0.58, 0, 1, n * 0.20, w)
    _line(d, [(n * 0.26, n * 0.74), (n * 0.26, n * 0.82), (n * 0.74, n * 0.82),
              (n * 0.74, n * 0.74)], w)


def ic_ticket(d, n):
    """Shield with a check = 'clear the boot ticket'."""
    w = _stroke(n)
    cx = n * 0.5
    pts = [(cx, n * 0.18), (n * 0.76, n * 0.28), (n * 0.76, n * 0.54),
           (cx, n * 0.82), (n * 0.24, n * 0.54), (n * 0.24, n * 0.28)]
    d.polygon(pts, outline=WHITE, width=w)
    _line(d, [(n * 0.38, n * 0.46), (n * 0.47, n * 0.56), (n * 0.64, n * 0.36)], w)


def ic_disk(d, n):
    """A hard disk platter (for 'repair boot partitions')."""
    w = _stroke(n)
    cx, cy = n * 0.5, n * 0.5
    _ring(d, cx, cy, n * 0.30, w)
    _ring(d, cx, cy, n * 0.08, w)
    a = math.radians(35)
    _line(d, [(cx + n * 0.08 * math.cos(a), cy + n * 0.08 * math.sin(a)),
              (cx + n * 0.30 * math.cos(a), cy + n * 0.30 * math.sin(a))], w)


def ic_disk_repair(d, n):
    """A disk with a small wrench = 'repair Windows volume'."""
    w = _stroke(n)
    cx, cy = n * 0.44, n * 0.46
    _ring(d, cx, cy, n * 0.24, w)
    _ring(d, cx, cy, n * 0.06, max(2, w - SS))
    # wrench overlay lower-right
    _line(d, [(n * 0.56, n * 0.60), (n * 0.78, n * 0.82)], w)
    _ring(d, n * 0.82, n * 0.60, n * 0.09, w)
    d.rectangle([n * 0.80, n * 0.50, n * 0.94, n * 0.60], fill=0)


ICONS = {
    "winre_ic_continue": (ic_arrow_right, ICON_PX),
    "winre_ic_back": (ic_arrow_left, BACK_PX),
    "winre_ic_recovery": (ic_restart, ICON_PX),
    "winre_ic_poweroff": (ic_power, ICON_PX),
    "winre_ic_troubleshoot": (ic_wrench_tools, ICON_PX),
    "winre_ic_check": (ic_magnifier, ICON_PX),
    "winre_ic_erase": (ic_trash, ICON_PX),
    "winre_ic_save": (ic_save, ICON_PX),
    "winre_ic_debug": (ic_terminal, ICON_PX),
    "winre_ic_bug": (ic_bug, ICON_PX),
    "winre_ic_download": (ic_download, ICON_PX),
    "winre_ic_ticket": (ic_ticket, ICON_PX),
    "winre_ic_disk": (ic_disk, ICON_PX),
    "winre_ic_repair": (ic_disk_repair, ICON_PX),
}


def build_icons() -> dict[str, bytes]:
    out: dict[str, bytes] = {}
    for name, (fn, px) in ICONS.items():
        im, d, n = _canvas(px)
        fn(d, n)
        out[name] = _finish(im, px)
    return out


# --------------------------------------------------------------------------
# Rotating gears (original geometry, not Microsoft's UpdateOS frames)
# --------------------------------------------------------------------------
# Two meshing spur gears drawn from trapezoidal teeth on a pitch circle. They
# counter-rotate, as meshed gears must, and each frame steps them by one slice of
# a single tooth pitch. Because a T-tooth gear maps onto itself every 360/T
# degrees, a sequence spanning exactly one pitch loops seamlessly - the same
# reason the cog builder in the research tool did, arrived at from geometry here
# rather than from a source GIF.
COG_PX = 420
COG_TEETH = 8
COG_FRAMES = 16     # one 45-degree pitch, 16 distinct slices
COG_SS = 4


def _gear_polygon(cx, cy, pitch_r, tooth_h, teeth, phase):
    """A closed gear outline: trapezoidal teeth around a pitch circle."""
    tip = pitch_r + tooth_h / 2.0
    root = pitch_r - tooth_h / 2.0
    p = 2 * math.pi / teeth
    pts = []
    for i in range(teeth):
        c = i * p + phase
        # root, rise, tip, tip, fall, root -> a clean trapezoid tooth
        for da, r in ((-0.30 * p, root), (-0.17 * p, tip),
                      (0.17 * p, tip), (0.30 * p, root)):
            a = c + da
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    return pts


def _draw_gear(d, cx, cy, pitch_r, tooth_h, teeth, phase, bore_r, hub_r):
    d.polygon(_gear_polygon(cx, cy, pitch_r, tooth_h, teeth, phase), fill=WHITE)
    if hub_r:
        d.ellipse([cx - hub_r, cy - hub_r, cx + hub_r, cy + hub_r], fill=0)
        d.ellipse([cx - hub_r + 6 * COG_SS, cy - hub_r + 6 * COG_SS,
                   cx + hub_r - 6 * COG_SS, cy + hub_r - 6 * COG_SS], fill=WHITE)
    d.ellipse([cx - bore_r, cy - bore_r, cx + bore_r, cy + bore_r], fill=0)


def build_cog_frames(size: int = COG_PX, frames: int = COG_FRAMES) -> list[bytes]:
    n = size * COG_SS
    # Big gear upper-left, small gear lower-right, pitch circles close enough
    # that the teeth interleave (they mesh).
    big = dict(cx=n * 0.40, cy=n * 0.42, pitch_r=n * 0.255, tooth_h=n * 0.085,
               teeth=COG_TEETH, bore_r=n * 0.045, hub_r=n * 0.11)
    small = dict(cx=n * 0.70, cy=n * 0.70, pitch_r=n * 0.165, tooth_h=n * 0.075,
                 teeth=COG_TEETH, bore_r=n * 0.035, hub_r=n * 0.08)
    pitch = 2 * math.pi / COG_TEETH
    # half-tooth offset so a tooth of one sits in a gap of the other
    mesh = pitch / 2.0
    out: list[bytes] = []
    for k in range(frames):
        ang = k * pitch / frames
        im = Image.new("L", (n, n), 0)
        d = ImageDraw.Draw(im)
        _draw_gear(d, phase=ang, **big)
        _draw_gear(d, phase=mesh - ang, **small)
        grey = im.resize((size, size), Image.LANCZOS)
        rgb = Image.merge("RGB", (grey, grey, grey))
        buf = io.BytesIO()
        rgb.save(buf, "PNG", optimize=True)
        out.append(buf.getvalue())
    if len(set(out)) != len(out):
        raise SystemExit("cog frames repeat: the sweep spans more than one pitch")
    return out


# --------------------------------------------------------------------------
# Indeterminate progress bar (the "Copying Windows" sweep)
# --------------------------------------------------------------------------
BAR_W, BAR_H = 480, 12
BAR_FRAMES = 48
BAR_SEG = 0.30
BAR_TRACK = 44
BAR_SS = 4


def build_bar_frames(w: int = BAR_W, h: int = BAR_H, frames: int = BAR_FRAMES) -> list[bytes]:
    ss = BAR_SS
    W, H = w * ss, h * ss
    seg = int(W * BAR_SEG)
    radius = H // 2
    out: list[bytes] = []
    for k in range(frames):
        x0 = -seg + (k / frames) * (W + seg)
        im = Image.new("L", (W, H), 0)
        d = ImageDraw.Draw(im)
        d.rounded_rectangle([0, 0, W - 1, H - 1], radius=radius, fill=BAR_TRACK)
        d.rounded_rectangle([x0, 0, x0 + seg - 1, H - 1], radius=radius, fill=255)
        grey = im.resize((w, h), Image.LANCZOS)
        rgb = Image.merge("RGB", (grey, grey, grey))
        buf = io.BytesIO()
        rgb.save(buf, "PNG", optimize=True)
        out.append(buf.getvalue())
    if len(set(out)) != len(out):
        raise SystemExit("bar frames repeat: the sweep has stalled frames")
    return out


# --------------------------------------------------------------------------
# Stock-image recolour (build-time transform, NOT committed)
# --------------------------------------------------------------------------
# The stock TWRP theme carries its teal accent (#0090C9) baked into a handful of
# PNGs - the progress fill, the slider, the handle, the checkbox/radio ticks. The
# WinRE reskin replaces the teal *variables* everywhere they are referenced, but
# these few images hold the colour as pixels. build.py recolours the base image's
# own copies to Windows blue at build time; the result is never committed, so no
# TeamWin-derived bitmap enters the repo - only this transform does.
WIN_BLUE = (0x00, 0x67, 0xC0)
TWRP_TEAL = (0x00, 0x90, 0xC9)


def recolour_accent(png_bytes: bytes, target=WIN_BLUE, ref=TWRP_TEAL) -> bytes:
    """Map the teal accent to Windows blue, preserving alpha and shading."""
    im = Image.open(io.BytesIO(png_bytes)).convert("RGBA")
    px = im.load()
    tr, tg, tb = ref
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            # teal pixels are blue-dominant with mid green and near-zero red;
            # scale toward the target while keeping per-pixel brightness.
            if b > 90 and g > 60 and r < 90 and b >= g:
                scale = b / max(1, tb)
                px[x, y] = (min(255, int(target[0] * scale)),
                            min(255, int(target[1] * scale)),
                            min(255, int(target[2] * scale)), a)
    buf = io.BytesIO()
    im.save(buf, "PNG", optimize=True)
    return buf.getvalue()


# --------------------------------------------------------------------------

def write_all() -> None:
    IMAGES.mkdir(parents=True, exist_ok=True)
    icons = build_icons()
    for name, png in icons.items():
        (IMAGES / f"{name}.png").write_bytes(png)
    cogs = build_cog_frames()
    for i, png in enumerate(cogs, 1):
        (IMAGES / f"winrecog{i:03d}.png").write_bytes(png)
    bars = build_bar_frames()
    for i, png in enumerate(bars, 1):
        (IMAGES / f"winrebar{i:03d}.png").write_bytes(png)
    total = sum((IMAGES / p).stat().st_size for p in
                [f"{n}.png" for n in icons]
                + [f"winrecog{i:03d}.png" for i in range(1, len(cogs) + 1)]
                + [f"winrebar{i:03d}.png" for i in range(1, len(bars) + 1)])
    print(f"wrote {len(icons)} icons, {len(cogs)} cog frames, {len(bars)} bar frames "
          f"to {IMAGES} ({total:,} B)")


def contact_sheet(path: Path) -> None:
    icons = build_icons()
    cols = 5
    names = list(icons)
    rows = (len(names) + cols - 1) // cols + 1
    cell = 150
    sheet = Image.new("RGB", (cols * cell, rows * cell), (0, 0, 0))
    for i, name in enumerate(names):
        icon = Image.open(io.BytesIO(icons[name]))
        x, y = (i % cols) * cell, (i // cols) * cell
        sheet.paste(icon, (x + (cell - icon.width) // 2, y + (cell - icon.height) // 2))
    # a couple of gear frames along the bottom
    cogs = build_cog_frames()
    for j, fr in enumerate((0, 4, 8, 12)):
        g = Image.open(io.BytesIO(cogs[fr])).resize((cell, cell), Image.LANCZOS)
        sheet.paste(g, (j * cell, (rows - 1) * cell))
    sheet.save(path)
    print(f"wrote {path}")


if __name__ == "__main__":
    import sys

    if "--preview" in sys.argv:
        contact_sheet(HERE / "assets" / "preview.png")
    else:
        write_all()
        contact_sheet(HERE / "assets" / "preview.png")
