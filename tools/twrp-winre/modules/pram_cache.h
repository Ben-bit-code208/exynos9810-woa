/* SPDX-License-Identifier: GPL-2.0 */
/*
 * pram_cache.h - point-of-coherency cache maintenance for the Star2Lte
 * persistent-RAM retained records.
 *
 * The UEFI firmware and the boot shim read these records with the MMU either
 * off or through a different mapping than the Linux kernel uses, so a plain
 * writel() from a TWRP kernel module can sit in a dirty cache line the firmware
 * never sees. Every record write therefore has to be cleaned to the point of
 * coherency (dc cvac) and the region invalidated (dc ivac) so the next reader -
 * firmware or this module's own read-back - observes RAM, not a stale line.
 */
#ifndef STAR2LTE_PRAM_CACHE_H
#define STAR2LTE_PRAM_CACHE_H

#include <linux/types.h>

static inline unsigned long pram_cache_line_size(void)
{
	unsigned long ctr_el0;

	asm volatile("mrs %0, ctr_el0" : "=r" (ctr_el0));
	return 4UL << ((ctr_el0 >> 16) & 0xf);
}

static inline void pram_clean_to_poc(void *address, size_t size)
{
	unsigned long line_size = pram_cache_line_size();
	unsigned long cursor = (unsigned long)address & ~(line_size - 1);
	unsigned long end = (unsigned long)address + size;

	for (; cursor < end; cursor += line_size)
		asm volatile("dc cvac, %0" : : "r" (cursor) : "memory");
	asm volatile("dsb sy" : : : "memory");
}

static inline void pram_invalidate_from_poc(void *address, size_t size)
{
	unsigned long line_size = pram_cache_line_size();
	unsigned long cursor = (unsigned long)address & ~(line_size - 1);
	unsigned long end = (unsigned long)address + size;

	for (; cursor < end; cursor += line_size)
		asm volatile("dc ivac, %0" : : "r" (cursor) : "memory");
	asm volatile("dsb sy" : : : "memory");
}

#endif
