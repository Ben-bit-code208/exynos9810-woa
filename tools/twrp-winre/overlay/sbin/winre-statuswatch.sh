#!/sbin/sh
# SPDX-License-Identifier: BSD-2-Clause-Patent
#
# winre-statuswatch.sh - raise the "Installing Windows" screen while the
# installer is writing to the phone, and take it down when it stops.
#
# WHY A STATUS FILE INSTEAD OF WATCHING adb push
# The research build watched adbd's open file descriptors, because back then the
# installer pushed the whole Windows image straight over `adb push`. It no longer
# does: it pushes small chunks into TWRP's RAM (/tmp/s9woa) and writes the disk
# with a shell `dd if=/tmp/... of=/dev/block/sdaNN`, so adbd never opens the
# destination and the fd watcher sees nothing useful. Two detectors replace it:
#
#   PRIMARY   the installer writes /tmp/s9woa/status with key=value lines
#             (phase=, percent=, label=, done_bytes=, total_bytes=) and rewrites
#             it as it goes. That is authoritative: it names the phase ("Copying
#             Windows", "Writing boot files", "Installing firmware") and carries
#             a percentage, so the screen can say what is actually happening.
#             The installer deletes the file when it finishes; a file older than
#             STALE seconds is treated as a crashed installer and ignored.
#
#   FALLBACK  if there is no fresh status file but some process has
#             `of=/dev/block/` on its command line (a dd writing a block device),
#             raise a generic "Installing Windows" screen anyway. This costs one
#             grep across /proc/*/cmdline per tick and covers a manual dd or an
#             installer too old to write the status file.
#
# WHY THE BAR IS INDETERMINATE AND THE PERCENT ONLY IN THE CAPTION
# The only channel from a shell into a TWRP GUI variable is openrecoveryscript,
# and every ORS session bounces the display through singleaction_page (which
# carries its own console and progress bar). One bounce to raise and one to clear
# are invisible; a percentage pushed twice a second would strobe. So the animated
# bar stays indeterminate and the *caption* carries the phase and a coarse
# percent, updated only when the composed caption string actually changes and no
# more often than UPDATE_MS. A live progress bar bound to a variable would need a
# poke on every step and is deliberately not attempted; the phase caption is the
# meaningful signal here.
#
# SAFETY RULES (kept from the research pushwatch, each verified there)
#   * raise ONLY when the last "Set page:" in the recovery log is one of
#     RAISE_PAGES - static menus where no threaded action can be in flight;
#   * the clear is UNCONDITIONAL and retried, so the screen always comes back;
#   * every `twrp` call is wrapped in `timeout -t 8`, so a wedged GUI stops this
#     watcher rather than the other way round;
#   * a minimum dwell holds the screen a beat so an instant write still shows;
#   * the copy page's Home/Back go via 'main', which resets the flag, so a dead
#     watcher can never trap the UI.

LOG=/tmp/winre-statuswatch.log
: > "$LOG"
exec >>"$LOG" 2>&1

STATUS=/tmp/s9woa/status
POLL=0.25            # seconds per tick
STALE=30             # status file older than this = installer gone
IDLE_TICKS=8         # ~2 s of no activity before clearing
MIN_DWELL_TICKS=12   # ~3 s minimum on screen even for an instant write
UPDATE_MS=5000       # never repaint the caption faster than this
ORS_TIMEOUT=8        # busybox 1.22 wants `timeout -t SECS`
CLEAR_TRIES=4
RAISE_PAGES="winre_home winre_troubleshoot winre_advanced winre_advanced2 winre_output"

twrp_set() {
	timeout -t "$ORS_TIMEOUT" twrp set "$1" "$2" >/dev/null 2>&1
}

cur_page() {
	tail -n 300 /tmp/recovery.log 2>/dev/null \
		| grep "Set page:" | tail -n 1 | sed "s/.*'\(.*\)'.*/\1/"
}

now_ms() {
	echo $(( $(date +%s 2>/dev/null || echo 0) * 1000 ))
}

# Read the status file into LABEL/PCT and decide freshness. Existence is the
# primary signal (the installer deletes the file when done); the age check is a
# safety net for a crashed installer, and is skipped if stat is unavailable.
read_status() {
	LABEL=""; PCT=""
	[ -f "$STATUS" ] || return 1
	mt=$(stat -c %Y "$STATUS" 2>/dev/null)
	if [ -n "$mt" ]; then
		nw=$(date +%s 2>/dev/null || echo "$mt")
		[ $((nw - mt)) -le "$STALE" ] || return 1
	fi
	while IFS='=' read -r k v; do
		case "$k" in
			label) LABEL=$v ;;
			percent) PCT=$v ;;
		esac
	done < "$STATUS"
	return 0
}

dd_writer() {
	grep -qa "of=/dev/block/" /proc/[0-9]*/cmdline 2>/dev/null
}

compose() {
	c="$LABEL"
	[ -z "$c" ] && c="Installing Windows"
	# A single '%' is safe in a TWRP text node (gui_parse_text stops at an
	# unmatched one); avoid emitting two.
	if [ -n "$PCT" ]; then
		c="$c - ${PCT}%"
	fi
	echo "$c"
}

STATE=idle
HELD=0
IDLE=0
LASTCAP=""
LASTUPD=0

echo "=== winre statuswatch: $(date 2>/dev/null) ==="

while :; do
	active=0
	cap="Installing Windows"
	if read_status; then
		active=1
		cap=$(compose)
	elif dd_writer; then
		active=1
		cap="Installing Windows"
	fi

	[ "$STATE" = busy ] && HELD=$((HELD + 1))

	if [ "$active" = 1 ]; then
		IDLE=0
		if [ "$STATE" = idle ]; then
			page=$(cur_page)
			ok=0
			for p in $RAISE_PAGES; do
				[ "$page" = "$p" ] && ok=1 && break
			done
			if [ "$ok" = 1 ]; then
				# Aim the return page and the caption before raising the flag,
				# so the page draws correct the instant it appears.
				twrp_set winre_push_back "$page"
				twrp_set winre_push_sub "$cap"
				twrp_set tw_screen_timeout_secs 0
				if twrp_set winre_push 1; then
					STATE=busy; HELD=0; LASTCAP="$cap"; LASTUPD=$(now_ms)
					echo "raised on $page: $cap"
				else
					echo "raise poke failed; will retry next tick"
				fi
			else
				echo "active but page=$page is not interruptible; leaving the UI alone"
			fi
		elif [ "$cap" != "$LASTCAP" ]; then
			nowm=$(now_ms)
			if [ $((nowm - LASTUPD)) -ge "$UPDATE_MS" ]; then
				twrp_set winre_push_sub "$cap"
				LASTCAP="$cap"; LASTUPD=$nowm
				echo "caption: $cap"
			fi
		fi
	elif [ "$STATE" = busy ]; then
		IDLE=$((IDLE + 1))
		if [ "$HELD" -ge "$MIN_DWELL_TICKS" ] && [ "$IDLE" -ge "$IDLE_TICKS" ]; then
			n=0
			while [ "$n" -lt "$CLEAR_TRIES" ]; do
				twrp_set winre_push 0 && break
				n=$((n + 1))
				echo "  clear attempt $n failed; retrying"
				sleep 1
			done
			twrp_set winre_push_back winre_home
			echo "cleared after $HELD ticks on screen"
			STATE=idle; HELD=0; IDLE=0; LASTCAP=""
		fi
	fi

	sleep "$POLL"
done
