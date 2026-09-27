/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#ifndef STAR2LTE_HSI40_CENSUS_BACKEND_H
#define STAR2LTE_HSI40_CENSUS_BACKEND_H
#include "Hsi40HandoffBackend.h"
#include "Hsi40Census.h"
EFI_STATUS H40CensusAndPublish(EFI_DXE_SERVICES *,EFI_ACPI_TABLE_PROTOCOL *,
    CONST UINT8 Producer[16],BOOLEAN AuthorityVerified);
#endif
