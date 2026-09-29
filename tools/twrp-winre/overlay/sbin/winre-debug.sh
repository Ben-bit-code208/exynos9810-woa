#!/sbin/sh
# SPDX-License-Identifier: BSD-2-Clause-Patent
#
# winre-debug.sh - one-tap diagnostic bundle for the WinRE recovery shell.
#
# Everything that has ever been needed to explain a failed Windows-on-Exynos
# boot, collected while the phone is still in front of you. Written to
# /sdcard/WinRE so it survives the reboot and is reachable over MTP.

set -u

STAMP=$(date +%Y%m%d-%H%M%S 2>/dev/null || echo unknown)

grep -q ' /sdcard ' /proc/mounts 2>/dev/null || mount /sdcard >/dev/null 2>&1
grep -q ' /data ' /proc/mounts 2>/dev/null || mount /data >/dev/null 2>&1

OUT=""
for base in /sdcard /data/media/0 /data/media /external_sd /tmp; do
	[ -d "$base" ] || continue
	if mkdir -p "$base/WinRE/debug-$STAMP" 2>/dev/null; then
		OUT="$base/WinRE/debug-$STAMP"
		break
	fi
done
[ -n "$OUT" ] || { echo "Nowhere writable to collect into."; exit 1; }

echo "Collecting into $OUT"

grab() {
	label="$1"
	dest="$2"
	shift 2
	if "$@" > "$OUT/$dest" 2>&1; then
		echo "  ok      $label"
	else
		echo "  partial $label"
	fi
}

# --- persistent RAM ---------------------------------------------------------
if [ -d /sys/fs/pstore ]; then
	mkdir -p "$OUT/pstore"
	for f in /sys/fs/pstore/*; do
		[ -f "$f" ] && cp -f "$f" "$OUT/pstore/" 2>/dev/null
	done
	echo "  ok      pstore ($(ls "$OUT/pstore" 2>/dev/null | wc -l | tr -d ' ') record(s))"
	[ -x /sbin/winre-pram.sh ] && /sbin/winre-pram.sh dump > "$OUT/pram-raw.txt" 2>/dev/null
	[ -x /sbin/winre-pram.sh ] && /sbin/winre-pram.sh summary > "$OUT/pram-summary.txt" 2>&1
else
	echo "  absent  pstore"
fi

# --- kernel / recovery ------------------------------------------------------
grab "dmesg"        dmesg.txt        dmesg
grab "last kmsg"    last-kmsg.txt    cat /proc/last_kmsg
grab "cmdline"      cmdline.txt      cat /proc/cmdline
grab "mounts"       mounts.txt       cat /proc/mounts
grab "meminfo"      meminfo.txt      cat /proc/meminfo
grab "recovery.log" recovery.log     cat /tmp/recovery.log
grab "winre status" winre-status.txt cat /tmp/s9woa/status
grab "statuswatch"  statuswatch.txt  cat /tmp/winre-statuswatch.log

# --- storage topology -------------------------------------------------------
grab "partitions"   partitions.txt   cat /proc/partitions
grab "by-name"      by-name.txt      ls -la /dev/block/by-name
grab "props"        getprop.txt      getprop

# The BCB is how the bootloader is told where to go next; a stale one is a
# classic cause of "it keeps landing back in recovery".
for miscpath in /dev/block/by-name/misc /dev/block/by-name/MISC \
                /dev/block/platform/11120000.ufs/by-name/MISC; do
	if [ -e "$miscpath" ]; then
		dd if="$miscpath" of="$OUT/misc-bcb.bin" bs=2048 count=1 2>/dev/null &&
			echo "  ok      BCB (misc)"
		break
	fi
done

# --- UFS controller state ---------------------------------------------------
for d in /sys/bus/platform/drivers/ufshcd/*; do
	[ -d "$d" ] || continue
	mkdir -p "$OUT/ufs"
	for a in "$d"/*; do
		[ -f "$a" ] && cp -f "$a" "$OUT/ufs/" 2>/dev/null
	done
	echo "  ok      ufshcd sysfs"
	break
done

(cd "$(dirname "$OUT")" && ls -la "$(basename "$OUT")") > "$OUT/manifest.txt" 2>&1

echo ""
echo "Done. $(ls "$OUT" | wc -l | tr -d ' ') item(s) in:"
echo "$OUT"
