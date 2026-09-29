# WinRE-look TWRP for star2lte

A re-skin of the stock **TWRP 3.7.0_9-0** recovery for the Galaxy S9+
(SM-G965F, `star2lte`, Exynos 9810) that boots into a faithful Windows Recovery
Environment *Choose an option* screen, keeps every TWRP feature reachable, and
adds real repair actions plus an "Installing Windows" screen that comes up while
the installer writes the phone.

**End users never run this tool.** The C# installer
(`installer/src/S9Woa.Installer.Core/Twrp/WinReTwrpBuilder.cs`) does the same
build on the user's own PC, with no Python, from the same committed theme,
scripts and art. This directory is the reference implementation and the place to
iterate on the theme and the art. The two are kept in step by the tests on both
sides.

## Third-party assets — read this first

There is exactly one third-party asset, and it is never committed or
redistributed:

* **Segoe UI Light / Semilight / Regular** are copied from the *builder's own*
  `%WINDIR%\Fonts` at build time (`segoeuil.ttf`, `segoeuisl.ttf`,
  `segoeui.ttf`). They are licensed to the machine that owns Windows and are
  written into the recovery image on that machine. The image is for that
  device; do not redistribute the built image or `ui.zip`.

Everything else is **original**:

* the tile icons and the rotating gears are drawn procedurally by `mkassets.py`
  (no Segoe MDL2 glyphs, no Microsoft UpdateOS gear frames);
* the theme XML and the `/sbin` scripts are authored here;
* the two kernel modules under `modules/` are our own GPL-2.0 code (sources
  included).

The stock TWRP theme's own teal PNGs (the progress bar, slider, checkbox ticks)
are recoloured to Windows blue **at build time** from the base image's own
copies; the result is never committed, so no TeamWin-derived bitmap enters the
repo, only the recolour code.

## Building

```
python mkassets.py                                   # (re)generate the committed art
python build.py --source /path/to/twrp-3.7.0_9-0-star2lte.img --outdir work/out
python validate.py work/out/ui.zip --release
```

`--source` must be the official `twrp-3.7.0_9-0-star2lte.img` from
[twrp.me](https://twrp.me/samsung/samsunggalaxys9plus.html). The build never
rebuilds TWRP: the kernel (bar a four-byte power-off route patch), the DTBH
blob, the boot header and every untouched ramdisk entry are carried across
byte-for-byte; only `/twres`, the `/sbin` helpers, the modules and one line of
`init.recovery.usb.rc` change. If the recovery ever fails to boot, the cause is
in the short `build-report.txt`.

Outputs land in `--outdir`:

| File | What |
|---|---|
| `star2lte-winre-recovery.img` | flashable RECOVERY image |
| `ui.zip` | the same theme as a TWRP custom theme, no flashing needed |
| `build-report.txt` | every file that changed, with sizes |

The recovery partition is 68,149,248 bytes and a WinRE build is ~44 MiB, so
there is no need to subset the fonts; the full faces are copied.

## What the build changes

* **Home** — the WinRE *Choose an option* screen: Continue (to Windows),
  Restart to recovery, Troubleshoot, Turn off your PC.
* **Troubleshoot** — real repair actions, each printing a plain report on a
  WinRE output page:
  * **Repair Windows volume** — unmount `/data`, run `fsck.ntfs` (ntfs-3g's
    `ntfsfix`: fixes fundamentals and schedules Windows' own chkdsk; never
    deletes files), remount.
  * **Repair boot partitions** — `fsck.fat -a` on CACHE and SYSTEM; never
    reformats.
  * **Clear boot ticket** — zero the MISC bootloader control block (the exact
    range `BootRouteService.ClearBootRequestAsync` clears) then `insmod` the
    baked-in `rwd1_ack.ko` and report `/proc/rwd1_ack`.
  * **Advanced options** — Check PRAM, Collect diagnostics, Command Prompt,
    Erase PRAM, Full TWRP interface, Restart to Download mode.
  Every repair/destructive action goes through a WinRE-style confirm page.
* **Reskin** — the stock theme's global colours and fonts are overridden so
  every untouched stock page (install, backup, wipe, mount, settings, terminal,
  file manager, ...) renders black/white with a Windows-blue accent, no TWRP
  orange/teal, without rewriting any page logic.
* **Installing Windows** screen — see below.
* **Splash** — the rotating gears on black, replacing the TeamWin splash.

## The "Installing Windows" screen

`sbin/winre-statuswatch.sh` raises a WinRE "Installing Windows / please wait"
screen (gears, a sweeping bar, a phase caption) while the installer writes the
phone, and clears it when it stops. It has two detectors:

* **Primary — a status file.** The installer writes `/tmp/s9woa/status`
  (`phase=`, `label=`, `percent=`, `done_bytes=`, `total_bytes=`) and rewrites it
  as it goes; the watcher shows the `label` ("Copying Windows", "Writing boot
  files", "Installing firmware") and a coarse percent. A file older than 30 s is
  treated as a crashed installer.
* **Fallback — a dd writer.** If there is no fresh status file but some process
  has `of=/dev/block/` on its command line, a generic screen comes up anyway.

The bar is indeterminate and the percent lives only in the caption, updated at
most every 5 s and only on change: the sole channel from a shell into a TWRP GUI
variable is openrecoveryscript, and every ORS session bounces the display, so a
per-percent update would strobe. All the research safety rules are kept: raise
only from a static menu page, clear unconditionally, `timeout -t 8` around every
`twrp` call, a minimum dwell, and the copy page's Home/Back go via `main` so a
dead watcher can never trap the UI.

## The three "lock" problems

1. **Screen-timeout lock.** `main` sets `tw_screen_timeout_secs=0` at page load
   (the last writer wins, before the blank timer can fire), which disables
   blanking; the stock swipe-to-unlock `lock` page is renamed and replaced by a
   page that dismisses itself. So the recovery never blanks or asks to unlock
   while the installer works.
2. **Stale `ui.zip`.** TWRP loads `/sdcard/TWRP/theme/ui.zip` (i.e.
   `/data/media/TWRP/theme/ui.zip`) in preference to the baked-in theme, and
   that zip is checked *before* `postrecoveryboot.sh` runs, so the hook only
   heals the *next* boot. TWRP 3.7 offers no baked-theme hook that runs first,
   so for the current boot the installer deletes the zip over adb
   (`TwrpClient.RemoveStaleThemeOverrideAsync`); the baked hook covers the
   standalone case.
3. **No `twrp`/ORS on hot paths.** The installer drives TWRP with plain
   `adb shell` and `adb reboot`, never `twrp set` or `openrecoveryscript`, so it
   cannot wedge the GUI or trigger the ORS reboot-to-Android path.

## Device traps encoded here

These are measured on the device (see the research tool's notes), not guessed:

* command output reaches the on-screen console only through
  `function="terminalcommand"`, never `cmd` (log only) or `openrecoveryscript`
  (reboots to Android on success); every command runs through
  `sbin/winre-gui.sh`, which pads the console so the report is not buried under
  the boot log, using `echo " "` (a bare `echo` is dropped by the console);
* every `<animation>` needs `retainaspect="1"` or the gears render ~15.6% too
  tall on the 1440x2960 panel;
* a `<condition var2="">` is evaluated as false and silently disables its block;
* `--` is illegal inside an XML comment;
* busybox 1.22 wants `timeout -t SECS`;
* `setprop ctl.stop recovery` powers the phone off — never use it;
* internal storage is `/data/media`, not `/data/media/0`.

## Layout

```
bootimg.py    boot image split/join, newc cpio round-trip, LZMA-alone helpers
poweroff.py   the four-byte power-off route kernel patch (hash/pattern gated)
mkassets.py   original procedural art: line icons, rotating gears, progress bar
build.py      orchestrator; emits the flashable image, ui.zip and the report
validate.py   parses the built theme and checks refs, escapes, console rules
theme/        winre.xml (the WinRE pages) and splash.xml
overlay/sbin/ winre-gui.sh, winre-pram.sh, winre-debug.sh, winre-actions.sh,
              winre-statuswatch.sh, postrecoveryboot.sh (boot-time self-heal)
assets/       committed original PNGs (icons, gear frames, bar frames)
modules/      rwd1_ack.ko / rwd1_evidence_reader.ko and their GPL-2.0 sources
```
