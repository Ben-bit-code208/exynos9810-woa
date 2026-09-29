// SPDX-License-Identifier: GPL-2.0
/*
 * Read-only retained-evidence snapshot for the Star2Lte RWD1 harness.
 *
 * The acknowledgement module is deliberately destructive: loading it commits
 * TWRP_ACK and then clears all 16 RWD1 dwords.  This module provides the
 * pre-ack snapshot path the campaign harness needs.  It has no module
 * parameters and no MMIO write instruction.
 *
 * A single insertion captures:
 *   - two independently invalidated RWD1 reads (64 bytes each);
 *   - the 128-byte P3 retained record;
 *   - the 40-byte Windows UFS witness;
 *   - the 16-byte one-shot Windows witness arm record;
 *   - the 80-byte KEP record and two independent 240-byte high-bank reads;
 *   - reset status, the RWD1 entry cookie, and a stable-read flag.
 *
 * Copies are exposed through debugfs and remain immutable until unload.
 */

#include <linux/debugfs.h>
#include <linux/init.h>
#include <linux/io.h>
#include <linux/module.h>
#include <linux/string.h>
#include <linux/utsname.h>

#include "pram_cache.h"

#define EXPECTED_RELEASE    "4.9.118-g36e6ddff22b9"

#define RWD1_BASE           0x00000000fed13d80ULL
#define RWD1_BYTES          0x40u
#define RWD1_COOKIE_ADDR    0x00000000fed13dc0ULL
#define P3_BASE             0x00000000fed13e80ULL
#define P3_BYTES            0x80u
#define WINDOWS_ARM_BASE    0x00000000fed17f60ULL
#define WINDOWS_ARM_BYTES   0x10u
#define WINDOWS_WIT_BASE    0x00000000fed17f70ULL
#define WINDOWS_WIT_BYTES   0x28u
#define KEP_BASE            0x00000000fed17fb0ULL
#define KEP_BYTES           0x50u
#define HIGHBANK_BASE       0x00000000fed13f10ULL
#define HIGHBANK_BYTES      0xf0u
#define HIGHBANK_META_VER   0x00000001u
#define HIGHBANK_READ_POC_READL 0x00000001u
#define HIGHBANK_CAPTURE_STABLE 0x00000001u
#define HIGHBANK_CAPTURE_UNSTABLE 0x00000002u
#define PMU_RST_STAT_ADDR   0x0000000014060404ULL

struct rwd1_evidence_meta {
	u32 stable;
	u32 reset_status;
	u32 entry_cookie;
	u32 reserved;
};

struct highbank_evidence_meta_v1 {
	u32 version;
	u32 capture_status;
	u64 base;
	u32 bytes;
	u32 read_method;
};

static struct dentry *evidence_dir;
static u8 rwd1_first[RWD1_BYTES];
static u8 rwd1_second[RWD1_BYTES];
static u8 p3_record[P3_BYTES];
static u8 windows_witness[WINDOWS_WIT_BYTES];
static u8 windows_arm[WINDOWS_ARM_BYTES];
static u8 kep_record[KEP_BYTES];
static u8 highbank_first[HIGHBANK_BYTES] __aligned(4);
static u8 highbank_second[HIGHBANK_BYTES] __aligned(4);
static struct rwd1_evidence_meta evidence_meta;
static struct highbank_evidence_meta_v1 highbank_meta;

static struct debugfs_blob_wrapper rwd1_first_blob = {
	.data = rwd1_first,
	.size = sizeof(rwd1_first),
};
static struct debugfs_blob_wrapper rwd1_second_blob = {
	.data = rwd1_second,
	.size = sizeof(rwd1_second),
};
static struct debugfs_blob_wrapper p3_record_blob = {
	.data = p3_record,
	.size = sizeof(p3_record),
};
static struct debugfs_blob_wrapper windows_witness_blob = {
	.data = windows_witness,
	.size = sizeof(windows_witness),
};
static struct debugfs_blob_wrapper windows_arm_blob = {
	.data = windows_arm,
	.size = sizeof(windows_arm),
};
static struct debugfs_blob_wrapper kep_record_blob = {
	.data = kep_record,
	.size = sizeof(kep_record),
};
static struct debugfs_blob_wrapper highbank_first_blob = {
	.data = highbank_first,
	.size = sizeof(highbank_first),
};
static struct debugfs_blob_wrapper highbank_second_blob = {
	.data = highbank_second,
	.size = sizeof(highbank_second),
};
static struct debugfs_blob_wrapper highbank_meta_blob = {
	.data = &highbank_meta,
	.size = sizeof(highbank_meta),
};
static struct debugfs_blob_wrapper meta_blob = {
	.data = &evidence_meta,
	.size = sizeof(evidence_meta),
};

static void read_words(void __iomem *mapped, void *destination, size_t bytes)
{
	u32 *words = destination;
	unsigned int offset;

	for (offset = 0; offset < bytes; offset += sizeof(u32))
		words[offset / sizeof(u32)] = readl(mapped + offset);
}

static int snapshot_cached(u64 address, size_t bytes, void *destination)
{
	void __iomem *mapped;

	mapped = ioremap_cache(address, bytes);
	if (!mapped)
		return -ENOMEM;
	pram_invalidate_from_poc((void *)mapped, bytes);
	read_words(mapped, destination, bytes);
	mb();
	iounmap(mapped);
	return 0;
}

static int snapshot_rwd1(void)
{
	void __iomem *mapped;

	mapped = ioremap_cache(RWD1_BASE, RWD1_BYTES);
	if (!mapped)
		return -ENOMEM;

	pram_invalidate_from_poc((void *)mapped, RWD1_BYTES);
	read_words(mapped, rwd1_first, RWD1_BYTES);
	mb();
	pram_invalidate_from_poc((void *)mapped, RWD1_BYTES);
	read_words(mapped, rwd1_second, RWD1_BYTES);
	mb();
	iounmap(mapped);

	evidence_meta.stable =
		memcmp(rwd1_first, rwd1_second, RWD1_BYTES) == 0 ? 1u : 0u;
	return 0;
}

static int snapshot_highbank(void)
{
	void __iomem *mapped;

	mapped = ioremap_cache(HIGHBANK_BASE, HIGHBANK_BYTES);
	if (!mapped)
		return -ENOMEM;

	pram_invalidate_from_poc((void *)mapped, HIGHBANK_BYTES);
	read_words(mapped, highbank_first, HIGHBANK_BYTES);
	mb();
	pram_invalidate_from_poc((void *)mapped, HIGHBANK_BYTES);
	read_words(mapped, highbank_second, HIGHBANK_BYTES);
	mb();
	iounmap(mapped);

	highbank_meta.version = HIGHBANK_META_VER;
	highbank_meta.capture_status =
		memcmp(highbank_first, highbank_second, HIGHBANK_BYTES) == 0 ?
		HIGHBANK_CAPTURE_STABLE : HIGHBANK_CAPTURE_UNSTABLE;
	highbank_meta.base = HIGHBANK_BASE;
	highbank_meta.bytes = HIGHBANK_BYTES;
	highbank_meta.read_method = HIGHBANK_READ_POC_READL;
	return 0;
}

static int snapshot_scalar(u64 address, u32 *destination)
{
	void __iomem *mapped;

	mapped = ioremap(address, sizeof(u32));
	if (!mapped)
		return -ENOMEM;
	*destination = readl(mapped);
	iounmap(mapped);
	return 0;
}

static int create_blob(const char *name, struct debugfs_blob_wrapper *blob)
{
	struct dentry *entry;

	entry = debugfs_create_blob(name, 0400, evidence_dir, blob);
	if (IS_ERR(entry))
		return PTR_ERR(entry);
	return entry ? 0 : -ENOMEM;
}

static int __init rwd1_evidence_reader_init(void)
{
	int status;

	BUILD_BUG_ON(sizeof(struct rwd1_evidence_meta) != 16);
	BUILD_BUG_ON(sizeof(struct highbank_evidence_meta_v1) != 24);
	BUILD_BUG_ON(offsetof(struct highbank_evidence_meta_v1, base) != 8);
	BUILD_BUG_ON(offsetof(struct highbank_evidence_meta_v1, bytes) != 16);
	BUILD_BUG_ON(offsetof(struct highbank_evidence_meta_v1, read_method) != 20);
	if (strcmp(init_utsname()->release, EXPECTED_RELEASE))
		return -ENODEV;

	memset(rwd1_first, 0, sizeof(rwd1_first));
	memset(rwd1_second, 0, sizeof(rwd1_second));
	memset(p3_record, 0, sizeof(p3_record));
	memset(windows_witness, 0, sizeof(windows_witness));
	memset(windows_arm, 0, sizeof(windows_arm));
	memset(kep_record, 0, sizeof(kep_record));
	memset(highbank_first, 0, sizeof(highbank_first));
	memset(highbank_second, 0, sizeof(highbank_second));
	memset(&evidence_meta, 0, sizeof(evidence_meta));
	memset(&highbank_meta, 0, sizeof(highbank_meta));

	status = snapshot_rwd1();
	if (status)
		return status;
	status = snapshot_highbank();
	if (status)
		return status;
	status = snapshot_cached(P3_BASE, P3_BYTES, p3_record);
	if (status)
		return status;
	status = snapshot_cached(
		WINDOWS_WIT_BASE, WINDOWS_WIT_BYTES, windows_witness);
	if (status)
		return status;
	status = snapshot_cached(
		WINDOWS_ARM_BASE, WINDOWS_ARM_BYTES, windows_arm);
	if (status)
		return status;
	status = snapshot_cached(KEP_BASE, KEP_BYTES, kep_record);
	if (status)
		return status;
	status = snapshot_scalar(PMU_RST_STAT_ADDR, &evidence_meta.reset_status);
	if (status)
		return status;
	status = snapshot_cached(
		RWD1_COOKIE_ADDR, sizeof(evidence_meta.entry_cookie),
		&evidence_meta.entry_cookie);
	if (status)
		return status;

	evidence_dir = debugfs_create_dir("rwd1-evidence", NULL);
	if (IS_ERR_OR_NULL(evidence_dir))
		return evidence_dir ? PTR_ERR(evidence_dir) : -ENOMEM;

	status = create_blob("rwd1-first", &rwd1_first_blob);
	status |= create_blob("rwd1-second", &rwd1_second_blob);
	status |= create_blob("p3-record", &p3_record_blob);
	status |= create_blob("windows-witness", &windows_witness_blob);
	status |= create_blob("windows-arm", &windows_arm_blob);
	status |= create_blob("kep-record", &kep_record_blob);
	status |= create_blob("highbank-first", &highbank_first_blob);
	status |= create_blob("highbank-second", &highbank_second_blob);
	status |= create_blob("highbank-meta", &highbank_meta_blob);
	status |= create_blob("meta", &meta_blob);
	if (status) {
		debugfs_remove_recursive(evidence_dir);
		evidence_dir = NULL;
		return -ENOMEM;
	}

	pr_info(
		"rwd1_evidence_reader: stable=%u rst=%08x cookie=%08x "
		"rwd1_magic=%08x p3_magic=%08x win_magic=%08x "
		"highbank=%u highbank_magic=%08x\n",
		evidence_meta.stable,
		evidence_meta.reset_status,
		evidence_meta.entry_cookie,
		*(u32 *)rwd1_second,
		*(u32 *)p3_record,
		*(u32 *)windows_witness,
		highbank_meta.capture_status,
		*(u32 *)highbank_second);
	return 0;
}

static void __exit rwd1_evidence_reader_exit(void)
{
	debugfs_remove_recursive(evidence_dir);
	evidence_dir = NULL;
}

module_init(rwd1_evidence_reader_init);
module_exit(rwd1_evidence_reader_exit);
MODULE_LICENSE("GPL");
MODULE_DESCRIPTION("Read-only Star2Lte RWD1/P3/Windows/KEP/high-bank evidence snapshot");
