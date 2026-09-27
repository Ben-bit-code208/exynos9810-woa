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
    S9Woa.Installer.App/    WinUI 3 front-end
      Pages/                Welcome, Host, Phone, Image, Install, Tools, About
  tests/
    S9Woa.Installer.Core.Tests/   xUnit tests for the engine
```

The engine (`S9Woa.Installer.Core`) has no UI dependency so it can be tested and
scripted. The WinUI app is a thin shell over it.

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
- `TwrpFlasher` — `ITwrpFlasher` plus `HeimdallTwrpFlasher` (drives the
  open-source Heimdall, which flashes RECOVERY by name) and `TwrpFlashService`
  (preference order: native Odin when ported and validated, then Heimdall).

A self-contained C# Odin/Thor implementation is intended as the primary flasher;
it must be ported from the authoritative Heimdall protocol and validated on the
device before it is enabled — a guessed download-mode protocol can brick a phone,
so it is not hand-written from memory.

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
