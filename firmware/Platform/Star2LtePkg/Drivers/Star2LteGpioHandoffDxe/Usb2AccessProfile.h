/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#ifndef STAR2LTE_USB2_ACCESS_PROFILE_H
#define STAR2LTE_USB2_ACCESS_PROFILE_H
#ifndef STAR2LTE_USB2_ACCESS_PROBE
#define STAR2LTE_USB2_ACCESS_PROBE 0
#endif
#if STAR2LTE_USB2_ACCESS_PROBE != 0 && STAR2LTE_USB2_ACCESS_PROBE != 1
#error USB2 access publication must be zero or one.
#endif
#if STAR2LTE_USB2_ACCESS_PROBE
#if STAR2LTE_GPIO_HANDOFF_MODE != 4 || STAR2LTE_GPU_CENSUS_OBSERVER != 1 || \
    STAR2LTE_USB_HANDOFF_OBSERVER != 1 || STAR2LTE_USB_INHERITED_PMU_READ != 1 || \
    STAR2LTE_ACPM_FRAMEWORK_LOOPBACK != 1 || STAR2LTE_BCD_BLOCK_OBSERVER != 1 || \
    STAR2LTE_BCD_WRITE_OBSERVER
#error USB2 access requires the exact prepared-input-v4 census/USB/borrow/framework/block cohort.
#endif
#endif
#endif
