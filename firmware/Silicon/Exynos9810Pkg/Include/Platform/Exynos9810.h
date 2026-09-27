/** @file
  Exynos9810.h — central SoC register-block map for the Galaxy S9 / S9+
  (starlte / star2lte), Exynos 9810.

  ONE place for every MMIO base/size and the framebuffer geometry. Both the
  UEFI memory map (PlatformMemoryMapLib) and the platform drivers include this
  so there are no scattered magic numbers.

  VALUES SOURCED FROM mainline Linux (verified 2026-06):
    arch/arm64/boot/dts/exynos/exynos9810.dtsi        (SoC peripherals)
    arch/arm64/boot/dts/exynos/exynos9810-starlte.dts (S9 board: DRAM, fb)
    postmarketOS device-samsung-star2lte/deviceinfo   (boot geometry)

  Convention: a base of 0 means "unknown / not configured". The memory map lib
  skips zero-based device windows so a placeholder never maps PA 0 by accident.

  Lines still marked TODO-VERIFY are NOT in mainline and need on-device
  confirmation (decompile your device's DT and run tools/dt-extract.py).
**/

#ifndef EXYNOS9810_H_
#define EXYNOS9810_H_

//
// ---------------------------------------------------------------------------
// DRAM  [VERIFIED base; see note on banks]
//
// The S9 (starlte) memory map is multi-bank with a hole:
//   bank0 0x80000000 + 0x3C800000   (ends 0xBC800000)
//   <hole 0xBC800000 .. 0xC0000000 — NOT usable RAM>
//   bank1 0xC0000000 + 0x20000000
//   bank2 0xE1900000 + 0x1E700000
//   bank3 0x8_80000000 + 0x80000000 (high)
// The S9+ (star2lte, 6 GB) is not in mainline and its exact banks differ; only
// the base (0x80000000) and the framebuffer carveout are known to match.
//
// For UEFI bring-up (M2) we map ONLY bank0 — a safe subset present on both
// models, large enough to host the firmware (loaded at 0x80080000). Expand to
// the full bank list once verified on the target. Mapping across the hole as
// cached RAM would fault, so do NOT just declare one giant region.
// ---------------------------------------------------------------------------
//
#define EXYNOS_DRAM_BASE          0x0000000080000000ULL   // VERIFIED
#define EXYNOS_DRAM_SIZE          0x000000003C800000ULL   // VERIFIED bank0 (S9); conservative

//
// ---------------------------------------------------------------------------
// Debug UART (Samsung/Exynos UART — NOT 16550/PL011 compatible)
//
// VERIFIED on-device (SM-G965F live device tree, 2026-06): the stock kernel
// exposes uart@10440000 as the first PERIC0 UART. Multiple UARTs exist
// (0x10440000, 0x10450000, ...); 0x10440000 is the debug-console candidate.
// The framebuffer (below) remains the most reliable console for this SoC.
// ---------------------------------------------------------------------------
//
#define EXYNOS_UART_BASE          0x0000000010440000ULL   // VERIFIED (uart@10440000)
#define EXYNOS_UART_SIZE          0x0000000000001000ULL

//
// ---------------------------------------------------------------------------
// GICv2 interrupt controller  [VERIFIED: compatible "arm,gic-400"]
//
// gic-400 is GICv2 architecture: a memory-mapped CPU interface (GICC), NOT
// GICv3 redistributors. DT reg order: GICD, GICC, GICH, GICV.
//   GICD 0x10101000, GICC 0x10102000, GICH 0x10104000, GICV 0x10106000
// EDK2 ArmGicDxe: set PcdGicDistributorBase + PcdGicInterruptInterfaceBase.
// ACPI MADT: GICv2 GICC structures (phys base = GICC), distributor version 2,
// no GICR. Windows ARM64 supports GICv2 (Lumia 950 precedent).
// ---------------------------------------------------------------------------
//
#define EXYNOS_GICD_BASE          0x0000000010101000ULL   // VERIFIED (distributor)
#define EXYNOS_GICD_SIZE          0x0000000000001000ULL
#define EXYNOS_GICC_BASE          0x0000000010102000ULL   // VERIFIED (CPU interface)
#define EXYNOS_GICC_SIZE          0x0000000000002000ULL   // GICC alias window
#define EXYNOS_GICH_BASE          0x0000000010104000ULL   // VERIFIED (hypervisor)
#define EXYNOS_GICH_SIZE          0x0000000000002000ULL
#define EXYNOS_GICV_BASE          0x0000000010106000ULL   // VERIFIED (virtual CPU)
#define EXYNOS_GICV_SIZE          0x0000000000002000ULL

//
// ---------------------------------------------------------------------------
// Architected generic timer  [VERIFIED]
//
// CRITICAL: the stock Samsung bootloader does NOT program CNTFRQ_EL0, and the
// real counter frequency is 26 MHz. Firmware must HARDCODE this, not read the
// register (which returns garbage). Mainline DT carries the same workaround.
// Timer PPIs: secure 13, non-secure 14, virtual 11, hypervisor 10 (LEVEL_LOW)
//   -> ACPI GSIVs 29 / 30 / 27 / 26.
// ---------------------------------------------------------------------------
//
#define EXYNOS_ARCH_TIMER_FREQ_HZ 26000000u                // VERIFIED (26 MHz)

//
// ---------------------------------------------------------------------------
// Power management unit (PMU) + chip ID  [VERIFIED]
// ---------------------------------------------------------------------------
//
#define EXYNOS_PMU_BASE           0x0000000014060000ULL   // VERIFIED (pmu_system_controller)
#define EXYNOS_PMU_SIZE           0x0000000000010000ULL
#define EXYNOS_CHIPID_BASE        0x0000000010000000ULL   // VERIFIED
#define EXYNOS_CHIPID_SIZE        0x0000000000000100ULL
#define EXYNOS_SPEEDY_BASE        0x00000000141C0000ULL   // VERIFIED (speedy@141C0000)
#define EXYNOS_SPEEDY_SIZE        0x0000000000002000ULL

//
// ---------------------------------------------------------------------------
// Clock management (CMU). The TOP and FSYS0 windows are verified from the
// Exynos 9810 CAL data and the live USB device tree.
// ---------------------------------------------------------------------------
//
#define EXYNOS_CMU_TOP_BASE       0x000000001A240000ULL   // VERIFIED (CMU_TOP)
#define EXYNOS_CMU_TOP_SIZE       0x0000000000010000ULL
#define EXYNOS_CMU_FSYS0_BASE     0x0000000011000000ULL   // VERIFIED (CMU_FSYS0)
#define EXYNOS_CMU_FSYS0_SIZE     0x0000000000008000ULL

//
// ---------------------------------------------------------------------------
// UFS storage (JEDEC UFSHCI host + Samsung UFS PHY).
// Needed for M4 (Windows storage). VERIFIED on-device: ufs@0x11120000. The
// DSDT exposes ACPI\EXYN9810 for the custom read-only Storport miniport. The
// first Windows driver preserves and validates the UEFI-initialized link.
// ---------------------------------------------------------------------------
//
#define EXYNOS_UFS_HCI_BASE       0x0000000011120000ULL   // VERIFIED (ufs@0x11120000)
#define EXYNOS_UFS_HCI_SIZE       0x0000000000002000ULL
#define EXYNOS_UFS_PHY_BASE       0x0000000011130000ULL   // TODO-VERIFY (UFS PHY candidate)
#define EXYNOS_UFS_PHY_SIZE       0x0000000000001000ULL

//
// UFS sub-blocks. *** REGION IDENTITIES CORRECTED from the Samsung universal9810
// DT (ufs@0x11120000 reg list) + ufs-cal-9810 + on-device PMA-cal success. *** The
// constant NAMES below are kept (churn-avoidance) but DO NOT match their true blocks
// — an earlier /proc/iomem guess mislabeled them:
//   0x11121100 VS/vendor HCI (inside the 0x2000 HCI map above)           [name VS]     ok
//   0x11124000 PMA / analog M-PHY (0x800) — PMA cal here works on-device [name UNIPRO]
//   0x11110000 UNIPRO (DT reg #2, 0x8000) — DL/PA debug-APB timers @+0x7888 [name FMP]
//   0x11130000 UFSP/FMP protector (reg_ufsp; gates DMA, may need bypass)  [name PHY]
// *** The UNIPRO window MUST map its FULL 0x8000: the cal's UNIPRO_DBG_APB flow-control
// writes land at +0x7888 (page 7); a 0x1000 mapping synchronously data-aborts the moment
// they fire — cost a device cycle 2026-06-18 (sync exception at the cal write). ***
//
#define EXYNOS_UFS_VS_BASE        0x0000000011121100ULL   // vs_hci (correct)
#define EXYNOS_UFS_UNIPRO_BASE    0x0000000011124000ULL   // NAME=UNIPRO but is the PMA window
#define EXYNOS_UFS_UNIPRO_SIZE    0x0000000000001000ULL
#define EXYNOS_UFS_FMP_BASE       0x0000000011110000ULL   // NAME=FMP but is the UNIPRO window
#define EXYNOS_UFS_FMP_SIZE       0x0000000000008000ULL   // 0x8000: must cover UNIPRO_DBG_APB +0x7888
#define EXYNOS_UFS_IRQ_SPI        242                      // VERIFIED (UFS SPI 242)
#define EXYNOS_UFS_IRQ_GSIV       274                      // VERIFIED (SPI 242 + GIC SPI base 32)

//
// ---------------------------------------------------------------------------
// USB 3.0 DRD (DWC3 - exposes a standard xHCI register interface) + USB PHY.
// VERIFIED on-device: usb@10C00000 (with a dwc3 child). The DWC3 core register
// window is the standard xHCI MMIO that Windows' inbox USBXHCI.sys binds to via
// _CID PNP0D10. The PHY windows are verified from the live DT and /proc/iomem.
// ---------------------------------------------------------------------------
//
#define EXYNOS_USBDRD_BASE        0x0000000010C00000ULL   // VERIFIED (usb@10C00000)
#define EXYNOS_USBDRD_SIZE        0x0000000000010000ULL
#define EXYNOS_USBDRD_PHY_BASE    0x0000000011100000ULL   // VERIFIED (USB PHY/link control)
#define EXYNOS_USBDRD_PHY_SIZE    0x0000000000001000ULL
#define EXYNOS_USB_COMBO_PHY_BASE 0x00000000110A0000ULL   // VERIFIED (USB/DP combo PHY)
#define EXYNOS_USB_COMBO_PHY_SIZE 0x0000000000001000ULL
#define EXYNOS_USB_PCS_BASE       0x00000000110B0000ULL   // VERIFIED (USB/DP PCS)
#define EXYNOS_USB_PCS_SIZE       0x0000000000001000ULL

//
// ---------------------------------------------------------------------------
// Touchscreen bus + IRQ  (Samsung sec_ts touch IC over Exynos HSI2C)
//
// VERIFIED on-device: the touch node touchscreen@48 (compatible "sec,sec_ts")
// lives on hsi2c@104B0000 at 7-bit slave address 0x48. (Earlier 0x10970000 was
// a wrong guess — corrected from the live device tree.) The same 0x104B0000
// USI block can also be a UART; for touch it is in HSI2C mode.
//
// Windows uses a SpbCx controller driver for HSI2C10 and a separate SEC_TS HID
// function driver. The latter owns the ALIVE GPIO page because no Exynos GPIO
// controller driver exists to acknowledge the wakeup-EINT pending latch.
//
// VERIFIED from the Star2 DTS: sec,irq_gpio is GPA1_0 and sec,irq_type is
// 0x2008 (oneshot, level-low). Exynos9810 maps GPA1_0 to EINT8, GIC SPI 44;
// therefore the ACPI GSIV is 76.
// ---------------------------------------------------------------------------
//
#define EXYNOS_TOUCH_HSI2C_BASE   0x00000000104B0000ULL   // VERIFIED (hsi2c@104B0000)
#define EXYNOS_TOUCH_HSI2C_SIZE   0x0000000000001000ULL
#define EXYNOS_PERIC0_CMU_BASE    0x0000000010400000ULL   // VERIFIED (CMU_PERIC0)
#define EXYNOS_PERIC0_CMU_SIZE    0x0000000000004000ULL
#define EXYNOS_PERIC0_SYSREG_BASE 0x0000000010411000ULL   // VERIFIED (SYSREG_PERIC0)
#define EXYNOS_PERIC0_SYSREG_SIZE 0x0000000000001000ULL
#define EXYNOS_PERIC0_GPIO_BASE   0x0000000010430000ULL   // VERIFIED (GPIO_PERIC0)
#define EXYNOS_PERIC0_GPIO_SIZE   0x0000000000001000ULL
#define EXYNOS_ALIVE_GPIO_BASE    0x0000000014050000ULL   // VERIFIED (GPIO_ALIVE)
#define EXYNOS_ALIVE_GPIO_SIZE    0x0000000000001000ULL
#define EXYNOS_TOUCH_I2C_ADDR     0x48                     // VERIFIED (touchscreen@48, sec_ts)
#define EXYNOS_TOUCH_HSI2C_GSIV   436                      // VERIFIED (USI03_USI SPI 404 -> GSIV 436)
#define EXYNOS_TOUCH_IRQ_GSIV     76                       // VERIFIED (GPA1_0/EINT8 SPI 44 -> GSIV 76, level-low)

//
// ---------------------------------------------------------------------------
// Display: DECON (display controller) + DSIM (MIPI DSI master).
// VERIFIED on-device: decon_f@0x16030000 (main DECON), disp_ss@0x16010000,
// dsim@0x16080000. We do NOT re-init these for M3 (we reuse the sboot-lit
// framebuffer); these are only for a future native display driver.
// ---------------------------------------------------------------------------
//
#define EXYNOS_DECON_BASE         0x0000000016030000ULL   // VERIFIED (decon_f@0x16030000)
#define EXYNOS_DECON_SIZE         0x0000000000010000ULL
#define EXYNOS_DSIM_BASE          0x0000000016080000ULL   // VERIFIED (dsim@0x16080000)
#define EXYNOS_DSIM_SIZE          0x0000000000010000ULL

//
// ---------------------------------------------------------------------------
// Watchdog (Samsung s3c2410-style). VERIFIED on-device: watchdog_cl0@10050000
// (cluster 0), watchdog_cl1@10060000 (cluster 1). Not needed for M2.
// ---------------------------------------------------------------------------
//
#define EXYNOS_WDT_BASE           0x0000000010050000ULL   // VERIFIED (watchdog_cl0@10050000)
#define EXYNOS_WDT_SIZE           0x0000000000001000ULL

//
// ---------------------------------------------------------------------------
// Framebuffer  [VERIFIED from starlte chosen/simple-framebuffer + reserved-memory]
//
// sboot lights the panel and scans out from this carveout (no-map reserved).
// Format a8r8g8b8 => in little-endian memory the bytes are B,G,R,A, i.e.
// BGRA8888 => UEFI PixelBlueGreenRedReserved8BitPerColor. Stride = width*4.
// The S9 and S9+ share this 1440x2960 panel geometry and fb base.
// ---------------------------------------------------------------------------
//
#define EXYNOS_FB_BASE            0x00000000CC000000ULL   // VERIFIED (0xCC000000)
#define EXYNOS_FB_WIDTH           1440                     // VERIFIED
#define EXYNOS_FB_HEIGHT          2960                     // VERIFIED
#define EXYNOS_FB_BYTES_PER_PIXEL 4
#define EXYNOS_FB_STRIDE_PIXELS   EXYNOS_FB_WIDTH          // VERIFIED (width*4)
#define EXYNOS_FB_SIZE            ((UINT64)EXYNOS_FB_STRIDE_PIXELS * \
                                   EXYNOS_FB_HEIGHT * EXYNOS_FB_BYTES_PER_PIXEL)

#endif // EXYNOS9810_H_
