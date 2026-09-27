/* @file
 * bootshim.S — relocating loader that prepends the EDK II firmware device (FD).
 *
 * The Galaxy S9+ bootloader (sboot) loads an Android boot.img "kernel" and
 * jumps to its first byte. Our EDK II FD (STAR2LTE_UEFI.fd) is *linked to run*
 * at 0x90000000, but sboot drops the kernel near 0x80100000 (DRAM base +
 * text_offset). This shim bridges that gap.
 *
 * Layout after packaging:  [ this shim ][ STAR2LTE_UEFI.fd ]   (cat'd together)
 *
 * Flow (matches the proven edk2-exynos tools/BootShim + m1-hello start.S, both
 * device-verified on star2lte):
 *   1. sboot jumps to offset 0 -> `adr x1,_payload` captures the *runtime*
 *      address of the appended FD, then branch to _start.
 *   2. mask IRQs, stop the SoC watchdog, disable D/I caches (MMU is OFF at
 *      handoff, so these are direct device/RAM stores).
 *   3. copy the FD (UEFI_SIZE bytes) to UEFI_BASE (0x90000000) — clean System
 *      RAM ABOVE the RKP-monitored kernel zones (0x80094000-0x828DBFFF), so our
 *      writes don't trip the Samsung hypervisor's integrity reset.
 *   4. branch to UEFI_BASE. The FD's first word is an ARM64 `B` that GenFv wrote
 *      into the FV-header zero-vector, jumping to the PeilessSec SEC entry.
 *
 * The shim is fully position-independent (PC-relative adr / literal ldr /
 * register br), so it runs correctly wherever sboot places it.
 *
 * Build: clang -target aarch64-none-elf -c bootshim.S -o bootshim.elf
 *        llvm-objcopy -O binary bootshim.elf bootshim.bin
 * (No link step needed — there are no absolute relocations.)
 */

#ifndef UEFI_BASE
#define UEFI_BASE 0x0000000090000000   /* must equal PcdFdBaseAddress (DSC/FDF) */
#endif
#ifndef UEFI_SIZE
#define UEFI_SIZE 0x0000000000700000   /* must equal FD_SIZE (7 MB)             */
#endif

	.text
	.global _head
_head:
	adr	x1, _payload            /* x1 = runtime address of the appended FD   */
	b	_start                  /* (this pair is the header's code0/code1)   */
	/* ---- Linux ARM64 Image header (star2lte-proven values) ------------- */
	.quad	0x0000000000080000      /* text_offset: DRAM base + 0x80000          */
	.quad	0x0000000000800000      /* image_size: cover shim + 7 MB FD + margin */
	.quad	0x000000000000000a      /* flags: 4K pages, load anywhere, LE        */
	.quad	0                       /* res2                                      */
	.quad	0                       /* res3                                      */
	.quad	0                       /* res4                                      */
	.ascii	"ARM\x64"               /* magic = 0x644d5241                        */
	.long	0                       /* res5 (PE/COFF header offset; none)        */

_uefi_base:
	.quad	UEFI_BASE
_uefi_size:
	.quad	UEFI_SIZE

_start:
	msr	DAIFSet, #0xf           /* mask D/A/I/F (all interrupts)             */

	/* ---- Stop the SoC watchdog FIRST ----------------------------------
	 * sboot arms watchdog_cl0@0x10050000 + cl1@0x10060000 and hands off with
	 * a random amount of time left. Our DXE bring-up is long; WTCON=0 halts
	 * the counters before they can fire. MMU is OFF -> direct device stores.
	 * x0 is scratch (x1 holds the payload pointer and is preserved). */
	movz	x0, #0x1005, lsl #16    /* 0x10050000 (cl0 WTCON, offset 0x00)       */
	str	wzr, [x0]
	movz	x0, #0x1006, lsl #16    /* 0x10060000 (cl1 WTCON)                    */
	str	wzr, [x0]
	dsb	sy

	/* ---- Disable D/I caches before copying ----------------------------
	 * MMU is OFF at handoff (device-verified), so data stores reach RAM.
	 * Clearing I prevents a stale instruction-cache fetch of the freshly
	 * copied FD. EDK II re-enables MMU + caches properly in CpuDxe. */
	mrs	x0, SCTLR_EL1
	bic	x0, x0, #(1 << 2)       /* C = 0 : data cache off                    */
	bic	x0, x0, #(1 << 12)      /* I = 0 : instruction cache off             */
	msr	SCTLR_EL1, x0
	isb

	/* ---- DIAG breadcrumb 'A': shim entry, via persistent RAM ----------
	 * Write the m1-format ramoops pmsg header at 0xFED14000 (sig 'DBGC',
	 * start=0) then text 'A' with size=1. This survives the hang + a warm
	 * reboot, and TWRP exposes it at /sys/fs/pstore/pmsg-ramoops-0. Unlike the
	 * framebuffer, it does NOT depend on the display being visible. MMU off ->
	 * direct DRAM stores, dsb is enough (m1-proven). x12/w13 scratch; x1 kept. */
	movz	x12, #0x4000
	movk	x12, #0xFED1, lsl #16   /* x12 = 0xFED14000 (pmsg zone base)         */
	movz	w13, #0x4244
	movk	w13, #0x4347, lsl #16   /* w13 = 0x43474244 = 'DBGC' ramoops sig     */
	str	w13, [x12]              /* [0] = sig                                 */
	str	wzr, [x12, #4]          /* [4] = start = 0                           */
	mov	w13, #0x41              /* 'A'                                       */
	strb	w13, [x12, #12]         /* text[0] = 'A'                             */
	mov	w13, #1
	str	w13, [x12, #8]          /* [8] = size = 1                            */
	dsb	sy

	/* ---- DIAG: paint RED band (rows 200-650) — shim entry reached ------
	 * Direct stores to the panel scanout buffer (0xCC000000, 1440x2960 BGRA;
	 * MMU off = physical address, device-proven by m1-hello). Proves sboot
	 * jumped to us and stores work. x1 (FD pointer) preserved (we use x9-x11). */
	movz	x9, #0x9400
	movk	x9, #0xCC11, lsl #16    /* 0xCC119400 = FB + row 200                 */
	movz	w10, #0xE340
	movk	w10, #0x9, lsl #16      /* 648000 px = 450 rows x 1440                */
	movz	w11, #0x0000
	movk	w11, #0xFFFF, lsl #16   /* 0xFFFF0000 = opaque RED (BGRA)            */
.Lred:
	str	w11, [x9], #4
	subs	w10, w10, #1
	b.ne	.Lred
	dsb	sy

	/* ---- Copy the appended FD to UEFI_BASE ----------------------------
	 * src (x1) ~0x80100000, dst (x5) 0x90000000 — far apart, no overlap, so
	 * the ascending 16-byte copy never clobbers unread source. */
	ldr	x5, _uefi_base          /* x5 = 0x90000000 (destination)             */
	mov	x4, x1                  /* x4 = source (FD runtime address)          */
	cmp	x4, x5
	b.eq	2f                      /* already at the link address               */
	ldr	x6, _uefi_size          /* x6 = byte count                           */
1:	ldp	x2, x3, [x4], #16
	stp	x2, x3, [x5], #16
	subs	x6, x6, #16
	b.gt	1b
	ldr	x5, _uefi_base          /* reload destination base for the jump      */
	ic	iallu                   /* drop any stale I-cache lines              */
	dsb	sy
	isb

2:	/* ---- DIAG: paint GREEN band (rows 750-1200) — relocation done ------
	 * Reached after the FD copy (or already-at-link). About to br into the FD.
	 * If GREEN shows but no BLUE, the EDK II SEC entry faulted before C. */
	movz	x9, #0xEB00
	movk	x9, #0xCC41, lsl #16    /* 0xCC41EB00 = FB + row 750                 */
	movz	w10, #0xE340
	movk	w10, #0x9, lsl #16
	movz	w11, #0xFF00
	movk	w11, #0xFF00, lsl #16   /* 0xFF00FF00 = opaque GREEN                 */
.Lgreen:
	str	w11, [x9], #4
	subs	w10, w10, #1
	b.ne	.Lgreen
	dsb	sy

	/* ---- DIAG breadcrumb 'B': shim done, about to enter the FD --------- */
	movz	x12, #0x4000
	movk	x12, #0xFED1, lsl #16   /* 0xFED14000                                */
	mov	w13, #0x42              /* 'B'                                       */
	strb	w13, [x12, #13]         /* text[1] = 'B'                             */
	mov	w13, #2
	str	w13, [x12, #8]          /* size = 2                                  */
	dsb	sy

	/* ---- Set up a temporary stack before entering EDK II --------------
	 * PeilessSec's ModuleEntryPoint.S calls ArmPlatformPeiBootAction and
	 * ArmEnableVFP BEFORE it programs SP, relying on the entry SP being usable.
	 * sboot may leave SP pointing at a now-invalid/RKP-protected region, which
	 * would fault those pre-stack calls (the "AB but no C" hang). m1-hello always
	 * set SP first; do the same here. 0xA0000000 is plain DRAM above the 7 MB FD
	 * (ends ~0x90700000), below EDK II's own stack (~0xB4800000), and clear of the
	 * RKP kernel zone. EDK II reprograms SP to its real stack moments later. */
	movz	x6, #0x0000
	movk	x6, #0xA000, lsl #16    /* x6 = 0xA0000000                           */
	mov	sp, x6

	/* ---- Leave the MMU OFF for EDK II ----------------------------------
	 * We deliberately do NOT enable an MMU here. Earlier we did (to dodge
	 * pre-MMU literal-pool load faults), but that was a symptom of a TE-image
	 * stripping misalignment, since fixed properly at build time
	 * (/ALIGN:0x1000 + SEC rule Align=Auto). With the MMU left OFF, EDK II's
	 * MemoryPeim -> ArmConfigureMmu does its own correct break-before-make
	 * `ArmEnableMmu()`; if the shim had left the MMU ON, ArmConfigureMmu sees
	 * ArmMmuEnabled()==TRUE, SKIPS ArmEnableMmu, and just swaps TTBR0 on a live
	 * MMU -> hang (DEVICE-OBSERVED: trail stalled at '1', before ArmConfigureMmu
	 * returns). MMU off = all RAM is accessible (Device) for our pram breadcrumbs
	 * too. The firmware is built -mstrict-align so it makes no unaligned accesses
	 * before EDK II maps RAM as Normal.
	 *
	 * DIAG breadcrumb 'M' (= shim done its prep, about to enter the FD). */
	movz	x12, #0x400c
	movk	x12, #0xFED1, lsl #16   /* 0xFED1400C                                */
	ldur	w13, [x12, #-4]
	mov	w14, #0x4D              /* 'M'                                       */
	strb	w14, [x12, w13, uxtw]
	add	w13, w13, #1
	stur	w13, [x12, #-4]
	dsb	sy

	br	x5                      /* enter the FD @0x90000000 (B -> SEC entry) */

3:	wfe                             /* unreachable; park if the FD ever returns  */
	b	3b

	/* ---- The EDK II FD is concatenated immediately after this label ----
	 * _payload MUST be the last thing emitted so the packaging `cat` places
	 * the FD exactly where `adr x1,_payload` points. */
	.balign	8
_payload:
