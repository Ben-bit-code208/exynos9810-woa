#!/sbin/sh
# SPDX-License-Identifier: BSD-2-Clause-Patent
#
# winre-actions.sh - the Troubleshoot repair actions for the WinRE recovery.
#
#   clear-ticket    zero the Android bootloader control block in MISC (the same
#                   byte range the installer's BootRouteService clears), insmod
#                   the baked-in rwd1_ack.ko, then clear the retained startup
#                   records (RWD1 and P3, judged on every byte).
#   repair-volume   unmount /data, run fsck.ntfs on the Windows volume, remount.
#   repair-boot     run fsck.fat on the two FAT boot partitions (CACHE, SYSTEM).
#   prepare-boot    silent clear-ticket, run by "Continue" before it restarts
#                   into Windows (log: /tmp/winre-prepare-boot.log).
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

# Copies the whole RWD1 (64 B) and P3 (128 B) records into $EVDIR through the
# evidence reader's debugfs blobs. Returns non-zero when they cannot be read.
EVDIR=/tmp/winre-records
read_records() {
	reader="$MODDIR/rwd1_evidence_reader.ko"
	[ -f "$reader" ] || return 1
	grep -q ' /sys/kernel/debug ' /proc/mounts 2>/dev/null || mount -t debugfs none /sys/kernel/debug 2>/dev/null
	rmmod rwd1_evidence_reader 2>/dev/null
	insmod "$reader" 2>/dev/null || return 1
	mkdir -p "$EVDIR"
	ok=0
	cp /sys/kernel/debug/rwd1-evidence/rwd1-second "$EVDIR/rwd1" 2>/dev/null &&
		cp /sys/kernel/debug/rwd1-evidence/p3-record "$EVDIR/p3" 2>/dev/null && ok=1
	rmmod rwd1_evidence_reader 2>/dev/null
	[ "$ok" = "1" ]
}

# Number of non-zero bytes in a file.
nonzero_bytes() {
	tr -d '\000' < "$1" 2>/dev/null | wc -c | tr -d ' '
}

# Clears the retained startup records before Windows is started from here. The
# previous start is over, so anything left in either record is stale. The
# firmware's P3 startup gate halts - with the watchdog off, so the phone sits on
# the Samsung logo for good - on anything but an all-zero record, and the record
# that hung the reference phone had a ZERO first word with stray bits further
# in: every byte counts, never just the magic. A left-over RWD1 record sends the
# next start back here. Stock Android, Download mode, a power loss and a forced
# reset all leave such bytes behind. Mirrors BootRouteService on the host.
clear_startup_records() {
	if ! read_records; then
		echo "The startup records could not be read (rwd1_evidence_reader.ko missing or refused)."
		return 1
	fi
	r=$(nonzero_bytes "$EVDIR/rwd1")
	p=$(nonzero_bytes "$EVDIR/p3")
	echo "Recovery record (RWD1): $r of 64 bytes set"
	echo "Startup record (P3):    $p of 128 bytes set"
	if [ "$r" = "0" ] && [ "$p" = "0" ]; then
		echo "Both startup records are clear."
		return 0
	fi
	if [ "$r" != "0" ]; then
		rmmod rwd1_clear_poc 2>/dev/null
		if [ -f "$MODDIR/rwd1_clear_poc.ko" ] &&
			insmod "$MODDIR/rwd1_clear_poc.ko" authorize=CLEAR_INVALID_RWD1_SUPERVISED_V1 2>/dev/null; then
			echo "Cleared the recovery record."
		else
			echo "WARNING: could not clear the recovery record."
		fi
		rmmod rwd1_clear_poc 2>/dev/null
	fi
	if [ "$p" != "0" ]; then
		rmmod pram_smp_clear_poc 2>/dev/null
		if [ -f "$MODDIR/pram_smp_clear_poc.ko" ] && insmod "$MODDIR/pram_smp_clear_poc.ko" 2>/dev/null; then
			echo "Cleared the startup record (the Samsung-logo gate)."
		else
			echo "WARNING: could not clear the startup record."
		fi
		rmmod pram_smp_clear_poc 2>/dev/null
	fi
	if read_records && [ "$(nonzero_bytes "$EVDIR/rwd1")" = "0" ] && [ "$(nonzero_bytes "$EVDIR/p3")" = "0" ]; then
		echo "Both read back clear."
		return 0
	fi
	echo "WARNING: the startup records did not read back clear."
	return 1
}

# Zeroes Android's bootloader control block in MISC (e.g. 'boot-recovery',
# which makes S-Boot re-enter recovery): the range BootRouteService clears.
clear_misc() {
	misc=$(resolve_part MISC) || { echo "MISC partition not found; nothing to clear."; return 1; }
	echo "MISC partition: $misc"
	cmd=$(dd if="$misc" bs=32 count=1 2>/dev/null | tr -d '\000')
	if [ -z "$cmd" ]; then
		echo "No boot command set in MISC."
		return 0
	fi
	echo "Current boot command: '$cmd'"
	if dd if=/dev/zero of="$misc" bs=2048 count=1 conv=notrunc,fsync 2>/dev/null; then
		sync
		echo "Cleared the bootloader control block (first 2048 bytes)."
	else
		echo "WARNING: could not write MISC (is it read-only?)."
	fi
}

# Acknowledges a RECOVERY_PENDING record with the baked-in rwd1_ack.ko.
ack_record() {
	ack="$MODDIR/rwd1_ack.ko"
	if [ ! -f "$ack" ]; then
		echo "rwd1_ack.ko is not baked into this recovery; skipping the acknowledgement."
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
		0)         echo "Acknowledged a pending recovery record${before:+ (was $before)}." ;;
		-1 | -117) echo "No pending recovery record to acknowledge." ;;
		"")        echo "Could not read the module's status field." ;;
		*)         echo "The record was not acknowledged (status $status)." ;;
	esac
}

# ---------------------------------------------------------------------------

clear_ticket() {
	echo "Clear boot ticket"
	echo "================="
	echo ""
	clear_misc
	echo ""
	echo "-- retained recovery record ----------------------------------"
	ack_record
	echo ""
	echo "-- startup records (Samsung-logo gate) -----------------------"
	clear_startup_records
	echo ""
	echo "The next restart should go straight to Windows."
}

# Runs silently before "Continue" restarts into Windows, so a phone that landed
# here after a power loss, a failed start or the key combination does not stop
# at the Samsung logo on the way back. Logs to /tmp only: it must not mount the
# Windows volume (the TWRP reboot that follows handles its own file systems).
prepare_boot() {
	echo "Prepare Windows start: $(date 2>/dev/null || echo '?')"
	clear_misc
	ack_record
	clear_startup_records
	sync
	return 0
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

# prepare-boot runs from the "Continue" tile right before TWRP reboots: it logs
# to /tmp only and never mounts storage (pick_outdir would mount /data).
if [ "${1:-}" = "prepare-boot" ]; then
	prepare_boot >> /tmp/winre-prepare-boot.log 2>&1
	exit 0
fi

OUT=$(pick_outdir)
LOGFILE="$OUT/actions-latest.txt"

run() {
	case "$1" in
		clear-ticket)  clear_ticket ;;
		repair-volume) repair_volume ;;
		repair-boot)   repair_boot ;;
		*) echo "usage: winre-actions.sh [clear-ticket|repair-volume|repair-boot|prepare-boot]"; return 2 ;;
	esac
}

run "${1:-}" 2>&1 | tee "$LOGFILE"
echo ""
echo "Saved: $LOGFILE"
