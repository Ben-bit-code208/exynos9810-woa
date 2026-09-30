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

    python mkassets.py            # writes assets/images/*.png, assets/stock/*.png and preview
    python mkassets.py --preview  # only the contact sheet
    python mkassets.py --bars     # only the progress bars (sweep + install bar)
    python mkassets.py --stock    # only the stock-image replacements
    python mkassets.py --stock-preview   # review sheet of those in work/

assets/stock/ holds an original replacement for every bitmap in the stock TWRP
theme, under the stock names and sizes, so TWRP's own pages pick up the WinRE
look without their layouts changing (see build_stock).

The UpdateOS gear GIF is never read here and nothing from it is committed. If a
builder is given the user's own copy of it at build time, build.py (and the C#
installer) render its frames into the image being built instead of the
procedural gears below.

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

WHITE = 255
ICON_PX = 96        # tile icons
BACK_PX = 56        # the small top-left back arrow

# --------------------------------------------------------------------------
# Tile icons
# --------------------------------------------------------------------------
# Every icon is drawn on a 32-unit grid (like a 32 px icon font em) with one
# stroke weight, round caps and round joins, then rendered 8x oversize and
# shrunk, so strokes stay even and edges clean at the 1.33x the panel draws
# them at. Closed shapes (shield, box, drive, wrench, bug body) are drawn as a
# filled silhouette and outlined at exactly the stroke weight from a distance
# field, so curves, corners and unions all keep the same line width. Content
# sits inside units 3..29, matching the visual size of a WinRE glyph.

GRID = 32.0
WEIGHT = 1.5        # stroke width in grid units (4.5 px at 96, 2.6 px at 56)
ISS = 8             # icon supersample


class Pen:
    """Draws on an oversize canvas in grid units."""

    def __init__(self, px: int, weight: float | None = None):
        self.n = px * ISS
        self.k = self.n / GRID
        self.w = (WEIGHT if weight is None else weight) * self.k
        self.strokes = Image.new("L", (self.n, self.n), 0)
        self.d = ImageDraw.Draw(self.strokes)
        self.shapes = Image.new("L", (self.n, self.n), 0)
        self.sd = ImageDraw.Draw(self.shapes)
        self.cut = Image.new("L", (self.n, self.n), 0)
        self.cd = ImageDraw.Draw(self.cut)

    def p(self, x, y):
        return (x * self.k, y * self.k)

    # open strokes -----------------------------------------------------
    def line(self, *pts):
        """A polyline through grid points with round caps and joins."""
        xy = [self.p(x, y) for x, y in pts]
        if len(xy) > 1:
            self.d.line(xy, fill=WHITE, width=round(self.w))
        r = self.w / 2
        for x, y in xy:
            self.d.ellipse([x - r, y - r, x + r, y + r], fill=WHITE)

    def arc(self, cx, cy, r, a0, a1, step=2.0):
        """Clockwise (screen) arc from angle a0 to a1 in degrees; returns its end tangent."""
        count = max(2, int(abs(a1 - a0) / step) + 1)
        pts = []
        for i in range(count + 1):
            a = math.radians(a0 + (a1 - a0) * i / count)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
        self.line(*pts)
        a = math.radians(a1)
        sign = 1 if a1 >= a0 else -1
        return pts[-1], (-math.sin(a) * sign, math.cos(a) * sign)

    def head(self, tip, direction, size=5.0, spread=45.0):
        """An open arrowhead whose point is at tip, pointing along direction."""
        dx, dy = direction
        ln = math.hypot(dx, dy)
        dx, dy = dx / ln, dy / ln
        arms = []
        for s in (spread, -spread):
            a = math.radians(s)
            bx = -dx * math.cos(a) + dy * math.sin(a)
            by = -dx * math.sin(a) - dy * math.cos(a)
            arms.append((tip[0] + bx * size, tip[1] + by * size))
        self.line(arms[0], tip, arms[1])

    def dot(self, x, y, r):
        cx, cy = self.p(x, y)
        rr = r * self.k
        self.d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=WHITE)

    def fill_poly(self, *pts):
        """A solid polygon on the stroke layer (not outlined)."""
        self.d.polygon([self.p(x, y) for x, y in pts], fill=WHITE)

    # outlined silhouettes ----------------------------------------------
    def poly(self, *pts):
        self.sd.polygon([self.p(x, y) for x, y in pts], fill=WHITE)

    def ellipse(self, cx, cy, rx, ry=None):
        ry = rx if ry is None else ry
        self.sd.ellipse([*self.p(cx - rx, cy - ry), *self.p(cx + rx, cy + ry)], fill=WHITE)

    def rrect(self, x0, y0, x1, y1, r=0.0):
        self.sd.rounded_rectangle([*self.p(x0, y0), *self.p(x1, y1)], radius=r * self.k, fill=WHITE)

    def unshape(self, *pts):
        """Cut a polygon out of the silhouette (e.g. a wrench jaw)."""
        self.sd.polygon([self.p(x, y) for x, y in pts], fill=0)

    def un_ellipse(self, cx, cy, r):
        """Cut a disc out of the silhouette; the outline then rings the hole too."""
        self.sd.ellipse([*self.p(cx - r, cy - r), *self.p(cx + r, cy + r)], fill=0)

    def clear_circle(self, x, y, r):
        """Keep everything drawn so far out of a disc (room for a badge)."""
        cx, cy = self.p(x, y)
        rr = r * self.k
        self.cd.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=WHITE)

    def badge_later(self) -> "_Overlay":
        """A stroke layer drawn over the cut, for a badge's own lines."""
        return _Overlay(self)


class _Overlay:
    """Strokes for a badge: drawn after the cut so they survive it."""

    def __init__(self, pen: Pen):
        self.pen = pen
        self.layer = Image.new("L", (pen.n, pen.n), 0)
        self.d = ImageDraw.Draw(self.layer)

    def line(self, *pts):
        saved = self.pen.d
        self.pen.d = self.d
        try:
            self.pen.line(*pts)
        finally:
            self.pen.d = saved

    def arc(self, *args, **kw):
        saved = self.pen.d
        self.pen.d = self.d
        try:
            return self.pen.arc(*args, **kw)
        finally:
            self.pen.d = saved


def _compose(pen: Pen, overlays: list[_Overlay], px: int) -> bytes:
    """Outline the silhouettes at the stroke weight, add strokes and badges, and shrink."""
    small = _compose_mask(pen, overlays, px)
    rgb = Image.merge("RGB", (small, small, small))
    buf = io.BytesIO()
    rgb.save(buf, "PNG", optimize=True)
    return buf.getvalue()


def _compose_mask(pen: Pen, overlays: list[_Overlay], px: int) -> Image.Image:
    """The shrunk coverage mask behind _compose (8-bit, white = ink)."""
    import numpy as np
    from scipy import ndimage

    shape = np.asarray(pen.shapes) > 127
    if shape.any():
        half = pen.w / 2
        inside = ndimage.distance_transform_edt(shape)
        outside = ndimage.distance_transform_edt(~shape)
        outline = (shape & (inside <= half)) | (~shape & (outside <= half))
    else:
        outline = shape  # no silhouettes: nothing to outline (an all-foreground distance field is meaningless)
    art = np.maximum(np.asarray(pen.strokes), np.where(outline, 255, 0).astype(np.uint8))
    art[np.asarray(pen.cut) > 127] = 0
    for ov in overlays:
        art = np.maximum(art, np.asarray(ov.layer))
    return Image.fromarray(art, "L").resize((px, px), Image.LANCZOS)

# --------------------------------------------------------------------------
# individual icons, in grid units
# --------------------------------------------------------------------------

def ic_arrow_right(pen: Pen):
    pen.line((4, 16), (28, 16))
    pen.head((28, 16), (1, 0), size=8)


def ic_arrow_left(pen: Pen):
    pen.line((28, 16), (4, 16))
    pen.head((4, 16), (-1, 0), size=8)


def ic_restart(pen: Pen):
    """Restart: a clockwise circle whose arrowhead points the way it turns."""
    end, tangent = pen.arc(16, 17, 10.5, -35, 270)   # from 2 o'clock, round the bottom, to 12
    pen.head((end[0] + 0.6, end[1]), tangent, size=5.5)


def ic_power(pen: Pen):
    pen.arc(16, 17.5, 10.5, -58, 238)                  # ring open at the top
    pen.line((16, 3.5), (16, 15))


def ic_wrench(pen: Pen):
    """An open-end wrench, head up-right, as the 'troubleshoot' tile."""
    c = math.sqrt(0.5)
    hx, hy = 20.5, 11.5                                # head centre
    ax, ay = -c, c                                     # axis towards the handle (down-left)
    vx, vy = c, c                                      # perpendicular

    def at(u, v):
        return (hx + ax * u + vx * v, hy + ay * u + vy * v)

    pen.ellipse(hx, hy, 6.8)
    pen.poly(at(3, -2.3), at(18, -2.3), at(18, 2.3), at(3, 2.3))
    ex, ey = at(18, 0)
    pen.ellipse(ex, ey, 2.3)
    # the jaw opens away from the handle
    pen.unshape(at(0.8, -2.1), at(-9, -2.1), at(-9, 2.1), at(0.8, 2.1))


def ic_magnifier(pen: Pen):
    pen.ellipse(13.5, 13.5, 8.5)
    pen.line((20.2, 20.2), (27.5, 27.5))


def ic_trash(pen: Pen):
    pen.line((5, 8), (27, 8))
    pen.line((12, 8), (12.5, 4.5), (19.5, 4.5), (20, 8))
    pen.line((8, 8), (9.5, 28), (22.5, 28), (24, 8))
    pen.line((13.5, 13), (13.5, 23))
    pen.line((18.5, 13), (18.5, 23))


def ic_save(pen: Pen):
    """A floppy disk: outline with a clipped corner, shutter and label."""
    pen.poly((5, 5), (22.5, 5), (27, 9.5), (27, 27), (5, 27))
    pen.line((10.5, 5.5), (10.5, 11), (20.5, 11), (20.5, 5.5))
    pen.line((10, 26.5), (10, 18.5), (22, 18.5), (22, 26.5))


def ic_terminal(pen: Pen):
    """A console window with a >_ prompt."""
    pen.rrect(3, 6, 29, 26, r=1.2)
    pen.line((8.5, 12), (13, 16), (8.5, 20))
    pen.line((15.5, 20.5), (23, 20.5))


def ic_bug(pen: Pen):
    pen.ellipse(16, 19.5, 6.5, 8)
    pen.ellipse(16, 10.5, 4)
    pen.line((16, 14.5), (16, 27))
    for side in (-1, 1):
        s = lambda x: 16 + side * x  # noqa: E731
        pen.line((s(6.8), 15.5), (s(11.5), 12.5))
        pen.line((s(6.8), 20), (s(12), 20))
        pen.line((s(6.4), 24.5), (s(11), 28))
        pen.line((s(2.6), 7.2), (s(5), 3.5))


def ic_download(pen: Pen):
    pen.line((16, 4), (16, 20))
    pen.head((16, 20), (0, 1), size=7.5)
    pen.line((5, 21.5), (5, 27.5), (27, 27.5), (27, 21.5))


def _shield_points():
    pts = [(16, 3.5), (27, 7.5), (27, 15)]
    # right flank curving down to the point, then back up the left
    for i in range(1, 13):
        t = i / 12
        x = (1 - t) ** 2 * 27 + 2 * (1 - t) * t * 26 + t * t * 16
        y = (1 - t) ** 2 * 15 + 2 * (1 - t) * t * 24 + t * t * 29
        pts.append((x, y))
    for x, y in reversed(pts[3:-1]):
        pts.append((32 - x, y))
    pts += [(5, 15), (5, 7.5)]
    return pts


def ic_ticket(pen: Pen):
    """A shield with a tick: clear what keeps the phone out of Windows."""
    pen.poly(*_shield_points())
    pen.line((10.5, 16.5), (14.5, 20.5), (21.5, 12.5))


def _drive(pen: Pen, y0: float, y1: float):
    pen.rrect(3, y0, 29, y1, r=1.5)
    pen.dot(24, (y0 + y1) / 2, 1.3)
    pen.line((7.5, (y0 + y1) / 2), (15.5, (y0 + y1) / 2))


def ic_partitions(pen: Pen):
    """Two stacked drives: the boot partitions."""
    _drive(pen, 5, 14)
    _drive(pen, 18, 27)


def ic_drive_check(pen: Pen) -> list[_Overlay]:
    """A drive with a tick badge: repair the Windows volume."""
    _drive(pen, 5, 16.5)
    pen.clear_circle(22, 22.5, 8.2)
    badge = pen.badge_later()
    badge.arc(22, 22.5, 6.2, 0, 360, step=3)
    badge.line((19, 22.8), (21.3, 25), (25.3, 20.3))
    return [badge]


def ic_tools(pen: Pen):
    """Four tiles: the full TWRP interface with all its tools."""
    for x0, y0 in ((4, 4), (18, 4), (4, 18), (18, 18)):
        pen.rrect(x0, y0, x0 + 10, y0 + 10, r=1.5)


ICONS = {
    "winre_ic_continue": (ic_arrow_right, ICON_PX),
    "winre_ic_back": (ic_arrow_left, BACK_PX),
    "winre_ic_recovery": (ic_restart, ICON_PX),
    "winre_ic_poweroff": (ic_power, ICON_PX),
    "winre_ic_troubleshoot": (ic_wrench, ICON_PX),
    "winre_ic_check": (ic_magnifier, ICON_PX),
    "winre_ic_erase": (ic_trash, ICON_PX),
    "winre_ic_save": (ic_save, ICON_PX),
    "winre_ic_debug": (ic_terminal, ICON_PX),
    "winre_ic_bug": (ic_bug, ICON_PX),
    "winre_ic_download": (ic_download, ICON_PX),
    "winre_ic_ticket": (ic_ticket, ICON_PX),
    "winre_ic_disk": (ic_partitions, ICON_PX),
    "winre_ic_repair": (ic_drive_check, ICON_PX),
    "winre_ic_tools": (ic_tools, ICON_PX),
}


def build_icons() -> dict[str, bytes]:
    out: dict[str, bytes] = {}
    for name, (fn, px) in ICONS.items():
        pen = Pen(px)
        overlays = fn(pen)
        out[name] = _compose(pen, overlays or [], px)
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
# Windows palette shared by the bars and the stock-page replacements
# --------------------------------------------------------------------------
WIN_BLUE = (0x00, 0x78, 0xD4)      # accent: sliders, checkboxes, progress, selection
WIN_BLUE_DARK = (0x00, 0x5A, 0x9E)  # the accent one step darker (slider trail)
ACCENT_TEXT = (0x60, 0xCD, 0xFF)   # the accent as marks and text on black
CARD = (0x2B, 0x2B, 0x2B)          # flat buttons and tiles
SLIDER = (0x26, 0x26, 0x26)        # swipe-to-confirm track
TRACK = (0x3A, 0x3A, 0x3A)         # empty progress track
INK = (0xD6, 0xD6, 0xD6)           # line glyphs in the navigation bar and lists
SPACE_INK = (0x9A, 0x9A, 0x9A)
BLACK = (0, 0, 0)
WHITE_RGB = (255, 255, 255)


def _png(im: Image.Image) -> bytes:
    buf = io.BytesIO()
    im.save(buf, "PNG", optimize=True)
    return buf.getvalue()


# --------------------------------------------------------------------------
# Progress bars: the indeterminate "Copying Windows" sweep and the
# determinate bar the install page binds to the installer's percentage
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
        im = Image.new("RGB", (W, H), BLACK)
        d = ImageDraw.Draw(im)
        d.rounded_rectangle([0, 0, W - 1, H - 1], radius=radius, fill=(BAR_TRACK,) * 3)
        d.rounded_rectangle([x0, 0, x0 + seg - 1, H - 1], radius=radius, fill=WIN_BLUE)
        out.append(_png(im.resize((w, h), Image.LANCZOS)))
    if len(set(out)) != len(out):
        raise SystemExit("bar frames repeat: the sweep has stalled frames")
    return out


def build_install_bar() -> dict[str, bytes]:
    """Empty and full faces of the determinate install bar (same geometry as the sweep)."""
    W, H = BAR_W * BAR_SS, BAR_H * BAR_SS
    out: dict[str, bytes] = {}
    for name, fill in (("winre_pbar_empty", (BAR_TRACK,) * 3), ("winre_pbar_full", WIN_BLUE)):
        im = Image.new("RGB", (W, H), BLACK)
        ImageDraw.Draw(im).rounded_rectangle([0, 0, W - 1, H - 1], radius=H // 2, fill=fill)
        out[name] = _png(im.resize((BAR_W, BAR_H), Image.LANCZOS))
    return out


# --------------------------------------------------------------------------
# Replacements for every stock TWRP bitmap (original art, same names, sizes)
# --------------------------------------------------------------------------
# TWRP's own pages keep their layouts and simply find these images under the
# stock names, so install, wipe, backup, the keyboard and the rest pick up the
# WinRE look without their page logic being rewritten. Canvas sizes, and the
# insets of the button faces inside them, match the stock files exactly
# because the stock layouts are built around them. Nothing here is derived
# from TeamWin's bitmaps: every shape is drawn from the geometry below.
#
# Two encodings, chosen per image by what TWRP draws it on:
#   * OPAQUE RGB for images that only ever land on the black page background
#     (tiles, tracks, the navigation bar): the rule at the top of this file.
#   * STRAIGHT ALPHA carrying the ink colour in EVERY pixel's RGB, the fully
#     transparent ones included, for images TWRP draws over something else:
#     keyboard glyphs sit on the key colour, sort arrows on a button face, the
#     indeterminate sweep on the progress bar, list icons under the pressed-row
#     highlight. With uniform RGB, an upscaler that blends RGB without weighting
#     by alpha has nothing to smear - only coverage is interpolated. (A shape
#     whose RGB is black under a white alpha mask is what gets destroyed.)
STOCK = HERE / "assets" / "stock"
GEO_SS = 4


def _layers(w: int, h: int, layers, *, opaque: bool, bleed=WHITE_RGB) -> bytes:
    """Composite full-size (mask, colour) layers, straight-alpha "over", into a PNG."""
    import numpy as np

    rgb = np.empty((h, w, 3), np.float64)
    rgb[...] = bleed
    a = np.zeros((h, w), np.float64)
    for mask, colour in layers:
        m = np.asarray(mask, np.float64) / 255.0
        out_a = m + a * (1.0 - m)
        num = np.asarray(colour, np.float64) * m[..., None] + rgb * (a * (1.0 - m))[..., None]
        covered = out_a > 0
        rgb = np.where(covered[..., None], num / np.where(covered, out_a, 1.0)[..., None], rgb)
        a = out_a
    if opaque:
        px = np.clip(np.rint(rgb * a[..., None]), 0, 255).astype(np.uint8)
        return _png(Image.fromarray(px, "RGB"))
    px = np.dstack([np.clip(np.rint(rgb), 0, 255),
                    np.clip(np.rint(a * 255.0), 0, 255)]).astype(np.uint8)
    return _png(Image.fromarray(px, "RGBA"))


def _geo(w: int, h: int, draw) -> Image.Image:
    """A full-size coverage mask from ImageDraw geometry, box-filtered from 4x."""
    big = Image.new("L", (w * GEO_SS, h * GEO_SS), 0)
    draw(ImageDraw.Draw(big), GEO_SS)
    return big.resize((w, h), Image.BOX)


def _rrect(w, h, box, r, outline: float = 0):
    x0, y0, x1, y1 = box

    def draw(d, s):
        xy = [x0 * s, y0 * s, x1 * s - 1, y1 * s - 1]
        if outline:
            d.rounded_rectangle(xy, radius=r * s, outline=255, width=round(outline * s))
        else:
            d.rounded_rectangle(xy, radius=r * s, fill=255)
    return _geo(w, h, draw)


def _circle(w, h, cx, cy, radius, outline: float = 0):
    def draw(d, s):
        xy = [(cx - radius) * s, (cy - radius) * s, (cx + radius) * s - 1, (cy + radius) * s - 1]
        if outline:
            d.ellipse(xy, outline=255, width=round(outline * s))
        else:
            d.ellipse(xy, fill=255)
    return _geo(w, h, draw)


def _glyph(w, h, fn, px, cx, cy, weight: float | None = None) -> Image.Image:
    """A 32-grid Pen glyph rendered at px and centred at (cx, cy) on a w x h mask."""
    pen = Pen(px, weight)
    overlays = fn(pen) or []
    small = _compose_mask(pen, overlays, px)
    mask = Image.new("L", (w, h), 0)
    mask.paste(small, (round(cx - px / 2), round(cy - px / 2)))
    return mask


def _blank(w, h) -> bytes:
    return _png(Image.new("RGBA", (w, h), (255, 255, 255, 0)))


# ---- glyphs for the stock replacements, on the same 32-unit grid ----------

def gl_home(pen: Pen):
    pen.line((4.5, 15.5), (16, 5.5), (27.5, 15.5))
    pen.line((8, 12.5), (8, 27), (24, 27), (24, 12.5))
    pen.line((13.5, 27), (13.5, 20.5), (18.5, 20.5), (18.5, 27))


def _keyboard(pen: Pen, y0: float):
    pen.rrect(3, y0, 29, y0 + 15, r=1.5)
    for row, y in enumerate((y0 + 4.2, y0 + 7.8)):
        for x in (8.5, 12.5, 16.5, 20.5, 24.5) if row == 0 else (10.5, 14.5, 18.5, 22.5):
            pen.dot(x, y, 0.95)
    pen.line((11, y0 + 11.3), (21, y0 + 11.3))


def gl_kb_hide(pen: Pen):
    _keyboard(pen, 4)
    pen.line((12.5, 23.5), (16, 27), (19.5, 23.5))


def gl_kb_show(pen: Pen):
    pen.line((12.5, 8.5), (16, 5), (19.5, 8.5))
    _keyboard(pen, 13)


def gl_chevron(direction: str):
    pts = {"down": ((8, 12), (16, 20), (24, 12)), "up": ((8, 20), (16, 12), (24, 20)),
           "left": ((20, 8), (12, 16), (20, 24)), "right": ((12, 8), (20, 16), (12, 24))}[direction]

    def draw(pen: Pen):
        pen.line(*pts)
    return draw


def gl_backspace(pen: Pen):
    pen.poly((11, 7), (28, 7), (28, 25), (11, 25), (3.5, 16))
    pen.line((15.5, 12), (23.5, 20))
    pen.line((23.5, 12), (15.5, 20))


def gl_enter(pen: Pen):
    pen.line((26, 6), (26, 18), (7, 18))
    pen.head((7, 18), (-1, 0), size=6)


SHIFT_ARROW = ((16, 3.5), (28.5, 16), (21, 16), (21, 27.5), (11, 27.5), (11, 16), (3.5, 16))


def gl_shift(pen: Pen):
    pen.poly(*SHIFT_ARROW)


def gl_shift_fill(pen: Pen):
    pen.poly(*SHIFT_ARROW)
    pen.fill_poly(*SHIFT_ARROW)


def gl_settings(pen: Pen):
    pen.poly(*_gear_polygon(16, 16, 11.2, 3.6, 8, math.pi / 8))
    pen.un_ellipse(16, 16, 4.2)


def gl_clock(pen: Pen):
    pen.ellipse(16, 16, 12.5)
    pen.line((16, 8.5), (16, 16), (21.5, 19.5))


def gl_brightness(pen: Pen):
    pen.ellipse(16, 16, 5.5)
    for i in range(8):
        a = i * math.pi / 4
        pen.line((16 + 9.2 * math.cos(a), 16 + 9.2 * math.sin(a)),
                 (16 + 13 * math.cos(a), 16 + 13 * math.sin(a)))


def gl_vibrate(pen: Pen):
    pen.rrect(10.5, 4, 21.5, 28, r=1.8)
    pen.line((6.5, 10), (6.5, 22))
    pen.line((25.5, 10), (25.5, 22))
    pen.line((2.8, 13), (2.8, 19))
    pen.line((29.2, 13), (29.2, 19))


def gl_globe(pen: Pen):
    pen.ellipse(16, 16, 12.5)
    meridian = [(16 + 5.5 * math.cos(t), 16 + 12.5 * math.sin(t))
                for t in (i * 2 * math.pi / 72 for i in range(73))]
    pen.line(*meridian)
    pen.line((3.5, 16), (28.5, 16))
    for y in (10, 22):
        half = math.sqrt(12.5 ** 2 - (y - 16) ** 2) - 1.2
        pen.line((16 - half, y), (16 + half, y))


def gl_file(pen: Pen):
    pen.poly((7, 3.5), (19, 3.5), (25, 9.5), (25, 28.5), (7, 28.5))
    pen.line((19, 4), (19, 10), (24.5, 10))


def gl_folder(pen: Pen):
    pen.poly((3.5, 7), (12.5, 7), (15.5, 10), (28.5, 10), (28.5, 26), (3.5, 26))


def gl_folder_check(pen: Pen):
    gl_folder(pen)
    pen.line((11, 18), (14.5, 21.5), (21.5, 14.5))


def gl_check(pen: Pen):
    pen.line((7, 16.5), (13, 22.5), (25, 10.5))


def gl_double_chevron(pen: Pen):
    pen.line((8, 9), (15, 16), (8, 23))
    pen.line((17, 9), (24, 16), (17, 23))


def build_stock() -> dict[str, bytes]:
    """Every stock TWRP image, redrawn: name -> PNG bytes (57 images)."""
    out: dict[str, bytes] = {}

    def opaque(name, w, h, *layers):
        out[name] = _layers(w, h, layers, opaque=True)

    def alpha(name, w, h, bleed, *layers):
        out[name] = _layers(w, h, layers, opaque=False, bleed=bleed)

    # Flat tiles and buttons: the faces sit at the stock insets.
    opaque("main_button", 504, 288, (_rrect(504, 288, (36, 32, 468, 256), 6), CARD))
    opaque("main_button_half_height", 504, 192, (_rrect(504, 192, (36, 32, 468, 160), 6), CARD))
    opaque("main_button_half_height_full_width", 1008, 192,
           (_rrect(1008, 192, (36, 32, 972, 160), 6), CARD))
    opaque("tab_3", 312, 128, (_rrect(312, 128, (0, 32, 312, 96), 4), CARD))
    opaque("tab_4", 225, 128, (_rrect(225, 128, (0, 32, 225, 96), 4), CARD))

    # Navigation bar and keyboard toggles (always on the black navbar).
    for name, fn in (("back", ic_arrow_left), ("home", gl_home), ("console", ic_terminal),
                     ("kb_hide", gl_kb_hide), ("kb_show", gl_kb_show)):
        opaque(name, 265, 128, (_glyph(265, 128, fn, 52, 132.5, 64, weight=1.8), INK))

    # Progress: a thin Windows bar; the sweep is drawn over it, so it is alpha.
    opaque("progress_empty", 1008, 64, (_rrect(1008, 64, (0, 28, 1008, 36), 4), TRACK))
    opaque("progress_fill", 1008, 64, (_rrect(1008, 64, (0, 28, 1008, 36), 4), WIN_BLUE))
    seg, span = 216, 1008 + 216
    for k in range(12):
        x0 = round(-seg + (k + 0.5) * span / 12)
        alpha(f"indeterminate{k + 1:03d}", 1008, 64, ACCENT_TEXT,
              (_rrect(1008, 64, (x0, 28, x0 + seg, 36), 4), ACCENT_TEXT))

    # Swipe to confirm: dark track, darker-blue trail, blue handle with >>.
    opaque("slider", 936, 192, (_rrect(936, 192, (0, 32, 936, 160), 6), SLIDER))
    opaque("slider_used", 936, 192, (_rrect(936, 192, (0, 32, 936, 160), 6), WIN_BLUE_DARK))
    opaque("slider_touch", 288, 192, (_rrect(288, 192, (0, 32, 288, 160), 6), WIN_BLUE),
           (_glyph(288, 192, gl_double_chevron, 72, 144, 96, weight=2.2), WHITE_RGB))

    # Check boxes, radio buttons and the slider thumb / list bullet.
    alpha("checkbox_false", 72, 96, INK, (_rrect(72, 96, (3, 24, 51, 72), 6, outline=4), INK))
    alpha("checkbox_true", 72, 96, WIN_BLUE, (_rrect(72, 96, (3, 24, 51, 72), 6), WIN_BLUE),
          (_glyph(72, 96, gl_check, 48, 27, 48, weight=2.6), WHITE_RGB))
    alpha("radio_false", 72, 96, INK, (_circle(72, 96, 27, 48, 24, outline=4), INK))
    alpha("radio_true", 72, 96, WIN_BLUE, (_circle(72, 96, 27, 48, 24, outline=4), WIN_BLUE),
          (_circle(72, 96, 27, 48, 11), WIN_BLUE))
    alpha("handle", 72, 108, WIN_BLUE, (_circle(72, 108, 36, 54, 22), WIN_BLUE))

    # File-manager icons and the floating "select folder" button.
    alpha("file", 72, 96, INK, (_glyph(72, 96, gl_file, 54, 27, 48), INK))
    alpha("folder", 72, 96, INK, (_glyph(72, 96, gl_folder, 54, 27, 48), INK))
    alpha("fab_selectfolder", 216, 192, WIN_BLUE, (_rrect(216, 192, (36, 32, 180, 160), 10), WIN_BLUE),
          (_glyph(216, 192, gl_folder_check, 80, 108, 96, weight=1.7), WHITE_RGB))

    # Sort arrows sit on a tab_3 face, at the stock positions.
    alpha("sort_asc", 312, 128, WHITE_RGB, (_glyph(312, 128, gl_chevron("up"), 36, 36, 64, weight=2.4), WHITE_RGB))
    alpha("sort_desc", 312, 128, WHITE_RGB, (_glyph(312, 128, gl_chevron("down"), 36, 276, 64, weight=2.4), WHITE_RGB))
    out["sort_empty"] = _blank(312, 128)

    # Settings tabs.
    for name, fn in (("tab_general", gl_settings), ("tab_timezone", gl_clock),
                     ("tab_display", gl_brightness), ("tab_vibration", gl_vibrate),
                     ("tab_language", gl_globe)):
        alpha(name, 216, 96, WHITE_RGB, (_glyph(216, 96, fn, 60, 108, 48, weight=1.6), WHITE_RGB))

    # Keyboard glyphs sit on the key colour.
    for name, fn in (("backspace", gl_backspace), ("enter", gl_enter),
                     ("shift", gl_shift), ("shift_fill", gl_shift_fill)):
        alpha(name, 162, 161, WHITE_RGB, (_glyph(162, 161, fn, 64, 81, 80.5, weight=1.6), WHITE_RGB))

    def space(d, s):
        d.line([(214 * s, 92 * s), (214 * s, 104 * s), (326 * s, 104 * s), (326 * s, 92 * s)],
               fill=255, width=4 * s, joint="curve")
    alpha("space", 540, 161, SPACE_INK, (_geo(540, 161, space), SPACE_INK))
    for direction in ("left", "right", "up", "down"):
        alpha(f"kb_arrow_{direction}", 154, 96, WHITE_RGB,
              (_glyph(154, 96, gl_chevron(direction), 40, 77, 48, weight=2.2), WHITE_RGB))

    # Mouse pointer (only drawn when a mouse is attached).
    pointer = [(3, 2), (3, 35), (11, 28), (17, 41), (23, 38), (17, 25), (27, 25)]

    def pointer_fill(d, s):
        d.polygon([(x * s, y * s) for x, y in pointer], fill=255)

    def pointer_edge(d, s):
        d.polygon([(x * s, y * s) for x, y in pointer], outline=255, width=2 * s)
    alpha("cursor", 48, 48, WHITE_RGB, (_geo(48, 48, pointer_fill), WHITE_RGB), (_geo(48, 48, pointer_edge), BLACK))

    # The TeamWin logos and the old lock glyph are never drawn by the WinRE
    # theme; they are replaced with empty images so nothing of them can show.
    out["logo"] = _blank(184, 256)
    out["unlock_icon"] = _blank(344, 450)
    out["splashlogo"] = _blank(500, 500)
    out["splashteamwin"] = _blank(708, 96)
    return out


# Canvas sizes of the stock TWRP 3.7.0_9-0 images the replacements must match.
STOCK_SIZES = {
    "back": (265, 128), "home": (265, 128), "console": (265, 128), "kb_hide": (265, 128), "kb_show": (265, 128),
    "backspace": (162, 161), "enter": (162, 161), "shift": (162, 161), "shift_fill": (162, 161), "space": (540, 161),
    "checkbox_false": (72, 96), "checkbox_true": (72, 96), "radio_false": (72, 96), "radio_true": (72, 96),
    "file": (72, 96), "folder": (72, 96), "handle": (72, 108), "cursor": (48, 48), "fab_selectfolder": (216, 192),
    "progress_empty": (1008, 64), "progress_fill": (1008, 64),
    **{f"indeterminate{i:03d}": (1008, 64) for i in range(1, 13)},
    "kb_arrow_down": (154, 96), "kb_arrow_left": (154, 96), "kb_arrow_right": (154, 96), "kb_arrow_up": (154, 96),
    "logo": (184, 256), "main_button": (504, 288), "main_button_half_height": (504, 192),
    "main_button_half_height_full_width": (1008, 192), "slider": (936, 192), "slider_used": (936, 192),
    "slider_touch": (288, 192), "sort_asc": (312, 128), "sort_desc": (312, 128), "sort_empty": (312, 128),
    "splashlogo": (500, 500), "splashteamwin": (708, 96), "tab_3": (312, 128), "tab_4": (225, 128),
    "tab_display": (216, 96), "tab_general": (216, 96), "tab_language": (216, 96), "tab_timezone": (216, 96),
    "tab_vibration": (216, 96), "unlock_icon": (344, 450),
}


# --------------------------------------------------------------------------

def write_bars() -> None:
    IMAGES.mkdir(parents=True, exist_ok=True)
    bars = build_bar_frames()
    for i, png in enumerate(bars, 1):
        (IMAGES / f"winrebar{i:03d}.png").write_bytes(png)
    for name, png in build_install_bar().items():
        (IMAGES / f"{name}.png").write_bytes(png)
    print(f"wrote {len(bars)} sweep frames and the determinate install bar to {IMAGES}")


def write_stock() -> None:
    STOCK.mkdir(parents=True, exist_ok=True)
    art = build_stock()
    missing = sorted(set(STOCK_SIZES) - set(art))
    if missing:
        raise SystemExit(f"stock replacements missing: {missing}")
    for name, png in art.items():
        size = Image.open(io.BytesIO(png)).size
        if size != STOCK_SIZES[name]:
            raise SystemExit(f"{name}: drawn {size}, stock is {STOCK_SIZES[name]}")
        (STOCK / f"{name}.png").write_bytes(png)
    print(f"wrote {len(art)} stock replacements to {STOCK} "
          f"({sum(len(v) for v in art.values()):,} B)")


def write_all() -> None:
    IMAGES.mkdir(parents=True, exist_ok=True)
    icons = build_icons()
    for name, png in icons.items():
        (IMAGES / f"{name}.png").write_bytes(png)
    cogs = build_cog_frames()
    for i, png in enumerate(cogs, 1):
        (IMAGES / f"winrecog{i:03d}.png").write_bytes(png)
    print(f"wrote {len(icons)} icons and {len(cogs)} cog frames to {IMAGES}")
    write_bars()
    write_stock()


def stock_sheet(path: Path) -> None:
    """Contact sheet of the stock replacements on a mid-grey, for review."""
    art = build_stock()
    cols, cell_w, cell_h = 6, 280, 220
    rows = (len(art) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * cell_w, rows * cell_h), (40, 40, 40))
    for i, (name, png) in enumerate(sorted(art.items())):
        im = Image.open(io.BytesIO(png)).convert("RGBA")
        im.thumbnail((cell_w - 10, cell_h - 30))
        x, y = (i % cols) * cell_w, (i // cols) * cell_h
        sheet.paste(im, (x + 5, y + 25), im)
        ImageDraw.Draw(sheet).text((x + 5, y + 5), name, fill=(255, 255, 0))
    sheet.save(path)
    print(f"wrote {path}")


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

    flags = set(sys.argv[1:])
    if "--preview" in flags:
        contact_sheet(HERE / "assets" / "preview.png")
    elif flags & {"--stock", "--bars", "--stock-preview"}:
        # Regenerate one group without touching the committed icons.
        if "--bars" in flags:
            write_bars()
        if "--stock" in flags:
            write_stock()
        if "--stock-preview" in flags:
            (HERE / "work").mkdir(exist_ok=True)
            stock_sheet(HERE / "work" / "stock-preview.png")
    else:
        write_all()
        contact_sheet(HERE / "assets" / "preview.png")
