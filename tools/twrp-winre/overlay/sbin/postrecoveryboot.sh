#!/sbin/sh
# SPDX-License-Identifier: BSD-2-Clause-Patent
#
# postrecoveryboot.sh - TWRP runs this once per boot, from main() in twrp.cpp.
# It is our only chance to undo a theme that traps the UI, and it launches the
# copy-status watcher.
#
# Why this exists: /sdcard/TWRP/theme/ui.zip OVERRIDES the theme baked into the
# recovery image. A bad zip therefore survives reflashing the recovery, and if
# it wedges the UI there is no adb to delete it with - adb only comes up once the
# recovery binary reaches its "Starting MTP" step and writes sys.usb.config,
# which init.recovery.usb.rc waits on to bring the gadget up at all. A wedged
# startup takes adb AND MTP down together, leaving no way in. So the recovery
# image has to be able to heal itself without any host help.
#
# ORDERING, measured from a real recovery.log rather than assumed:
#   I:Loading package: TWRP (/data/media/TWRP/theme/ui.zip)
#   I:Set page: 'clear_vars' -> the theme's startup chain
#   Running boot script...            <- this script
# The custom theme is loaded, and its startup chain has already run, before we
# get control. So this heals the NEXT boot, not the current one. That is
# sufficient - one extra reboot beats being permanently locked out - and the
# installer additionally deletes a stale zip over adb before it relies on the
# theme, which covers the current boot for the automated path. See the docs.
#
# Everything here is best-effort and must never abort the boot.

LOG=/tmp/winre-selfheal.log
exec >>"$LOG" 2>&1
echo "=== winre self-heal: $(date 2>/dev/null) ==="

# Do NOT hardcode a storage path or filesystem. Internal storage is /data/media
# on this build (NOT /data/media/0), and /data is sda25 formatted NTFS/vfat, not
# the ext4 USERDATA the fstab describes, because it was repartitioned for the
# Windows-on-ARM work.
ROOTS="/data/media /data/media/0 /sdcard /external_sd"

if grep -q ' /data ' /proc/mounts 2>/dev/null; then
	echo "/data already mounted: $(grep ' /data ' /proc/mounts)"
else
	DEV=/dev/block/platform/11120000.ufs/by-name/USERDATA
	if [ -e "$DEV" ] && mount -o rw "$DEV" /data 2>/dev/null; then
		echo "mounted $DEV on /data (autodetected fs)"
	else
		echo "/data not mounted and could not mount it; no theme could have loaded"
	fi
fi

found=0
failed=0
for root in $ROOTS; do
	zip="$root/TWRP/theme/ui.zip"
	[ -f "$zip" ] || continue
	found=$((found + 1))
	echo "stale custom theme: $(ls -l "$zip" 2>/dev/null)"
	mv -f "$zip" "$zip.disabled" 2>/dev/null || rm -f "$zip" 2>/dev/null
	[ -f "$zip" ] && failed=$((failed + 1))
done

for hit in $(find /data/media /sdcard -maxdepth 4 -path '*/TWRP/theme/ui.zip' 2>/dev/null); do
	found=$((found + 1))
	echo "stale custom theme (find): $hit"
	mv -f "$hit" "$hit.disabled" 2>/dev/null || rm -f "$hit" 2>/dev/null
	[ -f "$hit" ] && failed=$((failed + 1))
done

sync
if [ "$found" = "0" ]; then
	echo "RESULT: no custom theme present"
elif [ "$failed" = "0" ]; then
	echo "RESULT: $found custom theme(s) neutralised; reboot recovery for a clean UI"
else
	echo "RESULT: FAILED to remove $failed of $found custom theme(s)"
fi

# A leftover ORS script is replayed at boot and its commands obeyed, and TWRP's
# response to a script that fails is to reboot into the system partition - which
# looks exactly like "recovery refuses to stay up".
for stale in /cache/recovery/openrecoveryscript /data/cache/recovery/openrecoveryscript; do
	if [ -f "$stale" ]; then
		echo "removing stale ORS: $stale"
		rm -f "$stale" 2>/dev/null
	fi
done

echo "=== done ==="

# Start the copy-status watcher. Deliberately last: everything above is
# self-heal work that must not be delayed by it, and TWRP waits for this script
# to exit before carrying on with boot, so the watcher has to be detached.
if [ -x /sbin/winre-statuswatch.sh ]; then
	setsid /sbin/winre-statuswatch.sh </dev/null >/dev/null 2>&1 &
	echo "started /sbin/winre-statuswatch.sh (pid $!)"
fi

exit 0
