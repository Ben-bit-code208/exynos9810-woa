/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#ifndef STAR2LTE_UART1_CENSUS_BACKEND_H
#define STAR2LTE_UART1_CENSUS_BACKEND_H
#include <Uefi.h>
#include <PiDxe.h>
#include <Protocol/AcpiTable.h>
#include "Uart1Census.h"
EFI_STATUS U1PrepareMemory(EFI_DXE_SERVICES *);
EFI_STATUS U1CensusAndPublish(EFI_DXE_SERVICES *, EFI_ACPI_TABLE_PROTOCOL *,
                             CONST UINT8[16], BOOLEAN);
#endif
