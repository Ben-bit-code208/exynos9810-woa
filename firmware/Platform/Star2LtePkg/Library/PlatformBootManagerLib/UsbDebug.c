/** @file
  Native USB diagnostics for Star2Lte.

  Snapshot mode is deliberately read-only. It records the verified TOP/FSYS0
  clock controls before any DWC3 or PHY register is touched.
**/

#include "UsbDebug.h"

#if STAR2LTE_USB_DEBUG_MODE >= STAR2LTE_USB_DEBUG_SNAPSHOT

#include <Library/PrintLib.h>
#include <Platform/Exynos9810.h>

#define CMU_TOP_USB_MUX       0x1060u
#define CMU_TOP_USB_DIV       0x1858u
#define CMU_TOP_USB_GATE      0x2064u

#define CMU_FSYS0_USB_MUX     0x01E0u
#define CMU_FSYS0_MUX_AUTOGATE 0x0228u
#define CMU_FSYS0_USB_GATE    0x206Cu
#define CMU_FSYS0_USB_PLL_QCH 0x3000u
#define CMU_FSYS0_USB_CTRL_QCH 0x304Cu
#define CMU_FSYS0_USB_LINK_QCH 0x3050u
#define CMU_FSYS0_USB_PHY_QCH 0x3054u
#define CMU_FSYS0_USB_PCS_QCH 0x3058u

STATIC BOOLEAN  mSnapshotTaken;

STATIC
UINT32
Star2LteUsbRead32 (
  IN UINT64  Base,
  IN UINT32  Offset
  )
{
  return *(volatile UINT32 *)(UINTN)(Base + Offset);
}

VOID
Star2LteUsbDebugSnapshot (
  IN STAR2LTE_USB_DEBUG_LOG  Log
  )
{
  CHAR8   Buffer[192];
  UINT32  TopMux;
  UINT32  TopDiv;
  UINT32  TopGate;
  UINT32  LocalMux;
  UINT32  MuxAutoGate;
  UINT32  LocalGate;
  UINT32  PllQch;
  UINT32  CtrlQch;
  UINT32  LinkQch;
  UINT32  PhyQch;
  UINT32  PcsQch;
  UINT32  Flags;

  if (mSnapshotTaken || (Log == NULL)) {
    return;
  }
  mSnapshotTaken = TRUE;

  TopMux     = Star2LteUsbRead32 (EXYNOS_CMU_TOP_BASE, CMU_TOP_USB_MUX);
  TopDiv     = Star2LteUsbRead32 (EXYNOS_CMU_TOP_BASE, CMU_TOP_USB_DIV);
  TopGate    = Star2LteUsbRead32 (EXYNOS_CMU_TOP_BASE, CMU_TOP_USB_GATE);
  LocalMux   = Star2LteUsbRead32 (EXYNOS_CMU_FSYS0_BASE, CMU_FSYS0_USB_MUX);
  MuxAutoGate = Star2LteUsbRead32 (EXYNOS_CMU_FSYS0_BASE, CMU_FSYS0_MUX_AUTOGATE);
  LocalGate  = Star2LteUsbRead32 (EXYNOS_CMU_FSYS0_BASE, CMU_FSYS0_USB_GATE);
  PllQch     = Star2LteUsbRead32 (EXYNOS_CMU_FSYS0_BASE, CMU_FSYS0_USB_PLL_QCH);
  CtrlQch    = Star2LteUsbRead32 (EXYNOS_CMU_FSYS0_BASE, CMU_FSYS0_USB_CTRL_QCH);
  LinkQch    = Star2LteUsbRead32 (EXYNOS_CMU_FSYS0_BASE, CMU_FSYS0_USB_LINK_QCH);
  PhyQch     = Star2LteUsbRead32 (EXYNOS_CMU_FSYS0_BASE, CMU_FSYS0_USB_PHY_QCH);
  PcsQch     = Star2LteUsbRead32 (EXYNOS_CMU_FSYS0_BASE, CMU_FSYS0_USB_PCS_QCH);

  AsciiSPrint (
    Buffer,
    sizeof (Buffer),
    "\n=USBCLK v=01 top=%08x/%08x/%08x fsys=%08x/%08x/%08x\n",
    TopMux,
    TopDiv,
    TopGate,
    LocalMux,
    MuxAutoGate,
    LocalGate
    );
  Log (Buffer);

  AsciiSPrint (
    Buffer,
    sizeof (Buffer),
    "=USBQCH pll=%08x ctl=%08x link=%08x phy=%08x pcs=%08x\n",
    PllQch,
    CtrlQch,
    LinkQch,
    PhyQch,
    PcsQch
    );
  Log (Buffer);

  Flags = 0;
  if ((LocalMux & (1u << 4)) != 0) {
    Flags |= (1u << 0);
  }
  if ((LocalMux & (1u << 7)) == 0) {
    Flags |= (1u << 1);
  }
  if ((LocalGate & (1u << 20)) != 0) {
    Flags |= (1u << 2);
  }
  if ((LocalGate & (1u << 21)) != 0) {
    Flags |= (1u << 3);
  }
  if ((LocalGate & (1u << 28)) != 0) {
    Flags |= (1u << 4);
  }
  if (((CtrlQch | LinkQch | PhyQch) & (1u << 1)) != 0) {
    Flags |= (1u << 5);
  }

  AsciiSPrint (Buffer, sizeof (Buffer), "=USBCLKF f=%08x ro=1 core=0 phy=0\n", Flags);
  Log (Buffer);
}

#endif
