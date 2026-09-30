# SPDX-License-Identifier: BSD-2-Clause-Patent
"""Build the redistributable recovery fonts from Microsoft's open-source Selawik.

Segoe UI, which the recovery normally copies from the builder's own Windows, may not
be redistributed. Selawik is Microsoft's open-source (SIL OFL 1.1) stand-in for it.
This compiles three faces from the Selawik sources at a pinned commit:

    winre-light.ttf      the Light master
    winre-semilight.ttf  interpolated halfway between Light and Regular (Segoe's
                         Semilight weight, 350); the Glyphs source's own Semilight
                         instance comes out identical to Regular
    winre-regular.ttf    the Regular master

Compiling changes the format, so under the OFL these are Modified Versions and may
not use the Reserved Font Name "Selawik": the family is renamed "S9WoA Sans". The
copyright notice is kept and the license travels with the fonts as OFL.txt (the
recovery builder copies it into the image next to them).

usage:
    git clone https://github.com/microsoft/Selawik <dir>
    pip install fontmake
    python make_fonts.py <dir> [--out <folder>]    (default: this folder)
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

PINNED_COMMIT = "89362e84731d1f5777fa078fd4d5ebbd339e4378"
FAMILY = "S9WoA Sans"
PS_FAMILY = "S9WoASans"
# 2023-06-05, the pinned commit's date: fixed timestamps make the fonts reproducible.
EPOCH = "1685923200"

DESIGNSPACE = """<?xml version="1.0" encoding="UTF-8"?>
<designspace format="4.1">
  <axes><axis tag="wght" name="Weight" minimum="300" default="400" maximum="400"/></axes>
  <sources>
    <source filename="{ufo}/Selawik-Light.ufo" name="Light"><location><dimension name="Weight" xvalue="300"/></location></source>
    <source filename="{ufo}/Selawik-Regular.ufo" name="Regular"><location><dimension name="Weight" xvalue="400"/></location></source>
  </sources>
  <instances>
    <instance familyname="Selawik" stylename="Semilight" filename="Selawik-Semilight.ufo">
      <location><dimension name="Weight" xvalue="350"/></location>
    </instance>
  </instances>
</designspace>
"""


def fontmake(args: list[str], cwd: Path) -> None:
    env = dict(os.environ, SOURCE_DATE_EPOCH=EPOCH)
    result = subprocess.run([sys.executable, "-m", "fontmake", *args, "-o", "ttf", "--no-autohint",
                             "--output-dir", str(cwd)], cwd=cwd, env=env, capture_output=True, text=True)
    if result.returncode != 0:
        raise SystemExit(f"fontmake failed:\n{result.stdout[-2000:]}{result.stderr[-2000:]}")


def rename(src: Path, dest: Path, style: str, weight: int) -> None:
    from fontTools.ttLib import TTFont

    font = TTFont(src, recalcTimestamp=False)
    name = font["name"]
    family = FAMILY if style == "Regular" else f"{FAMILY} {style}"
    for name_id in (1, 2, 3, 4, 6, 16, 17, 21, 22):
        name.removeNames(nameID=name_id)
    values = {
        1: family,
        2: "Regular",
        3: f"{PS_FAMILY}-{style}; derived from Selawik ({PINNED_COMMIT[:7]})",
        4: family,
        6: f"{PS_FAMILY}-{style}",
        10: "Compiled from the Selawik sources by the exynos9810-woa project; renamed per the SIL OFL.",
        13: "This Font Software is licensed under the SIL Open Font License, Version 1.1.",
        14: "https://openfontlicense.org",
        16: FAMILY,
        17: style,
    }
    for name_id, value in values.items():
        name.setName(value, name_id, 3, 1, 0x409)
        name.setName(value, name_id, 1, 0, 0)
    font["OS/2"].usWeightClass = weight
    font["OS/2"].achVendID = "NONE"
    font["head"].created = font["head"].modified = int(EPOCH) + 2082844800  # seconds since 1904
    leftover = [r.nameID for r in name.names if "selawik" in r.toUnicode().lower() and r.nameID not in (0, 3, 7, 10)]
    if leftover:
        raise SystemExit(f"{dest.name}: name IDs {leftover} still use the reserved name")
    font.save(dest)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("selawik", type=Path, help="a clone of https://github.com/microsoft/Selawik")
    parser.add_argument("--out", type=Path, default=Path(__file__).resolve().parent)
    args = parser.parse_args()

    commit = subprocess.run(["git", "-C", str(args.selawik), "rev-parse", "HEAD"],
                            capture_output=True, text=True).stdout.strip()
    if commit != PINNED_COMMIT:
        raise SystemExit(f"Selawik is at {commit or '?'}; check out {PINNED_COMMIT} first.")
    ufo = (args.selawik / "Source files" / "UFO").resolve()
    args.out.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="s9woa-fonts-") as tmp:
        work = Path(tmp)
        fontmake(["-u", str(ufo / "Selawik-Light.ufo")], work)
        fontmake(["-u", str(ufo / "Selawik-Regular.ufo")], work)
        ds = work / "semilight.designspace"
        ds.write_text(DESIGNSPACE.format(ufo=ufo.as_posix()), encoding="utf-8")
        fontmake(["-m", str(ds), "-i"], work)
        for style, weight in (("Light", 300), ("Semilight", 350), ("Regular", 400)):
            rename(work / f"Selawik-{style}.ttf", args.out / f"winre-{style.lower()}.ttf", style, weight)
    shutil.copyfile(args.selawik / "LICENSE.txt", args.out / "OFL.txt")
    for f in sorted(args.out.glob("winre-*.ttf")):
        print(f.name, f.stat().st_size)
    return 0


if __name__ == "__main__":
    sys.exit(main())
