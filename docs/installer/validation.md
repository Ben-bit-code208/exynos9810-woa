# On-device validation checklist

This is the first end-to-end run on real hardware. The install stages are marked
**Experimental** because they have not been validated on the device yet — this
run is that validation. Work through it in order and note where it stops.

## Before you start

- [ ] Galaxy S9+ **SM-G965F** (Exynos 9810). No other model.
- [ ] A full charge, and a **USB cable that does data** (many only charge).
- [ ] A microSD card in the phone (partition backups and image windows stage
      through `/external_sd`).
- [ ] On the PC: run the installer **as administrator**.
- [ ] `adb` on `PATH` (`winget install Google.PlatformTools`).
- [ ] `heimdall.exe` on `PATH` or in `tools\heimdall\`, with the Zadig/libusbK
      driver bound to the phone's Download-mode interface.
- [ ] Samsung USB Driver for Mobile Phones installed.
- [ ] `payload\twrp.img` (TWRP for star2lte) and `payload\uefi.img` (from
      `firmware\`) next to the installer.
- [ ] `payload\drivers\` containing the built UFS and touch driver packages.
- [ ] At least ~80 GB free on the PC drive that holds the work folder.

## Run

1. **Enable experimental steps.** On the Install page tick *Run experimental
   steps*. Nothing destructive runs until you do.
2. **Check this PC / Identify the phone.** These must pass green. If the phone
   shows as unauthorized, accept the debugging prompt on the phone.
3. **Get Windows / Build the Windows image.** Pick your Windows 11 **22621/22631
   ARM64** media and your account on the Windows page first. The image build runs
   entirely on the PC (30+ minutes) and produces `work\out\windows.img` and
   `work\out\esp`. ✅ Confirm both exist before continuing.
4. **Unlock the bootloader.** Guided — follow the on-screen steps. This wipes
   Android. Re-enable USB debugging afterwards.
5. **Install TWRP.** Put the phone in Download mode when asked. Boot TWRP
   immediately after flashing (Volume Up + Bixby + Power) so stock recovery is
   not restored.
6. **Back up the phone.** ✅ Confirm `backups\<serial>\efs.img` and
   `backup-manifest.json` exist and verified. **Copy this folder somewhere safe
   now** — it is how you return to stock.
7. **Prepare partitions.** Verifies the layout by name; no writes.
8. **Copy Windows to the phone.** Writes `windows.img` to USERDATA and the boot
   files to the EFI partition, verifying every window. This is long.
9. **Install UEFI.** Writes `uefi.img` to BOOT. RECOVERY keeps TWRP.
10. **First boot.** The phone reboots into Windows; OOBE finishes on its own and
    signs you in to the account you chose.

## What to record if it stops

For the stage that failed, capture:

- the stage name and the exact message in the installer,
- the **Activity** log (Open logs folder on the Install or Tools page),
- for a device step, the output of `adb devices` and, in TWRP,
  `ls -l /dev/block/by-name`.

Known-risky points on a first run (report back with the above):

- **Image build** — bcdboot must produce ARM64 boot files; the VHDX is created
  4Kn to match the phone's UFS. If Windows later bugchecks
  `INACCESSIBLE_BOOT_DEVICE`, suspect sector size or the BCD `locate` retarget.
- **Copy Windows / First boot** — if the phone reaches UEFI but not Windows, the
  boot files or BCD device retarget are the first suspects; TWRP still boots, so
  it is recoverable.

## Recovery

If Windows does not boot, the phone still boots **TWRP** from RECOVERY. Restore
Android by reflashing stock firmware in Download mode (Odin/Heimdall) and, if
needed, restoring `efs.img` from your backup. The installer never writes EFS,
BOOT-as-recovery, or the GPT.
