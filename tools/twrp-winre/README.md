# WinRE-look TWRP for star2lte

A re-skin of the stock **TWRP 3.7.0_9-0** recovery for the Galaxy S9+
(SM-G965F, `star2lte`, Exynos 9810) that boots into a faithful Windows Recovery
Environment *Choose an option* screen, draws **every** TWRP page in the WinRE
look, keeps every TWRP feature reachable, and adds real repair actions plus a
live "Installing Windows" screen while the installer writes the phone.

**End users never run this tool.** The C# installer
(`installer/src/S9Woa.Installer.Core/Twrp/WinReTwrpBuilder.cs`) does the same
build on the user's own PC, with no Python, from the same committed theme,
reskin rules, scripts and art. This directory is the reference implementation
and the place to iterate on the theme and the art. The two are kept in step by
the tests on both sides.

## Third-party assets — read this first

Two Microsoft assets can end up in a built image. Neither is ever committed or
redistributed; both come from the builder's own machine at build time:

* **Segoe UI Light / Semilight / Regular** are copied from the builder's own
  `%WINDIR%\Fonts` (`segoeuil.ttf`, `segoeuisl.ttf`, `segoeui.ttf`).
* **The UpdateOS gear animation** (`UpdateOS-GearAnimation.gif`) is optional.
  Given with `--gears` (or, in the installer, found as `*GearAnimation*.gif` in
  the build folder), its frames are rendered into the image being built
  instead of the procedural gears. The GIF and its frames never touch this
  repository.

Both are licensed to the machine that owns them and are written into that
machine's recovery image. Do not redistribute a built image or `ui.zip`.

Everything else is **original**:

* the tile icons, the procedural gears and bars, and a replacement for **every
  one of the 57 stock TWRP bitmaps** (same names and sizes: tiles, sliders,
  checkboxes, keyboard glyphs, navigation bar, progress bar, ...) are drawn by
  `mkassets.py` and committed under `assets/`. The TeamWin logos are replaced by
  empty images, so no TeamWin bitmap is in the repo or in a built image;
* `theme/winre.xml`, `theme/splash.xml` and the reskin rules in
  `theme/reskin.xml` are written here. The rules only name short anchors in the
  official TWRP XML; no stock theme text is copied into the repo;
* the `/sbin` scripts are written here, and the four kernel modules under
  `modules/` are our own GPL-2.0 code (sources included): `rwd1_ack` and
  `rwd1_evidence_reader` read and acknowledge the recovery record, and
  `rwd1_clear_poc` (token-gated) and `pram_smp_clear_poc` zero the RWD1 and P3
  startup records — judged on every byte, since any non-zero P3 content halts
  UEFI at the Samsung logo. `overlay/sbin/rebootsystem.sh` is TWRP's
  reboot-to-system hook: it runs `winre-actions.sh prepare-boot` (MISC request,
  acknowledgement, startup records) before every restart into Windows.
  `overlay/sbin/winre-ntfs-watchdog.sh` is baked in and registered as an init
  service that breaks the ntfs-3g FUSE self-deadlock on the phone.

## Building

```
python mkassets.py --stock --bars       # regenerate the stock replacements and bars (icons untouched)
python build.py --source /path/to/twrp-3.7.0_9-0-star2lte.img --outdir work/out
python build.py --source ... --outdir work/out --gears /path/to/UpdateOS-GearAnimation.gif
python validate.py work/out/ui.zip --release [--pages]
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
| `build-report.txt` | every file that changed, with sizes, and which gears |

The recovery partition is 68,149,248 bytes and a WinRE build is about 42 MiB,
so there is no need to subset the fonts; the full faces are copied. The marker
`/twres/winre-build.txt` records the builder version, the base image hash and
the gears (`builtin` or the GIF's SHA-256); the installer rebuilds when any of
them no longer matches.

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
    range `BootRouteService.ClearBootRequestAsync` clears), `insmod` the baked-in
    `rwd1_ack.ko` and report `/proc/rwd1_ack`, then read the RWD1 and P3 startup
    records whole and zero whichever is not all zero, reading them back (the same
    fix `BootRouteService` runs before every first-boot attempt and
    `rebootsystem.sh` runs before every *Continue*, so the next restart goes
    straight to Windows instead of hanging at the Samsung logo).
  * **Advanced options** — Check PRAM, Collect diagnostics, Command Prompt,
    Erase PRAM, Full TWRP interface, Restart to Download mode.
  Every repair/destructive action goes through a WinRE-style confirm page.
* **Every stock page in the WinRE look** (`theme/reskin.xml`, applied by both
  builders, each operation with an exact match count so a different base theme
  stops the build instead of half-applying):
  * palette: black, white, grey, Windows blue `#0078D4` for sliders,
    checkboxes, progress and selection, `#60CDFF` where the accent is text;
    the console's error/warning/highlight lines become soft Windows colours;
  * fonts: Segoe UI Light titles, Regular body and keyboard labels;
  * the page template has no blue header bar, no logo and no status row: a
    back arrow sits beside the Light title (as in Windows Settings) and the
    navigation bar is flat black with line glyphs;
  * operation pages (flash, backup, restore, file actions, decrypt) show the
    turning gears instead of a scrolling log; afterwards a *Show details* link
    opens the log in the console overlay;
  * the keyboards are dark with white labels; tab bars are a quiet dark band;
  * the TWRP pages TWRP opens by itself are WinRE pages from `winre.xml`:
    `lock` (tap anywhere), `singleaction_page` (every openrecoveryscript
    command passes through it), `action_page` and `action_complete`;
  * all 57 stock bitmaps are replaced by the originals in `assets/stock`.
* **Installing Windows** screen — see below.
* **Splash** — the gears on black, replacing the TeamWin splash.

`validate.py --release` proves the coverage: it classifies every page (WinRE,
stock page on the WinRE template, logic-only, renamed-and-unreachable, or an
allow-listed overlay with a stated reason) and fails on any page, template,
style or variable still using a TWRP colour, the TeamWin logo, the stock header
band or a scrolling log, and on any bitmap that is not our own art. The current
result is 0 leftovers; the allow-list is the three overlays that are drawn only
from reskinned variables (`slideout` — the details console, `select_storage`,
`select_language`).

## The "Installing Windows" screen

`sbin/winre-statuswatch.sh` raises a WinRE "Installing Windows" screen (gears,
the phase, a live percentage bar and a detail line such as
"37% complete · 7.4 of 20.0 GB") while the installer writes the phone, and
clears it when it stops.

**What it watches.** The installer keeps `/tmp/s9woa/status` current
(`phase=`, `label=`, `percent=`, `detail=`, `done_bytes=`, `total_bytes=`). It
does not spend a round trip on that: the write is appended to the shell command
it runs anyway for every chunk (`...; s9r=$?; {write status.tmp && mv};
exit $s9r`, so the command's exit code is unchanged and the file is replaced
atomically). The file is removed when a phase ends; one whose contents have not
changed for 300 s is treated as a crashed installer. With no status file, any
process with `of=/dev/block/` on its command line (a `dd` writing a block
device) raises a generic screen.

**How it is shown without blinking.** The first version pushed the caption
through `twrp set`. TWRP runs every openrecoveryscript command except
`changepage`, `reloadtheme` and `dumpstrings` as an action on
`singleaction_page` and then switches back (`gui.cpp ors_command_read`), so each
update flashed the stock *Running Recovery Commands* page (blue header, TeamWin
logo, red and white console lines) for a frame. Now:

* live values are Android properties: the watcher `setprop`s `s9woa.label`,
  `s9woa.pct`, `s9woa.detail`, `s9woa.mode` and `s9woa.show`. TWRP's
  `DataManager::GetValue` resolves `property.<name>` with `property_get()` on
  every read, so `%property.s9woa.label%` in a `<text>` and a `<progressbar>`
  bound to `property.s9woa.pct` follow them with no page change;
* the screen is raised and cleared with `twrp changepage=` — served directly on
  the GUI thread — once each per install phase (once more only if a phase
  switches between the percentage bar and the plain sweep);
* `twrp set` is never used. As a safety net, `singleaction_page` itself is a
  WinRE page that, while the install screen is up, draws the very same body
  (the shared `winre_install_body` template), so even a stray ORS command from
  elsewhere cannot be seen.

`tw_cpu_temp` (a value TWRP re-reads from a thermal sysfs file) was considered
as a channel via a bind mount; properties are simpler, need no mount and update
on the next frame instead of on a 5 s cache.

If `setprop` does not work on the phone, the watcher notices at start and raises
`winre_install_static` instead: the same screen with a sweep and a fixed caption.

Safety rules kept from the research watcher: raise only over the static WinRE
menus, never over a running report, confirmation or TWRP operation; clear only
if the screen is still showing and go back to the page it covered; if the user
leaves it (its back arrow, Back and Home go to `main`) do not raise it again for
that phase; `timeout -t 8` around every `twrp` call; a minimum dwell so an
instant phase does not strobe; every page change is verified against the
`Set page:` lines in `/tmp/recovery.log`, because the CLI exits 0 even when
TWRP refuses a command.

## The three "lock" problems

1. **Screen-timeout lock.** `main` sets `tw_screen_timeout_secs=0` at page load,
   and so does the install screen, which disables blanking. The stock
   swipe-to-unlock `lock` page is replaced by a WinRE page that unlocks on any
   tap, Back or Home. The previous replacement dismissed itself from its load
   action — but `PageSet::SetOverlay` runs an overlay's load actions *before*
   pushing it, so that popped nothing and left an invisible overlay eating every
   touch. `validate.py` now rejects a load-time dismiss.
2. **Stale `ui.zip`.** TWRP loads `/sdcard/TWRP/theme/ui.zip` (i.e.
   `/data/media/TWRP/theme/ui.zip`) in preference to the baked-in theme, and
   that zip is checked *before* `postrecoveryboot.sh` runs, so the hook only
   heals the *next* boot. TWRP 3.7 offers no baked-theme hook that runs first,
   so for the current boot the installer deletes the zip over adb
   (`TwrpClient.RemoveStaleThemeOverrideAsync`); the baked hook covers the
   standalone case.
3. **No `twrp`/ORS on hot paths.** The installer drives TWRP with plain
   `adb shell` and `adb reboot`, never `twrp set` or `openrecoveryscript`, and
   the watcher uses only `twrp changepage=`.

## Device traps encoded here

These are measured on the device (see the research tool's notes) or read from
the TWRP 3.7 source, not guessed:

* command output reaches the on-screen console only through
  `function="terminalcommand"`, never `cmd` (log only) or `openrecoveryscript`
  (reboots to Android on success); every command runs through
  `sbin/winre-gui.sh`, which pads the console so the report is not buried under
  the boot log, using `echo " "` (a bare `echo` is dropped by the console);
* every `<animation>` needs `retainaspect="1"` or the gears render ~15.6% too
  tall on the 1440x2960 panel;
* a `<condition var2="">` is evaluated as false and silently disables its block;
* conditions are evaluated when a page is entered (and on GUI variable
  changes), not on every frame, so a condition on a property is fixed for the
  visit; live values go in `<text>` and `<progressbar>` instead;
* a `<text>` showing a property only refreshes through GUIText's shared
  every-fourth-update counter, which reaches every text on a page only when the
  page shows an **odd** number of texts (buttons with a font count): the install
  screen keeps five;
* `GUIProgressBar` leaves its slide counter uninitialised and, when non-zero,
  writes its own value back into the bound variable; the install screen sets
  `ui_progress_frames=0` on entry, which zeroes it;
* an animation advances every `floor(30/fps)+1` passes of a 30 Hz loop, so the
  gear speed is chosen from that set (the UpdateOS GIF's 350 ms → fps 3);
* `--` is illegal inside an XML comment;
* busybox 1.22 wants `timeout -t SECS` and its `sleep` takes whole seconds
  (use `usleep`);
* `setprop ctl.stop recovery` powers the phone off — never use it;
* internal storage is `/data/media`, not `/data/media/0`.

## Checking a flashed build

The watcher logs to `/tmp/winre-statuswatch.log`. Without an install running:

```
adb shell 'mkdir -p /tmp/s9woa; printf "phase=copy\nlabel=Copying Windows\npercent=37\ndetail=37%% complete\n" > /tmp/s9woa/status'
adb shell 'getprop s9woa.show; getprop s9woa.pct; cat /tmp/winre-statuswatch.log'
adb shell 'sed -i s/percent=37/percent=62/ /tmp/s9woa/status'   # the bar moves, no page change
adb shell "grep -E \"Set page|Command '\" /tmp/recovery.log | tail -n 8"
adb shell 'rm /tmp/s9woa/status'                                  # clears after ~3 s
```

`/tmp/recovery.log` should show exactly one `Set page: 'winre_install'` and one
return, and no `singleaction_page`.

## Layout

```
bootimg.py    boot image split/join, newc cpio round-trip, LZMA-alone helpers
poweroff.py   the four-byte power-off route kernel patch (hash/pattern gated)
mkassets.py   original procedural art: icons, gears, bars, stock replacements
build.py      orchestrator; emits the flashable image, ui.zip and the report
validate.py   checks refs, escapes, console rules, the install/lock screens,
              the watcher contract, and page coverage (--release, --pages)
theme/        winre.xml (WinRE pages), splash.xml, reskin.xml (stock reskin)
overlay/sbin/ winre-gui.sh, winre-pram.sh, winre-debug.sh, winre-actions.sh,
              winre-statuswatch.sh, postrecoveryboot.sh (boot-time self-heal),
              winre-ntfs-watchdog.sh (ntfs-3g FUSE deadlock breaker, init service)
assets/       images/: icons, gear/bar frames, install bar
              stock/: an original for every stock TWRP bitmap
modules/      rwd1_ack, rwd1_evidence_reader, rwd1_clear_poc, pram_smp_clear_poc
              (.ko + GPL-2.0 sources and pram_cache.h)
```
