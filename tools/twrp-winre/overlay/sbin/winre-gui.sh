#!/sbin/sh
# SPDX-License-Identifier: BSD-2-Clause-Patent
#
# winre-gui.sh - run a WinRE tool with the console scrolled clean, then exec it.
#
# TWRP's <console> is one global scrollback shared by every page, and it starts
# filling during boot. By the time the user taps a tile it already holds the
# tail of the boot log, so a report rendered into it appears underneath lines
# like "Running boot script..." and "Full SELinux support is present." - which
# is fine for a debugger and completely wrong for something meant to look like
# the Windows Recovery Environment.
#
# There is no console-clear GUI action in this build. <action function="clear"/>
# is silently ignored: it was already on a tile and the boot chatter still
# showed up on screen, and sbin/recovery contains no clearconsole symbol to bind
# to. So the console cannot be emptied, only scrolled. It always auto-scrolls to
# the bottom, so printing a screenful of blank lines first pushes the chatter off
# the top and leaves the report alone.
#
# The padding must be a single SPACE, never a bare `echo`. TWRP's console drops
# zero-length strings: they reach /tmp/recovery.log but are never drawn. A first
# attempt at this file used bare `echo` and was a visual no-op for exactly that
# reason.
#
# winre_console_h is 1000 in the theme's logical space and winre_mono is a
# size-22 TTF, giving roughly 40 visible rows. PAD is set well above that so the
# padding still covers the pane if the font metrics change; surplus lines just
# scroll off the top and cost nothing. Undershooting is the only visible failure.
PAD=60

if [ $# -eq 0 ]; then
    echo "usage: winre-gui.sh <command> [args...]" >&2
    exit 2
fi

i=0
while [ "$i" -lt "$PAD" ]; do
    echo " "
    i=$((i + 1))
done

# exec so the tool keeps this PID and its exit status is the one TWRP sees.
exec "$@"
