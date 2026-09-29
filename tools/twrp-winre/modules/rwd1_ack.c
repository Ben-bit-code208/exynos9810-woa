// SPDX-License-Identifier: GPL-2.0
/*
 * TWRP-side acknowledgement and clear for the Star2Lte RWD1 transaction.
 */

#include <linux/init.h>
#include <linux/io.h>
#include <linux/module.h>
#include <linux/proc_fs.h>
#include <linux/seq_file.h>
#include <soc/samsung/exynos-pmu.h>

#include "pram_cache.h"

#define RWD1_BASE              0x00000000fed13d80ULL
#define RWD1_BYTES             0x40u
#define RWD1_DWORDS            16u
#define RWD1_COOKIE_ADDR       0x00000000fed13dc0ULL
#define PMU_RST_STAT_ADDR      0x0000000014060404ULL
#define PMU_WDT_DISABLE        0x0408u
#define PMU_WDT_MASK_RESET     0x040cu
#define PMU_INFORM2            0x0808u
#define PMU_INFORM3            0x080cu
#define PMU_SYSIP_DAT0         0x0810u
#define RST_STAT_CLUSTER0_WDT  0x01000000u
#define WDT_BASE               0x0000000010050000ULL
#define WDT_BYTES              0x1000u
#define RWD1_COOKIE_VALUE      0x594c4156u
#define RWD1_MAGIC             0x31445752u
#define RWD1_VERSION_LENGTH    0x00400001u
#define RWD1_COMMIT_MAGIC      0x21445752u
#define RWD1_CHECKSUM_SEED     0xa5a55a5au

#define W_GENERATION           2u
#define W_GENERATION_INV       3u
#define W_STATE                4u
#define W_OWNER                5u
#define W_PHASE                6u
#define W_REASON               7u
#define W_RESET_STATUS         8u
#define W_HEARTBEAT            10u
#define W_HEARTBEAT_INV        11u
#define W_CHECKSUM             12u
#define W_CHECKSUM_INV         13u
#define W_COMMIT               14u
#define W_COMMIT_INV           15u

#define STATE_RECOVERY_PENDING 0x000000a0u
#define STATE_TWRP_ACK         0x000000b0u
#define STATE_CONTROLLED_STOP  0x00000090u
#define OWNER_TWRP             0x00000007u
#define PHASE_TWRP_ALIVE       0x0000000fu
#define REASON_WATCHDOG_RESET  0x00000001u

static struct proc_dir_entry *result_proc;
static u32 captured[RWD1_DWORDS];
static u32 acknowledged[RWD1_DWORDS];
static int result_status;
static bool was_empty;
static bool was_valid;
static bool was_cleared;
static u32 reset_status;
static u32 entry_cookie;

static int rwd1_normalize_recovery_hardware(void)
{
	void __iomem *wdt;
	u32 disable;
	u32 mask_reset;
	u32 inform2;
	u32 inform3;
	u32 sysip;
	int status;

	wdt = ioremap(WDT_BASE, WDT_BYTES);
	if (!wdt)
		return -ENOMEM;
	writel(0, wdt);
	wmb();
	if (readl(wdt) != 0) {
		iounmap(wdt);
		return -EIO;
	}
	iounmap(wdt);

	status = exynos_pmu_read(PMU_WDT_DISABLE, &disable);
	status |= exynos_pmu_read(PMU_WDT_MASK_RESET, &mask_reset);
	if (status)
		return -EIO;
	status = exynos_pmu_write(PMU_WDT_DISABLE,
				  disable | RST_STAT_CLUSTER0_WDT);
	status |= exynos_pmu_write(PMU_WDT_MASK_RESET,
				   mask_reset | RST_STAT_CLUSTER0_WDT);
	status |= exynos_pmu_write(PMU_INFORM2, 0);
	status |= exynos_pmu_write(PMU_INFORM3, 0);
	status |= exynos_pmu_write(PMU_SYSIP_DAT0, 0);
	if (status)
		return -EIO;

	status = exynos_pmu_read(PMU_WDT_DISABLE, &disable);
	status |= exynos_pmu_read(PMU_WDT_MASK_RESET, &mask_reset);
	status |= exynos_pmu_read(PMU_INFORM2, &inform2);
	status |= exynos_pmu_read(PMU_INFORM3, &inform3);
	status |= exynos_pmu_read(PMU_SYSIP_DAT0, &sysip);
	if (status ||
	    !(disable & RST_STAT_CLUSTER0_WDT) ||
	    !(mask_reset & RST_STAT_CLUSTER0_WDT) ||
	    inform2 || inform3 || sysip)
		return -EIO;
	return 0;
}

static u32 rwd1_checksum(const u32 *record)
{
	u32 value = RWD1_CHECKSUM_SEED;
	unsigned int index;

	for (index = 0; index < W_CHECKSUM; index++)
		value ^= record[index];
	return value;
}

static bool rwd1_all_zero(const u32 *record)
{
	unsigned int index;

	for (index = 0; index < RWD1_DWORDS; index++)
		if (record[index] != 0)
			return false;
	return true;
}

static bool rwd1_all_ones(const u32 *record)
{
	unsigned int index;

	for (index = 0; index < RWD1_DWORDS; index++)
		if (record[index] != 0xffffffffu)
			return false;
	return true;
}

static bool rwd1_valid(const u32 *record)
{
	return record[0] == RWD1_MAGIC &&
	       record[1] == RWD1_VERSION_LENGTH &&
	       (record[W_GENERATION] ^ record[W_GENERATION_INV]) == 0xffffffffu &&
	       (record[W_HEARTBEAT] ^ record[W_HEARTBEAT_INV]) == 0xffffffffu &&
	       (record[W_CHECKSUM] ^ record[W_CHECKSUM_INV]) == 0xffffffffu &&
	       record[W_COMMIT] == RWD1_COMMIT_MAGIC &&
	       record[W_COMMIT_INV] == ~RWD1_COMMIT_MAGIC &&
	       record[W_CHECKSUM] == rwd1_checksum(record);
}

static void rwd1_read(void __iomem *base, u32 *record)
{
	unsigned int index;

	for (index = 0; index < RWD1_DWORDS; index++)
		record[index] = readl(base + index * sizeof(u32));
}

static bool rwd1_stable_read(void __iomem *base, u32 *record)
{
	u32 first[RWD1_DWORDS];
	unsigned int index;

	pram_invalidate_from_poc((void *)base, RWD1_BYTES);
	rwd1_read(base, first);
	mb();
	pram_invalidate_from_poc((void *)base, RWD1_BYTES);
	rwd1_read(base, record);
	mb();
	for (index = 0; index < RWD1_DWORDS; index++)
		if (first[index] != record[index])
			return false;
	return true;
}

static void rwd1_commit(void __iomem *base, u32 *record)
{
	unsigned int index;

	record[0] = RWD1_MAGIC;
	record[1] = RWD1_VERSION_LENGTH;
	record[W_GENERATION_INV] = ~record[W_GENERATION];
	record[W_HEARTBEAT_INV] = ~record[W_HEARTBEAT];
	record[W_CHECKSUM] = 0;
	record[W_CHECKSUM_INV] = 0;
	record[W_COMMIT] = RWD1_COMMIT_MAGIC;
	record[W_COMMIT_INV] = ~RWD1_COMMIT_MAGIC;
	record[W_CHECKSUM] = rwd1_checksum(record);
	record[W_CHECKSUM_INV] = ~record[W_CHECKSUM];

	writel(0, base + W_COMMIT * sizeof(u32));
	mb();
	for (index = 0; index <= W_CHECKSUM_INV; index++)
		writel(record[index], base + index * sizeof(u32));
	mb();
	writel(record[W_COMMIT_INV], base + W_COMMIT_INV * sizeof(u32));
	mb();
	writel(record[W_COMMIT], base + W_COMMIT * sizeof(u32));
	pram_clean_to_poc((void *)base, RWD1_BYTES);
}

static int result_show(struct seq_file *m, void *unused)
{
	unsigned int index;

	(void)unused;
	seq_printf(m,
		   "rwd1_ack status=%d empty=%u valid=%u cleared=%u state_before=0x%08x reason=0x%08x generation=0x%08x\n",
		   result_status, was_empty ? 1 : 0, was_valid ? 1 : 0,
		   was_cleared ? 1 : 0, captured[W_STATE], captured[W_REASON],
		   captured[W_GENERATION]);
	seq_printf(m, "reset_status=0x%08x entry_cookie=0x%08x\n",
		   reset_status, entry_cookie);
	seq_puts(m, "before=");
	for (index = 0; index < RWD1_DWORDS; index++)
		seq_printf(m, "%s%08x", index ? "," : "", captured[index]);
	seq_putc(m, '\n');
	seq_puts(m, "ack=");
	for (index = 0; index < RWD1_DWORDS; index++)
		seq_printf(m, "%s%08x", index ? "," : "", acknowledged[index]);
	seq_putc(m, '\n');
	return 0;
}

static int result_open(struct inode *inode, struct file *file)
{
	return single_open(file, result_show, NULL);
}

static const struct file_operations result_fops = {
	.owner = THIS_MODULE,
	.open = result_open,
	.read = seq_read,
	.llseek = seq_lseek,
	.release = single_release,
};

static int __init notrace rwd1_ack_init(void)
{
	void __iomem *base;
	void __iomem *cookie;
	void __iomem *rst_stat;
	u32 verify[RWD1_DWORDS];
	unsigned int index;

	base = ioremap_cache(RWD1_BASE, RWD1_BYTES);
	if (!base)
		return -ENOMEM;
	cookie = ioremap_cache(RWD1_COOKIE_ADDR, sizeof(u32));
	if (!cookie) {
		iounmap(base);
		return -ENOMEM;
	}
	rst_stat = ioremap(PMU_RST_STAT_ADDR, sizeof(u32));
	if (!rst_stat) {
		iounmap(cookie);
		iounmap(base);
		return -ENOMEM;
	}
	reset_status = readl(rst_stat);
	pram_invalidate_from_poc((void *)cookie, sizeof(u32));
	entry_cookie = readl(cookie);
	result_status = rwd1_normalize_recovery_hardware();
	if (result_status)
		goto out;

	if (!rwd1_stable_read(base, captured)) {
		result_status = -EAGAIN;
		goto out;
	}
	was_empty = rwd1_all_zero(captured) || rwd1_all_ones(captured);
	if (was_empty) {
		for (index = 0; index < RWD1_DWORDS; index++)
			writel(0, base + index * sizeof(u32));
		pram_clean_to_poc((void *)base, RWD1_BYTES);
		pram_invalidate_from_poc((void *)base, RWD1_BYTES);
		rwd1_read(base, verify);
		if (!rwd1_all_zero(verify)) {
			result_status = -EIO;
			goto out;
		}
		was_cleared = true;
		result_status = 0;
		goto clear_cookie;
	}

	was_valid = rwd1_valid(captured);
	if (!was_valid) {
		if ((reset_status & RST_STAT_CLUSTER0_WDT) != 0 &&
		    entry_cookie == RWD1_COOKIE_VALUE &&
		    captured[0] == RWD1_MAGIC &&
		    captured[W_STATE] == 0x10u &&
		    captured[W_OWNER] == 1u &&
		    captured[W_PHASE] == 1u) {
			for (index = 0; index < RWD1_DWORDS; index++)
				acknowledged[index] = 0;
			acknowledged[W_GENERATION] = 1;
			acknowledged[W_HEARTBEAT] = 0;
			acknowledged[W_STATE] = STATE_TWRP_ACK;
			acknowledged[W_OWNER] = OWNER_TWRP;
			acknowledged[W_PHASE] = PHASE_TWRP_ALIVE;
			acknowledged[W_REASON] = REASON_WATCHDOG_RESET;
			acknowledged[W_RESET_STATUS] = reset_status;
			rwd1_commit(base, acknowledged);
			if (!rwd1_stable_read(base, verify) || !rwd1_valid(verify)) {
				result_status = -EIO;
				goto out;
			}
			was_valid = true;
			goto clear_record;
		}
		result_status = -EUCLEAN;
		goto out;
	}
	if (captured[W_STATE] != STATE_RECOVERY_PENDING &&
	    !((reset_status & RST_STAT_CLUSTER0_WDT) != 0 &&
	      captured[W_STATE] != STATE_CONTROLLED_STOP &&
	      captured[W_STATE] != STATE_TWRP_ACK)) {
		result_status = -EPERM;
		goto out;
	}

	for (index = 0; index < RWD1_DWORDS; index++)
		acknowledged[index] = captured[index];
	acknowledged[W_STATE] = STATE_TWRP_ACK;
	acknowledged[W_OWNER] = OWNER_TWRP;
	acknowledged[W_PHASE] = PHASE_TWRP_ALIVE;
	if ((reset_status & RST_STAT_CLUSTER0_WDT) != 0) {
		acknowledged[W_REASON] = REASON_WATCHDOG_RESET;
		acknowledged[W_RESET_STATUS] = reset_status;
	}
	rwd1_commit(base, acknowledged);
	if (!rwd1_stable_read(base, verify) || !rwd1_valid(verify)) {
		result_status = -EIO;
		goto out;
	}
	for (index = 0; index < RWD1_DWORDS; index++)
		if (verify[index] != acknowledged[index]) {
			result_status = -EIO;
			goto out;
		}

clear_record:
	for (index = 0; index < RWD1_DWORDS; index++)
		writel(0, base + index * sizeof(u32));
	pram_clean_to_poc((void *)base, RWD1_BYTES);
	pram_invalidate_from_poc((void *)base, RWD1_BYTES);
	rwd1_read(base, verify);
	if (!rwd1_all_zero(verify)) {
		result_status = -EIO;
		goto out;
	}
	was_cleared = true;

clear_cookie:
	writel(0, cookie);
	pram_clean_to_poc((void *)cookie, sizeof(u32));
	result_status = 0;

out:
	pr_info("rwd1_ack status=%d empty=%u valid=%u cleared=%u state=%08x reason=%08x generation=%08x rst=%08x cookie=%08x\n",
		result_status, was_empty ? 1 : 0, was_valid ? 1 : 0,
		was_cleared ? 1 : 0, captured[W_STATE], captured[W_REASON],
		captured[W_GENERATION], reset_status, entry_cookie);
	iounmap(rst_stat);
	iounmap(cookie);
	iounmap(base);

	result_proc = proc_create("rwd1_ack", 0444, NULL, &result_fops);
	if (!result_proc)
		return -ENOMEM;
	return 0;
}

static void __exit notrace rwd1_ack_exit(void)
{
	remove_proc_entry("rwd1_ack", NULL);
}

module_init(rwd1_ack_init);
module_exit(rwd1_ack_exit);
MODULE_LICENSE("GPL");
MODULE_DESCRIPTION("Acknowledge and clear Star2Lte RWD1 only after TWRP is alive");
