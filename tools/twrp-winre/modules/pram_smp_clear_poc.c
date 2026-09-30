// SPDX-License-Identifier: GPL-2.0
/*
 * pram_smp_clear_poc.c - clear the Star2Lte SMP1 (P3) retained startup record.
 *
 * Separate from the RWD1 recovery record, the firmware keeps a 128-byte
 * baseline startup record at 0xFED13E80 (the "P3"/SMP1 record). If that record
 * holds a value that is neither all-zero nor the erased all-ones pattern - a
 * mixed/garbage word, as left behind by a stock Android boot, a Download-mode
 * session or a forced reset - the firmware's early startup gate treats it as an
 * invalid transaction, disables the boot watchdog and halts. On the phone this
 * looks exactly like an indefinite hang on the Samsung logo, and it is NOT
 * fixed by reflashing firmware or by acknowledging RWD1: the P3 word has to be
 * returned to a clean all-zero state, which the gate accepts as "no record".
 *
 * This module zeroes the 128-byte record, cleans it to the point of coherency
 * and invalidates the region so the firmware reads RAM rather than a stale
 * cache line, then verifies the read-back. It logs the pre-clear magic and the
 * final status so the operation is auditable from dmesg. It takes no
 * parameters: zeroing a record the firmware itself treats as absent is always
 * safe, and clearing it is the maintained remedy for the logo-hang gate.
 */

#include <linux/init.h>
#include <linux/io.h>
#include <linux/module.h>

#include "pram_cache.h"

#define SMP_RECORD_BASE 0x00000000fed13e80ULL
#define SMP_RECORD_SIZE 0x00000080U

static int __init notrace pram_smp_clear_poc_init(void)
{
	void __iomem *base;
	unsigned int offset;
	int status = 0;

	base = ioremap_cache(SMP_RECORD_BASE, SMP_RECORD_SIZE);
	if (!base)
		return -ENOMEM;

	pr_info("pram_smp_clear_poc begin before=%08x\n", readl(base));
	for (offset = 0; offset < SMP_RECORD_SIZE; offset += sizeof(u32))
		writel(0, base + offset);

	pram_clean_to_poc((void *)base, SMP_RECORD_SIZE);
	pram_invalidate_from_poc((void *)base, SMP_RECORD_SIZE);

	for (offset = 0; offset < SMP_RECORD_SIZE; offset += sizeof(u32)) {
		if (readl(base + offset) != 0) {
			pr_err("pram_smp_clear_poc verify_failed offset=%04x value=%08x\n",
				offset, readl(base + offset));
			status = -EIO;
			break;
		}
	}
	pr_info("pram_smp_clear_poc end status=%d\n", status);

	iounmap(base);
	return status;
}

static void __exit notrace pram_smp_clear_poc_exit(void)
{
}

module_init(pram_smp_clear_poc_init);
module_exit(pram_smp_clear_poc_exit);

MODULE_LICENSE("GPL");
MODULE_DESCRIPTION("PoC-clean Star2Lte SMP1 (P3) retained-record clear and readback");
