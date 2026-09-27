/** @file
  Publishes device-tree-verified Exynos 9810 DRAM bank-1 ranges.

  The PEI window remains at 0x90000000..0xBC800000. DXE adds only ranges
  outside that window and excludes the fixed seclog and active framebuffer.
**/

#include <Uefi.h>
#include <Library/ArmMmuLib.h>
#include <Library/BaseLib.h>
#include <Library/DebugLib.h>
#include <Library/DxeServicesTableLib.h>

#define STAR2LTE_RAM_STATUS_ADDR  0xFED17F60ULL

typedef struct {
  EFI_PHYSICAL_ADDRESS  Base;
  UINT64                Length;
} STAR2LTE_RAM_RANGE;

STATIC CONST STAR2LTE_RAM_RANGE  mRamRanges[] = {
  // Bank 1 below the framebuffer; excludes seclog at 0xC0000000..0xC0080000.
  { 0x00000000C0080000ULL, 0x000000000BF80000ULL },
  // Bank 1 above the page-rounded framebuffer end (0xCD044000).
  { 0x00000000CD044000ULL, 0x0000000012FBC000ULL },
};

STATIC
EFI_STATUS
Star2LteAddRamRange (
  IN CONST STAR2LTE_RAM_RANGE  *Range
  )
{
  EFI_STATUS  Status;

  //
  // Map the range before publishing it as conventional memory. AddMemorySpace
  // may grow DXE's memory-map bookkeeping and allocate from the newly added
  // highest range immediately.
  //
  Status = ArmSetMemoryAttributes (
             Range->Base,
             Range->Length,
             EFI_MEMORY_WB,
             0
             );
  if (EFI_ERROR (Status)) {
    return Status;
  }

  Status = gDS->AddMemorySpace (
                  EfiGcdMemoryTypeSystemMemory,
                  Range->Base,
                  Range->Length,
                  EFI_MEMORY_UC | EFI_MEMORY_WC | EFI_MEMORY_WT | EFI_MEMORY_WB
                  );
  if (EFI_ERROR (Status)) {
    return Status;
  }

  Status = gDS->SetMemorySpaceAttributes (
                  Range->Base,
                  Range->Length,
                  EFI_MEMORY_WB
                  );
  if (EFI_ERROR (Status)) {
    gDS->RemoveMemorySpace (Range->Base, Range->Length);
    return Status;
  }

  return EFI_SUCCESS;
}

EFI_STATUS
EFIAPI
Star2LteRamManagerEntry (
  IN EFI_HANDLE        ImageHandle,
  IN EFI_SYSTEM_TABLE  *SystemTable
  )
{
  volatile UINT32  *Result;
  EFI_STATUS       Status;
  EFI_STATUS       RollbackStatus;
  UINTN            Index;
  UINTN            MappedCount;

  Result    = (volatile UINT32 *)(UINTN)STAR2LTE_RAM_STATUS_ADDR;
  Result[0] = 0x52414D32u; // 'RAM2'
  Result[1] = MAX_UINT32;
  Result[2] = MAX_UINT32;
  MappedCount = 0;

  for (Index = 0; Index < ARRAY_SIZE (mRamRanges); Index++) {
    Status = Star2LteAddRamRange (&mRamRanges[Index]);
    Result[Index + 1] = (UINT32)Status;
    ArmDataSynchronizationBarrier ();
    if (EFI_ERROR (Status)) {
      while (MappedCount > 0) {
        MappedCount--;
        RollbackStatus = gDS->RemoveMemorySpace (
                                mRamRanges[MappedCount].Base,
                                mRamRanges[MappedCount].Length
                                );
        if (EFI_ERROR (RollbackStatus)) {
          DEBUG ((
            DEBUG_ERROR,
            "Star2LteRamManager: rollback failed for 0x%Lx: %r\n",
            mRamRanges[MappedCount].Base,
            RollbackStatus
            ));
        }
      }

      DEBUG ((
        DEBUG_ERROR,
        "Star2LteRamManager: failed to add 0x%Lx..0x%Lx: %r\n",
        mRamRanges[Index].Base,
        mRamRanges[Index].Base + mRamRanges[Index].Length,
        Status
        ));
      return Status;
    }

    MappedCount++;
  }

  DEBUG ((
    DEBUG_INFO,
    "Star2LteRamManager: published %u verified bank-1 ranges\n",
    (UINT32)ARRAY_SIZE (mRamRanges)
    ));
  return EFI_SUCCESS;
}
