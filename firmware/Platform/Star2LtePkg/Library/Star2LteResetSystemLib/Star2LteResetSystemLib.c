/** @file
  Runtime reset and power-off support for Exynos 9810 / Galaxy S9+.

  PSCI remains the preferred path. If the secure monitor returns, use the
  Exynos PMU mechanisms used by Samsung's Exynos9810 kernel.
**/

#include <PiDxe.h>

#include <IndustryStandard/ArmStdSmc.h>

#include <Library/ArmLib.h>
#include <Library/ArmMonitorLib.h>
#include <Library/BaseLib.h>
#include <Library/DebugLib.h>
#include <Library/DxeServicesTableLib.h>
#include <Library/IoLib.h>
#include <Library/ResetSystemLib.h>
#include <Library/UefiBootServicesTableLib.h>
#include <Library/UefiRuntimeLib.h>
#include <Platform/Exynos9810.h>

#define EXYNOS9810_PMU_SWRESET_OFFSET         0x0400U
#define EXYNOS9810_PMU_PS_HOLD_CONTROL_OFFSET 0x330CU
#define EXYNOS9810_PMU_PS_HOLD_ENABLE         BIT8
#define STAR2LTE_PRAM_BASE                     0xFED14000ULL
#define STAR2LTE_PRAM_MAGIC                    0x43474244U
#define STAR2LTE_PRAM_HEADER_SIZE              12U
#define STAR2LTE_PRAM_CAPACITY                 0x3F00U

STATIC volatile UINT32  *mSwReset;
STATIC volatile UINT32  *mPsHoldControl;
STATIC volatile UINT8   *mPram;
STATIC BOOLEAN          mSwResetVirtualReady;
STATIC BOOLEAN          mPsHoldVirtualReady;
STATIC BOOLEAN          mPramVirtualReady;
STATIC EFI_EVENT        mVirtualAddressChangeEvent;

STATIC
EFI_STATUS
MarkRangeRuntime (
  IN UINTN  Address,
  IN UINTN  Size
  )
{
  EFI_PHYSICAL_ADDRESS             PageAddress;
  EFI_GCD_MEMORY_SPACE_DESCRIPTOR  Descriptor;
  EFI_STATUS                       Status;
  UINT64                           Attributes;
  UINT64                           Capabilities;
  UINT64                           RangeLength;

  PageAddress = Address & ~(EFI_PHYSICAL_ADDRESS)EFI_PAGE_MASK;
  RangeLength = EFI_PAGES_TO_SIZE (
                  EFI_SIZE_TO_PAGES ((Address & EFI_PAGE_MASK) + Size)
                  );
  Status      = gDS->GetMemorySpaceDescriptor (PageAddress, &Descriptor);
  if (EFI_ERROR (Status)) {
    return Status;
  }

  if (Descriptor.GcdMemoryType == EfiGcdMemoryTypeNonExistent) {
    Status = gDS->AddMemorySpace (
                    EfiGcdMemoryTypeMemoryMappedIo,
                    PageAddress,
                    RangeLength,
                    EFI_MEMORY_UC | EFI_MEMORY_RUNTIME
                    );
    if (EFI_ERROR (Status)) {
      return Status;
    }

    Attributes = EFI_MEMORY_UC | EFI_MEMORY_RUNTIME;
  } else {
    if (Descriptor.GcdMemoryType != EfiGcdMemoryTypeMemoryMappedIo) {
      return EFI_UNSUPPORTED;
    }

    if (((Descriptor.Attributes & EFI_CACHE_ATTRIBUTE_MASK) != 0) &&
        ((Descriptor.Attributes & EFI_CACHE_ATTRIBUTE_MASK) != EFI_MEMORY_UC))
    {
      return EFI_UNSUPPORTED;
    }

    Capabilities = Descriptor.Capabilities | EFI_MEMORY_UC | EFI_MEMORY_RUNTIME;
    if (Capabilities != Descriptor.Capabilities) {
      Status = gDS->SetMemorySpaceCapabilities (
                      PageAddress,
                      RangeLength,
                      Capabilities
                      );
      if (EFI_ERROR (Status)) {
        return Status;
      }
    }

    Attributes = Descriptor.Attributes | EFI_MEMORY_UC | EFI_MEMORY_RUNTIME;
  }

  return gDS->SetMemorySpaceAttributes (
                PageAddress,
                RangeLength,
                Attributes
                );
}

STATIC
VOID
EFIAPI
VirtualAddressChange (
  IN EFI_EVENT  Event,
  IN VOID       *Context
  )
{
  EFI_STATUS  Status;
  VOID        *Address;

  Address = (VOID *)(UINTN)mSwReset;
  Status  = EfiConvertPointer (0, &Address);
  if (!EFI_ERROR (Status)) {
    mSwReset             = (volatile UINT32 *)Address;
    mSwResetVirtualReady = TRUE;
  }

  Address = (VOID *)(UINTN)mPsHoldControl;
  Status  = EfiConvertPointer (0, &Address);
  if (!EFI_ERROR (Status)) {
    mPsHoldControl             = (volatile UINT32 *)Address;
    mPsHoldVirtualReady        = TRUE;
  }

  Address = (VOID *)(UINTN)mPram;
  Status  = EfiConvertPointer (0, &Address);
  if (!EFI_ERROR (Status)) {
    mPram             = (volatile UINT8 *)Address;
    mPramVirtualReady = TRUE;
  }
}

EFI_STATUS
EFIAPI
Star2LteResetSystemLibConstructor (
  IN EFI_HANDLE        ImageHandle,
  IN EFI_SYSTEM_TABLE  *SystemTable
  )
{
  EFI_STATUS  Status;

  mSwReset       = (volatile UINT32 *)(UINTN)(EXYNOS_PMU_BASE + EXYNOS9810_PMU_SWRESET_OFFSET);
  mPsHoldControl = (volatile UINT32 *)(UINTN)(EXYNOS_PMU_BASE + EXYNOS9810_PMU_PS_HOLD_CONTROL_OFFSET);
  mPram          = (volatile UINT8 *)(UINTN)STAR2LTE_PRAM_BASE;

  Status = MarkRangeRuntime ((UINTN)mSwReset, sizeof (*mSwReset));
  if (EFI_ERROR (Status)) {
    DEBUG ((DEBUG_ERROR, "ResetSystem: SWRESET runtime mapping failed: %r\n", Status));
  }

  Status = MarkRangeRuntime ((UINTN)mPsHoldControl, sizeof (*mPsHoldControl));
  if (EFI_ERROR (Status)) {
    DEBUG ((DEBUG_ERROR, "ResetSystem: PS_HOLD runtime mapping failed: %r\n", Status));
  }

  Status = MarkRangeRuntime (
             (UINTN)mPram,
             STAR2LTE_PRAM_HEADER_SIZE + STAR2LTE_PRAM_CAPACITY
             );
  if (EFI_ERROR (Status)) {
    DEBUG ((DEBUG_ERROR, "ResetSystem: PRAM runtime mapping failed: %r\n", Status));
  }

  Status = gBS->CreateEvent (
                  EVT_SIGNAL_VIRTUAL_ADDRESS_CHANGE,
                  TPL_NOTIFY,
                  VirtualAddressChange,
                  NULL,
                  &mVirtualAddressChangeEvent
                  );
  if (EFI_ERROR (Status)) {
    DEBUG ((DEBUG_ERROR, "ResetSystem: virtual-address event failed: %r\n", Status));
  }

  //
  // Preserve PSCI ResetSystem services even if the optional PMU fallback could
  // not be made runtime-addressable.
  //
  return EFI_SUCCESS;
}

STATIC
VOID
RuntimePramByte (
  IN UINT8  Value
  )
{
  volatile UINT32  *Words;
  UINT32           Position;

  if (EfiGoneVirtual () && !mPramVirtualReady) {
    return;
  }

  Words = (volatile UINT32 *)(UINTN)mPram;
  if (Words[0] != STAR2LTE_PRAM_MAGIC) {
    Words[0] = STAR2LTE_PRAM_MAGIC;
    Words[1] = 0;
    Words[2] = 0;
  }

  Position = Words[2];
  if (Position < STAR2LTE_PRAM_CAPACITY) {
    mPram[STAR2LTE_PRAM_HEADER_SIZE + Position] = Value;
    Words[2]                                    = Position + 1U;
  }
}

STATIC
VOID
RuntimePramString (
  IN CONST CHAR8  *String
  )
{
  while (*String != '\0') {
    RuntimePramByte ((UINT8)*String++);
  }
}

STATIC
VOID
RuntimePramHex64 (
  IN UINT64  Value
  )
{
  STATIC CONST CHAR8  Hex[] = "0123456789abcdef";
  UINTN               Index;

  for (Index = 0; Index < 16; Index++) {
    RuntimePramByte ((UINT8)Hex[(Value >> (60U - (Index * 4U))) & 0xFU]);
  }
}

STATIC
VOID
RuntimePramMarker (
  IN CONST CHAR8  *Tag,
  IN UINT64       Value
  )
{
  RuntimePramString ("\n=");
  RuntimePramString (Tag);
  RuntimePramByte ((UINT8)' ');
  RuntimePramHex64 (Value);
  ArmDataSynchronizationBarrier ();
}

STATIC
VOID
WaitForReset (
  VOID
  )
{
  DisableInterrupts ();
  for (;;) {
    ArmCallWFI ();
  }
}

STATIC
VOID
AttemptPsciSystemReset (
  VOID
  )
{
  ARM_MONITOR_ARGS  Args;

  Args.Arg0 = ARM_SMC_ID_PSCI_SYSTEM_RESET;
  ArmMonitorCall (&Args);
  RuntimePramMarker ("PSR", Args.Arg0);
}

STATIC
VOID
AttemptPsciSystemReset2 (
  VOID
  )
{
  ARM_MONITOR_ARGS  Args;

  Args.Arg0 = ARM_SMC_ID_PSCI_FEATURES;
  Args.Arg1 = ARM_SMC_ID_PSCI_SYSTEM_RESET2_AARCH64;
  ArmMonitorCall (&Args);
  RuntimePramMarker ("P2F", Args.Arg0);
  if (Args.Arg0 == ARM_SMC_PSCI_RET_SUCCESS) {
    Args.Arg0 = ARM_SMC_ID_PSCI_SYSTEM_RESET2_AARCH64;
    Args.Arg1 = 0;
    Args.Arg2 = 0;
    ArmMonitorCall (&Args);
    RuntimePramMarker ("P2R", Args.Arg0);
  }
}

STATIC
VOID
ExynosPmuReset (
  VOID
  )
{
  if (!EfiGoneVirtual () || mSwResetVirtualReady) {
    RuntimePramMarker ("SWR", BIT0);
    RuntimePramMarker ("SWA", BIT0);
    MmioWrite32 ((UINTN)mSwReset, BIT0);
    ArmDataSynchronizationBarrier ();
    ArmInstructionSynchronizationBarrier ();
  }

  WaitForReset ();
}

STATIC
VOID
ExynosPmuPowerOff (
  VOID
  )
{
  UINT32  Value;

  if (!EfiGoneVirtual () || mPsHoldVirtualReady) {
    Value = MmioRead32 ((UINTN)mPsHoldControl);
    RuntimePramMarker ("PHB", Value);
    Value &= ~EXYNOS9810_PMU_PS_HOLD_ENABLE;
    RuntimePramMarker ("PHA", Value);
    MmioWrite32 (
      (UINTN)mPsHoldControl,
      Value
      );
    ArmDataSynchronizationBarrier ();
    ArmInstructionSynchronizationBarrier ();
  }

  WaitForReset ();
}

VOID
EFIAPI
ResetCold (
  VOID
  )
{
  AttemptPsciSystemReset ();
  ExynosPmuReset ();
}

VOID
EFIAPI
ResetWarm (
  VOID
  )
{
  AttemptPsciSystemReset2 ();
  AttemptPsciSystemReset ();
  ExynosPmuReset ();
}

VOID
EFIAPI
ResetShutdown (
  VOID
  )
{
  ARM_MONITOR_ARGS  Args;

  Args.Arg0 = ARM_SMC_ID_PSCI_SYSTEM_OFF;
  ArmMonitorCall (&Args);
  RuntimePramMarker ("PSO", Args.Arg0);
  ExynosPmuPowerOff ();
}

VOID
EFIAPI
ResetPlatformSpecific (
  IN UINTN  DataSize,
  IN VOID   *ResetData
  )
{
  ResetCold ();
}

VOID
EFIAPI
ResetSystem (
  IN EFI_RESET_TYPE  ResetType,
  IN EFI_STATUS      ResetStatus,
  IN UINTN           DataSize,
  IN VOID            *ResetData OPTIONAL
  )
{
  UINT32  State;

  State = (UINT32)ResetType;
  if (EfiGoneVirtual ()) {
    State |= BIT8;
  }

  if (mSwResetVirtualReady) {
    State |= BIT9;
  }

  if (mPsHoldVirtualReady) {
    State |= BIT10;
  }

  if (mPramVirtualReady) {
    State |= BIT11;
  }

  RuntimePramMarker ("RSE", State);
  RuntimePramMarker ("RSS", ResetStatus);
  RuntimePramMarker ("RSD", DataSize);

  switch (ResetType) {
    case EfiResetWarm:
      ResetWarm ();
      break;

    case EfiResetCold:
      ResetCold ();
      break;

    case EfiResetShutdown:
      ResetShutdown ();
      break;

    case EfiResetPlatformSpecific:
      ResetPlatformSpecific (DataSize, ResetData);
      break;

    default:
      RuntimePramMarker ("UNS", ResetType);
      WaitForReset ();
  }
}
