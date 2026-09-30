#!/sbin/sh
# SPDX-License-Identifier: BSD-2-Clause-Patent
#
# winre-statuswatch.sh - show the "Installing Windows" screen while the
# installer writes to the phone, with a live percentage, and never let TWRP
# flash its own "Running Recovery Commands" page over it.
#
# WHAT IT WATCHES
#   PRIMARY   /tmp/s9woa/status, a key=value file the installer rewrites as it
#             goes (phase=, label=, percent=, detail=, done_bytes=,
#             total_bytes=). The installer appends the write to shell commands
#             it runs anyway, so keeping it current costs no extra round trip,
#             and it replaces the file atomically (write + mv). It deletes the
#             file when a phase ends. A file whose contents have not changed
#             for STALE seconds is treated as a crashed installer.
#   FALLBACK  with no fresh status file, a process with `of=/dev/block/` on its
#             command line (a dd writing a block device) raises a generic
#             screen, checked every couple of seconds.
#
# HOW THE SCREEN IS DRIVEN - AND WHY IT NO LONGER BLINKS
#   The first version pushed the caption and percentage into GUI variables
#   with `twrp set`. TWRP runs every openrecoveryscript command except
#   changepage/reloadtheme/dumpstrings as a GUI action on singleaction_page and
#   then switches back (gui.cpp ors_command_read), so each update flashed the
#   stock page - blue header, TeamWin logo, a console of red and white lines -
#   for a frame. Now:
#     * live values travel as Android properties (setprop s9woa.*). TWRP's
#       DataManager resolves "property.<name>" with property_get() on every
#       read, so %property.s9woa.label% in a <text> and a <progressbar> bound
#       to property.s9woa.pct follow them with no page change at all;
#     * the screen is raised and cleared with `twrp changepage=<page>`, which
#       TWRP serves directly on the GUI thread: one call to raise, one to
#       clear, per install phase (and one more only if a phase switches
#       between a percentage bar and the plain sweep);
#     * `twrp set` is never used.
#   The CLI exits 0 even when TWRP refuses a command (it refuses everything
#   while tw_busy is set), so every page change is verified against the
#   "Set page:" lines TWRP logs to /tmp/recovery.log.
#
# SAFETY RULES (from the research push watcher, each verified there)
#   * raise ONLY over the static WinRE menus in RAISE_PAGES - never over a
#     running report, a confirmation or a TWRP operation;
#   * clear only if the install screen is still what is showing, and go back
#     to the page it covered; if the user already left it (the back arrow,
#     Back or Home on it all go to 'main'), do not navigate, and do not raise
#     it again for the same phase;
#   * every `twrp` call is wrapped in `timeout -t 8` (busybox 1.22 syntax), so a
#     wedged GUI stops this watcher rather than the other way round;
#   * a minimum dwell keeps an instant phase from strobing the screen.

LOG=/tmp/winre-statuswatch.log
: > "$LOG"
exec >>"$LOG" 2>&1

STATUS=/tmp/s9woa/status
TICK_US=250000        # one tick; busybox 1.22 sleep takes whole seconds only
STALE=300             # s with no change to the status file = installer gone
IDLE_TICKS=8          # ~2 s with no activity before clearing
MIN_DWELL_TICKS=12    # ~3 s on screen at least
PAGE_CHECK_TICKS=8    # while shown, look for a user escape every ~2 s
DD_CHECK_TICKS=8      # while idle, the dd fallback scan every ~2 s
ORS_TIMEOUT=8
RAISE_PAGES="winre_home winre_troubleshoot winre_advanced winre_advanced2 winre_output"
SCREEN=winre_install

cur_page() {
	tail -n 400 /tmp/recovery.log 2>/dev/null \
		| grep "Set page: '" | tail -n 1 | sed "s/.*Set page: '\([^']*\)'.*/\1/"
}

# The page on screen, looking through a stray openrecoveryscript bounce: TWRP
# returns from singleaction_page to the page it interrupted, so wait it out.
settled_page() {
	n=0
	pg=$(cur_page)
	while [ "$pg" = singleaction_page ] && [ "$n" -lt 8 ]; do
		sleep 1
		n=$((n + 1))
		pg=$(cur_page)
	done
	echo "$pg"
}

changepage() {
	timeout -t "$ORS_TIMEOUT" twrp "changepage=$1" >/dev/null 2>&1
	usleep 300000
	[ "$(cur_page)" = "$1" ]
}

is_raise_page() {
	for p in $RAISE_PAGES; do
		[ "$1" = "$p" ] && return 0
	done
	return 1
}

now() {
	read -r up _ < /proc/uptime
	NOW=${up%%.*}
}

# Fork-free read of the status file into PHASE/LABEL/PCT/DETAIL; SIG is the
# whole content, so any rewrite by the installer counts as a sign of life.
read_status() {
	PHASE=""; LABEL=""; PCT=""; DETAIL=""; SIG=""
	[ -f "$STATUS" ] || return 1
	while IFS='=' read -r k v; do
		case "$k" in
			phase) PHASE=$v ;;
			label) LABEL=$v ;;
			percent) PCT=$v ;;
			detail) DETAIL=$v ;;
		esac
		SIG="$SIG|$k=$v"
	done < "$STATUS"
	[ -n "$SIG" ]
}

# setprop only what changed; each call is a fork. Without working properties
# (PROPS=0) the static screen is used and there is nothing to publish.
publish() {
	[ "$PROPS" = 1 ] || return 0
	[ "$MODE" != "$P_MODE" ] && setprop s9woa.mode "$MODE" && P_MODE=$MODE
	[ "$LABEL" != "$P_LABEL" ] && setprop s9woa.label "${LABEL:-Installing Windows}" && P_LABEL=$LABEL
	[ "$PCT" != "$P_PCT" ] && setprop s9woa.pct "${PCT:-0}" && P_PCT=$PCT
	[ "$DETAIL" != "$P_DETAIL" ] && setprop s9woa.detail "${DETAIL:- }" && P_DETAIL=$DETAIL
}

forget_published() {
	P_MODE="-"; P_LABEL="-"; P_PCT="-"; P_DETAIL="-"
}

show_prop() {
	[ "$PROPS" = 1 ] && setprop s9woa.show "$1"
}

raise() {
	page=$(cur_page)
	if ! is_raise_page "$page"; then
		if [ "$page" != "$REFUSED" ]; then
			echo "active but page=$page is not one to interrupt; leaving the UI alone"
			REFUSED=$page
		fi
		return 1
	fi
	forget_published
	publish
	show_prop 1
	if changepage "$SCREEN"; then
		ORIGIN=$page; STATE=shown; HELD=0; IDLE=0; SHOWN_MODE=$MODE; REFUSED=""
		echo "raised $SCREEN over $page: ${LABEL:-Installing Windows} ${PCT:+$PCT%} ($MODE)"
		# Re-assert once the page has drawn, in case the progress bar's first
		# update wrote into s9woa.pct (see the note on winre_install).
		usleep 300000
		forget_published
		publish
		return 0
	fi
	show_prop 0
	echo "raise did not take (page=$(cur_page)); will retry"
	return 1
}

clear_screen() {
	page=$(settled_page)
	if [ "$page" = "$SCREEN" ]; then
		n=0
		until changepage "$ORIGIN"; do
			n=$((n + 1))
			echo "  clear attempt $n did not take (page=$(cur_page))"
			[ "$n" -ge 4 ] && break
			sleep 1
		done
		echo "cleared back to $ORIGIN after $HELD ticks"
	else
		echo "the user already left the screen (page=$page); not navigating"
	fi
	show_prop 0
	STATE=idle; HELD=0; IDLE=0
}

# Live values need Android properties; prove they work before relying on them.
PROPS=0
if setprop s9woa.show 0 2>/dev/null && [ "$(getprop s9woa.show 2>/dev/null)" = 0 ]; then
	PROPS=1
else
	SCREEN=winre_install_static
fi
STATE=idle            # idle | shown | dismissed
HELD=0; IDLE=0; TICK=0
ORIGIN=winre_home; SHOWN_MODE=""; DISMISSED=""; REFUSED=""
LASTSIG=""; CHANGED=0; DD=0
forget_published

echo "=== winre statuswatch: $(date 2>/dev/null) ==="
echo "properties: $([ "$PROPS" = 1 ] && echo "working, live screen $SCREEN" || echo "NOT working, static screen $SCREEN")"

while :; do
	TICK=$((TICK + 1))
	now
	active=0
	if read_status; then
		if [ "$SIG" != "$LASTSIG" ]; then
			LASTSIG=$SIG
			CHANGED=$NOW
		fi
		if [ $((NOW - CHANGED)) -le "$STALE" ]; then
			active=1
			[ -z "$PHASE" ] && PHASE=status
		fi
	fi
	if [ "$active" = 0 ]; then
		# Fallback: a dd writing a block device. The [k] keeps grep from
		# matching its own command line.
		if [ "$STATE" != idle ] || [ $((TICK % DD_CHECK_TICKS)) = 0 ]; then
			DD=0
			grep -qa "of=/dev/bloc[k]/" /proc/[0-9]*/cmdline 2>/dev/null && DD=1
		fi
		if [ "$DD" = 1 ]; then
			active=1
			PHASE=dd; LABEL="Writing to the phone's storage"; PCT=""; DETAIL=" "
		fi
	fi
	if [ -n "$PCT" ]; then MODE=percent; else MODE=busy; fi

	case "$STATE" in
		idle)
			if [ "$active" = 1 ] && [ "$PHASE" != "$DISMISSED" ]; then
				raise
			fi
			[ "$active" = 0 ] && DISMISSED=""
			;;
		shown)
			HELD=$((HELD + 1))
			if [ "$active" = 1 ]; then
				IDLE=0
				publish
				if [ $((HELD % PAGE_CHECK_TICKS)) = 0 ] && [ "$(settled_page)" != "$SCREEN" ]; then
					echo "the user left the screen; not raising it again for phase $PHASE"
					show_prop 0
					STATE=dismissed; DISMISSED=$PHASE
				elif [ "$MODE" != "$SHOWN_MODE" ] && [ "$PROPS" = 1 ]; then
					# The bar and the sweep are chosen when the page is entered.
					if changepage "$SCREEN"; then
						echo "switched to the $MODE bar for phase $PHASE"
					fi
					SHOWN_MODE=$MODE
				fi
			else
				IDLE=$((IDLE + 1))
				if [ "$HELD" -ge "$MIN_DWELL_TICKS" ] && [ "$IDLE" -ge "$IDLE_TICKS" ]; then
					clear_screen
				fi
			fi
			;;
		dismissed)
			if [ "$active" = 0 ] || [ "$PHASE" != "$DISMISSED" ]; then
				STATE=idle
			fi
			;;
	esac

	usleep "$TICK_US"
done
