/** @file
  Injects Project Silicium's current CNTFRQ_EL0 workaround into winload.

  Based on Mu-Silicium KernelErrataPatcherLib and Samsung ShellCode.h at
  commit d5b3e9ebe74dc175c3c103c0966f8d245d38728e.

  Copyright (c) 2021 Samuel Tulach
  Copyright (c) 2022-2023 DuoWoA authors
  Copyright (c) 2026 Project Silicium

  SPDX-License-Identifier: MIT
**/

#include <Uefi.h>
#include <IndustryStandard/PeImage.h>
#include <Library/BaseMemoryLib.h>
#include <Library/CacheMaintenanceLib.h>
#include <Library/UefiBootServicesTableLib.h>
#include <Protocol/MemoryAttribute.h>

#include "MuCntfrqKep.h"

#define STAR2LTE_KEP_SCAN_LENGTH       0x00300000ULL
#define STAR2LTE_KEP_MAX_IMAGE_SIZE    0x08000000U
#define STAR2LTE_ARM64_INSTRUCTION_LEN sizeof (UINT32)

typedef struct {
  UINT16  TransferToKernelOffset;
  UINT32  TargetInstruction;
} STAR2LTE_WINLOAD_SEMESTER;

STATIC CONST STAR2LTE_WINLOAD_SEMESTER  mWinloadSemesters[] = {
  { 0x0400, 0xD2800002 },
  { 0x0480, 0xD2800002 },
  { 0x0490, 0xD2800002 },
  { 0x0850, 0xD2800002 },
  { 0x08D0, 0x52800014 },
  { 0x0C60, 0x52800015 },
};

STATIC CONST UINT8  mCntfrqShellCode[] = {
  0xEA, 0x03, 0x00, 0xAA, 0x47, 0x0D, 0x41, 0xF8,
  0xFF, 0x00, 0x0A, 0xEB, 0x20, 0x06, 0x00, 0x54,
  0x09, 0x00, 0x80, 0xD2, 0x66, 0x00, 0x80, 0xD2,
  0x26, 0x01, 0xA0, 0xF2, 0xC6, 0x02, 0xC0, 0xF2,
  0x26, 0x01, 0xE0, 0xF2, 0x08, 0x01, 0x9C, 0x52,
  0x68, 0xA7, 0xBA, 0x72, 0x0B, 0x80, 0xB2, 0x52,
  0x0D, 0x31, 0x86, 0xD2, 0x0D, 0x54, 0xBA, 0xF2,
  0x0D, 0x01, 0xCA, 0xF2, 0xED, 0x52, 0xFE, 0xF2,
  0x0C, 0x78, 0x80, 0x52, 0xEC, 0xCB, 0xBA, 0x72,
  0x08, 0x00, 0x00, 0x14, 0x49, 0xF0, 0x00, 0xD1,
  0x4D, 0x40, 0x1C, 0xF8, 0x4C, 0xC0, 0x1C, 0xB8,
  0x89, 0x02, 0x00, 0xB5, 0xE7, 0x00, 0x40, 0xF9,
  0xFF, 0x00, 0x0A, 0xEB, 0x1B, 0x00, 0x00, 0x14,
  0xE5, 0x18, 0x40, 0xF9, 0xE4, 0x40, 0x40, 0xB9,
  0x84, 0x00, 0x05, 0x8B, 0xBF, 0x00, 0x04, 0xEB,
  0x22, 0xFF, 0xFF, 0x54, 0xE2, 0x03, 0x05, 0xAA,
  0x43, 0x00, 0x40, 0xF9, 0x7F, 0x00, 0x06, 0xEB,
  0x20, 0xFE, 0xFF, 0x54, 0x42, 0x10, 0x00, 0x91,
  0x5F, 0x00, 0x04, 0xEB, 0x63, 0xFF, 0xFF, 0x54,
  0xF0, 0xFF, 0xFF, 0x17, 0xA5, 0x10, 0x00, 0x91,
  0xBF, 0x00, 0x04, 0xEB, 0xC2, 0xFD, 0xFF, 0x54,
  0xA2, 0x00, 0x40, 0xB9, 0x5F, 0x00, 0x08, 0x6B,
  0x61, 0xFF, 0xFF, 0x54, 0x22, 0x01, 0x05, 0xCB,
  0x42, 0x6C, 0x02, 0x53, 0x42, 0x00, 0x0B, 0x2A,
  0xA2, 0x00, 0x00, 0xB9, 0xF6, 0xFF, 0xFF, 0x17,
  0x02, 0x00, 0x00, 0x14, 0x1F, 0x20, 0x03, 0xD5,
};

STATIC
BOOLEAN
Star2LteBuildBranchLink (
  IN  EFI_PHYSICAL_ADDRESS  From,
  IN  EFI_PHYSICAL_ADDRESS  To,
  OUT UINT32                *Instruction
  )
{
  INT64  Delta;

  if (Instruction == NULL) {
    return FALSE;
  }

  Delta = (INT64)To - (INT64)From;
  if (((Delta & 0x3) != 0) || (Delta < -0x08000000LL) || (Delta > 0x07FFFFFCLL)) {
    return FALSE;
  }

  *Instruction = 0x94000000U | ((UINT32)(Delta >> 2) & 0x03FFFFFFU);
  return TRUE;
}

EFI_STATUS
Star2LteLocatePeImageFromAddress (
  IN  EFI_PHYSICAL_ADDRESS  Address,
  OUT EFI_PHYSICAL_ADDRESS  *Base,
  OUT UINTN                 *Length
  )
{
  EFI_PHYSICAL_ADDRESS     Current;
  EFI_PHYSICAL_ADDRESS     ScanEnd;
  EFI_IMAGE_DOS_HEADER     *DosHeader;
  EFI_IMAGE_NT_HEADERS64   *NtHeader;
  UINT32                   ImageSize;

  if ((Address == 0) || (Base == NULL) || (Length == NULL)) {
    return EFI_INVALID_PARAMETER;
  }

  Current = Address & ~(EFI_PHYSICAL_ADDRESS)EFI_PAGE_MASK;
  ScanEnd = (Current > STAR2LTE_KEP_SCAN_LENGTH) ?
            Current - STAR2LTE_KEP_SCAN_LENGTH :
            0;

  while (Current >= ScanEnd) {
    DosHeader = (EFI_IMAGE_DOS_HEADER *)(UINTN)Current;
    if ((DosHeader->e_magic == EFI_IMAGE_DOS_SIGNATURE) &&
        (DosHeader->e_lfanew >= sizeof (EFI_IMAGE_DOS_HEADER)) &&
        (DosHeader->e_lfanew <= 0x1000)) {
      NtHeader = (EFI_IMAGE_NT_HEADERS64 *)(UINTN)(Current + DosHeader->e_lfanew);
      if ((NtHeader->Signature == EFI_IMAGE_NT_SIGNATURE) &&
          (NtHeader->FileHeader.Machine == EFI_IMAGE_MACHINE_AARCH64) &&
          (NtHeader->OptionalHeader.Magic == EFI_IMAGE_NT_OPTIONAL_HDR64_MAGIC)) {
        ImageSize = NtHeader->OptionalHeader.SizeOfImage;
        if ((ImageSize >= EFI_PAGE_SIZE) &&
            (ImageSize <= STAR2LTE_KEP_MAX_IMAGE_SIZE) &&
            (Address >= Current) &&
            (Address < Current + ImageSize)) {
          *Base   = Current;
          *Length = ALIGN_VALUE ((UINTN)ImageSize, EFI_PAGE_SIZE);
          return EFI_SUCCESS;
        }
      }
    }

    if ((Current < EFI_PAGE_SIZE) || (Current == ScanEnd)) {
      break;
    }

    Current -= EFI_PAGE_SIZE;
  }

  return EFI_NOT_FOUND;
}

STATIC
EFI_STATUS
Star2LtePatchWinloadTransfer (
  IN EFI_PHYSICAL_ADDRESS  Base,
  IN UINTN                 Length
  )
{
  EFI_MEMORY_ATTRIBUTE_PROTOCOL  *MemoryAttribute;
  EFI_STATUS                     Status;
  EFI_STATUS                     RestoreStatus;
  EFI_PHYSICAL_ADDRESS           Current;
  EFI_PHYSICAL_ADDRESS           End;
  EFI_PHYSICAL_ADDRESS           NewTransfer;
  EFI_PHYSICAL_ADDRESS           Transfer;
  UINT32                         ExpectedBranch;
  UINT32                         NewBranch;
  UINTN                          Semester;
  BOOLEAN                        ProtectionCleared;

  MemoryAttribute   = NULL;
  ProtectionCleared = FALSE;
  Status = gBS->LocateProtocol (
                  &gEfiMemoryAttributeProtocolGuid,
                  NULL,
                  (VOID **)&MemoryAttribute
                  );
  if (!EFI_ERROR (Status) && (MemoryAttribute != NULL)) {
    Status = MemoryAttribute->ClearMemoryAttributes (
                                MemoryAttribute,
                                Base,
                                Length,
                                EFI_MEMORY_RO
                                );
    if (EFI_ERROR (Status)) {
      return Status;
    }

    ProtectionCleared = TRUE;
  } else if (Status != EFI_NOT_FOUND) {
    return Status;
  }

  Status = EFI_NOT_FOUND;
  End    = Base + Length;
  for (Semester = 0; Semester < ARRAY_SIZE (mWinloadSemesters); Semester++) {
    Transfer = Base + 0xC00 + mWinloadSemesters[Semester].TransferToKernelOffset;
    if ((Transfer <= Base + sizeof (mCntfrqShellCode)) ||
        (Transfer + (2 * STAR2LTE_ARM64_INSTRUCTION_LEN) > End)) {
      continue;
    }

    NewTransfer = Transfer - sizeof (mCntfrqShellCode);
    for (Current = Transfer;
         Current + (2 * STAR2LTE_ARM64_INSTRUCTION_LEN) <= End;
         Current += STAR2LTE_ARM64_INSTRUCTION_LEN) {
      if (!Star2LteBuildBranchLink (Current, Transfer, &ExpectedBranch) ||
          (*(volatile UINT32 *)(UINTN)(Current + STAR2LTE_ARM64_INSTRUCTION_LEN) !=
           mWinloadSemesters[Semester].TargetInstruction) ||
          !Star2LteBuildBranchLink (Current, NewTransfer, &NewBranch)) {
        continue;
      }

      if (*(volatile UINT32 *)(UINTN)Current == NewBranch) {
        Status = EFI_SUCCESS;
        break;
      }

      if (*(volatile UINT32 *)(UINTN)Current != ExpectedBranch) {
        continue;
      }

      CopyMem (
        (VOID *)(UINTN)NewTransfer,
        mCntfrqShellCode,
        sizeof (mCntfrqShellCode)
        );
      *(volatile UINT32 *)(UINTN)Current = NewBranch;

      WriteBackInvalidateDataCacheRange (
        (VOID *)(UINTN)NewTransfer,
        sizeof (mCntfrqShellCode)
        );
      InvalidateInstructionCacheRange (
        (VOID *)(UINTN)NewTransfer,
        sizeof (mCntfrqShellCode)
        );
      WriteBackInvalidateDataCacheRange (
        (VOID *)(UINTN)Current,
        STAR2LTE_ARM64_INSTRUCTION_LEN
        );
      InvalidateInstructionCacheRange (
        (VOID *)(UINTN)Current,
        STAR2LTE_ARM64_INSTRUCTION_LEN
        );
      Status = EFI_SUCCESS;
      break;
    }

    if (!EFI_ERROR (Status)) {
      break;
    }
  }

  if (ProtectionCleared) {
    RestoreStatus = MemoryAttribute->SetMemoryAttributes (
                                       MemoryAttribute,
                                       Base,
                                       Length,
                                       EFI_MEMORY_RO
                                       );
    if (EFI_ERROR (RestoreStatus)) {
      return RestoreStatus;
    }
  }

  return Status;
}

EFI_STATUS
Star2LteInstallMuCntfrqKep (
  IN EFI_PHYSICAL_ADDRESS  WinloadReturnAddress
  )
{
  EFI_PHYSICAL_ADDRESS  WinloadBase;
  EFI_STATUS            Status;
  UINTN                 WinloadLength;

  Status = Star2LteLocatePeImageFromAddress (
             WinloadReturnAddress,
             &WinloadBase,
             &WinloadLength
             );
  if (EFI_ERROR (Status)) {
    return Status;
  }

  return Star2LtePatchWinloadTransfer (WinloadBase, WinloadLength);
}
