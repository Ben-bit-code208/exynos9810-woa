# Windows on the Galaxy S9+ (Exynos 9810)

Run Windows 11 (ARM64) on the Samsung Galaxy S9+ **SM-G965F** (Exynos 9810,
"star2lte"). This repository holds the firmware, drivers and a graphical
installer that takes a phone from a fully stock Android state to installed
Windows.

> **One validated device, one validated build.** Everything here has only been
> proven on **SM-G965F** with **Windows 11 22621 (ARM64)**. The installer knows
> which steps are automated and which are not: unfinished stages are shown as
> *coming soon* and the installer stops before them rather than guessing. Do not
> point it at another model or firmware.

## What's in the box

| Path | Component | License |
|------|-----------|---------|
| `firmware/` | EDK2-based UEFI for star2lte (Platform + Silicon packages, boot shim, build/pack scripts) | BSD-2-Clause-Patent |
| `drivers/Exynos9810Ufs/` | UFS storage driver | BSD-2-Clause-Patent |
| `drivers/S6SY761Touch/` | Touchscreen driver (derived from the upstream Linux `sec_ts`/`s6sy761` driver) | GPL-2.0-only |
| `installer/` | WinUI 3 installer (`S9Woa.Installer.App`) over a reusable C# engine (`S9Woa.Installer.Core`) | BSD-2-Clause-Patent |
| `tools/release/` | Release importer and leak scanner that build this public tree | BSD-2-Clause-Patent |

## The installer

The installer is a Windows desktop app. It walks the end-to-end flow and, for
each stage, either performs the work or guides you through the manual part and
verifies the result.

| Stage | State |
|-------|-------|
| Check this PC (admin, disk, adb, USB driver) | ✅ Ready |
| Identify the phone | ✅ Ready |
| Get Windows media | ✅ Ready |
| Build the Windows image | 🧪 Experimental (VHDX: apply + drivers + slim + OOBE unattend + bcdboot, exported to a raw image) |
| Unlock the bootloader | 🧑 Guided (you do it; the installer verifies) |
| Install TWRP | 🧪 Experimental (flashes RECOVERY by name via Heimdall) |
| Back up EFS / modem / partition table | 🧪 Experimental (verified backup to your PC) |
| Prepare partitions | 🧪 Experimental (verifies the layout by name) |
| Copy Windows to the phone | 🧪 Experimental (raw image → USERDATA + boot files → EFI, verified) |
| Install UEFI | 🧪 Experimental (UEFI → BOOT, verified) |
| First boot | 🧪 Experimental (reboots into Windows; OOBE finishes to the desktop unattended) |

The flow is end to end: from plugging the phone in to landing on the Windows
desktop. All the slow PC-side work (choosing media and building the image) runs
**before** the phone is touched, so the on-device steps — unlock, TWRP, backup,
and the verified writes — run back to back. The **Build the Windows image** step
produces a bootable Windows volume (with your drivers, the optional slim profile
and an OOBE answer file that creates your local account and skips the setup
screens); **Copy Windows** writes it plus the boot files to the phone. After
**First boot**, Windows setup completes on its own and signs you in.

**Experimental** steps are automated and unit-tested but not yet validated end
to end on the reference device. They stay off until you tick *Run experimental
steps* on the Install page. Please only enable them on a device you are prepared
to recover from the backups this installer makes.

Quality-of-life tools (e.g. "Restart to TWRP" from inside Windows) live on the
**Tools** page.

### First-run setup

The first time it runs, the installer opens **Set up**. The install pages stay
locked until everything below is ready, and the choices are remembered in
`%LOCALAPPDATA%\S9WoaInstaller\toolset.json`.

| Item | How Setup provides it |
|------|------------------------|
| Android platform tools (adb) | winget `Google.PlatformTools` |
| Heimdall | winget `BenjaminDobell.Heimdall` |
| Zadig | winget `akeo.ie.Zadig` |
| Samsung USB driver | You download Samsung's installer; Setup runs it only if it is validly signed by Samsung Electronics |
| TWRP for star2lte | Setup opens the official TWRP page; you choose the downloaded `twrp-*-star2lte.img` (checked for the model name, the boot-image header and the RECOVERY size) |
| UEFI image | Latest project release (`uefi.img`), or your local build folder |
| Phone drivers | Latest project release (`drivers.zip`), or your local build folder |
| Download-mode USB driver (WinUSB) | Done later, while installing TWRP: Setup opens Zadig to bind the phone's Download-mode interface |

**Set up automatically** installs everything that needs no decision from you.
Release downloads are kept only if they match the release's `SHA256SUMS`; a
release without that file is refused. Any program can be replaced with your own
copy (*Use a different file…*), and a local build folder takes precedence over
releases.

The raw Windows image and boot files are produced automatically by the **Build
the Windows image** step (under the installer's work folder); you don't supply
them.

### Publishing a release payload

`tools\release\make-payload.ps1` assembles the three release assets the
installer downloads (`uefi.img`, `drivers.zip`, `SHA256SUMS`) from your builds:

```powershell
.\tools\release\make-payload.ps1 -Uefi <packed uefi boot.img> `
    -Drivers <Exynos9810Ufs package>, <S6SY761Touch package> -OutDir out\release
gh release create v0.1.0 (Get-ChildItem out\release).FullName
```

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

### Build the drivers

See `drivers/Exynos9810Ufs/build-ufs.ps1` and
`drivers/S6SY761Touch/README.md`. Both take a `-WdkRoot` and produce
test-signable driver packages.

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
`LICENSE` file governs that subtree.
