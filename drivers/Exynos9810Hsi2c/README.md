# Exynos 9810 HSI2C10 SpbCx driver

This ARM64 KMDF driver exposes the Galaxy S9+ HSI2C10 block to Windows through
SpbCx. It binds to `ACPI\SAMS9810`; the touchscreen remains a separate
`ACPI\SCSY0761` function device (see `../S6SY761Touch`) and is not advertised as
HID-over-I2C.

The controller driver:

- validates and maps the HSI2C10, PERIC0 CMU, PERIC0 SYSREG, and PERIC0 GPIO
  resources;
- selects USI03 I2C mode, enables its clock path, and configures GPP1_4/GPP1_5;
- derives 100 kHz or 400 kHz timing from the fixed 200 MHz source clock;
- services FIFO transfers through a WDF ISR/DPC pair;
- preserves repeated-start semantics by omitting STOP on non-final sequence
  transfers;
- resets the controller after cancellation, timeout, NACK, abort, or a short
  transfer.

Install the controller package before the S6SY761 function-driver package. The
firmware DSDT must provide the exact resources documented in
`exynos9810hsi2c.h`.

## Build

Requires Visual Studio with the ARM64 build tools and the WDK (10.0.26100).

```powershell
msbuild Exynos9810Hsi2c.vcxproj /p:Configuration=Release /p:Platform=ARM64
```

The package (`.inf`, `.sys`, `.cat`) lands in `ARM64\Release\Exynos9810Hsi2c`.
Like the other drivers here it is test-signed, so Windows needs test signing
on (the installer turns it on in the image it builds).

## Provenance

These files are the sources the released `Exynos9810Hsi2c.sys` was built from
(the driver validated end to end on the reference phone), unchanged apart from
the line endings git normalizes. That build's
Authenticode SHA-256 (which leaves out the signature) is
`9897e4e240787b46edec95c419e51a35b3b71e6f410a42123e10e9c5ecbd676d`. The released
file differs from it only in the debug directory's PDB path, where the build
machine's path was replaced by `C:\build\Exynos9810Hsi2c.pdb` (Authenticode
SHA-256 `6ef2e601f4c0946852bab12344a811d92282c7d1545dc92f628fbd988a93c02c`),
and is re-signed with the project's test certificate.

## License

Original work is under the project's **BSD-2-Clause-Patent** license (`LICENSE`).
The WDF/SpbCx framework scaffolding comes from Microsoft's SkeletonI2C sample
(`microsoft/Windows-driver-samples`, `spb/SkeletonI2C`): `driver.h` and
`i2ctrace.h` carry Microsoft's copyright, and the sample-derived structure of
`driver.cpp`, `device.cpp`, `controller.cpp` and `internal.h` is conservatively
treated as sample-derived too. Those portions are Copyright (c) Microsoft
Corporation and are used under the **Microsoft Public License** (`MS-PL.txt`),
so this folder as a whole is `BSD-2-Clause-Patent AND MS-PL`. The hardware engine
(clocking, FIFO transfers, recovery) is original.
