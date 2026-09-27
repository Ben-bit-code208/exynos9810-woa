/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#ifndef STAR2LTE_HSI40_WIN11_AUTHORITY_H
#define STAR2LTE_HSI40_WIN11_AUTHORITY_H
#include <stddef.h>
#include <stdint.h>

#define H40_W11_ROOT_TABLES 5u
#define H40_W11_POLICY_TABLES 6u
#define H40_W11_OK 0u
#define H40_W11_DISABLED 1u
#define H40_W11_ARGUMENT 2u
#define H40_W11_INVENTORY 3u
#define H40_W11_BAD_TABLE 4u
#define H40_W11_CHECKSUM 5u
#define H40_W11_IMMUTABLE 6u
#define H40_W11_TOPOLOGY 7u

typedef struct {
    const uint8_t *Data;
    size_t Bytes;
} H40_W11_TABLE;
typedef struct {
    H40_W11_TABLE Tables[H40_W11_POLICY_TABLES];
    const uint8_t *Compare[H40_W11_POLICY_TABLES];
    uint32_t MatOffsets[8];
} H40_W11_POLICY;
typedef struct {
    uint32_t Size,Version,Result,TableIndex,Offset,Expected,Observed,ActiveMask;
} H40_W11_AUTHORITY;

/* Inputs are complete, mapped ACPI snapshots; this core never reads MMIO. */
uint32_t H40Win11Authority(const H40_W11_POLICY *Policy,
    const H40_W11_TABLE *Root,size_t Count,H40_W11_TABLE Dsdt,
    uint32_t Enabled,H40_W11_AUTHORITY *Result);
#endif
