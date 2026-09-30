#!/sbin/sh
# SPDX-License-Identifier: BSD-2-Clause-Patent
#
# winre-ntfs-watchdog.sh - break the ntfs-3g FUSE self-deadlock from the phone.
#
# TWRP auto-mounts the Windows volume (/data, NTFS on this repartitioned phone)
# with ntfs-3g, a single FUSE daemon. Android's FUSE asks that daemon for the
# canonical path of any entry opened under the mount (FUSE_CANONICAL_PATH). When
# the recovery indexes /data (backup/file-manager walk), the daemon replies to a
# canonical-path request from inside its own reply-write, and that reply does a
# nested kern_path() that needs another lookup on the SAME single-threaded mount
# - so the daemon waits for itself. Captured stacks:
#
#   mount.ntfs : request_wait_answer <- fuse_dev_do_write <- kern_path
#                <- fuse_lookup <- ... (S state, wchan request_wait_answer)
#   recovery   : request_wait_answer <- fuse_dentry_canonical_path <- do_sys_open
#   every sh   : D state on fuse_lock_inode
#
# Once wedged, the recovery binary never reaches "Starting MTP", so it never
# writes sys.usb.config and the USB gadget - adb AND MTP - never comes up. There
# is then no way in from a host: the phone just sits on the TWRP splash. So the
# recovery has to break this itself. This is an init service (see the .rc our
# builder injects); it starts at boot, cwd '/', independent of the recovery
# binary, and touches only procfs/tmpfs - never /data - so it cannot deadlock.
#
# The fix is exactly the proven one: pin the wedged daemon's OOM score to the
# maximum and trip the OOM killer with sysrq 'f'. Killing the DAEMON aborts the
# FUSE connection, so every request waiting on it (the recovery included) returns
# with an error and the UI comes up. A plain SIGKILL is unreliable here: once a
# fatal signal is taken the request falls into an UNINTERRUPTIBLE wait, so we go
# straight for the OOM path that reaped it on the bench.
#
# Everything is best-effort and must never wedge; the loop runs until the phone
# leaves recovery.

set -u
cd /

LOG=/tmp/winre-ntfswd.log
exec >>"$LOG" 2>&1
echo "=== winre ntfs watchdog: $(date 2>/dev/null || echo '?') ==="

# The self-deadlock signature on the ntfs-3g daemon: parked in request_wait_answer
# with fuse_dev_do_write on its stack (it is servicing a reply, not idle). A
# healthy daemon waits in fuse_dev_read/wait_woken with an empty write path, so
# this pair is unambiguous. Require it to persist across a few samples so a
# genuinely quick canonical-path reply is never mistaken for the deadlock.
STUCK_SAMPLES_NEEDED=3   # consecutive hits ...
SAMPLE_SECS=3           # ... spaced this far apart => ~9 s before we act.

# Is $1 an ntfs-3g mount daemon?
is_ntfs_daemon() {
	c=$(tr '\000' ' ' < "/proc/$1/cmdline" 2>/dev/null)
	case "$c" in
		*mount.ntfs*|*ntfs-3g*) return 0 ;;
		*) return 1 ;;
	esac
}

# Does $1 show the FUSE reply-write self-deadlock right now?
is_wedged() {
	wchan=$(cat "/proc/$1/wchan" 2>/dev/null || echo '')
	[ "$wchan" = "request_wait_answer" ] || return 1
	# Confirm with the kernel stack: the daemon is inside its own reply write.
	grep -q 'fuse_dev_do_write' "/proc/$1/stack" 2>/dev/null && return 0
	return 1
}

break_deadlock() {
	pid=$1
	echo "$(date 2>/dev/null || echo '?'): breaking ntfs-3g deadlock, mount.ntfs pid $pid"
	cat "/proc/$pid/stack" 2>/dev/null | sed 's/^/    /'
	# Make this daemon the unambiguous OOM victim, then trip the OOM killer.
	echo 1000 > "/proc/$pid/oom_score_adj" 2>/dev/null
	# A direct kill first (cheap; frees the process if it is still interruptible),
	# then the proven sysrq OOM path if it is still there.
	kill -9 "$pid" 2>/dev/null
	sleep 2
	if [ -d "/proc/$pid" ]; then
		sync
		echo f > /proc/sysrq-trigger 2>/dev/null
		sleep 2
	fi
	if [ -d "/proc/$pid" ]; then
		echo "  mount.ntfs $pid still present after the OOM path"
	else
		echo "  mount.ntfs $pid gone; the FUSE connection is aborted and the UI can come up"
	fi
	sync
}

# Track a per-pid consecutive-hit count without associative arrays (mksh-safe):
# we only ever watch one daemon at a time, so a single pid/count pair is enough.
watched=-1
hits=0

while :; do
	# Stop once the phone has left recovery (booting Windows / powering off).
	[ -e /sbin/recovery ] || { sleep "$SAMPLE_SECS"; }

	found=""
	for d in /proc/[0-9]*; do
		pid=${d#/proc/}
		is_ntfs_daemon "$pid" || continue
		if is_wedged "$pid"; then
			found=$pid
			break
		fi
	done

	if [ -n "$found" ]; then
		if [ "$found" = "$watched" ]; then
			hits=$((hits + 1))
		else
			watched=$found
			hits=1
		fi
		if [ "$hits" -ge "$STUCK_SAMPLES_NEEDED" ]; then
			break_deadlock "$found"
			watched=-1
			hits=0
		fi
	else
		watched=-1
		hits=0
	fi

	sleep "$SAMPLE_SECS"
done
