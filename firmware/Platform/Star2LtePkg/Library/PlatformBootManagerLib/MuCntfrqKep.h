/** @file
  Windows kernel CNTFRQ_EL0 erratum patch injection.

  SPDX-License-Identifier: MIT
**/

#ifndef MU_CNTFRQ_KEP_H_
#define MU_CNTFRQ_KEP_H_

#include <Uefi.h>

EFI_STATUS
Star2LteInstallMuCntfrqKep (
  IN EFI_PHYSICAL_ADDRESS  WinloadReturnAddress
  );

EFI_STATUS
Star2LteLocatePeImageFromAddress (
  IN  EFI_PHYSICAL_ADDRESS  Address,
  OUT EFI_PHYSICAL_ADDRESS  *Base,
  OUT UINTN                 *Length
  );

#endif
