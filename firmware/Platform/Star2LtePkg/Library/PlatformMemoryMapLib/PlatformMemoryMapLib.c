/** @file
  PlatformMemoryMapLib — Exynos 9810 / Galaxy S9+.

  Returns the ARM memory region descriptors used to build the UEFI page tables.
  Stays 1:1 with docs/02-memory-map.md and pulls every base/size from the single
  source of truth: Silicon/Exynos9810Pkg/Include/Platform/Exynos9810.h.

  Rules:
   - DRAM windows: ARM_MEMORY_REGION_ATTRIBUTE_WRITE_BACK (cached).
   - MMIO blocks: ARM_MEMORY_REGION_ATTRIBUTE_DEVICE (non-cached).
   - reserved-memory carveouts (TrustZone, CP/modem, GPU fw) must NOT be exposed
     as conventional RAM.
   - Device windows whose base is still 0 (unverified placeholder) are SKIPPED,
     so a TODO value never maps physical address 0 by accident.
**/

#include <Library/ArmPlatformLib.h>
#include <Library/ArmLib.h>
#include <Library/DebugLib.h>
#include <Platform/Exynos9810.h>
#include "Star2LteFbText.h"

#ifndef STAR2LTE_EARLY_FB_TEXT
#define STAR2LTE_EARLY_FB_TEXT 0
#endif
#ifndef STAR2LTE_USB_DEBUG_MODE
#define STAR2LTE_USB_DEBUG_MODE 0
#endif

#define MMAP_DRAM   ARM_MEMORY_REGION_ATTRIBUTE_WRITE_BACK
#define MMAP_DEV    ARM_MEMORY_REGION_ATTRIBUTE_DEVICE

//
// Table of candidate MMIO device windows. Anything with Base == 0 is skipped
// until it is filled in (Exynos9810.h). Keep in sync with docs/02-memory-map.md.
//
typedef struct {
  UINT64        Base;
  UINT64        Size;
  CONST CHAR8   *Name;
} MMIO_REGION;

STATIC CONST MMIO_REGION  mMmioRegions[] = {
  { EXYNOS_UART_BASE,       EXYNOS_UART_SIZE,       "UART"     },
  { EXYNOS_GICD_BASE,       EXYNOS_GICD_SIZE,       "GICD"     },
  { EXYNOS_GICC_BASE,       EXYNOS_GICC_SIZE,       "GICC"     },   // GICv2 CPU interface
  { EXYNOS_GICH_BASE,       EXYNOS_GICH_SIZE,       "GICH"     },   // GICv2 hypervisor
  { EXYNOS_GICV_BASE,       EXYNOS_GICV_SIZE,       "GICV"     },   // GICv2 virtual CPU
  { EXYNOS_CHIPID_BASE,     EXYNOS_CHIPID_SIZE,     "CHIPID"   },
  { EXYNOS_PMU_BASE,        EXYNOS_PMU_SIZE,        "PMU"      },
  { EXYNOS_SPEEDY_BASE,     EXYNOS_SPEEDY_SIZE,     "SPEEDY"   },
#if STAR2LTE_USB_DEBUG_MODE >= 1
  { EXYNOS_CMU_TOP_BASE,    EXYNOS_CMU_TOP_SIZE,    "CMU_TOP"  },
  { EXYNOS_CMU_FSYS0_BASE,  EXYNOS_CMU_FSYS0_SIZE,  "CMU_FSYS0" },
#else
  // Preserve ext209's exact page-table inputs outside an explicit USB profile.
  { 0,                       EXYNOS_CMU_TOP_SIZE,    "CMU_TOP"  },
#endif
  { EXYNOS_UFS_HCI_BASE,    EXYNOS_UFS_HCI_SIZE,    "UFS_HCI"  },
  { EXYNOS_UFS_PHY_BASE,    EXYNOS_UFS_PHY_SIZE,    "UFS_PHY"  },
  { EXYNOS_UFS_FMP_BASE,    EXYNOS_UFS_FMP_SIZE,    "UFS_FMP"  },
  { EXYNOS_UFS_UNIPRO_BASE, EXYNOS_UFS_UNIPRO_SIZE, "UFS_UNIPRO" },
  //
  // CYCLE 63: FSYS1 SYSREG (0x11010000). Holds the UFS DMA IO-coherency / sharability control at
  // 0x11010700 (bits 8|9). The reference exynos_ufs_modify_sysreg sets these (ufs-io-coherency DT node);
  // we never map/touch it. Mapped DEVICE so the UFS HC driver can READ it (cycle 63 is read-only: see if
  // sboot already set coherency) and, if warranted, set it later. 0x1000 covers 0x11010700.
  //
  { 0x0000000011010000ULL,  0x0000000000001000ULL, "FSYS1_SYSREG" },
  { EXYNOS_USBDRD_BASE,     EXYNOS_USBDRD_SIZE,     "USBDRD"   },
#if STAR2LTE_USB_DEBUG_MODE >= 1
  { EXYNOS_USBDRD_PHY_BASE, EXYNOS_USBDRD_PHY_SIZE, "USB_PHY"  },
#else
  // Preserve the inert legacy descriptor in the control profile.
  { 0x0000000010CB0000ULL,  0x0000000000001000ULL, "USB_PHY_LEGACY" },
#endif
  { EXYNOS_DECON_BASE,      EXYNOS_DECON_SIZE,      "DECON"    },
  { EXYNOS_DSIM_BASE,       EXYNOS_DSIM_SIZE,       "DSIM"     },
  { EXYNOS_WDT_BASE,        EXYNOS_WDT_SIZE,        "WDT"      },
};

#define MMIO_REGION_COUNT  (sizeof (mMmioRegions) / sizeof (mMmioRegions[0]))

/**
  Build the platform memory map. Caller frees nothing; table is static.

  @param[out] MemoryTable  Pointer to the descriptor array (NULL-terminated by
                           a zero-length entry).
**/
VOID
ArmPlatformGetVirtualMemoryMap (
  OUT ARM_MEMORY_REGION_DESCRIPTOR  **MemoryTable
  )
{
  // DRAM + framebuffer + pram-diag + all MMIO windows + terminator.
  STATIC ARM_MEMORY_REGION_DESCRIPTOR  Table[MMIO_REGION_COUNT + 5];
  UINTN                                Index;
  UINTN                                Region;

  ASSERT (MemoryTable != NULL);
  Index = 0;

  //
  // DIAG breadcrumb 'D' to persistent RAM (0xFED14000, ramoops pmsg) = MemoryPeim
  // asked for the memory map to build page tables; EDK II got through SEC + serial
  // banner and is about to enable the MMU. Readable from TWRP via
  // /sys/fs/pstore/pmsg-ramoops-0. Pre-MMU = direct DRAM store.
  //
  {
    volatile UINT32  *PW = (volatile UINT32 *)(UINTN)0xFED14000ULL;
    volatile UINT8   *PB = (volatile UINT8 *)(UINTN)0xFED14000ULL;
    UINT32           PSize;
    if (PW[0] != 0x43474244u) {
      PW[0] = 0x43474244u; PW[1] = 0u; PW[2] = 0u;
    }
    PSize = PW[2];
    if (PSize < 0x3F00u) {
      PB[12u + PSize] = (UINT8)'D';
      PW[2]           = PSize + 1u;
    }
    ArmDataSynchronizationBarrier ();
  }

  //
  // ext46 reserved-memory carveout REVERTED (ext47). Marking rkp_region
  // (0xAF800000+0x2000000) and tima_region (0xB8000000+0x200000) as
  // EfiReservedMemoryType via BuildMemoryAllocationHob REGRESSED the boot: winload
  // stopped loading the kernel entirely and dropped to its recovery screen (PRAM
  // pram-ext46-*.txt: it drew a new static framebuffer f25eea76, read OsIndications,
  // and spun on GetTime with ZERO further disk I/O — vs the working v10 boot which
  // did the big `bio sz=17df6000` kernel load right at that point). The carveout
  // also disproves the "TZASC silently drops writes" theory: winload itself writes
  // that RAM successfully in v10 (it runs to handoff), so if those writes vanished
  // winload — not just the kernel — would have died. Root cause of the =KLAT v10
  // TTBR hang is elsewhere; the decisive next datum is the actual TTBR0 root that
  // winload programmed (captured by the ext47 x8 dump hook in PlatformBootManagerLib).
  //

#if STAR2LTE_EARLY_FB_TEXT
  //
  // On-panel progress text (replaces the old yellow diagnostic band). Reached
  // when MemoryPeim asks for the memory map to build the page tables — i.e. SEC
  // is done and the MMU is about to come up. Drawn direct to the scanout buffer
  // (MMU still OFF here = physical); becomes visible at the first DECON present.
  //
  Star2LteFbText (1300u, "Star2Lte UEFI  -  memory map ready, starting DXE core...");
#endif

  //
  // --- DRAM (cached, identity-mapped) ---
  //
  Table[Index].PhysicalBase = EXYNOS_DRAM_BASE;
  Table[Index].VirtualBase  = EXYNOS_DRAM_BASE;
  Table[Index].Length       = EXYNOS_DRAM_SIZE;
  Table[Index].Attributes   = MMAP_DRAM;
  Index++;

  //
  // --- Framebuffer carveout (device/non-cached so writes hit the panel) ---
  // Only mapped once the base is verified.
  //
  // NOTE: EDK II's ArmConfigureMmu -> UpdateRegionMappingRecursive ASSERTs that
  // both the start AND end of every region are 4 KB page aligned:
  //   ASSERT (((RegionStart | RegionEnd) & EFI_PAGE_MASK) == 0);
  // With PcdDebugPropertyMask asserts enabled, a violation becomes a silent
  // CpuDeadLoop() (the firmware just hangs inside ArmConfigureMmu). The raw
  // framebuffer size 1440*2960*4 = 0x1043400 is NOT a page multiple, so its end
  // address is unaligned. Round every region length up to a whole page so the
  // mapper never trips the assert.
  //
  if (EXYNOS_FB_BASE != 0) {
    Table[Index].PhysicalBase = EXYNOS_FB_BASE;
    Table[Index].VirtualBase  = EXYNOS_FB_BASE;
    Table[Index].Length       = EFI_PAGES_TO_SIZE (EFI_SIZE_TO_PAGES (EXYNOS_FB_SIZE));
    Table[Index].Attributes   = MMAP_DEV;
    Index++;
  }

  //
  // --- ramoops/pram diagnostic zone (Device/non-cached) -------------------
  // The bring-up breadcrumb console lives at 0xFED14000 (and dump zone
  // 0xFED10000). It sits ABOVE EXYNOS_DRAM (which ends at 0xBC800000) so it is
  // NOT covered by the DRAM mapping. Map a 64KB Device window here so the
  // breadcrumb writes still reach physical DRAM AFTER EDK II's MMU replaces the
  // boot shim's coarse identity map (otherwise they'd fault/be lost and we go
  // blind right at the SEC->PEI->DXE handoff). Remove with the diag code.
  //
  Table[Index].PhysicalBase = 0xFED10000ULL;
  Table[Index].VirtualBase  = 0xFED10000ULL;
  Table[Index].Length       = 0x00010000ULL;   // 64 KB
  Table[Index].Attributes   = MMAP_DEV;
  Index++;

  //
  // --- MMIO device windows (skip unverified zero bases) ---
  //
  for (Region = 0; Region < MMIO_REGION_COUNT; Region++) {
    if (mMmioRegions[Region].Base == 0) {
      DEBUG ((DEBUG_WARN, "MemoryMap: skipping unverified MMIO '%a'.\n",
        mMmioRegions[Region].Name));
      continue;
    }
    Table[Index].PhysicalBase = mMmioRegions[Region].Base;
    Table[Index].VirtualBase  = mMmioRegions[Region].Base;
    Table[Index].Length       = EFI_PAGES_TO_SIZE (EFI_SIZE_TO_PAGES (mMmioRegions[Region].Size));
    Table[Index].Attributes   = MMAP_DEV;
    Index++;
  }

  //
  // --- Terminator (zero-length entry) ---
  //
  Table[Index].PhysicalBase = 0;
  Table[Index].VirtualBase  = 0;
  Table[Index].Length       = 0;
  Table[Index].Attributes   = 0;

  *MemoryTable = Table;
}
