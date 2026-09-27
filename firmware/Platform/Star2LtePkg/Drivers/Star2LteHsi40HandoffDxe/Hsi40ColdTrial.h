/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#ifndef STAR2LTE_HSI40_COLD_TRIAL_H
#define STAR2LTE_HSI40_COLD_TRIAL_H
#include "Hsi40HandoffBackend.h"
#include "Hsi40ColdInit.h"
#ifndef H40_COLD_RESET_ONLY
#define H40_COLD_RESET_ONLY 0
#endif
#define H40_COLD_TRIAL_RECORD_OFFSET 68u
#define H40_COLD_TRIAL_TABLE_BYTES (H40_COLD_TRIAL_RECORD_OFFSET+sizeof(H40_COLD_RECORD))
int H40ColdWriteAllowed(const H40_COLD_RECORD *,uint32_t,uint32_t);
EFI_STATUS H40ColdTrialAndPublish(EFI_DXE_SERVICES *,EFI_BOOT_SERVICES *,
    EFI_ACPI_TABLE_PROTOCOL *,CONST UINT8 Producer[16],BOOLEAN AuthorityVerified);
#endif
