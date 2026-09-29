#!/sbin/sh
# SPDX-License-Identifier: BSD-2-Clause-Patent
#
# winre-actions.sh - the Troubleshoot repair actions for the WinRE recovery.
#
#   clear-ticket    zero the Android bootloader control block in MISC (the same
#                   byte range the installer's BootRouteService clears), then
#                   insmod the baked-in rwd1_ack.ko and report /proc/rwd1_ack.
#   repair-volume   unmount /data, run fsck.ntfs on the Windows volume, remount.
#   repair-boot     run fsck.fat on the two FAT boot partitions (CACHE, SYSTEM).
#
# Every action prints a plain report that /sbin/winre-gui.sh streams onto the
# WinRE output page. Nothing here reformats or deletes user data.

set -u

# Baked-in GPL modules (our own): built for this exact TWRP kernel.
MODDIR=/sbin/s9woa
STAMP=$(date +%Y%m%d-%H%M%S 2>/dev/null || echo unknown)

pick_outdir() {
	grep -q ' /sdcard ' /proc/mounts 2>/dev/null || mount /sdcard >/dev/null 2>&1
	grep -q ' /data ' /proc/mounts 2>/dev/null || mount /data >/dev/null 2>&1
	for base in /sdcard /data/media/0 /data/media /external_sd /tmp; do
		[ -d "$base" ] || continue
		if mkdir -p "$base/WinRE" 2>/dev/null && touch "$base/WinRE/.w" 2>/dev/null; then
			rm -f "$base/WinRE/.w"
			echo "$base/WinRE"
			return 0
		fi
	done
	mkdir -p /tmp/WinRE 2>/dev/null
	echo /tmp/WinRE
}

# Resolve a by-name partition to its block node, trying both the direct symlink
# dir and the platform path, and matching the name case-insensitively (this
# phone spells them upper case: USERDATA, CACHE, SYSTEM, MISC). Prints the node
# path on stdout, or nothing.
resolve_part() {
	want=$1
	for dir in /dev/block/by-name /dev/block/platform/11120000.ufs/by-name \
	           /dev/block/bootdevice/by-name; do
		[ -d "$dir" ] || continue
		for cand in "$dir/$want" "$dir/$(echo "$want" | tr 'A-Z' 'a-z')" \
		            "$dir/$(echo "$want" | tr 'a-z' 'A-Z')"; do
			[ -e "$cand" ] && { readlink -f "$cand" 2>/dev/null || echo "$cand"; return 0; }
		done
	done
	return 1
}

# ---------------------------------------------------------------------------

clear_ticket() {
	echo "Clear boot ticket"
	echo "================="
	echo ""
	misc=$(resolve_part MISC) || { echo "MISC partition not found; nothing to clear."; return 1; }
	echo "MISC partition: $misc"

	# The Android bootloader_message command field is the first 32 bytes; a
	# non-empty value here (e.g. 'boot-recovery') is what makes S-Boot re-enter
	# recovery. Report it before clearing.
	cmd=$(dd if="$misc" bs=32 count=1 2>/dev/null | tr -d '\000')
	if [ -n "$cmd" ]; then
		echo "Current boot command: '$cmd'"
	else
		echo "No boot command set in MISC."
	fi

	# Zero the first 2048 bytes, exactly the range BootRouteService clears.
	if dd if=/dev/zero of="$misc" bs=2048 count=1 conv=notrunc,fsync 2>/dev/null; then
		sync
		echo "Cleared the bootloader control block (first 2048 bytes)."
	else
		echo "WARNING: could not write MISC (is it read-only?)."
	fi

	echo ""
	echo "-- retained recovery record ----------------------------------"
	ack="$MODDIR/rwd1_ack.ko"
	if [ ! -f "$ack" ]; then
		echo "rwd1_ack.ko is not baked into this recovery; skipping."
		echo "The MISC clear above is still applied."
		return 0
	fi
	rmmod rwd1_ack 2>/dev/null
	insmod "$ack" 2>/dev/null
	line=$(cat /proc/rwd1_ack 2>/dev/null)
	rmmod rwd1_ack 2>/dev/null
	if [ -z "$line" ]; then
		echo "The rwd1_ack module produced no result (already loaded, or blocked)."
		return 0
	fi
	echo "raw: $line"
	# Map the status code to words: 0 cleared, -1 nothing pending, -117 invalid.
	status=$(echo "$line" | sed -n 's/.*status=\(-\{0,1\}[0-9]\{1,\}\).*/\1/p')
	before=$(echo "$line" | sed -n 's/.*state_before=\(0x[0-9A-Fa-f]\{1,\}\).*/\1/p')
	case "$status" in
		0)    echo "Cleared a pending recovery record${before:+ (was $before)}." ;;
		-1)   echo "No pending recovery record (nothing to acknowledge)." ;;
		-117) echo "No valid recovery record - normal after a full power-off." ;;
		"")   echo "Could not read the module's status field." ;;
		*)    echo "The record was left as is (status $status)." ;;
	esac
	echo ""
	echo "The next restart should go straight to Windows."
}

# ---------------------------------------------------------------------------

repair_volume() {
	echo "Repair Windows volume"
	echo "====================="
	echo ""
	dev=$(resolve_part USERDATA) || { echo "USERDATA partition not found."; return 1; }
	echo "Windows volume: $dev"

	if grep -q ' /data ' /proc/mounts 2>/dev/null; then
		echo "Unmounting /data before the check..."
		umount /data 2>/dev/null || umount -l /data 2>/dev/null
	fi
	if grep -q ' /data ' /proc/mounts 2>/dev/null; then
		echo "WARNING: /data is still mounted; the check may refuse to run."
	fi

	echo ""
	echo "-- fsck.ntfs -------------------------------------------------"
	# fsck.ntfs here is ntfs-3g's ntfsfix: it repairs fundamental NTFS
	# inconsistencies, resets the journal, and schedules Windows' own chkdsk on
	# the next boot. It does NOT delete files. Run it plain (no destructive
	# flags) so the conservative behaviour is what happens.
	if command -v fsck.ntfs >/dev/null 2>&1; then
		fsck.ntfs "$dev" 2>&1
		rc=$?
		echo "fsck.ntfs exit code: $rc"
	else
		echo "fsck.ntfs is not present in this recovery."
		rc=127
	fi

	echo ""
	echo "-- remount ---------------------------------------------------"
	mkdir -p /data 2>/dev/null
	if mount.ntfs "$dev" /data 2>/dev/null || mount -t ntfs "$dev" /data 2>/dev/null; then
		echo "Remounted the Windows volume at /data."
	else
		echo "Could not remount /data; TWRP will mount it again on the next action."
	fi
	echo ""
	if [ "$rc" = "0" ]; then
		echo "Check complete. Windows will finish the repair on its next start."
	else
		echo "Check finished with warnings (code $rc). See the report above."
	fi
}

# ---------------------------------------------------------------------------

repair_boot() {
	echo "Repair boot partitions"
	echo "======================"
	echo ""
	if ! command -v fsck.fat >/dev/null 2>&1; then
		echo "fsck.fat is not present in this recovery."
		return 1
	fi
	any=0
	for name in CACHE SYSTEM; do
		dev=$(resolve_part "$name") || { echo "$name: partition not found, skipping."; echo ""; continue; }
		any=1
		echo "-- $name ($dev) ----------------------------------------------"
		# Make sure the firmware's FAT partitions are not mounted underneath us.
		for mp in /cache /system /system_root; do
			if grep -q " $mp " /proc/mounts 2>/dev/null; then
				dm=$(grep " $mp " /proc/mounts | awk '{print $1}')
				[ "$dm" = "$dev" ] && umount "$mp" 2>/dev/null
			fi
		done
		# -a: automatically apply the safe repairs, no prompts, never reformat.
		fsck.fat -a "$dev" 2>&1
		echo "fsck.fat exit code: $?"
		echo ""
	done
	[ "$any" = "1" ] || { echo "Neither CACHE nor SYSTEM was found."; return 1; }
	echo "Done. The boot partitions were checked in place; nothing was reformatted."
}

# ---------------------------------------------------------------------------

OUT=$(pick_outdir)
LOGFILE="$OUT/actions-latest.txt"

run() {
	case "$1" in
		clear-ticket)  clear_ticket ;;
		repair-volume) repair_volume ;;
		repair-boot)   repair_boot ;;
		*) echo "usage: winre-actions.sh [clear-ticket|repair-volume|repair-boot]"; return 2 ;;
	esac
}

run "${1:-}" 2>&1 | tee "$LOGFILE"
echo ""
echo "Saved: $LOGFILE"
