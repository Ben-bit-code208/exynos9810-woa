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
- `NotImplemented` — planned; the installer stops before it.

`InstallState` (persisted as JSON under `%LOCALAPPDATA%\S9WoaInstaller`) records
per-stage status so an install can resume. When you finish automating a stage,
change its `Availability` to `Ready` **and** add coverage in the Core tests.
Wiring for running a stage lives in `Pages/InstallPage.xaml.cs` (`RunStageAsync`).

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
