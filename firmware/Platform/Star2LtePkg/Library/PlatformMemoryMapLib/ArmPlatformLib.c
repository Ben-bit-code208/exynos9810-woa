/** @file
  ArmPlatformLib entry points for Star2LtePkg (Exynos 9810 / Galaxy S9+).

  Complements PlatformMemoryMapLib.c (which provides ArmPlatformGetVirtualMemoryMap)
  to form a complete ArmPlatformLib used by the ArmPlatformPkg PrePi boot flow.

  These are minimal, "sboot already did the heavy lifting" implementations:
  DRAM and the debug UART clock/pinmux are assumed live on entry. Fill in real
  init only where a milestone proves it necessary.
**/

#include <Library/ArmPlatformLib.h>
#include <Library/ArmLib.h>
#include <Library/DebugLib.h>
#include <Library/PcdLib.h>
#include <Ppi/ArmMpCoreInfo.h>
#include "Star2LteFbText.h"

#ifndef STAR2LTE_EARLY_FB_TEXT
#define STAR2LTE_EARLY_FB_TEXT 0
#endif

//
// DIAG (bring-up): append one breadcrumb char to the persistent-RAM ramoops
// pmsg zone (0xFED14000, m1 'DBGC' format) so TWRP can read how far we got via
// /sys/fs/pstore/pmsg-ramoops-0 even when the display shows nothing. Re-ensures
// the header in case the shim's breadcrumb did not run. Pre-MMU here = direct
// DRAM store; a dsb is sufficient.
//
STATIC
VOID
M1Pram (
  IN CHAR8  C
  )
{
  volatile UINT32  *W = (volatile UINT32 *)(UINTN)0xFED14000ULL;
  volatile UINT8   *B = (volatile UINT8 *)(UINTN)0xFED14000ULL;
  UINT32           Size;

  if (W[0] != 0x43474244u) {   // 'DBGC' ramoops signature
    W[0] = 0x43474244u;
    W[1] = 0u;
    W[2] = 0u;
  }
  Size = W[2];
  if (Size < 0x3F00u) {
    B[12u + Size] = (UINT8)C;
    W[2]          = Size + 1u;
  }
  ArmDataSynchronizationBarrier ();
}

/**
  Per-core early init, called very early from PrePi for each booting CPU.
  sboot leaves DRAM and the debug UART configured, so nothing is mandatory yet.
**/
RETURN_STATUS
ArmPlatformInitialize (
  IN UINTN  MpId
  )
{
  // DIAG breadcrumb 'C' = PeilessSec CEntryPoint reached our platform C code.
  // FIRST proof the EDK II SEC asm entry + stack are good and C is executing.
  M1Pram ('C');

#if STAR2LTE_EARLY_FB_TEXT
  // On-panel progress text (replaces the old blue diagnostic band). Drawn direct
  // to the scanout buffer; becomes visible at the first DECON present (BDS).
  Star2LteFbText (1240u, "Star2Lte UEFI  -  starting (SEC: platform init)...");
#endif

  // TODO: any clock/GPIO setup that MUST precede DRAM/UART use (likely none,
  // because sboot configured them before chainloading us).
  return RETURN_SUCCESS;
}

/**
  DRAM is already trained and online (sboot used it). Nothing to do.
**/
VOID
ArmPlatformInitializeSystemMemory (
  VOID
  )
{
}

/**
  Action taken at the very start of the PEI phase. No-op for now.
**/
VOID
ArmPlatformPeiBootAction (
  VOID
  )
{
  // DIAG breadcrumb 'P': this is the FIRST C function PeilessSec's asm entry
  // calls, BEFORE the EDK II stack is set up. If 'P' appears in pram (trail
  // "ABP..."), the FD-entry branch + SEC asm prologue are good and we are
  // executing firmware C; if it does NOT appear (trail stays "AB"), the branch
  // target is wrong or the asm faults before any bl. Keep this leaf-simple.
  M1Pram ('P');
}

/**
  Boot mode. We always do a full configuration boot (no S3/resume path yet).
**/
EFI_BOOT_MODE
ArmPlatformGetBootMode (
  VOID
  )
{
  return BOOT_WITH_FULL_CONFIGURATION;
}

/**
  Linear core position used to index per-core stacks/structures.
  Exynos 9810 = 2 clusters x 4 cores. position = cluster * coresPerCluster + core.
**/
UINTN
ArmPlatformGetCorePosition (
  IN UINTN  MpId
  )
{
  UINTN  CoresPerCluster;

  CoresPerCluster = PcdGet32 (PcdCoreCount) / PcdGet32 (PcdClusterCount);
  return (GET_CLUSTER_ID (MpId) * CoresPerCluster) + GET_CORE_ID (MpId);
}

/**
  MpId of the core sboot boots us on. Almost certainly LITTLE cluster 0 core 0
  (MpId 0x0), but verify via the UART log in Milestone 1.
**/
UINTN
ArmPlatformGetPrimaryCoreMpId (
  VOID
  )
{
  return (UINTN)PcdGet32 (PcdArmPrimaryCore);
}

/**
  TRUE if MpId is the primary (boot) core. Compare cluster+core affinity only.
**/
BOOLEAN
ArmPlatformIsPrimaryCore (
  IN UINTN  MpId
  )
{
  return (BOOLEAN)((MpId & (ARM_CLUSTER_MASK | ARM_CORE_MASK)) ==
                   (PcdGet32 (PcdArmPrimaryCore) & (ARM_CLUSTER_MASK | ARM_CORE_MASK)));
}

/**
  MP Core Info for star2lte / Exynos 9810.

  This SoC is an 8-core SMP part (4x Cortex-A55 in cluster 0, MPIDR aff 0.0.0..0.0.3;
  4x Mongoose M3 in cluster 1), so MPIDR_EL1.U == 0 and ArmIsMpCore() returns TRUE.
  PeilessSec/SecMain therefore *requires* the ARM_MP_CORE_INFO_PPI (it does
  ASSERT_EFI_ERROR on GetPlatformPpi (gArmMpCoreInfoPpiGuid)); leaving the PPI list
  empty made SecMain CpuDeadLoop right after MemoryPeim (device-confirmed: the SEC
  breadcrumb trail stopped at 'F' with no 'G').

  We report the boot core only. The mailbox fields are zeroed because secondary
  cores on this platform are started via PSCI (SMC), not a hardware mailbox; nothing
  in the current DXE set consumes the mailbox addresses. This satisfies SecMain's
  contract so PEI can proceed into DXE. Expand to the full 8-core table when real
  multi-core support (PSCI MP services) is added.
**/
STATIC ARM_CORE_INFO  mStar2LteCoreInfoTable[] = {
  {
    // Cluster 0, Core 0 — the primary/boot core (MPIDR affinity 0.0.0).
    0x0000000000000000ULL,   // Mpidr
    0, 0, 0, 0               // Mailbox Set/Get/Clear addresses + clear value (unused; PSCI)
  }
};

STATIC
EFI_STATUS
EFIAPI
Star2LteGetMpCoreInfo (
  OUT UINTN          *CoreCount,
  OUT ARM_CORE_INFO  **CoreInfoTable
  )
{
  if ((CoreCount == NULL) || (CoreInfoTable == NULL)) {
    return EFI_INVALID_PARAMETER;
  }

  *CoreCount     = ARRAY_SIZE (mStar2LteCoreInfoTable);
  *CoreInfoTable = mStar2LteCoreInfoTable;
  return EFI_SUCCESS;
}

STATIC ARM_MP_CORE_INFO_PPI  mStar2LteMpCoreInfoPpi = {
  Star2LteGetMpCoreInfo
};

STATIC EFI_PEI_PPI_DESCRIPTOR  mStar2LtePlatformPpiList[] = {
  {
    EFI_PEI_PPI_DESCRIPTOR_PPI | EFI_PEI_PPI_DESCRIPTOR_TERMINATE_LIST,
    &gArmMpCoreInfoPpiGuid,
    &mStar2LteMpCoreInfoPpi
  }
};

/**
  Provide the platform PEI PPI list. star2lte must publish the ARM MP Core Info
  PPI (see above) so PeilessSec/SecMain does not assert on an MP-capable part.
**/
VOID
ArmPlatformGetPlatformPpiList (
  OUT UINTN                   *PpiListSize,
  OUT EFI_PEI_PPI_DESCRIPTOR  **PpiList
  )
{
  *PpiListSize = sizeof (mStar2LtePlatformPpiList);
  *PpiList     = mStar2LtePlatformPpiList;
}
