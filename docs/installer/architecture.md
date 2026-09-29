# Installer architecture

This document is for maintainers of the project. For how to *use* the installer,
see the top-level [README](../../README.md).

## Layout

```
installer/
  src/
    S9Woa.Installer.Core/   reusable engine, no UI dependency
      Device/               ADB client, device snapshot, eligibility checks, actions
      Host/                 host-PC preflight (admin, disk, tools)
      Image/                Windows image servicing and slim (tiny11-style) plans
      Processes/            process launching / output capture helpers
      Recovery/             UCR1 "restart to recovery" ticket protocol
      Stages/               StageCatalog + resumable InstallState
      Toolset/              first-run toolset: detect, acquire and remember tools
    S9Woa.Installer.App/    WinUI 3 front-end
      Wizard.cs             IWizardStep contract and step-rail model
      MainWindow            wizard shell: step rail, content surface, footer
      Pages/                Welcome, Setup, Host, Phone, Image, Install (steps); Tools, About (side pages)
  tests/
    S9Woa.Installer.Core.Tests/   xUnit tests for the engine
```

The engine (`S9Woa.Installer.Core`) has no UI dependency so it can be tested and
scripted. The WinUI app is a thin shell over it.

## Wizard shell

The app is a linear wizard. `MainWindow` owns navigation: a step rail on the
left (numbered circles that turn into checkmarks), the current page on a
layered content surface over Mica, and a fixed footer with *Step n of 6*, **Back**
and one accent primary button. Each step page implements `IWizardStep`:

- `CanAdvance` enables the primary button (for example, Setup only once
  `ToolsetManager.IsComplete`, Phone only for an eligible device).
- `NextLabel` names it (*Get started*, *Continue*, *Review*, *Install*).
- `OnAdvanceAsync` runs before leaving; returning `false` stays on the page
  (the Install step uses this to start the install in place).
- `CanGoBack` is `false` while a page is busy; the shell then locks Back, the
  rail and the side-page links.

The rail lets you jump back to any step already reached, never ahead of it.
Device tools and About open as side pages outside the flow. Debug builds accept
`--unlock-all-steps` to reach every step for UI work; Release builds ignore it.

## First-run toolset

`Toolset/ToolDefinitions.cs` lists everything the installer needs; the Setup
page renders one card per entry. `ToolsetManager` detects each item, acquires it,
and persists choices to `toolset.json` (release repository, local build folder,
program overrides). The install pages are locked until `ToolsetManager.IsComplete`
holds: every required item is `Ready`; optional ones (the Heimdall fallback) never block.

- **Programs** (adb, Heimdall, Zadig) — `ToolLocator` checks, in order: a
  user override, the app's `tools\` folder, winget portable installs (per-user and
  machine, packages and links), then `PATH`. `WingetClient` installs by exact id;
  success is judged by re-detection, not winget exit codes.
- **Samsung USB driver** — detected by its `dg_ssudbus` service. The user's
  downloaded installer runs only after `AuthenticodeVerifier` (`WinVerifyTrust`)
  confirms a trusted chain and a Samsung Electronics signer.
- **TWRP** — the user's download is accepted only if the name is a star2lte build
  and `BootImage` sees an `ANDROID!` header that fits RECOVERY.
- **UEFI and drivers** — `ReleaseClient` reads the latest release of the
  configured repository and keeps an asset only if it matches `SHA256SUMS` (a
  release without it is refused). `drivers.zip` is extracted with an
  archive-escape guard. A local build folder (`UseBuildFolder`) takes precedence;
  it imports the newest valid `*uefi*.img` and only *built* driver packages (an
  `.inf` next to a `.sys`). `tools/release/make-payload.ps1` produces the matching
  release assets.
- **Download-mode driver** — `DownloadModeDriver` reads the USB enumeration
  key for `VID_04E8&PID_685D`. It matters only for the optional Heimdall
  fallback, which needs Zadig to bind WinUSB; the built-in flasher does not.

Registry, signature checks, winget and HTTP are behind interfaces, so the tests
cover these paths without touching the PC or the network.

## Stages and honest gating

`Stages/StageCatalog.cs` is the single source of truth for the install flow.
Each `StageDefinition` has an `Availability`:

- `Ready` — implemented and validated on the reference device.
- `Guided` — the user performs the step by hand; the installer explains it and
  verifies the result.
- `Experimental` — automated and unit-tested, but not yet validated end to end
  on the reference device. The installer runs these only when the user opts in
  with *Run experimental steps* on the Install page.
- `NotImplemented` — planned; the installer stops before it.

`InstallState` (persisted as JSON under `%LOCALAPPDATA%\S9WoaInstaller`) records
per-stage status so an install can resume. When you finish automating a stage,
add coverage in the Core tests and move it from `Experimental` to `Ready` once it
is validated on hardware. Wiring for running a stage lives in
`Pages/InstallPage.xaml.cs` (`RunStageAsync`).

## Deploy engine

`Core/Deploy` holds the device-facing engine, all behind injectable process
seams so it is unit-tested without hardware:

- `TwrpClient` — adb-over-TWRP primitives: list partitions by name, read size,
  `dd`, sha256, push/pull. Binary payloads always move as files on the SD card,
  never as captured stdout, so nothing is corrupted by text decoding.
- `PartitionMap` — the validated star2lte partition names.
- `BackupService` — verified identity backup (efs and friends) to the PC, with a
  device/PC sha256 cross-check and a JSON manifest. Refuses if `efs` is missing.
- `TwrpFlasher` — `ITwrpFlasher`, `TwrpFlashService` (uses the first available
  flasher) and `HeimdallTwrpFlasher` (fallback; needs Zadig's WinUSB binding).
- `Odin/` — the built-in flasher. `OdinSession` speaks Samsung's Download-mode
  protocol (ODIN/LOKE handshake, begin session with protocol-version probe,
  PIT dump in 500-byte parts, file sequences of 128 KiB or 1 MiB parts, end
  session without reboot), implemented from the protocol as documented by the
  open-source Heimdall and Thor projects. `SerialOdinTransport` carries it over
  the COM port the Samsung USB driver creates for Download mode, so no driver is
  replaced; `DownloadModePort` finds that port (a present `VID_04E8&PID_685D`
  instance). `OdinTwrpFlasher` looks up RECOVERY in the phone's PIT by name,
  flashes TWRP and leaves the phone in Download mode for the TWRP key combo.
  `Pit` parses the partition table. A simulated bootloader covers it in tests.
- `TransferService` — writes prepared images through TWRP. For the raw Windows
  volume it reads the NTFS `$Bitmap` (`Image/NtfsAllocation`) and writes only the
  MiBs that hold used clusters (plus the first and last MiB, for the boot sector
  and its backup), which is about 10 GiB of a 53 GiB Core volume.
  `TransferPlanner` groups them into chunks of up to 128 MiB; each is staged in
  TWRP's RAM (`/tmp`) rather than on the SD card, and the next chunk crosses USB
  while the current one is written. With *Verify every write* on (the default),
  each chunk is read back and its SHA-256 compared with the source. Skipping
  free space is safe because NTFS never reads unallocated clusters: a sparse
  copy and a full copy of the same image give identical `chkdsk` results. If the
  image is not NTFS, everything is written, with all-zero chunks filled on the
  phone. Small images (UEFI) go to BOOT whole. The service refuses a destination
  smaller than the image and refuses to write a mounted target.
- `BootFilesService` — mounts the phone's FAT EFI system partition and copies the
  built ESP tree onto it (plus a `/cache` BCD copy the firmware also consults).
- `BootConfiguration` — retargets the freshly built BCD so the boot manager and
  loader find Windows by locating `\Windows`, rather than by a partition GUID the
  raw USERDATA volume does not have. Uses only public `bcdedit` features.

`Image/VhdxImageBuilder` runs the whole host build: it scripts diskpart to create
an ESP + MSR + NTFS layout in a VHDX, applies the edition, injects drivers, runs
the slim profile, writes the OOBE answer file (`UnattendXml`), runs `bcdboot`,
retargets the BCD, then exports the NTFS volume to `work\out\windows.img`
(`RawImageExporter` over a `VolumeDiskSource`) and copies the ESP to
`work\out\esp`. Those two outputs are exactly what the transfer stage writes to
the phone.

- `Image/UnattendXml` — a clean-room generator of the standard `oobeSystem`
  answer file: a local administrator account, skipped EULA/privacy/MSA/wireless
  screens, and locale/time-zone/computer-name. This is what makes OOBE finish to
  the desktop without user input.

The validated device targets are in `Deploy/PartitionMap`: Windows to
`USERDATA`, UEFI to `BOOT`, TWRP to `RECOVERY`, and boot files to the FAT EFI
system partition.

## Image profiles

`Image/SlimPlan` turns a profile into ordered operations that `ImageServicer`
applies offline (all DISM work runs with the image's registry hives unloaded):

- **Stock** — drivers only.
- **Lite** — removes consumer inbox apps and applies ads/telemetry defaults;
  Windows Update keeps working.
- **Core** — follows tiny11Coremaker.ps1: Lite plus CBS package removal
  (Defender, IE, Media Player, language features, ...), Edge/WebView2/OneDrive and
  WinRE deletion, a component store (WinSxS) cut down to the servicing stack and
  runtime assemblies, tiny11's registry set (Windows Update and Defender off,
  Copilot/Teams/Outlook blocked) and a final `/ResetBase`. All of it works from an
  x64 PC on an ARM64 image.

## Release pipeline (maintainers only)

This public repository is **generated**, not edited in place at the source of
truth. The flow is:

1. Product source lives in a private research repository.
2. `tools/release/import_sources.py` copies only the paths named in
   `release/import-manifest.json` into this tree. Nothing outside the manifest
   is ever published (`global_exclude` drops logs, receipts, provenance, etc.).
3. During import, two transforms keep public edits reproducible:
   - **`resolve_macros`** — a unifdef-like resolver that removes dead
     `#if`/`#ifdef` branches for named flags (used to drop diagnostic/hook code
     from the firmware and to force the portable-kernel contract in the UFS
     driver).
   - **per-entry patches** — small diffs applied after the copy, recorded with
     `--make-patches` and reapplied on every import. An overwrite guard refuses
     to import over a public file that was hand-edited without recording a patch;
     `--force` discards such edits.
4. `tools/release/scan.py` is the leak gate. It rejects vendor/kernel internals,
   build hashes, workspace paths, personal emails and stray binaries.
   `release/scan-allow.txt` lists the few intentional exceptions (the scanner's
   own tests, upstream GPL author emails, the WinPE `X:` system path, the app
   icon).

### Private, not-in-git files (back these up)

Two things are needed to reproduce a release but are intentionally **not**
committed (see `.gitignore`):

- **`.release-patches/`** — the per-entry patch files. Their *removed* lines can
  contain scrubbed private text, so they never go public. Regenerate with
  `python tools\release\import_sources.py --source <research-repo> --make-patches`.
- **`.release-denylist.txt`** — additional local scanner patterns.

If you lose these you can still ship, but you lose the automated, reproducible
scrub — you would have to redo the public edits by hand. **Back them up
privately** alongside the research repository.

### Typical maintainer loop

```powershell
# edit an imported file in this public repo, then:
python tools\release\import_sources.py --source <research-repo> --make-patches
python tools\release\import_sources.py --source <research-repo>   # verify reapply
python tools\release\scan.py                                      # must be clean
python -m pytest -q tools\release\tests
```
