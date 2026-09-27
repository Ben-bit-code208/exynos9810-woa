# Contributing

Thanks for your interest. A few things make this project unusual, so please read
this first.

## The golden rule: one validated device

Everything here is proven on exactly one configuration — **SM-G965F** (Exynos
9810, star2lte) running **Windows 11 22621 (ARM64)**. Please do **not** relax
the installer's device checks or mark a stage as "Ready" for hardware or a build
you have not personally validated end to end. It is fine — encouraged — to add
support for a new device or build, but do it behind the same honest gating: a
stage is `Ready` only when it has been validated, otherwise `Guided` or
`NotImplemented` (see `installer/src/S9Woa.Installer.Core/Stages/StageCatalog.cs`).

## Building and testing

```powershell
# Installer
cd installer
dotnet build S9Woa.Installer.sln -c Debug -p:Platform=x64
dotnet test tests\S9Woa.Installer.Core.Tests\S9Woa.Installer.Core.Tests.csproj -p:Platform=x64

# Release tooling
python -m pytest -q tools\release\tests
python tools\release\scan.py
```

CI runs the release scan, the Python tests, the .NET tests and an x64 build of
the app. All four must pass.

## Licensing of contributions

- Code you add to the firmware, drivers (except the touch driver), installer or
  release tools is contributed under **BSD-2-Clause-Patent**.
- Changes under `drivers/S6SY761Touch/` are **GPL-2.0-only** and must keep the
  upstream copyright and license notices intact.
- Add an `SPDX-License-Identifier` header to new source files.
- Do not add third-party code or binaries without a compatible license, and
  update [NOTICE](NOTICE) when you build on someone else's work.

## No leaked internals

This public tree is generated from a private research repository by
`tools/release/import_sources.py` and gated by `tools/release/scan.py`. Do not
paste in reverse-engineered vendor internals, proprietary binaries, Microsoft
kernel disassembly, build hashes or personal certificate thumbprints. The
scanner will reject the obvious cases, but please don't rely on it. Maintainer
notes on how the import works are in
[`docs/installer/architecture.md`](docs/installer/architecture.md).

## Style

- Keep comments about *why*, not machine-generated *what*. Avoid pasting
  instruction listings or address tables into comments.
- Match the surrounding code style. C# is nullable-enabled with warnings as
  errors; keep the build warning-clean.
