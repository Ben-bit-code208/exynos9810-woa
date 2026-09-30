#!/sbin/sh
# SPDX-License-Identifier: BSD-2-Clause-Patent
#
# rebootsystem.sh - TWRP runs this (when present) right before every restart
# into "system": the WinRE "Continue" tile, the stock Reboot > System menu and
# ORS scripts alike (check_and_run_script in twrp-functions.cpp).
#
# On this phone "system" is Windows, started by the UEFI in BOOT. A phone that
# landed in recovery after a power loss, a failed start, Download mode or the
# key combination can hold a stale boot request in MISC and left-over bytes in
# the retained startup records; the firmware then routes straight back here or
# halts at the Samsung logo with its watchdog off. prepare-boot clears all of
# that (see winre-actions.sh). It never mounts storage and must never block the
# reboot, so any failure is ignored.

[ -x /sbin/winre-actions.sh ] && /sbin/winre-actions.sh prepare-boot
exit 0
