# On-device validation checklist

Use this to validate the installer on real hardware (a new release, a new phone
firmware or a new Windows build). Every stage passed it end to end on the
reference phone, from stock Android to the Windows desktop. Work through it in
order and note where it stops.

## Before you start

- [ ] Galaxy S9+ **SM-G965F** (Exynos 9810). No other model.
- [ ] A full charge, and a **USB cable that does data** (many only charge).
- [ ] On the PC: run the installer **as administrator**.
- [ ] Complete the **Set up** page: *Set up automatically* installs adb, Heimdall
      and Zadig and downloads the verified UEFI and driver payloads; you add the
      Samsung USB driver installer and the TWRP `twrp-*-star2lte.img`. Using a
      local build folder instead of a release is fine.
- [ ] At least ~80 GB free on the PC drive that holds the work folder.

## Run

1. **Start.** On the Install page press *Install*. Nothing is written to the
   phone before the backup step.
2. **Check this PC / Identify the phone.** These must pass green. If the phone
   shows as unauthorized, accept the debugging prompt on the phone.
3. **Get Windows / Build the Windows image.** Pick any Windows 11 **ARM64**
   media (22621.2428 and 22621.7582 have matching UEFI builds; other builds get the
   nearest one and may stop at the Samsung logo) and your account on the Windows page first. The image build runs
   entirely on the PC (30+ minutes) and produces `work\out\windows.img` and
   `work\out\esp`. ✅ Confirm both exist before continuing.
4. **Unlock the bootloader.** Guided — follow the on-screen steps. This wipes
   Android. Re-enable USB debugging afterwards.
5. **Install TWRP.** The installer restarts the phone into Download mode, writes
   TWRP to RECOVERY and BOOT, and restarts it straight into TWRP. ✅ Confirm the
   phone shows TWRP without any key presses. If Android starts instead, report it:
   Android restores its own recovery.
6. **Back up the phone.** ✅ Confirm `backups\phone-<hash>\efs.img` and
   `backup-manifest.json` exist and verified. **Copy this folder somewhere safe
   now** — it is how you return to stock.
7. **Prepare partitions.** Verifies the layout by name; no writes.
8. **Copy Windows to the phone.** Writes `windows.img` to USERDATA and the boot
   files to the EFI partition, verifying every window. This is long.
9. **Install UEFI.** Writes the UEFI build for your Windows build (from
   `firmware.json`) to BOOT. RECOVERY keeps TWRP.
10. **First boot.** The installer clears the phone's startup records, the phone
    reboots into Windows, and OOBE finishes on its own (at 275 DPI display
    scaling) and signs you in to the account you chose.

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
