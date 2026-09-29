#!/sbin/sh
# SPDX-License-Identifier: BSD-2-Clause-Patent
#
# winre-pram.sh - persistent-RAM crash-ring tool for the WinRE recovery shell.
#
# The Exynos9810 UFS miniport and the UEFI firmware both log into the ramoops
# pmsg zone, and a bugcheck destroys the crashing boot's own ring - so "no
# evidence" and "it died" look identical unless the latch fields are read back
# on the FOLLOWING boot. This decodes the ring on the phone itself, which is the
# whole point of the channel: nobody should have to photograph a screen.
#
# Two rules are load-bearing:
#   1. Values are matched only when they are >= 8 hex digits. The driver
#      zero-pads every field, so a shorter value means the ring wrapped and cut
#      it; accepting it would report a truncated number as real.
#   2. Records are NOT uniform: short records omit every W* write counter, and
#      the last record of a boot is very often a short one. Each field is
#      therefore carried forward from the last record that actually contained it.
#
# Usage: winre-pram.sh summary | dump | save | erase

set -u

RING_DIR=${WINRE_RING_DIR:-/sys/fs/pstore}
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

find_ring() {
	for f in "$RING_DIR"/pmsg-ramoops-0 "$RING_DIR"/pmsg-ramoops-*; do
		[ -f "$f" ] && { echo "$f"; return 0; }
	done
	return 1
}

decode() {
	ring="$1"
	awk '
	function h2d(s,   i, c, d, r) {
		r = 0
		for (i = 1; i <= length(s); i++) {
			c = toupper(substr(s, i, 1))
			d = index("0123456789ABCDEF", c) - 1
			if (d < 0) return -1
			r = r * 16 + d
		}
		return r
	}
	function say(k, note,   v) {
		if (!(k in val)) { printf "  %-9s %-18s %12s   %s\n", k, "--", "", "(never reported)"; return }
		v = val[k]
		if (length(v) > 8)
			printf "  %-9s %-18s %12s   %s\n", k, "0x" v, "", note
		else
			printf "  %-9s %-18s %12d   %s\n", k, "0x" v, h2d(v), note
	}
	function num(k) { return (k in val) ? h2d(val[k]) : -1 }

	/^=UFS/ {
		rec++
		s = $0
		while (match(s, /[A-Z][A-Z0-9]*=[0-9A-Fa-f]+/) > 0) {
			tok = substr(s, RSTART, RLENGTH)
			s   = substr(s, RSTART + RLENGTH)
			eq  = index(tok, "=")
			k   = substr(tok, 1, eq - 1)
			v   = substr(tok, eq + 1)
			if (length(v) < 8) continue
			val[k] = v
		}
		next
	}
	/^=(NTPATCH|CFQSCAN|TS|ACPIINST)/ {
		if (nfw < 6) { fw[nfw++] = $0 }
		next
	}

	END {
		printf "records      %d UFS  %d firmware marker(s)\n", rec, nfw
		if (rec == 0 && nfw == 0) {
			print ""
			print "The ring holds no recognisable telemetry."
			print "Either nothing ran that logs here, or the ring was already erased."
			exit 0
		}
		if (nfw > 0) {
			print ""
			print "-- firmware markers ------------------------------------------"
			for (i = 0; i < nfw; i++) print "  " fw[i]
		}
		if (rec == 0) {
			print ""
			print "No UFS miniport records: the driver never reached HwInitialize,"
			print "or the PRAM window was not mappable from Windows."
			exit 0
		}
		print ""
		print "-- state (each field from the last record that carried it) ---"
		say("SEQ",     "record index")
		say("STAGE",   "10 = OPERATIONAL")
		say("FSTAGE",  "failure stage, 0 = none")
		say("FMASK",   "failure bits")
		say("STARTED", "0 = adapter latched off")
		say("FATAL",   "1 = FatalError latched")
		say("EXECREJ", "10 = ISSUED (reached hardware)")
		say("DONE",    "completed through hardware")
		say("CONTAINED", "containment events")
		say("REARM",   "consecutive re-arms")
		say("LASTOCS", "0 = OK, 7 = FATAL_ERROR")
		say("IS",      "b9 UTP b16 DEVFATAL b17 HOSTFATAL")
		say("PRDTN",   "must be ceil(REQLEN/4096)")
		say("REQLEN",  "bytes requested")
		say("DINLEN",  "bytes delivered")

		print ""
		print "-- write arm -------------------------------------------------"
		say("WMODE",   "0 DISARMED 1 DRY_RUN 2 LIVE")
		say("WLOCK",   "1 = previous boot died mid-write")
		say("WATT",    "attempt that killed that boot")
		say("WTRIED",  "reached the data-out gate")
		say("WISSUED", "ACTUALLY sent to the device")
		say("WVERIFY", "read back equal; must equal WISSUED")
		say("BATTACK", "1 = healthy, >=2 boots never got here")

		print ""
		print "-- verdict ---------------------------------------------------"
		v = 0
		if (num("WLOCK") == 1) {
			printf "  CRASH LATCH SET: the previous boot bugchecked mid-write\n"
			printf "  on attempt %d. Writes are refused this boot.\n", num("WATT")
			v++
		}
		if (num("FATAL") == 1)     { print "  FatalError is latched - the adapter gave up."; v++ }
		if (num("STARTED") == 0)   { print "  Adapter latched OFF (STARTED=0)."; v++ }
		if (num("CONTAINED") > 0)  { printf "  %d containment event(s) recorded.\n", num("CONTAINED"); v++ }
		if (num("LASTOCS") > 0)    { printf "  Last OCS = 0x%02x (non-zero = transfer error).\n", num("LASTOCS"); v++ }
		if (num("WISSUED") >= 0 && num("WISSUED") != num("WVERIFY")) {
			printf "  WISSUED %d != WVERIFY %d - a live write did not read back.\n", num("WISSUED"), num("WVERIFY")
			v++
		}
		if (num("WISSUED") > 0 && num("WISSUED") % 2 == 1)
			print "  WISSUED is ODD - the ladder writes in pattern/restore pairs."
		if (v == 0) print "  Nothing anomalous: no crash latch, no fatal, no containment."
	}
	' "$ring"
}

case "${1:-summary}" in
summary)
	OUT=$(pick_outdir)
	RING=$(find_ring) || {
		echo "No pstore ring found under $RING_DIR."
		echo "ramoops is not exposing a pmsg zone in this boot."
		exit 1
	}
	SZ=$(wc -c < "$RING" 2>/dev/null | tr -d ' ')
	SHA=$(sha256sum "$RING" 2>/dev/null | cut -c1-16)
	{
		echo "ring         $RING"
		echo "size         $SZ bytes   sha256 ${SHA:-n/a}..."
		decode "$RING"
	} | tee "$OUT/pram-latest.txt"
	cp -f "$RING" "$OUT/pram-ring-$STAMP.bin" 2>/dev/null
	echo ""
	echo "Saved: $OUT/pram-latest.txt"
	echo "Raw:   $OUT/pram-ring-$STAMP.bin"
	;;
dump)
	RING=$(find_ring) || { echo "No pstore ring found."; exit 1; }
	cat "$RING"
	;;
save)
	OUT=$(pick_outdir)
	n=0
	for f in "$RING_DIR"/*; do
		[ -f "$f" ] || continue
		cp -f "$f" "$OUT/$(basename "$f")-$STAMP" 2>/dev/null && n=$((n + 1))
	done
	echo "Copied $n pstore record(s) to $OUT"
	;;
erase)
	OUT=$(pick_outdir)
	RING=$(find_ring) || { echo "No pstore ring found - nothing to erase."; exit 0; }
	cp -f "$RING" "$OUT/pram-ring-$STAMP.bin" 2>/dev/null
	decode "$RING" > "$OUT/pram-erased-$STAMP.txt" 2>/dev/null
	echo "Backed up to $OUT/pram-ring-$STAMP.bin"
	n=0
	for f in "$RING_DIR"/*; do
		[ -f "$f" ] || continue
		if rm -f "$f" 2>/dev/null; then
			echo "  cleared $(basename "$f")"
			n=$((n + 1))
		else
			echo "  FAILED  $(basename "$f")"
		fi
	done
	left=$(ls "$RING_DIR" 2>/dev/null | wc -l | tr -d ' ')
	echo ""
	echo "Erased $n record(s); $left remain."
	if [ "$left" = "0" ]; then
		echo "The ring is empty. The next boot starts from a clean slate."
	else
		echo "Some records survived - they will be re-read next boot."
	fi
	;;
*)
	echo "usage: winre-pram.sh [summary|dump|save|erase]"
	exit 2
	;;
esac
