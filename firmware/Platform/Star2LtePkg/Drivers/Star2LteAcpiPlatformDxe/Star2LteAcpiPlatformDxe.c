/** @file
  Installs the platform ACPI tables packaged as a freeform FFS.
**/

#include <Uefi.h>
#include <Library/BaseMemoryLib.h>
#include <Library/DebugLib.h>
#include <Library/UefiBootServicesTableLib.h>
#include <Protocol/AcpiTable.h>
#include <Protocol/FirmwareVolume2.h>
#include <IndustryStandard/Acpi.h>
#include <IndustryStandard/Acpi63.h>
#include <Library/CacheMaintenanceLib.h>

STATIC EFI_GUID  mStar2LteAcpiTablesFileGuid = {
  0x11111111, 0x2222, 0x3333, { 0x44, 0x44, 0x55, 0x55, 0x55, 0x55, 0x55, 0x01 }
};

STATIC
VOID
AcpiPramByte (
  IN UINT8  Ch
  )
{
  volatile UINT32  *Words;
  volatile UINT8   *Bytes;
  UINT32           Position;

  Words = (volatile UINT32 *)(UINTN)0xFED14000ULL;
  Bytes = (volatile UINT8  *)(UINTN)0xFED14000ULL;
  if (Words[0] != 0x43474244u) {
    Words[0] = 0x43474244u;
    Words[1] = 0;
    Words[2] = 0;
  }
  Position = Words[2];
  if (Position < 0x3F00u) {
    Bytes[12u + Position] = Ch;
    Words[2] = Position + 1u;
  }
}

STATIC
VOID
AcpiPramStr (
  IN CONST CHAR8  *Str
  )
{
  while (*Str != '\0') {
    AcpiPramByte ((UINT8)*Str++);
  }
}

STATIC
VOID
AcpiPramHex (
  IN UINT32  Val,
  IN UINTN   Nibbles
  )
{
  CONST CHAR8  *Hex;

  Hex = "0123456789abcdef";
  while (Nibbles-- > 0) {
    AcpiPramByte ((UINT8)Hex[(Val >> (Nibbles * 4u)) & 0xFu]);
  }
}

STATIC
VOID
AcpiPramSig (
  IN UINT32  Signature
  )
{
  AcpiPramByte ((UINT8)(Signature & 0xFFu));
  AcpiPramByte ((UINT8)((Signature >> 8) & 0xFFu));
  AcpiPramByte ((UINT8)((Signature >> 16) & 0xFFu));
  AcpiPramByte ((UINT8)((Signature >> 24) & 0xFFu));
}

//
// --- GTDT memory-mapped counter fix for CNTFRQ_EL0 = 0 ---------------------
//
// On this SoC EL3 never programs CNTFRQ_EL0, so it reads 0 on every core and
// Windows' architected timer has no frequency -> deterministic hang right after
// the logo. CNTPCT_EL0 (the count) DOES advance (fed by the Samsung MCT global
// counter at 26 MHz). The ACPI GTDT lets us hand the OS a memory-mapped counter
// description instead: a CntControlBase frame whose CNTFID0 = 26 MHz (the missing
// frequency), and CntReadBase pointed at the real free-running MCT count. This is
// the ARM/SBSA-blessed path for "CNTFRQ not correctly initialized": read the
// frequency from CNTControlBase.CNTFID0.
//
#define STAR2_CNTFRQ_HZ     26000000u      // 0x18CBA80 = DT arch-timer clock-frequency
#define STAR2_MCT_CNTCV_PA  0x10040100ULL  // MCT G_CNT low/high = 64-bit free-running count

//
// EXPERIMENT (2026-07-07, ext39): Mu-Silicium's exynos-refactor "Allow Windows
// Boot" config leaves the GTDT counter bases all-ones (no memory-mapped counter)
// and fixes CNTFRQ_EL0=0 PURELY by patching the kernel's `mrs x8,cntfrq_el0`
// reads to return 26 MHz. We already apply those CNTFRQ register patches (static
// + dynamic T6 scanner). Handing Windows an ADDITIONAL memory-mapped counter
// frame (our synthetic CntControlBase + Samsung MCT CntReadBase) makes the kernel
// prefer the MMIO timer path; if that frame is unusable in the kernel's own VA
// map, the scheduler never ticks -> logo with NO spinning dots (exactly our
// symptom). Set to 0 to match Mu (leave GTDT all-ones, register timer only).
// Set to 1 to restore the memory-mapped counter frame patch.
//
#ifndef STAR2LTE_GTDT_MMIO_COUNTER
#define STAR2LTE_GTDT_MMIO_COUNTER  0
#endif

// Register offsets within an ARM CNTControlBase frame (DDI0487, memory-mapped).
#define CNT_CR     0x00u   // Counter Control Register (bit0 EN = counter enabled)
#define CNT_SR     0x04u   // Counter Status Register
#define CNT_CV_LO  0x08u   // Counter Count Value [31:0]
#define CNT_CV_HI  0x0Cu   // Counter Count Value [63:32]
#define CNT_FID0   0x20u   // Frequency mode table entry 0 = base frequency (Hz)
#define CNT_FID1   0x24u   // Frequency mode table terminator (0)

STATIC
VOID
Star2LtePatchGtdt (
  IN OUT EFI_ACPI_DESCRIPTION_HEADER  *Header
  )
{
  EFI_ACPI_6_3_GENERIC_TIMER_DESCRIPTION_TABLE  *Gtdt;
  EFI_STATUS                                    Status;
  EFI_PHYSICAL_ADDRESS                          FrameAddr;
  volatile UINT8                                *Frame;
  UINT8                                         *Bytes;
  UINT8                                         Sum;
  UINTN                                         Index;

  if (Header->Length < sizeof (EFI_ACPI_6_3_GENERIC_TIMER_DESCRIPTION_TABLE)) {
    return;
  }
  Gtdt = (EFI_ACPI_6_3_GENERIC_TIMER_DESCRIPTION_TABLE *)Header;

  //
  // ext39 experiment: when the memory-mapped counter is disabled, leave the
  // GTDT CntControlBase/CntReadBase as the all-ones placeholders from Gtdt.aslc
  // so Windows uses the CNTVCT_EL0/CNTFRQ_EL0 system-register timer only (the
  // Mu-Silicium "Allow Windows Boot" path). CNTFRQ_EL0=0 is fixed by the kernel
  // mrs-patch, not here. Skipping avoids handing the kernel an MMIO timer frame
  // it may be unable to use in its own VA map (logo hang, no spinner).
  //
  if (STAR2LTE_GTDT_MMIO_COUNTER == 0) {
    AcpiPramStr ("\n=GTDTPATCH sk=1");
    return;
  }


  // memory is carved out of usable RAM so Windows never allocates over it, and
  // it maps the base as MMIO (uncached) via the GTDT physical address.
  //
  FrameAddr = 0;
  Status    = gBS->AllocatePages (AllocateAnyPages, EfiReservedMemoryType, 1, &FrameAddr);
  AcpiPramStr ("\n=GTDTPATCH al=");
  AcpiPramByte (EFI_ERROR (Status) ? (UINT8)'E' : (UINT8)'S');
  if (EFI_ERROR (Status)) {
    AcpiPramHex ((UINT32)(Status & 0xFFFFu), 4);
    return;
  }

  Frame = (volatile UINT8 *)(UINTN)FrameAddr;
  ZeroMem ((VOID *)(UINTN)FrameAddr, SIZE_4KB);
  *(volatile UINT32 *)(Frame + CNT_CR)    = 0x1u;             // counter enabled
  *(volatile UINT32 *)(Frame + CNT_SR)    = 0x0u;
  *(volatile UINT32 *)(Frame + CNT_CV_LO) = 0x0u;             // count is read via CntReadBase (MCT)
  *(volatile UINT32 *)(Frame + CNT_CV_HI) = 0x0u;
  *(volatile UINT32 *)(Frame + CNT_FID0)  = STAR2_CNTFRQ_HZ;  // 26 MHz base frequency
  *(volatile UINT32 *)(Frame + CNT_FID1)  = 0x0u;             // frequency table terminator

  //
  // Flush the frame to DRAM: Windows maps this base as uncached MMIO and must
  // observe the values we wrote through the firmware's cached mapping.
  //
  WriteBackDataCacheRange ((VOID *)(UINTN)FrameAddr, SIZE_4KB);

  Gtdt->CntControlBasePhysicalAddress = FrameAddr;
  Gtdt->CntReadBasePhysicalAddress    = STAR2_MCT_CNTCV_PA;

  //
  // Recompute the ACPI 8-bit checksum over the whole (now-patched) table.
  //
  Gtdt->Header.Checksum = 0;
  Bytes = (UINT8 *)Header;
  Sum   = 0;
  for (Index = 0; Index < Header->Length; Index++) {
    Sum = (UINT8)(Sum + Bytes[Index]);
  }
  Gtdt->Header.Checksum = (UINT8)(0 - Sum);

  //
  // Stamp the patched values into a fixed scratch word (0xFED13F00, in the mapped
  // pram-diag zone, OUTSIDE the DBGC trace ring at 0xFED14000). The survivable
  // =EXC exception dump reads this back to confirm the patch applied with the
  // right values even after the trace ring has wrapped past this early marker.
  //
  {
    volatile UINT32  *Scratch = (volatile UINT32 *)(UINTN)0xFED13F00ULL;
    Scratch[0] = 0x47544450u;                     // 'GTDP' magic
    Scratch[1] = (UINT32)FrameAddr;               // CntControlBase (low 32)
    Scratch[2] = (UINT32)STAR2_MCT_CNTCV_PA;      // CntReadBase (low 32)
    Scratch[3] = STAR2_CNTFRQ_HZ;                 // CNTFID0 frequency
  }

  AcpiPramStr ("cc=");
  AcpiPramHex ((UINT32)(FrameAddr >> 32), 8);
  AcpiPramHex ((UINT32)FrameAddr, 8);
  AcpiPramStr (" cr=");
  AcpiPramHex ((UINT32)STAR2_MCT_CNTCV_PA, 8);
  AcpiPramStr (" fq=");
  AcpiPramHex (STAR2_CNTFRQ_HZ, 8);
}

EFI_STATUS
EFIAPI
Star2LteAcpiPlatformEntry (
  IN EFI_HANDLE        ImageHandle,
  IN EFI_SYSTEM_TABLE  *SystemTable
  )
{
  EFI_STATUS                      Status;
  EFI_ACPI_TABLE_PROTOCOL         *AcpiTable;
  EFI_HANDLE                      *FvHandles;
  UINTN                           FvCount;
  UINTN                           FvIndex;
  UINTN                           InstalledCount;

  AcpiTable      = NULL;
  FvHandles      = NULL;
  FvCount        = 0;
  InstalledCount = 0;

  Status = gBS->LocateProtocol (&gEfiAcpiTableProtocolGuid, NULL, (VOID **)&AcpiTable);
  if (EFI_ERROR (Status)) {
    return Status;
  }

  Status = gBS->LocateHandleBuffer (
                  ByProtocol,
                  &gEfiFirmwareVolume2ProtocolGuid,
                  NULL,
                  &FvCount,
                  &FvHandles
                  );
  AcpiPramStr ("\n=ACPIINST A22 fvst=");
  AcpiPramByte (EFI_ERROR (Status) ? (UINT8)'E' : (UINT8)'S');
  AcpiPramHex ((UINT32)(Status & 0xFFFFu), 4);
  AcpiPramStr (" n=");
  AcpiPramHex ((UINT32)FvCount, 2);

  if (EFI_ERROR (Status)) {
    return Status;
  }

  for (FvIndex = 0; FvIndex < FvCount; FvIndex++) {
    EFI_FIRMWARE_VOLUME2_PROTOCOL  *Fv;
    UINTN                          SectionInstance;

    Status = gBS->HandleProtocol (FvHandles[FvIndex], &gEfiFirmwareVolume2ProtocolGuid, (VOID **)&Fv);
    if (EFI_ERROR (Status)) {
      continue;
    }

    for (SectionInstance = 0; SectionInstance < 16; SectionInstance++) {
      VOID                         *TableBuffer;
      UINTN                        TableSize;
      UINT32                       AuthenticationStatus;
      EFI_ACPI_DESCRIPTION_HEADER  *Header;
      UINTN                        TableKey;

      TableBuffer          = NULL;
      TableSize            = 0;
      AuthenticationStatus = 0;
      Status = Fv->ReadSection (
                     Fv,
                     &mStar2LteAcpiTablesFileGuid,
                     EFI_SECTION_RAW,
                     SectionInstance,
                     &TableBuffer,
                     &TableSize,
                     &AuthenticationStatus
                     );
      if (EFI_ERROR (Status)) {
        if ((Status == EFI_NOT_FOUND) && (SectionInstance != 0)) {
          break;
        }
        continue;
      }

      Header = (EFI_ACPI_DESCRIPTION_HEADER *)TableBuffer;
      if ((TableSize >= sizeof (EFI_ACPI_DESCRIPTION_HEADER)) &&
          (Header->Length <= TableSize) &&
          (Header->Length >= sizeof (EFI_ACPI_DESCRIPTION_HEADER))) {
        //
        // Give Windows a timer frequency via the GTDT memory-mapped counter
        // frames (CNTFRQ_EL0 reads 0 on this SoC).
        //
        if (Header->Signature == EFI_ACPI_6_3_GENERIC_TIMER_DESCRIPTION_TABLE_SIGNATURE) {
          Star2LtePatchGtdt (Header);
        }
        TableKey = 0;
        Status = AcpiTable->InstallAcpiTable (AcpiTable, Header, Header->Length, &TableKey);
        AcpiPramStr (" s");
        AcpiPramHex ((UINT32)SectionInstance, 1);
        AcpiPramByte ('=');
        AcpiPramSig (Header->Signature);
        AcpiPramStr (" len=");
        AcpiPramHex (Header->Length, 4);
        AcpiPramStr (" st=");
        AcpiPramByte (EFI_ERROR (Status) ? (UINT8)'E' : (UINT8)'S');
        AcpiPramHex ((UINT32)(Status & 0xFFFFu), 4);
        if (!EFI_ERROR (Status)) {
          InstalledCount++;
        }
      }

      if (TableBuffer != NULL) {
        gBS->FreePool (TableBuffer);
      }
    }
  }

  if (FvHandles != NULL) {
    gBS->FreePool (FvHandles);
  }

  AcpiPramStr (" done=");
  AcpiPramHex ((UINT32)InstalledCount, 2);
  AcpiPramByte ('\n');

  return (InstalledCount != 0) ? EFI_SUCCESS : EFI_NOT_FOUND;
}