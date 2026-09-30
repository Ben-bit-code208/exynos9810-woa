# Windows on the Galaxy S9+ (Exynos 9810)

Run Windows 11 (ARM64) on the Samsung Galaxy S9+ **SM-G965F** (Exynos 9810,
"star2lte"). This repository holds the firmware, drivers and a graphical
installer that takes a phone from a fully stock Android state to installed
Windows.

> **One validated device.** Everything here has only been proven on
> **SM-G965F** with **Windows 11 22H2/23H2 ARM64** (the builds listed in the
> firmware catalog: 22621/22631.2428 and 22621.7582/22631.7584). The installer
> knows which steps are automated and which are not: unfinished stages are shown
> as *coming soon* and the installer stops before them rather than guessing. Do
> not point it at another model or firmware.

## What's in the box

| Path | Component | License |
|------|-----------|---------|
| `firmware/` | EDK2-based UEFI for star2lte (Platform + Silicon packages, boot shim, build/pack scripts) | BSD-2-Clause-Patent |
| `drivers/Exynos9810Ufs/` | UFS storage driver (`drivers/include/` holds its shared record definitions) | BSD-2-Clause-Patent |
| `drivers/Exynos9810Hsi2c/` | HSI2C bus controller (SpbCx) the touchscreen sits on | BSD-2-Clause-Patent AND MS-PL (framework from Microsoft's SkeletonI2C sample) |
| `drivers/S6SY761Touch/` | Touchscreen driver (derived from the upstream Linux `sec_ts`/`s6sy761` driver) | GPL-2.0-only |
| `installer/` | WinUI 3 installer (`S9Woa.Installer.App`) over a reusable C# engine (`S9Woa.Installer.Core`) | BSD-2-Clause-Patent |
| `tools/twrp-winre/` | The WinRE look the installer gives TWRP on your PC (theme, scripts, original artwork, GPL kernel modules) | BSD-2-Clause-Patent; modules GPL-2.0 |
| `tools/release/` | Release importer, leak scanner and release payload script | BSD-2-Clause-Patent |

The [releases](../../releases) carry the built UEFI images (`firmware.json` plus
one image per supported Windows build) and `drivers.zip` (the three driver
packages, test-signed), all listed in `SHA256SUMS`. The installer downloads and
verifies them for you. Windows itself is never redistributed: bring your own
ARM64 media.

## The installer

The installer is a Windows desktop app. It walks the end-to-end flow and, for
each stage, either performs the work or guides you through the manual part and
verifies the result.

| Stage | State |
|-------|-------|
| Check this PC (admin, disk, adb, USB driver) | ✅ Ready |
| Identify the phone | ✅ Ready |
| Get Windows media | ✅ Ready |
| Build the Windows image | ✅ Ready (VHDX: apply + drivers + slim + OOBE unattend + bcdboot, exported to a raw image) |
| Unlock the bootloader | 🧑 Guided (you do it; the installer verifies) |
| Install TWRP | ✅ Ready (flashes RECOVERY and BOOT by name over Samsung's Download-mode protocol, through the Samsung USB driver — no Zadig — and the phone starts TWRP by itself) |
| Back up EFS / modem / partition table | ✅ Ready (verified backup to your PC) |
| Prepare partitions | ✅ Ready (verifies the layout by name) |
| Copy Windows to the phone | ✅ Ready (only the used NTFS clusters → USERDATA, staged in TWRP's RAM; boot files → EFI; optional read-back verification) |
| Install UEFI | ✅ Ready (the UEFI build for your Windows build → BOOT) |
| First boot | ✅ Ready (clears the startup records, reboots into Windows; OOBE finishes to the desktop unattended) |

The flow is end to end: from plugging the phone in to landing on the Windows
desktop. All the slow PC-side work (choosing media and building the image) runs
**before** the phone is touched, so the on-device steps — unlock, TWRP, backup,
and the verified writes — run back to back. The **Build the Windows image** step
produces a bootable Windows volume (with your drivers, the optional slim profile
and an OOBE answer file that creates your local account, skips the setup
screens and sets the display scaling to 275 DPI for the phone's screen); **Copy Windows** writes it plus the boot files to the phone. After
**First boot**, Windows setup completes on its own and signs you in.

Every stage has been run end to end on the reference phone, from stock Android
to the Windows desktop. The installer still backs up the phone before it writes
anything that cannot be recovered, and keeps those backups on your PC: keep them.

Quality-of-life tools (e.g. "Restart to TWRP" from inside Windows) live on the
**Tools** page.

### First-run setup

The first time it runs, the installer opens **Set up**. The install pages stay
locked until everything below is ready, and the choices are remembered in
`%LOCALAPPDATA%\S9WoaInstaller\toolset.json` (or in the `data` folder next to
the app, when there is one: portable mode).

| Item | How Setup provides it |
|------|------------------------|
| Android platform tools (adb) | winget `Google.PlatformTools` |
| Heimdall (optional) | winget `BenjaminDobell.Heimdall`; only a fallback, used if the Download-mode interface has been switched to WinUSB |
| Zadig (optional) | winget `akeo.ie.Zadig`; only for the Heimdall fallback |
| Samsung USB driver | You download Samsung's installer; Setup runs it only if it is validly signed by Samsung Electronics |
| TWRP for star2lte | Setup opens the official TWRP page; you choose the downloaded `twrp-3.7.0_9-0-star2lte.img` (checked for the model name, the boot-image header and the RECOVERY size). The installer re-skins it on your PC into a WinRE-look recovery and flashes that; a prebuilt WinRE-look image is also accepted as-is. If your build folder holds your own copy of Microsoft's `UpdateOS-GearAnimation.gif` (e.g. in `twrp\`), the recovery is built with those gears; otherwise it uses its own drawn gears |
| UEFI image | Latest project release (`firmware.json` and the UEFI image for each Windows build it lists), or your local build folder |
| Phone drivers | Latest project release (`drivers.zip`), or your local build folder |
| Download-mode USB driver for Heimdall (optional) | Not needed: the installer flashes TWRP through the Samsung USB driver. Only for the Heimdall fallback, via Zadig |

**Set up automatically** installs everything that needs no decision from you.
Release downloads are kept only if they match the release's `SHA256SUMS` (and,
for the UEFI images, the SHA-256 recorded in `firmware.json` as well); a
release without that file is refused. Any program can be replaced with your own
copy (*Use a different file…*), and a local build folder takes precedence over
releases.

The raw Windows image and boot files are produced automatically by the **Build
the Windows image** step (under the installer's work folder); you don't supply
them.

### Publishing a release payload

`tools\release\make-payload.ps1` assembles the release assets the installer
downloads (`firmware.json` with its UEFI images, `drivers.zip`, `SHA256SUMS`)
from your builds; every UEFI image is checked against the hash `firmware.json`
records for it:

```powershell
.\tools\release\make-payload.ps1 -FirmwareCatalog <folder with firmware.json> `
    -Drivers <Exynos9810Ufs package>, <Exynos9810Hsi2c package>, <S6SY761Touch package> -OutDir out\release
gh release create v0.1.0 (Get-ChildItem out\release).FullName
```

Never attach Windows media, a built Windows image, or a WinRE-look recovery to a
release: the recovery carries Segoe UI from the builder's Windows (and optionally
Microsoft's UpdateOS animation), which is why the installer builds it on each
user's PC from the official TWRP.

### Build the installer

Requirements: Windows 11, the .NET 8 SDK, and the Windows App SDK workload
(Visual Studio 2022 "WinUI" workload or the standalone components).

```powershell
cd installer
dotnet build S9Woa.Installer.sln -c Release -p:Platform=x64
dotnet test tests\S9Woa.Installer.Core.Tests\S9Woa.Installer.Core.Tests.csproj -p:Platform=x64
```

`-p:Platform=ARM64` builds the on-device (ARM64) flavour.

### Build the firmware

Requires EDK2 BaseTools, the CLANGPDB toolchain (LLVM), Python and GnuWin32
make. See `firmware/tools/build-uefi.ps1` and `firmware/tools/pack-uefi.ps1`.
The UEFI images in the releases are the builds validated on the phone, one per
Windows build (see `firmware.json`); they come from the research lineage of this
tree, and reproducing them byte for byte from `firmware/` is not scripted yet.

### Build the drivers

- **UFS storage**: `drivers/Exynos9810Ufs/build-ufs.ps1` (fixed cl/link command
  lines, reproducible). The released driver is
  `build-ufs.ps1 -SourceDir drivers\Exynos9810Ufs -OutDir <new folder> -WideUncached -DmaWindow`,
  which reproduces it byte for byte (unsigned SHA-256
  `a80b222939e7ec81522221740de5346b9799ff68492a875d996fdf947138eaa7`).
- **HSI2C**: `msbuild drivers\Exynos9810Hsi2c\Exynos9810Hsi2c.vcxproj /p:Configuration=Release /p:Platform=ARM64`
  (needs the WDK's Visual Studio integration); see its README.
- **Touch**: `drivers/S6SY761Touch/README.md`.

All three are test-signed, so Windows runs them with test signing on, which the
installer enables in the image it builds.

## Safety

Installing Windows **erases Android** and touches low-level partitions. The
installer backs up EFS, modem calibration and the partition table before any
destructive step. Keep those backups: they are how you return to stock. There is
no warranty — see the license.

## Credits

This work stands on a lot of other people's. See [NOTICE](NOTICE).

## License

Firmware, drivers (except the touch driver), the installer and the release tools
are under the **BSD-2-Clause-Patent** license (see [LICENSE](LICENSE)). The
touchscreen driver under `drivers/S6SY761Touch/` is **GPL-2.0-only**; its own
`LICENSE` file governs that subtree. The HSI2C driver under
`drivers/Exynos9810Hsi2c/` is BSD-2-Clause-Patent **AND MS-PL** (its framework
scaffolding comes from Microsoft's SkeletonI2C sample; see its README and
`MS-PL.txt`).
