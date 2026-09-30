// SPDX-License-Identifier: GPL-2.0
/*
 * rwd1_clear_poc.c - explicitly authorized supervised clear of an INVALID
 * Star2Lte RWD1 recovery transaction record.
 *
 * The generation-guarded acknowledgement module (rwd1_ack.ko) deliberately
 * refuses to touch a record whose contents are not a well-formed RWD1
 * transaction: on random garbage it returns -EUCLEAN and leaves the bytes in
 * place. That is the safe default, but it means a phone whose RWD1 word was
 * left as garbage - e.g. after a stock Android flash, a Download-mode session
 * or a forced reset - keeps being routed back to TWRP (and can hang UEFI at the
 * Samsung logo) on every boot, with no way forward.
 *
 * This module is the escape hatch for exactly that case. It is gated behind an
 * exact authorization token so it can never fire by accident, and it only ever
 * writes zeros: it zeroes the 0x44-byte record (17 dwords, including the SEC
 * cookie), which the firmware treats as "no transaction" and re-initialises
 * cleanly on the next boot. It logs every dword it is about to overwrite and
 * verifies the read-back, so the operation is auditable from dmesg.
 *
 * Load it with:
 *   insmod rwd1_clear_poc.ko authorize=CLEAR_INVALID_RWD1_SUPERVISED_V1
 */

#include <linux/init.h>
#include <linux/io.h>
#include <linux/module.h>
#include <linux/string.h>

#include "pram_cache.h"

#define RWD1_BASE       0x00000000fed13d80ULL
#define RWD1_TOTAL      0x44u
#define RWD1_DWORDS     17u
#define CLEAR_TOKEN     "CLEAR_INVALID_RWD1_SUPERVISED_V1"

static char *authorize;
module_param(authorize, charp, 0400);
MODULE_PARM_DESC(authorize, "Required exact supervised-clear authorization token");

static int __init notrace rwd1_clear_poc_init(void)
{
	void __iomem *base;
	unsigned int index;
	int status = 0;

	if (!authorize || strcmp(authorize, CLEAR_TOKEN) != 0)
		return -EPERM;

	base = ioremap_cache(RWD1_BASE, RWD1_TOTAL);
	if (!base)
		return -ENOMEM;

	pram_invalidate_from_poc((void *)base, RWD1_TOTAL);
	for (index = 0; index < RWD1_DWORDS; index++)
		pr_info("rwd1_clear_poc before[%02u]=%08x\n",
			index, readl(base + index * sizeof(u32)));

	for (index = 0; index < RWD1_DWORDS; index++)
		writel(0, base + index * sizeof(u32));
	pram_clean_to_poc((void *)base, RWD1_TOTAL);
	pram_invalidate_from_poc((void *)base, RWD1_TOTAL);

	for (index = 0; index < RWD1_DWORDS; index++) {
		u32 value = readl(base + index * sizeof(u32));

		if (value != 0) {
			pr_err("rwd1_clear_poc verify[%02u]=%08x\n", index, value);
			status = -EIO;
		}
	}

	pr_info("rwd1_clear_poc status=%d bytes=%u\n", status, RWD1_TOTAL);
	iounmap(base);
	return status;
}

static void __exit notrace rwd1_clear_poc_exit(void)
{
}

module_init(rwd1_clear_poc_init);
module_exit(rwd1_clear_poc_exit);
MODULE_LICENSE("GPL");
MODULE_DESCRIPTION("Explicit supervised clear for an invalid Star2Lte RWD1 record");
