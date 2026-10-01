# S6SY761Touch gesture-report candidate

**Origin.** This driver is derived from TheMorc's (Richard Gráčik's)
[S6SY761Touch](https://github.com/TheMorc/S6SY761Touch) Windows driver, GPL-2.0,
adapted for the Galaxy S9+. His driver is built on Microsoft's vhidmini2 KMDF
sample (MS-PL), began as a fork of Gustave Monce's
[nt36xxx_win](https://github.com/edk2-porting/nt36xxx_win), and ports the Linux
sec_ts / s6sy761 driver by Andi Shyti and Samsung Electronics. Their copyright
notices are kept in the sources; see the repository's `NOTICE`.

**Not deployed or hardware-qualified.** This is a bounded gesture-delivery
update to the retained v24 `S6SY761Touch` driver, not the disabled public-GPIO
migration or the unrelated `SecTouch` prototype. It retains the existing
`ACPI\SCSY0761` binding, SPB transport, legacy interrupt ownership, HID descriptor,
64-byte reports, demand-start service and KMDF 1.15. No firmware change is needed.

`Device.c`, `Device.h`, `common.h` and the INF were forked from
`..\S6SY761TouchGpio\RetainedV24`; that frozen baseline is not modified.
The original source identity and licensing are preserved in that directory's
`RetainedV24.sha256.json`. The starting Device.c SHA-256 is
`3a6027c60c9c1f4967d66d0dc049ed120059c7a778886a9884c3ba325984be38`.
The candidate INF version is `6.0.5058.3`.

## Report-delivery changes

* Preserve finger-down position and scan time. Only consecutive movement-only
  reports with identical active-contact sets can replace one another.
  Presses, releases and an unknown contact's first MOVE remain boundaries.
* Hold an entire 64-event controller FIFO burst without the old 32-report
  overflow reset. A full queue can still coalesce its last movement report.
  Sustained overload retains the existing counted all-up recovery.
* Do not consume a report when a HID read buffer is too short. The next valid
  read receives the same down/up transition instead of silently losing it.

These fix reproducible software defects under batching/backpressure. They do
not establish the cause of a particular physical gesture failure. No guessed
debounce, coordinate smoothing, gesture timeout, synthetic release or Windows
registry setting is introduced. Windows continues to recognize gestures.
Electrical noise, controller/I2C errors, Windows gesture configuration and
physical usability still require observations on the phone.

## Build and regression coverage

Use the existing VS/WDK installation; choose a new output directory outside
the repository:

```powershell
.\Build-Touch.ps1 -OutputDirectory "$env:TEMP\touch-build"
```

The script builds and executes production C through the existing touch WDF/SPB
host mocks, then builds an actual ARM64 SYS, validates the INF and generates an
**unsigned** catalog/package. It does not install, sign, flash, reboot or change
device trust. `touch-build.json` records the source/package/compiler identities
and explicit hardware/deployment limitations.

Nine host groups cover retained down coordinates/timestamps, a three-second
hold and scan-time rollover, two taps with moves in one FIFO batch, all 64 FIFO
transitions, full-queue movement coalescing and ring wrap, short buffers on down
and up, ten contacts, I2C/overload recovery, cancellation and completion reentry.
The same tests compile against untouched v24 as a negative control: five
selected cases must fail at their specific regression assertions.
The shared mock clock defaults to its previous value, so the existing GPIO
migration's descriptor/report equivalence suite remains unchanged.

Before installation, confirm the current phone's installed package and Windows
gesture settings, back up the package with a verified recovery route, and use
the established signing/deployment gates. Do not install alongside a GPIOClx
owner of the legacy ALIVE bank. Acceptance needs real repeated double-taps,
stationary holds, dragging and multitouch, including under load; host report
tests alone cannot qualify Windows gesture recognition.
