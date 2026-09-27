/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#ifndef STAR2LTE_HSI40_HANDOFF_BACKEND_H
#define STAR2LTE_HSI40_HANDOFF_BACKEND_H
#include <Uefi.h>
#include <PiDxe.h>
#include <Protocol/AcpiTable.h>
#include "Hsi40HandoffCore.h"
EFI_STATUS H40PrepareMemory(EFI_DXE_SERVICES *);
EFI_STATUS H40PrepareCensusMemory(EFI_DXE_SERVICES *);
EFI_STATUS H40PrepareAndPublish(EFI_DXE_SERVICES *,EFI_ACPI_TABLE_PROTOCOL *,
    CONST UINT8 Producer[16],BOOLEAN AuthorityVerified,CONST VOID *DeviceSsdt,UINTN DeviceSsdtBytes);
#endif
