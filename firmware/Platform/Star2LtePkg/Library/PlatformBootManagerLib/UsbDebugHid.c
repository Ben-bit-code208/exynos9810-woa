/** @file
  Polling-only DWC3 HID transport for the Star2Lte diagnostic profile.

  The transport owns the controller only while boot services are active.  It
  uses a USB2 vendor HID input report so Windows can consume firmware logs
  without a custom kernel driver.
**/

#include <Uefi.h>

#include <IndustryStandard/Usb.h>
#include <Library/BaseLib.h>
#include <Library/BaseMemoryLib.h>
#include <Library/CacheMaintenanceLib.h>
#include <Library/IoLib.h>
#include <Library/PrintLib.h>
#include <Library/TimerLib.h>
#include <Library/UefiBootServicesTableLib.h>
#include <Platform/Exynos9810.h>

#include "UsbDebug.h"

#if STAR2LTE_USB_DEBUG_MODE == STAR2LTE_USB_DEBUG_HID

#define USB_TOP_MUX                    (EXYNOS_CMU_TOP_BASE + 0x1060u)
#define USB_TOP_DIV                    (EXYNOS_CMU_TOP_BASE + 0x1858u)
#define USB_TOP_GATE                   (EXYNOS_CMU_TOP_BASE + 0x2064u)
#define USB_FSYS_MUX                   (EXYNOS_CMU_FSYS0_BASE + 0x01E0u)
#define USB_FSYS_MUX_AUTOGATE          (EXYNOS_CMU_FSYS0_BASE + 0x0228u)
#define USB_FSYS_GATE                  (EXYNOS_CMU_FSYS0_BASE + 0x206Cu)
#define USB_FSYS_QCH_CMU               (EXYNOS_CMU_FSYS0_BASE + 0x3000u)
#define USB_FSYS_QCH_CTRL              (EXYNOS_CMU_FSYS0_BASE + 0x304Cu)
#define USB_FSYS_QCH_LINK              (EXYNOS_CMU_FSYS0_BASE + 0x3050u)
#define USB_FSYS_QCH_PHY               (EXYNOS_CMU_FSYS0_BASE + 0x3054u)
#define USB_FSYS_QCH_PCS               (EXYNOS_CMU_FSYS0_BASE + 0x3058u)

#define USB_CLK_AUTOGATE               BIT28
#define USB_CLK_GATE_MANUAL             BIT20
#define USB_CLK_GATE_VALUE              BIT21
#define USB_TOP_MUX_SELECT_MASK         0x3u
#define USB_TOP_MUX_SELECT_PLL2_DIV2    0x2u
#define USB_TOP_MUX_BUSY                BIT16
#define USB_TOP_DIV_RATIO_MASK          0xFu
#define USB_TOP_DIV_RATIO_NORMAL        0x7u
#define USB_TOP_DIV_BUSY                BIT16
#define USB_FSYS_MUX_SELECT             BIT4
#define USB_FSYS_MUX_BUSY               BIT7
#define USB_QCH_ENABLE                  BIT0
#define USB_QCH_REQUEST                 BIT1
#define USB_QCH_IGNORE_FORCE_PM         BIT2

#define USB_CLK_ERROR_TOP_MUX           BIT0
#define USB_CLK_ERROR_TOP_DIV           BIT1
#define USB_CLK_ERROR_TOP_GATE          BIT2
#define USB_CLK_ERROR_FSYS_MUX          BIT3
#define USB_CLK_ERROR_FSYS_AUTOGATE     BIT4
#define USB_CLK_ERROR_FSYS_GATE         BIT5
#define USB_CLK_ERROR_QCH_SHIFT         8u

#define USB_PHY_LINK_CTRL               0x04u
#define USB_PHY_CLKRST                  0x20u
#define USB_PHY_PWR                     0x24u
#define USB_PHY_COMBO_PMA_CTRL          0x48u
#define USB_PHY_UTMI                    0x50u
#define USB_PHY_HSP                     0x54u
#define USB_PHY_HSP_TEST                0x5Cu

#define USB_PHY_LINK_PIPE_RX_IDLE       BIT18
#define USB_PHY_LINK_PIPE_STATUS        BIT17
#define USB_PHY_LINK_PIPE_FORCE         BIT16
#define USB_PHY_LINK_DIS_QACT_LINK      BIT12
#define USB_PHY_LINK_DIS_QACT_ID0       BIT11
#define USB_PHY_LINK_DIS_QACT_VBUS      BIT10
#define USB_PHY_LINK_DIS_QACT_BVALID    BIT9
#define USB_PHY_LINK_FORCE_QACT         BIT8
#define USB_PHY_LINK_FILTER_MASK        (0xFu << 4)
#define USB_PHY_CLKRST_PHY_RESET        BIT3
#define USB_PHY_CLKRST_PHY_SELECT       BIT2
#define USB_PHY_CLKRST_PORT_RESET       BIT1
#define USB_PHY_CLKRST_LINK_RESET       BIT0
#define USB_PHY_PMA_LOW_POWER_N         BIT4
#define USB_PHY_UTMI_VBUS_VALID         BIT5
#define USB_PHY_UTMI_BVALID             BIT4
#define USB_PHY_UTMI_DP_PULLDOWN        BIT3
#define USB_PHY_UTMI_DM_PULLDOWN        BIT2
#define USB_PHY_UTMI_SUSPEND            BIT1
#define USB_PHY_UTMI_SLEEP              BIT0
#define USB_PHY_HSP_VBUS_EXT_SELECT     BIT13
#define USB_PHY_HSP_VBUS_EXT            BIT12
#define USB_PHY_HSP_UTMI_SUSPEND        BIT9
#define USB_PHY_HSP_COMMON_ON           BIT8
#define USB_PHY_HSP_TEST_SIDDQ          BIT24

#define DWC3_GSBUSCFG0                  0xC100u
#define DWC3_GSBUSCFG1                  0xC104u
#define DWC3_GCTL                       0xC110u
#define DWC3_GSNPSID                    0xC120u
#define DWC3_GUCTL                      0xC12Cu
#define DWC3_GUSB2PHYCFG0               0xC200u
#define DWC3_GUSB3PIPECTL0              0xC2C0u
#define DWC3_GEVNTADRLO0                0xC400u
#define DWC3_GEVNTADRHI0                0xC404u
#define DWC3_GEVNTSIZ0                  0xC408u
#define DWC3_GEVNTCOUNT0                0xC40Cu
#define DWC3_DCFG                       0xC700u
#define DWC3_DCTL                       0xC704u
#define DWC3_DEVTEN                     0xC708u
#define DWC3_DSTS                       0xC70Cu
#define DWC3_DALEPENA                   0xC720u
#define DWC3_DEP_BASE(Ep)               (0xC800u + ((Ep) * 0x10u))
#define DWC3_DEPCMDPAR2                 0x00u
#define DWC3_DEPCMDPAR1                 0x04u
#define DWC3_DEPCMDPAR0                 0x08u
#define DWC3_DEPCMD                     0x0Cu

#define DWC3_GCTL_PRTCAP_MASK           (0x3u << 12)
#define DWC3_GCTL_PRTCAP_DEVICE         (0x2u << 12)
#define DWC3_GCTL_CORE_SOFT_RESET       BIT11
#define DWC3_GCTL_DSBLCLKGTNG           BIT0
#define DWC3_GCTL_SOFITPSYNC             BIT10
#define DWC3_GCTL_RAMCLKSEL_MASK        (0x3u << 6)
#define DWC3_GSBUSCFG0_INCRBRSTEN       BIT0
#define DWC3_GSBUSCFG0_INCR16BRSTEN     BIT3
#define DWC3_GSBUSCFG0_DESWRREQINFO     (2u << 16)
#define DWC3_GSBUSCFG0_DATWRREQINFO     (2u << 20)
#define DWC3_GSBUSCFG0_DESRDREQINFO     (2u << 24)
#define DWC3_GSBUSCFG0_DATRDREQINFO     (2u << 28)
#define DWC3_GSBUSCFG1_BREQLIMIT_MASK   (0xFu << 8)
#define DWC3_GSBUSCFG1_BREQLIMIT(Value) (((Value) & 0xFu) << 8)
#define DWC3_GUCTL_REFCLKPER_MASK       (0x3FFu << 22)
#define DWC3_GUCTL_REFCLKPER(Value)      (((Value) & 0x3FFu) << 22)
#define DWC3_GUCTL_USBHSTINAUTORETRYEN  BIT14
#define DWC3_GUCTL_SPRSCTRLTRANSEN      BIT17
#define DWC3_PHY_SOFT_RESET             BIT31
#define DWC3_GUSB2PHYCFG_U2_FREECLK     BIT30
#define DWC3_GUSB2PHYCFG_SUSPHY         BIT6
#define DWC3_GUSB2PHYCFG_ENBLSLPM       BIT8
#define DWC3_GUSB3PIPECTL_DISRXDETINP3  BIT28
#define DWC3_GUSB3PIPECTL_U1U2EXITFAIL  BIT25
#define DWC3_GUSB3PIPECTL_SUSPHY        BIT17
#define DWC3_EVENT_SIZE_MASK            0xFFFFu
#define DWC3_EVENT_INT_MASK             BIT31
#define DWC3_DCFG_SPEED_MASK            0x7u
#define DWC3_DCFG_HIGH_SPEED            0x0u
#define DWC3_DCFG_ADDRESS_MASK          (0x7Fu << 3)
#define DWC3_DCFG_ADDRESS(Value)        ((Value) << 3)
#define DWC3_DCTL_RUN_STOP              BIT31
#define DWC3_DCTL_CORE_SOFT_RESET       BIT30
#define DWC3_DSTS_CONTROLLER_HALTED     BIT22
#define DWC3_DSTS_SPEED_MASK            0x7u
#define DWC3_DEVTEN_CONNECT_DONE        BIT2
#define DWC3_DEVTEN_USB_RESET           BIT1
#define DWC3_DEVTEN_DISCONNECT          BIT0
#define DWC3_DALEPENA_EP(Ep)            (1u << (Ep))

#define DWC3_DEPCMD_PARAM(Value)        ((Value) << 16)
#define DWC3_DEPCMD_STATUS(Value)       (((Value) >> 12) & 0xFu)
#define DWC3_DEPCMD_RESOURCE(Value)     (((Value) >> 16) & 0x7Fu)
#define DWC3_DEPCMD_ACTIVE              BIT10
#define DWC3_DEPCMD_FORCE_REMOVE        BIT11
#define DWC3_DEPCMD_SET_CONFIG          0x01u
#define DWC3_DEPCMD_SET_RESOURCE        0x02u
#define DWC3_DEPCMD_CLEAR_STALL         0x05u
#define DWC3_DEPCMD_SET_STALL           0x04u
#define DWC3_DEPCMD_START_TRANSFER      0x06u
#define DWC3_DEPCMD_END_TRANSFER        0x08u
#define DWC3_DEPCMD_START_CONFIG        0x09u

#define DWC3_DEPCFG_XFER_COMPLETE       BIT8
#define DWC3_DEPCFG_XFER_PROGRESS       BIT9
#define DWC3_DEPCFG_XFER_NOT_READY      BIT10
#define DWC3_DEPCFG_INTERVAL(Value)     (((Value) & 0xFFu) << 16)
#define DWC3_DEPCFG_EP_NUMBER(Value)    (((Value) & 0x1Fu) << 25)
#define DWC3_DEPCFG_EP_TYPE(Value)      (((Value) & 0x3u) << 1)
#define DWC3_DEPCFG_MAX_PACKET(Value)   (((Value) & 0x7FFu) << 3)
#define DWC3_DEPCFG_FIFO(Value)         (((Value) & 0x1Fu) << 17)
#define DWC3_DEPXFERCFG_RESOURCES(Value) ((Value) & 0xFFFFu)

#define DWC3_TRB_HWO                    BIT0
#define DWC3_TRB_LAST                   BIT1
#define DWC3_TRB_INTERRUPT_SHORT        BIT10
#define DWC3_TRB_INTERRUPT_COMPLETE     BIT11
#define DWC3_TRB_TYPE(Value)            (((Value) & 0x3Fu) << 4)
#define DWC3_TRB_NORMAL                 DWC3_TRB_TYPE (1)
#define DWC3_TRB_CONTROL_SETUP          DWC3_TRB_TYPE (2)
#define DWC3_TRB_CONTROL_STATUS2        DWC3_TRB_TYPE (3)
#define DWC3_TRB_CONTROL_STATUS3        DWC3_TRB_TYPE (4)
#define DWC3_TRB_CONTROL_DATA           DWC3_TRB_TYPE (5)

#define DWC3_EVENT_ENDPOINT             0u
#define DWC3_EVENT_DEVICE               1u
#define DWC3_ENDPOINT_XFER_COMPLETE     1u
#define DWC3_ENDPOINT_XFER_PROGRESS     2u
#define DWC3_DEVICE_DISCONNECT          0u
#define DWC3_DEVICE_USB_RESET           1u
#define DWC3_DEVICE_CONNECT_DONE        2u

#define USB_EP0_OUT                     0u
#define USB_EP0_IN                      1u
#define USB_HID_EP_IN                   3u
#define USB_DWC3_ENDPOINT_COUNT         32u
#define USB_EP0_MAX_PACKET              64u
#define USB_HID_REPORT_SIZE             64u
#define USB_EVENT_BUFFER_SIZE           EFI_PAGE_SIZE
#define USB_LOG_RING_SIZE               4096u
#define USB_EVENT_BUDGET                64u
#define USB_COMMAND_TIMEOUT_US          1000u
#define USB_RUN_TIMEOUT_US              100000u
#define USB_STOP_TIMEOUT_US             5000u
#define USB_HEARTBEAT_POLLS             500u
#define USB_EVENT_LOG_BUDGET            32u

#define USB_DMA_SETUP_OFFSET            0x000u
#define USB_DMA_EP0_TRB_OFFSET          0x040u
#define USB_DMA_CONTROL_OFFSET          0x080u
#define USB_DMA_CONTROL_SIZE            0x200u
#define USB_DMA_HID_TRB_OFFSET          0x300u
#define USB_DMA_HID_REPORT_OFFSET       0x340u

#define USB_DESC_TYPE_DEVICE_QUALIFIER  0x06u
#define USB_DESC_TYPE_OTHER_SPEED       0x07u

#define USB_HID_GET_REPORT              0x01u
#define USB_HID_GET_IDLE                0x02u
#define USB_HID_GET_PROTOCOL            0x03u
#define USB_HID_SET_IDLE                0x0Au
#define USB_HID_SET_PROTOCOL            0x0Bu

typedef struct {
  UINT32  BufferLow;
  UINT32  BufferHigh;
  UINT32  Size;
  UINT32  Control;
} USB_DWC3_TRB;

typedef struct {
  UINTN   Address;
  UINT32  Value;
} USB_SAVED_REGISTER;

typedef enum {
  UsbEp0Setup,
  UsbEp0Data,
  UsbEp0Status
} USB_EP0_STATE;

STATIC CONST UINTN  mQchAddresses[] = {
  USB_FSYS_QCH_CMU,
  USB_FSYS_QCH_CTRL,
  USB_FSYS_QCH_LINK,
  USB_FSYS_QCH_PHY,
  USB_FSYS_QCH_PCS
};

STATIC CONST UINTN  mPhyOffsets[] = {
  USB_PHY_LINK_CTRL,
  USB_PHY_CLKRST,
  USB_PHY_PWR,
  USB_PHY_COMBO_PMA_CTRL,
  USB_PHY_UTMI,
  USB_PHY_HSP,
  USB_PHY_HSP_TEST
};

STATIC USB_SAVED_REGISTER  mPhyState[ARRAY_SIZE (mPhyOffsets)];

STATIC CONST UINT8  mDeviceDescriptor[] = {
  18, USB_DESC_TYPE_DEVICE,
  0x00, 0x02,
  0x00, 0x00, 0x00,
  USB_EP0_MAX_PACKET,
  0xE8, 0x04,
  0x61, 0x68,
  0x00, 0x01,
  1, 2, 3,
  1
};

STATIC CONST UINT8  mConfigurationDescriptor[] = {
  9, USB_DESC_TYPE_CONFIG,
  34, 0,
  1, 1, 0,
  0x80, 50,
  9, USB_DESC_TYPE_INTERFACE,
  0, 0, 1,
  0x03, 0x00, 0x00,
  0,
  9, USB_DESC_TYPE_HID,
  0x11, 0x01,
  0x00,
  1,
  USB_DESC_TYPE_REPORT,
  21, 0,
  7, USB_DESC_TYPE_ENDPOINT,
  0x81,
  USB_ENDPOINT_INTERRUPT,
  USB_HID_REPORT_SIZE, 0,
  1
};

STATIC CONST UINT8  mHidDescriptor[] = {
  9, USB_DESC_TYPE_HID,
  0x11, 0x01,
  0x00,
  1,
  USB_DESC_TYPE_REPORT,
  21, 0
};

STATIC CONST UINT8  mReportDescriptor[] = {
  0x06, 0x00, 0xFF,
  0x09, 0x01,
  0xA1, 0x01,
  0x15, 0x00,
  0x26, 0xFF, 0x00,
  0x75, 0x08,
  0x95, USB_HID_REPORT_SIZE,
  0x09, 0x01,
  0x81, 0x02,
  0xC0
};

STATIC CONST UINT8  mDeviceQualifierDescriptor[] = {
  10, USB_DESC_TYPE_DEVICE_QUALIFIER,
  0x00, 0x02,
  0x00, 0x00, 0x00,
  USB_EP0_MAX_PACKET,
  1,
  0
};

STATIC_ASSERT (
  sizeof (mConfigurationDescriptor) == 34,
  "USB HID configuration descriptor size mismatch"
  );
STATIC_ASSERT (
  sizeof (mReportDescriptor) == 21,
  "USB HID report descriptor size mismatch"
  );

STATIC VOID                    *mEventPage;
STATIC VOID                    *mTransferPage;
STATIC UINT8                   *mSetupBuffer;
STATIC USB_DWC3_TRB            *mEp0Trb;
STATIC UINT8                   *mControlBuffer;
STATIC USB_DWC3_TRB            *mHidTrb;
STATIC UINT8                   *mHidReport;
STATIC EFI_EVENT               mPollEvent;
STATIC STAR2LTE_USB_DEBUG_LOG  mLog;
STATIC volatile BOOLEAN        mActive;
STATIC volatile BOOLEAN        mPolling;
STATIC BOOLEAN                 mPhySaved;
STATIC BOOLEAN                 mControllerTouched;
STATIC BOOLEAN                 mConfigured;
STATIC BOOLEAN                 mHidBusy;
STATIC BOOLEAN                 mEp0Busy[2];
STATIC UINT8                   mHidResource;
STATIC UINT8                   mAddress;
STATIC UINT8                   mPendingAddress;
STATIC BOOLEAN                 mPendingAddressValid;
STATIC UINT8                   mPendingConfiguration;
STATIC BOOLEAN                 mPendingConfigurationValid;
STATIC BOOLEAN                 mDataWasIn;
STATIC UINT8                   mLinkSpeed;
STATIC USB_EP0_STATE           mEp0State;
STATIC UINT32                  mEventPosition;
STATIC UINT32                  mEventLogBudget;
STATIC UINT32                  mSequence;
STATIC UINT32                  mHeartbeatPolls;
STATIC volatile UINT32         mLogHead;
STATIC volatile UINT32         mLogTail;
STATIC volatile UINT32         mLogDrops;
STATIC UINT8                   mLogRing[USB_LOG_RING_SIZE];

STATIC
UINT32
UsbDwcRead (
  IN UINTN  Offset
  )
{
  return MmioRead32 (EXYNOS_USBDRD_BASE + Offset);
}

STATIC
VOID
UsbDwcWrite (
  IN UINTN   Offset,
  IN UINT32  Value
  )
{
  MmioWrite32 (EXYNOS_USBDRD_BASE + Offset, Value);
}

STATIC
BOOLEAN
UsbWaitClear (
  IN UINTN   Address,
  IN UINT32  Mask,
  IN UINTN   TimeoutUs
  )
{
  while (TimeoutUs-- != 0u) {
    if ((MmioRead32 (Address) & Mask) == 0u) {
      return TRUE;
    }

    MicroSecondDelay (1);
  }

  return FALSE;
}

STATIC
BOOLEAN
UsbWaitSet (
  IN UINTN   Address,
  IN UINT32  Mask,
  IN UINTN   TimeoutUs
  )
{
  while (TimeoutUs-- != 0u) {
    if ((MmioRead32 (Address) & Mask) == Mask) {
      return TRUE;
    }

    MicroSecondDelay (1);
  }

  return FALSE;
}

STATIC
VOID
UsbLogValue (
  IN CONST CHAR8  *Name,
  IN UINT32       Value
  )
{
  CHAR8  Message[64];

  if (mLog == NULL) {
    return;
  }

  AsciiSPrint (Message, sizeof (Message), "\n=%a %08x\n", Name, Value);
  mLog (Message);
}

STATIC
VOID
UsbLogControllerState (
  VOID
  )
{
  UsbLogValue ("USBGBS0", UsbDwcRead (DWC3_GSBUSCFG0));
  UsbLogValue ("USBGBS1", UsbDwcRead (DWC3_GSBUSCFG1));
  UsbLogValue ("USBGCTL", UsbDwcRead (DWC3_GCTL));
  UsbLogValue ("USBGUCT", UsbDwcRead (DWC3_GUCTL));
  UsbLogValue ("USBGU2P", UsbDwcRead (DWC3_GUSB2PHYCFG0));
  UsbLogValue ("USBGU3P", UsbDwcRead (DWC3_GUSB3PIPECTL0));
  UsbLogValue ("USBDCTL", UsbDwcRead (DWC3_DCTL));
  UsbLogValue ("USBDSTS", UsbDwcRead (DWC3_DSTS));
  UsbLogValue (
    "USBCLKR",
    MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_CLKRST)
    );
  UsbLogValue (
    "USBUTMI",
    MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_UTMI)
    );
  UsbLogValue (
    "USBHSPR",
    MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_HSP)
    );
  UsbLogValue (
    "USBLINK",
    MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_LINK_CTRL)
    );
}

STATIC
EFI_STATUS
UsbAllocateDmaPage (
  OUT VOID  **Page
  )
{
  EFI_PHYSICAL_ADDRESS  Address;
  EFI_STATUS            Status;

  Address = MAX_UINT32;
  Status  = gBS->AllocatePages (
                   AllocateMaxAddress,
                   EfiBootServicesData,
                   1,
                   &Address
                   );
  if (EFI_ERROR (Status)) {
    *Page = NULL;
    return Status;
  }

  *Page = (VOID *)(UINTN)Address;
  return EFI_SUCCESS;
}

STATIC
VOID
UsbSaveRegisters (
  OUT USB_SAVED_REGISTER  *State,
  IN  CONST UINTN         *Addresses,
  IN  UINTN               Count
  )
{
  UINTN  Index;

  for (Index = 0; Index < Count; Index++) {
    State[Index].Address = Addresses[Index];
    State[Index].Value   = MmioRead32 (Addresses[Index]);
  }
}

STATIC
VOID
UsbRestoreRegisters (
  IN USB_SAVED_REGISTER  *State,
  IN UINTN               Count
  )
{
  while (Count-- != 0u) {
    MmioWrite32 (State[Count].Address, State[Count].Value);
  }

  MemoryFence ();
}

STATIC
UINT32
UsbValidateClocks (
  VOID
  )
{
  UINT32  Errors;
  UINT32  Value;
  UINTN   Index;

  Errors = 0;

  Value = MmioRead32 (USB_TOP_MUX);
  if ((Value & (USB_TOP_MUX_SELECT_MASK |
                USB_TOP_MUX_BUSY |
                USB_CLK_AUTOGATE)) != USB_TOP_MUX_SELECT_PLL2_DIV2) {
    Errors |= USB_CLK_ERROR_TOP_MUX;
  }

  Value = MmioRead32 (USB_TOP_DIV);
  if ((Value & (USB_TOP_DIV_RATIO_MASK |
                USB_TOP_DIV_BUSY |
                USB_CLK_AUTOGATE)) != USB_TOP_DIV_RATIO_NORMAL) {
    Errors |= USB_CLK_ERROR_TOP_DIV;
  }

  Value = MmioRead32 (USB_TOP_GATE);
  if ((Value & (USB_CLK_GATE_MANUAL |
                USB_CLK_GATE_VALUE |
                USB_CLK_AUTOGATE)) != USB_CLK_GATE_VALUE) {
    Errors |= USB_CLK_ERROR_TOP_GATE;
  }

  Value = MmioRead32 (USB_FSYS_MUX_AUTOGATE);
  if ((Value & USB_CLK_AUTOGATE) != 0u) {
    Errors |= USB_CLK_ERROR_FSYS_AUTOGATE;
  }

  Value = MmioRead32 (USB_FSYS_MUX);
  if ((Value & (USB_FSYS_MUX_SELECT |
                USB_FSYS_MUX_BUSY)) != USB_FSYS_MUX_SELECT) {
    Errors |= USB_CLK_ERROR_FSYS_MUX;
  }

  Value = MmioRead32 (USB_FSYS_GATE);
  if ((Value & (USB_CLK_GATE_MANUAL |
                USB_CLK_GATE_VALUE |
                USB_CLK_AUTOGATE)) != USB_CLK_GATE_VALUE) {
    Errors |= USB_CLK_ERROR_FSYS_GATE;
  }

  for (Index = 0; Index < ARRAY_SIZE (mQchAddresses); Index++) {
    Value = MmioRead32 (mQchAddresses[Index]);
    if ((Value & (USB_QCH_ENABLE |
                  USB_QCH_REQUEST |
                  USB_QCH_IGNORE_FORCE_PM)) != USB_QCH_REQUEST) {
      Errors |= (UINT32)(1u << (USB_CLK_ERROR_QCH_SHIFT + Index));
    }
  }

  return Errors;
}

STATIC
VOID
UsbEnablePhy (
  VOID
  )
{
  UINTN   Addresses[ARRAY_SIZE (mPhyOffsets)];
  UINT32  Value;
  UINTN   Index;

  for (Index = 0; Index < ARRAY_SIZE (mPhyOffsets); Index++) {
    Addresses[Index] = EXYNOS_USBDRD_PHY_BASE + mPhyOffsets[Index];
  }

  UsbSaveRegisters (mPhyState, Addresses, ARRAY_SIZE (Addresses));
  mPhySaved = TRUE;

  Value  = MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_LINK_CTRL);
  Value |= USB_PHY_LINK_DIS_QACT_ID0 |
           USB_PHY_LINK_DIS_QACT_VBUS |
           USB_PHY_LINK_DIS_QACT_BVALID |
           USB_PHY_LINK_DIS_QACT_LINK;
  Value &= ~USB_PHY_LINK_FORCE_QACT;
  MicroSecondDelay (500);
  MmioWrite32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_LINK_CTRL, Value);
  MicroSecondDelay (500);
  Value = MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_LINK_CTRL);
  Value |= USB_PHY_LINK_FORCE_QACT;
  MicroSecondDelay (500);
  MmioWrite32 (
    EXYNOS_USBDRD_PHY_BASE + USB_PHY_LINK_CTRL,
    Value
    );

  Value = MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_CLKRST);
  MmioWrite32 (
    EXYNOS_USBDRD_PHY_BASE + USB_PHY_CLKRST,
    Value | USB_PHY_CLKRST_LINK_RESET
    );
  MicroSecondDelay (10);
  MmioWrite32 (
    EXYNOS_USBDRD_PHY_BASE + USB_PHY_CLKRST,
    Value & ~USB_PHY_CLKRST_LINK_RESET
    );

  Value = MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_CLKRST);
  Value |= USB_PHY_CLKRST_PHY_RESET | USB_PHY_CLKRST_PHY_SELECT;
  MmioWrite32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_CLKRST, Value);

  Value = MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_HSP_TEST);
  MmioWrite32 (
    EXYNOS_USBDRD_PHY_BASE + USB_PHY_HSP_TEST,
    Value & ~USB_PHY_HSP_TEST_SIDDQ
    );

  Value  = MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_UTMI);
  Value &= ~(USB_PHY_UTMI_SUSPEND |
             USB_PHY_UTMI_SLEEP |
             USB_PHY_UTMI_DP_PULLDOWN |
             USB_PHY_UTMI_DM_PULLDOWN);
  MmioWrite32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_UTMI, Value);

  Value = MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_HSP);
  Value |= USB_PHY_HSP_UTMI_SUSPEND | USB_PHY_HSP_COMMON_ON;
  MmioWrite32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_HSP, Value);

  MicroSecondDelay (100);
  Value  = MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_CLKRST);
  Value |= USB_PHY_CLKRST_PHY_SELECT;
  Value &= ~(USB_PHY_CLKRST_PHY_RESET | USB_PHY_CLKRST_PORT_RESET);
  MmioWrite32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_CLKRST, Value);

  Value = MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_LINK_CTRL);
  Value |= USB_PHY_LINK_FILTER_MASK |
           USB_PHY_LINK_PIPE_FORCE |
           USB_PHY_LINK_PIPE_RX_IDLE;
  Value &= ~USB_PHY_LINK_PIPE_STATUS;
  MmioWrite32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_LINK_CTRL, Value);

  Value = MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_COMBO_PMA_CTRL);
  MmioWrite32 (
    EXYNOS_USBDRD_PHY_BASE + USB_PHY_COMBO_PMA_CTRL,
    Value | USB_PHY_PMA_LOW_POWER_N
    );

  Value = MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_UTMI);
  MmioWrite32 (
    EXYNOS_USBDRD_PHY_BASE + USB_PHY_UTMI,
    Value | USB_PHY_UTMI_BVALID | USB_PHY_UTMI_VBUS_VALID
    );

  Value = MmioRead32 (EXYNOS_USBDRD_PHY_BASE + USB_PHY_HSP);
  MmioWrite32 (
    EXYNOS_USBDRD_PHY_BASE + USB_PHY_HSP,
    Value | USB_PHY_HSP_VBUS_EXT_SELECT | USB_PHY_HSP_VBUS_EXT
    );

  MemoryFence ();
}

STATIC
BOOLEAN
UsbEpCommand (
  IN  UINTN   Endpoint,
  IN  UINT32  Command,
  IN  UINT32  Parameter0,
  IN  UINT32  Parameter1,
  IN  UINT32  Parameter2,
  OUT UINT8   *Resource OPTIONAL
  )
{
  UINTN   Base;
  UINT32  Value;
  UINTN   Timeout;

  Base = DWC3_DEP_BASE (Endpoint);
  UsbDwcWrite (Base + DWC3_DEPCMDPAR0, Parameter0);
  UsbDwcWrite (Base + DWC3_DEPCMDPAR1, Parameter1);
  UsbDwcWrite (Base + DWC3_DEPCMDPAR2, Parameter2);
  UsbDwcWrite (Base + DWC3_DEPCMD, Command | DWC3_DEPCMD_ACTIVE);

  for (Timeout = 0; Timeout < USB_COMMAND_TIMEOUT_US; Timeout++) {
    Value = UsbDwcRead (Base + DWC3_DEPCMD);
    if ((Value & DWC3_DEPCMD_ACTIVE) == 0u) {
      if (Resource != NULL) {
        *Resource = (UINT8)DWC3_DEPCMD_RESOURCE (Value);
      }

      if (DWC3_DEPCMD_STATUS (Value) != 0u) {
        UsbLogValue (
          "USBDEPC",
          (((UINT32)Endpoint & 0x7Fu) << 24) |
          ((Command & 0xFFu) << 16) |
          (Value & 0xFFFFu)
          );
        return FALSE;
      }

      return TRUE;
    }

    MicroSecondDelay (1);
  }

  UsbLogValue (
    "USBDEPC",
    BIT31 |
    (((UINT32)Endpoint & 0x7Fu) << 24) |
    ((Command & 0xFFu) << 16) |
    (Value & 0xFFFFu)
    );
  return FALSE;
}

STATIC
VOID
UsbPrepareTrb (
  OUT USB_DWC3_TRB  *Trb,
  IN  VOID          *Buffer,
  IN  UINT32        Length,
  IN  UINT32        Type
  )
{
  UINT64  Address;

  Address         = (UINT64)(UINTN)Buffer;
  Trb->BufferLow  = (UINT32)Address;
  Trb->BufferHigh = (UINT32)(Address >> 32);
  Trb->Size       = Length;
  Trb->Control    = Type |
                    DWC3_TRB_HWO |
                    DWC3_TRB_LAST |
                    DWC3_TRB_INTERRUPT_SHORT |
                    DWC3_TRB_INTERRUPT_COMPLETE;
  WriteBackDataCacheRange (Trb, sizeof (*Trb));
}

STATIC
BOOLEAN
UsbStartTransfer (
  IN  UINTN         Endpoint,
  IN  USB_DWC3_TRB  *Trb,
  OUT UINT8         *Resource OPTIONAL
  )
{
  UINT64  Address;

  Address = (UINT64)(UINTN)Trb;
  return UsbEpCommand (
           Endpoint,
           DWC3_DEPCMD_START_TRANSFER,
           (UINT32)(Address >> 32),
           (UINT32)Address,
           0,
           Resource
           );
}

STATIC
BOOLEAN
UsbConfigureEndpoint (
  IN UINTN   Endpoint,
  IN UINT32  Type,
  IN UINT32  MaxPacket,
  IN UINT32  Interval
  )
{
  UINT32  Parameter0;
  UINT32  Parameter1;

  Parameter0 = DWC3_DEPCFG_EP_TYPE (Type) |
               DWC3_DEPCFG_MAX_PACKET (MaxPacket);
  if ((Endpoint & 1u) != 0u) {
    Parameter0 |= DWC3_DEPCFG_FIFO (Endpoint >> 1);
  }

  Parameter1 = DWC3_DEPCFG_EP_NUMBER (Endpoint) |
               DWC3_DEPCFG_XFER_COMPLETE;
  if (Endpoint <= USB_EP0_IN) {
    Parameter1 |= DWC3_DEPCFG_XFER_NOT_READY;
  }

  if (Interval != 0u) {
    Parameter1 |= DWC3_DEPCFG_INTERVAL (Interval - 1u);
  }

  return UsbEpCommand (
           Endpoint,
           DWC3_DEPCMD_SET_CONFIG,
           Parameter0,
           Parameter1,
           0,
           NULL
           );
}

STATIC
BOOLEAN
UsbInitializeEndpoints (
  VOID
  )
{
  UINTN  Endpoint;

  if (!UsbEpCommand (
         USB_EP0_OUT,
         DWC3_DEPCMD_START_CONFIG,
         0,
         0,
         0,
         NULL
         )) {
    return FALSE;
  }

  for (Endpoint = 0; Endpoint < USB_DWC3_ENDPOINT_COUNT; Endpoint++) {
    if (!UsbEpCommand (
           Endpoint,
           DWC3_DEPCMD_SET_RESOURCE,
           DWC3_DEPXFERCFG_RESOURCES (1),
           0,
           0,
           NULL
           )) {
      return FALSE;
    }
  }

  if (!UsbConfigureEndpoint (
         USB_EP0_OUT,
         USB_ENDPOINT_CONTROL,
         USB_EP0_MAX_PACKET,
         0
         ) ||
      !UsbConfigureEndpoint (
         USB_EP0_IN,
         USB_ENDPOINT_CONTROL,
         USB_EP0_MAX_PACKET,
         0
         ) ||
      !UsbConfigureEndpoint (
         USB_HID_EP_IN,
         USB_ENDPOINT_INTERRUPT,
         USB_HID_REPORT_SIZE,
         1
         )) {
    return FALSE;
  }

  UsbDwcWrite (
    DWC3_DALEPENA,
    DWC3_DALEPENA_EP (USB_EP0_OUT) |
    DWC3_DALEPENA_EP (USB_EP0_IN)
    );
  return TRUE;
}

STATIC
BOOLEAN
UsbEp0ArmSetup (
  VOID
  )
{
  if (mEp0Busy[USB_EP0_OUT]) {
    return TRUE;
  }

  ZeroMem (mSetupBuffer, sizeof (USB_DEVICE_REQUEST));
  WriteBackDataCacheRange (mSetupBuffer, 64);
  UsbPrepareTrb (
    mEp0Trb,
    mSetupBuffer,
    sizeof (USB_DEVICE_REQUEST),
    DWC3_TRB_CONTROL_SETUP
    );
  if (!UsbStartTransfer (USB_EP0_OUT, mEp0Trb, NULL)) {
    return FALSE;
  }

  mEp0State               = UsbEp0Setup;
  mEp0Busy[USB_EP0_OUT]   = TRUE;
  mEp0Busy[USB_EP0_IN]    = FALSE;
  mPendingAddressValid    = FALSE;
  mPendingConfigurationValid = FALSE;
  return TRUE;
}

STATIC
BOOLEAN
UsbEp0StartPhase (
  IN UINTN   Endpoint,
  IN VOID    *Buffer,
  IN UINT32  Length,
  IN UINT32  TrbType
  )
{
  if (Length != 0u) {
    WriteBackDataCacheRange (Buffer, Length);
  }

  UsbPrepareTrb (mEp0Trb, Buffer, Length, TrbType);
  if (!UsbStartTransfer (Endpoint, mEp0Trb, NULL)) {
    return FALSE;
  }

  mEp0Busy[USB_EP0_OUT] = (BOOLEAN)(Endpoint == USB_EP0_OUT);
  mEp0Busy[USB_EP0_IN]  = (BOOLEAN)(Endpoint == USB_EP0_IN);
  return TRUE;
}

STATIC
BOOLEAN
UsbEp0StartStatus (
  IN BOOLEAN  ThreeStage
  )
{
  UINTN   Endpoint;
  UINT32  TrbType;

  Endpoint = (ThreeStage && mDataWasIn) ? USB_EP0_OUT : USB_EP0_IN;
  TrbType  = ThreeStage ?
             DWC3_TRB_CONTROL_STATUS3 :
             DWC3_TRB_CONTROL_STATUS2;
  mEp0State = UsbEp0Status;
  return UsbEp0StartPhase (Endpoint, mControlBuffer, 0, TrbType);
}

STATIC
UINTN
UsbBuildStringDescriptor (
  IN  UINT8  Index,
  OUT UINT8  *Buffer,
  IN  UINTN  Capacity
  )
{
  CONST CHAR8  *String;
  UINTN        Length;
  UINTN        Position;

  if (Capacity < 4u) {
    return 0;
  }

  if (Index == 0u) {
    Buffer[0] = 4;
    Buffer[1] = USB_DESC_TYPE_STRING;
    Buffer[2] = 0x09;
    Buffer[3] = 0x04;
    return 4;
  }

  switch (Index) {
    case 1:
      String = "Star2Lte EDK2";
      break;
    case 2:
      String = "S9+ UEFI Debug";
      break;
    case 3:
      String = "S9PUEFI0001";
      break;
    default:
      return 0;
  }

  Length = AsciiStrLen (String);
  if ((2u + (Length * 2u)) > Capacity) {
    Length = (Capacity - 2u) / 2u;
  }

  Buffer[0] = (UINT8)(2u + (Length * 2u));
  Buffer[1] = USB_DESC_TYPE_STRING;
  for (Position = 0; Position < Length; Position++) {
    Buffer[2u + (Position * 2u)] = (UINT8)String[Position];
    Buffer[3u + (Position * 2u)] = 0;
  }

  return Buffer[0];
}

STATIC
CONST UINT8 *
UsbGetDescriptor (
  IN  UINT8  Type,
  IN  UINT8  Index,
  OUT UINTN  *Length
  )
{
  *Length = 0;
  switch (Type) {
    case USB_DESC_TYPE_DEVICE:
      *Length = sizeof (mDeviceDescriptor);
      return mDeviceDescriptor;
    case USB_DESC_TYPE_CONFIG:
      *Length = sizeof (mConfigurationDescriptor);
      return mConfigurationDescriptor;
    case USB_DESC_TYPE_STRING:
      *Length = UsbBuildStringDescriptor (
                  Index,
                  mControlBuffer,
                  USB_DMA_CONTROL_SIZE
                  );
      return mControlBuffer;
    case USB_DESC_TYPE_HID:
      *Length = sizeof (mHidDescriptor);
      return mHidDescriptor;
    case USB_DESC_TYPE_REPORT:
      *Length = sizeof (mReportDescriptor);
      return mReportDescriptor;
    case USB_DESC_TYPE_DEVICE_QUALIFIER:
      *Length = sizeof (mDeviceQualifierDescriptor);
      return mDeviceQualifierDescriptor;
    case USB_DESC_TYPE_OTHER_SPEED:
      CopyMem (
        mControlBuffer,
        mConfigurationDescriptor,
        sizeof (mConfigurationDescriptor)
        );
      mControlBuffer[1] = USB_DESC_TYPE_OTHER_SPEED;
      *Length = sizeof (mConfigurationDescriptor);
      return mControlBuffer;
    default:
      return NULL;
  }
}

STATIC
VOID
UsbSetAddress (
  IN UINT8  Address
  )
{
  UINT32  Value;

  Value  = UsbDwcRead (DWC3_DCFG);
  Value &= ~DWC3_DCFG_ADDRESS_MASK;
  Value |= DWC3_DCFG_ADDRESS (Address);
  UsbDwcWrite (DWC3_DCFG, Value);
  mAddress = Address;
}

STATIC
VOID
UsbSetConfiguration (
  IN UINT8  Configuration
  )
{
  UINT32  Value;

  Value = UsbDwcRead (DWC3_DALEPENA);
  if (Configuration == 1u) {
    UsbDwcWrite (
      DWC3_DALEPENA,
      Value | DWC3_DALEPENA_EP (USB_HID_EP_IN)
      );
    mConfigured = TRUE;
  } else {
    if (mHidBusy && (mHidResource != 0u)) {
      UsbEpCommand (
        USB_HID_EP_IN,
        DWC3_DEPCMD_END_TRANSFER |
        DWC3_DEPCMD_PARAM (mHidResource) |
        DWC3_DEPCMD_FORCE_REMOVE,
        0,
        0,
        0,
        NULL
        );
    }

    UsbDwcWrite (
      DWC3_DALEPENA,
      Value & ~DWC3_DALEPENA_EP (USB_HID_EP_IN)
      );
    mConfigured = FALSE;
    mHidBusy    = FALSE;
    mHidResource = 0;
  }
}

STATIC
VOID
UsbEp0Stall (
  VOID
  )
{
  UsbEpCommand (USB_EP0_OUT, DWC3_DEPCMD_SET_STALL, 0, 0, 0, NULL);
  UsbEpCommand (USB_EP0_IN, DWC3_DEPCMD_SET_STALL, 0, 0, 0, NULL);
  mEp0Busy[USB_EP0_OUT] = FALSE;
  mEp0Busy[USB_EP0_IN]  = FALSE;
  UsbEp0ArmSetup ();
}

STATIC
VOID
UsbEp0HandleSetup (
  VOID
  )
{
  USB_DEVICE_REQUEST  Request;
  CONST UINT8         *Response;
  UINTN               ResponseLength;
  UINTN               TransferLength;
  BOOLEAN             Accepted;
  BOOLEAN             HasData;
  UINT8               DescriptorType;
  UINT8               DescriptorIndex;
  UINT8               Endpoint;

  InvalidateDataCacheRange (mSetupBuffer, 64);
  CopyMem (&Request, mSetupBuffer, sizeof (Request));
  Response       = NULL;
  ResponseLength = 0;
  Accepted       = FALSE;
  HasData        = FALSE;
  mDataWasIn     = (BOOLEAN)((Request.RequestType & 0x80u) != 0u);

  if ((Request.RequestType & 0x60u) == USB_REQ_TYPE_STANDARD) {
    switch (Request.Request) {
      case USB_REQ_GET_STATUS:
        mControlBuffer[0] = 0;
        mControlBuffer[1] = 0;
        Response          = mControlBuffer;
        ResponseLength    = 2;
        Accepted          = TRUE;
        HasData           = TRUE;
        break;
      case USB_REQ_CLEAR_FEATURE:
        if (Request.Value == USB_FEATURE_ENDPOINT_HALT) {
          Endpoint = (UINT8)Request.Index;
          if ((Endpoint & USB_ENDPOINT_DIR_IN) != 0u) {
            Endpoint = (UINT8)(((Endpoint & 0x0Fu) * 2u) + 1u);
          } else {
            Endpoint = (UINT8)((Endpoint & 0x0Fu) * 2u);
          }

          Accepted = UsbEpCommand (
                       Endpoint,
                       DWC3_DEPCMD_CLEAR_STALL,
                       0,
                       0,
                       0,
                       NULL
                       );
        }

        break;
      case USB_REQ_SET_ADDRESS:
        if (Request.Value <= 127u) {
          mPendingAddress      = (UINT8)Request.Value;
          mPendingAddressValid = TRUE;
          Accepted             = TRUE;
        }

        break;
      case USB_REQ_GET_DESCRIPTOR:
        DescriptorType  = (UINT8)(Request.Value >> 8);
        DescriptorIndex = (UINT8)Request.Value;
        Response         = UsbGetDescriptor (
                             DescriptorType,
                             DescriptorIndex,
                             &ResponseLength
                             );
        Accepted = (BOOLEAN)(Response != NULL);
        HasData  = Accepted;
        break;
      case USB_REQ_GET_CONFIG:
        mControlBuffer[0] = mConfigured ? 1u : 0u;
        Response          = mControlBuffer;
        ResponseLength    = 1;
        Accepted          = TRUE;
        HasData           = TRUE;
        break;
      case USB_REQ_SET_CONFIG:
        if (Request.Value <= 1u) {
          mPendingConfiguration      = (UINT8)Request.Value;
          mPendingConfigurationValid = TRUE;
          Accepted                   = TRUE;
        }

        break;
      case USB_REQ_GET_INTERFACE:
        mControlBuffer[0] = 0;
        Response          = mControlBuffer;
        ResponseLength    = 1;
        Accepted          = TRUE;
        HasData           = TRUE;
        break;
      case USB_REQ_SET_INTERFACE:
        Accepted = (BOOLEAN)(Request.Value == 0u);
        break;
      default:
        break;
    }
  } else if ((Request.RequestType & 0x60u) == USB_REQ_TYPE_CLASS) {
    switch (Request.Request) {
      case USB_HID_GET_REPORT:
        ZeroMem (mControlBuffer, USB_HID_REPORT_SIZE);
        AsciiSPrint (
          (CHAR8 *)mControlBuffer,
          USB_HID_REPORT_SIZE,
          "S9DBG/1 source=uefi seq=%08x state=control",
          mSequence
          );
        Response       = mControlBuffer;
        ResponseLength = USB_HID_REPORT_SIZE;
        Accepted       = TRUE;
        HasData        = TRUE;
        break;
      case USB_HID_GET_IDLE:
      case USB_HID_GET_PROTOCOL:
        mControlBuffer[0] = 0;
        Response          = mControlBuffer;
        ResponseLength    = 1;
        Accepted          = TRUE;
        HasData           = TRUE;
        break;
      case USB_HID_SET_IDLE:
      case USB_HID_SET_PROTOCOL:
        Accepted = TRUE;
        break;
      default:
        break;
    }
  }

  if (!Accepted || (HasData && !mDataWasIn)) {
    UsbEp0Stall ();
    return;
  }

  if (HasData) {
    TransferLength = ResponseLength;
    if (TransferLength > Request.Length) {
      TransferLength = Request.Length;
    }

    if (TransferLength > USB_DMA_CONTROL_SIZE) {
      TransferLength = USB_DMA_CONTROL_SIZE;
    }

    if (Response != mControlBuffer) {
      CopyMem (mControlBuffer, Response, TransferLength);
    }

    mEp0State = UsbEp0Data;
    if (!UsbEp0StartPhase (
           USB_EP0_IN,
           mControlBuffer,
           (UINT32)TransferLength,
           DWC3_TRB_CONTROL_DATA
           )) {
      UsbEp0Stall ();
    }

    return;
  }

  if (!UsbEp0StartStatus (FALSE)) {
    UsbEp0Stall ();
  }
}

STATIC
VOID
UsbEp0Complete (
  IN UINTN  Endpoint
  )
{
  if (Endpoint <= USB_EP0_IN) {
    mEp0Busy[Endpoint] = FALSE;
  }

  switch (mEp0State) {
    case UsbEp0Setup:
      if (Endpoint == USB_EP0_OUT) {
        UsbEp0HandleSetup ();
      }

      break;
    case UsbEp0Data:
      if (!UsbEp0StartStatus (TRUE)) {
        UsbEp0Stall ();
      }

      break;
    case UsbEp0Status:
      if (mPendingAddressValid) {
        UsbSetAddress (mPendingAddress);
        mPendingAddressValid = FALSE;
      }

      if (mPendingConfigurationValid) {
        UsbSetConfiguration (mPendingConfiguration);
        mPendingConfigurationValid = FALSE;
      }

      UsbEp0ArmSetup ();
      break;
    default:
      UsbEp0Stall ();
      break;
  }
}

STATIC
VOID
UsbHandleReset (
  VOID
  )
{
  mConfigured               = FALSE;
  mHidBusy                  = FALSE;
  mHidResource              = 0;
  mEp0Busy[USB_EP0_OUT]     = FALSE;
  mEp0Busy[USB_EP0_IN]      = FALSE;
  mPendingAddressValid      = FALSE;
  mPendingConfigurationValid = FALSE;
  UsbSetAddress (0);
  UsbDwcWrite (
    DWC3_DALEPENA,
    DWC3_DALEPENA_EP (USB_EP0_OUT) |
    DWC3_DALEPENA_EP (USB_EP0_IN)
    );
  UsbEp0ArmSetup ();
}

STATIC
VOID
UsbHandleDeviceEvent (
  IN UINT32  Event
  )
{
  UINT32  Type;
  UINT32  Value;

  if (mEventLogBudget != 0u) {
    UsbLogValue ("USBDEVT", Event);
    mEventLogBudget--;
  }

  Type = (Event >> 8) & 0xFu;
  switch (Type) {
    case DWC3_DEVICE_DISCONNECT:
      mConfigured = FALSE;
      mHidBusy    = FALSE;
      break;
    case DWC3_DEVICE_USB_RESET:
      UsbHandleReset ();
      break;
    case DWC3_DEVICE_CONNECT_DONE:
      Value = UsbDwcRead (DWC3_GCTL);
      UsbDwcWrite (DWC3_GCTL, Value | DWC3_GCTL_RAMCLKSEL_MASK);
      Value      = UsbDwcRead (DWC3_DSTS);
      mLinkSpeed = (UINT8)(Value & DWC3_DSTS_SPEED_MASK);
      UsbLogValue ("USBRUNS", Value);
      if (!mEp0Busy[USB_EP0_OUT] && !mEp0Busy[USB_EP0_IN]) {
        UsbEp0ArmSetup ();
      }

      break;
    default:
      break;
  }
}

STATIC
VOID
UsbHandleEndpointEvent (
  IN UINT32  Event
  )
{
  UINTN   Endpoint;
  UINT32  Type;

  if (mEventLogBudget != 0u) {
    UsbLogValue ("USBEEVT", Event);
    mEventLogBudget--;
  }

  Endpoint = (Event >> 1) & 0x1Fu;
  Type     = (Event >> 6) & 0xFu;
  if ((Type != DWC3_ENDPOINT_XFER_COMPLETE) &&
      (Type != DWC3_ENDPOINT_XFER_PROGRESS)) {
    return;
  }

  if (Endpoint <= USB_EP0_IN) {
    UsbEp0Complete (Endpoint);
  } else if (Endpoint == USB_HID_EP_IN) {
    mHidBusy     = FALSE;
    mHidResource = 0;
  }
}

STATIC
VOID
UsbPollEvents (
  VOID
  )
{
  UINT32  Count;
  UINT32  Budget;
  UINT32  Event;

  Count = UsbDwcRead (DWC3_GEVNTCOUNT0) & DWC3_EVENT_SIZE_MASK;
  Count &= ~0x3u;
  if (Count == 0u) {
    return;
  }

  InvalidateDataCacheRange (mEventPage, USB_EVENT_BUFFER_SIZE);
  Budget = USB_EVENT_BUDGET;
  while ((Count >= sizeof (UINT32)) && (Budget-- != 0u)) {
    CopyMem (
      &Event,
      (UINT8 *)mEventPage + mEventPosition,
      sizeof (Event)
      );
    mEventPosition = (mEventPosition + sizeof (Event)) %
                     USB_EVENT_BUFFER_SIZE;
    Count -= sizeof (Event);
    UsbDwcWrite (DWC3_GEVNTCOUNT0, sizeof (Event));

    if ((Event & 1u) == DWC3_EVENT_DEVICE) {
      UsbHandleDeviceEvent (Event);
    } else {
      UsbHandleEndpointEvent (Event);
    }
  }
}

STATIC
UINTN
UsbBuildHidReport (
  OUT UINT32  *NewTail
  )
{
  UINT32  Head;
  UINT32  Tail;
  UINTN   Position;

  ZeroMem (mHidReport, USB_HID_REPORT_SIZE);
  Position = AsciiSPrint (
               (CHAR8 *)mHidReport,
               USB_HID_REPORT_SIZE,
               "S9DBG/1 source=uefi seq=%08x ",
               mSequence
               );
  Head = mLogHead;
  Tail = mLogTail;
  if (Head == Tail) {
    Position += AsciiSPrint (
                  (CHAR8 *)mHidReport + Position,
                  USB_HID_REPORT_SIZE - Position,
                  "state=%02x drop=%u",
                  mConfigured ? 1u : 0u,
                  mLogDrops
                  );
    if (Position < USB_HID_REPORT_SIZE) {
      Position += AsciiSPrint (
                    (CHAR8 *)mHidReport + Position,
                    USB_HID_REPORT_SIZE - Position,
                    " speed=%u",
                    mLinkSpeed
                    );
    }
  } else {
    while ((Tail != Head) && (Position < USB_HID_REPORT_SIZE)) {
      mHidReport[Position++] = mLogRing[Tail & (USB_LOG_RING_SIZE - 1u)];
      Tail++;
    }
  }

  *NewTail = Tail;
  return Position;
}

STATIC
VOID
UsbKickHid (
  VOID
  )
{
  UINT32  NewTail;

  if (!mConfigured || mHidBusy) {
    return;
  }

  if (mLogHead == mLogTail) {
    if (++mHeartbeatPolls < USB_HEARTBEAT_POLLS) {
      return;
    }

    mHeartbeatPolls = 0;
  } else {
    mHeartbeatPolls = 0;
  }

  UsbBuildHidReport (&NewTail);
  WriteBackDataCacheRange (mHidReport, USB_HID_REPORT_SIZE);
  UsbPrepareTrb (
    mHidTrb,
    mHidReport,
    USB_HID_REPORT_SIZE,
    DWC3_TRB_NORMAL
    );
  if (UsbStartTransfer (USB_HID_EP_IN, mHidTrb, &mHidResource)) {
    mLogTail = NewTail;
    mSequence++;
    mHidBusy = TRUE;
  }
}

STATIC
VOID
EFIAPI
UsbPollTimer (
  IN EFI_EVENT  Event,
  IN VOID       *Context
  )
{
  (VOID)Event;
  (VOID)Context;

  if (!mActive || mPolling) {
    return;
  }

  mPolling = TRUE;
  UsbPollEvents ();
  UsbKickHid ();
  mPolling = FALSE;
}

STATIC
BOOLEAN
UsbInitializeController (
  VOID
  )
{
  UINT64  EventAddress;
  UINT32  Value;

  Value = UsbDwcRead (DWC3_GSNPSID);
  UsbLogValue ("USBHIDID", Value);
  if ((Value & 0xFFFF0000u) != 0x55330000u) {
    UsbLogValue ("USBHIDS", 0x80000001u);
    return FALSE;
  }

  UsbLogValue ("USBHIDS", 1);
  mControllerTouched = TRUE;

  UsbDwcWrite (DWC3_DCTL, DWC3_DCTL_CORE_SOFT_RESET);
  if (!UsbWaitClear (
         EXYNOS_USBDRD_BASE + DWC3_DCTL,
         DWC3_DCTL_CORE_SOFT_RESET,
         5000
         )) {
    UsbLogValue ("USBHIDS", 0x80000010u);
    UsbLogControllerState ();
    return FALSE;
  }

  UsbLogValue ("USBHIDS", 0x10u);
  UsbEnablePhy ();
  UsbLogValue ("USBHIDS", 2);

  Value = UsbDwcRead (DWC3_GCTL);
  UsbDwcWrite (DWC3_GCTL, Value | DWC3_GCTL_CORE_SOFT_RESET);
  UsbLogValue ("USBHIDS", 3);
  UsbDwcWrite (
    DWC3_GUSB2PHYCFG0,
    UsbDwcRead (DWC3_GUSB2PHYCFG0) | DWC3_PHY_SOFT_RESET
    );
  UsbDwcWrite (
    DWC3_GUSB3PIPECTL0,
    UsbDwcRead (DWC3_GUSB3PIPECTL0) | DWC3_PHY_SOFT_RESET
    );
  MicroSecondDelay (100);
  UsbDwcWrite (
    DWC3_GUSB2PHYCFG0,
    UsbDwcRead (DWC3_GUSB2PHYCFG0) & ~DWC3_PHY_SOFT_RESET
    );
  UsbDwcWrite (
    DWC3_GUSB3PIPECTL0,
    UsbDwcRead (DWC3_GUSB3PIPECTL0) & ~DWC3_PHY_SOFT_RESET
    );
  MicroSecondDelay (100);
  UsbDwcWrite (
    DWC3_GCTL,
    UsbDwcRead (DWC3_GCTL) & ~DWC3_GCTL_CORE_SOFT_RESET
    );
  UsbLogValue ("USBHIDS", 4);

  Value  = UsbDwcRead (DWC3_GCTL);
  Value &= ~(DWC3_GCTL_PRTCAP_MASK | DWC3_GCTL_DSBLCLKGTNG);
  Value |= DWC3_GCTL_PRTCAP_DEVICE | DWC3_GCTL_SOFITPSYNC;
  UsbDwcWrite (DWC3_GCTL, Value);

  Value  = UsbDwcRead (DWC3_DCTL);
  Value &= ~DWC3_DCTL_RUN_STOP;
  UsbDwcWrite (DWC3_DCTL, Value | DWC3_DCTL_CORE_SOFT_RESET);
  if (!UsbWaitClear (
         EXYNOS_USBDRD_BASE + DWC3_DCTL,
         DWC3_DCTL_CORE_SOFT_RESET,
         USB_COMMAND_TIMEOUT_US
         )) {
    UsbLogValue ("USBHIDS", 0x80000005u);
    UsbLogControllerState ();
    return FALSE;
  }

  MicroSecondDelay (100000);
  UsbLogValue ("USBHIDS", 5);
  Value = UsbDwcRead (DWC3_GSBUSCFG0);
  Value |= DWC3_GSBUSCFG0_INCRBRSTEN |
           DWC3_GSBUSCFG0_INCR16BRSTEN |
           DWC3_GSBUSCFG0_DESWRREQINFO |
           DWC3_GSBUSCFG0_DATWRREQINFO |
           DWC3_GSBUSCFG0_DESRDREQINFO |
           DWC3_GSBUSCFG0_DATRDREQINFO;
  UsbDwcWrite (DWC3_GSBUSCFG0, Value);

  Value  = UsbDwcRead (DWC3_GSBUSCFG1);
  Value &= ~DWC3_GSBUSCFG1_BREQLIMIT_MASK;
  Value |= DWC3_GSBUSCFG1_BREQLIMIT (3);
  UsbDwcWrite (DWC3_GSBUSCFG1, Value);

  Value = UsbDwcRead (DWC3_GUCTL);
  Value &= ~DWC3_GUCTL_REFCLKPER_MASK;
  Value |= DWC3_GUCTL_REFCLKPER (0x14);
  Value |= DWC3_GUCTL_USBHSTINAUTORETRYEN |
           DWC3_GUCTL_SPRSCTRLTRANSEN;
  UsbDwcWrite (DWC3_GUCTL, Value);

  Value = UsbDwcRead (DWC3_GUSB3PIPECTL0);
  Value |= DWC3_GUSB3PIPECTL_DISRXDETINP3 |
           DWC3_GUSB3PIPECTL_U1U2EXITFAIL |
           DWC3_GUSB3PIPECTL_SUSPHY;
  UsbDwcWrite (DWC3_GUSB3PIPECTL0, Value);

  Value  = UsbDwcRead (DWC3_GUSB2PHYCFG0);
  Value &= ~(DWC3_GUSB2PHYCFG_U2_FREECLK |
             DWC3_GUSB2PHYCFG_SUSPHY |
             DWC3_GUSB2PHYCFG_ENBLSLPM);
  UsbDwcWrite (DWC3_GUSB2PHYCFG0, Value);

  Value  = UsbDwcRead (DWC3_DCFG);
  Value &= ~(DWC3_DCFG_SPEED_MASK | DWC3_DCFG_ADDRESS_MASK);
  Value |= DWC3_DCFG_HIGH_SPEED;
  UsbDwcWrite (DWC3_DCFG, Value);

  EventAddress = (UINT64)(UINTN)mEventPage;
  UsbDwcWrite (DWC3_GEVNTSIZ0, DWC3_EVENT_INT_MASK);
  UsbDwcWrite (DWC3_GEVNTADRLO0, (UINT32)EventAddress);
  UsbDwcWrite (DWC3_GEVNTADRHI0, (UINT32)(EventAddress >> 32));
  UsbDwcWrite (DWC3_GEVNTCOUNT0, 0);
  UsbDwcWrite (DWC3_GEVNTSIZ0, USB_EVENT_BUFFER_SIZE);
  UsbDwcWrite (
    DWC3_DEVTEN,
    DWC3_DEVTEN_DISCONNECT |
    DWC3_DEVTEN_USB_RESET |
    DWC3_DEVTEN_CONNECT_DONE
    );

  UsbLogValue ("USBHIDS", 6);
  if (!UsbInitializeEndpoints ()) {
    UsbLogValue ("USBHIDS", 0x80000007u);
    UsbLogControllerState ();
    return FALSE;
  }

  UsbLogValue ("USBHIDS", 7);
  if (!UsbEp0ArmSetup ()) {
    UsbLogValue ("USBHIDS", 0x80000008u);
    UsbLogControllerState ();
    return FALSE;
  }

  UsbLogValue ("USBHIDS", 8);
  UsbDwcWrite (
    DWC3_DCTL,
    UsbDwcRead (DWC3_DCTL) | DWC3_DCTL_RUN_STOP
    );
  if (!UsbWaitClear (
         EXYNOS_USBDRD_BASE + DWC3_DSTS,
         DWC3_DSTS_CONTROLLER_HALTED,
         USB_RUN_TIMEOUT_US
         )) {
    UsbLogValue ("USBHIDS", 0x80000009u);
    UsbLogControllerState ();
    return FALSE;
  }

  UsbLogValue ("USBRUNS", UsbDwcRead (DWC3_DSTS));
  UsbLogValue ("USBHIDS", 9);
  return TRUE;
}

STATIC
VOID
UsbStopController (
  VOID
  )
{
  UINT32  Count;

  UsbDwcWrite (DWC3_DEVTEN, 0);
  UsbDwcWrite (
    DWC3_DCTL,
    UsbDwcRead (DWC3_DCTL) & ~DWC3_DCTL_RUN_STOP
    );
  if (!UsbWaitSet (
         EXYNOS_USBDRD_BASE + DWC3_DSTS,
         DWC3_DSTS_CONTROLLER_HALTED,
         USB_STOP_TIMEOUT_US
         )) {
    UsbDwcWrite (
      DWC3_DCTL,
      UsbDwcRead (DWC3_DCTL) | DWC3_DCTL_CORE_SOFT_RESET
      );
    UsbWaitClear (
      EXYNOS_USBDRD_BASE + DWC3_DCTL,
      DWC3_DCTL_CORE_SOFT_RESET,
      USB_COMMAND_TIMEOUT_US
      );
  }

  UsbDwcWrite (DWC3_GEVNTSIZ0, DWC3_EVENT_INT_MASK);
  Count = UsbDwcRead (DWC3_GEVNTCOUNT0) & DWC3_EVENT_SIZE_MASK;
  if (Count != 0u) {
    UsbDwcWrite (DWC3_GEVNTCOUNT0, Count);
  }

  UsbDwcWrite (DWC3_DALEPENA, 0);
  UsbDwcWrite (DWC3_GEVNTADRLO0, 0);
  UsbDwcWrite (DWC3_GEVNTADRHI0, 0);
  MemoryFence ();
  mControllerTouched = FALSE;
}

EFI_STATUS
Star2LteUsbDebugStart (
  IN STAR2LTE_USB_DEBUG_LOG  Log
  )
{
  UINT32      ClockErrors;
  EFI_STATUS  Status;

  if (mActive) {
    return EFI_ALREADY_STARTED;
  }

  mLog          = Log;
  mEventPosition = 0;
  mEventLogBudget = USB_EVENT_LOG_BUDGET;
  mSequence      = 0;
  mHeartbeatPolls = 0;
  mLogHead       = 0;
  mLogTail       = 0;
  mLogDrops      = 0;
  mLinkSpeed     = 0xFFu;

  ClockErrors = UsbValidateClocks ();
  UsbLogValue ("USBCLKV", ClockErrors);
  if (ClockErrors != 0u) {
    Status = EFI_NOT_READY;
    UsbLogValue ("USBHIDF", 1);
    goto Error;
  }

  Status = UsbAllocateDmaPage (&mEventPage);
  if (EFI_ERROR (Status)) {
    goto Error;
  }

  Status = UsbAllocateDmaPage (&mTransferPage);
  if (EFI_ERROR (Status)) {
    goto Error;
  }

  ZeroMem (mEventPage, EFI_PAGE_SIZE);
  ZeroMem (mTransferPage, EFI_PAGE_SIZE);
  WriteBackDataCacheRange (mEventPage, EFI_PAGE_SIZE);
  WriteBackDataCacheRange (mTransferPage, EFI_PAGE_SIZE);

  mSetupBuffer  = (UINT8 *)mTransferPage + USB_DMA_SETUP_OFFSET;
  mEp0Trb       = (USB_DWC3_TRB *)((UINT8 *)mTransferPage + USB_DMA_EP0_TRB_OFFSET);
  mControlBuffer = (UINT8 *)mTransferPage + USB_DMA_CONTROL_OFFSET;
  mHidTrb       = (USB_DWC3_TRB *)((UINT8 *)mTransferPage + USB_DMA_HID_TRB_OFFSET);
  mHidReport    = (UINT8 *)mTransferPage + USB_DMA_HID_REPORT_OFFSET;

  if (!UsbInitializeController ()) {
    Status = EFI_DEVICE_ERROR;
    UsbLogValue ("USBHIDF", 2);
    goto Error;
  }

  mActive = TRUE;
  Status = gBS->CreateEvent (
                  EVT_TIMER | EVT_NOTIFY_SIGNAL,
                  TPL_CALLBACK,
                  UsbPollTimer,
                  NULL,
                  &mPollEvent
                  );
  if (EFI_ERROR (Status)) {
    goto Error;
  }

  Status = gBS->SetTimer (mPollEvent, TimerPeriodic, 10000u);
  if (EFI_ERROR (Status)) {
    gBS->CloseEvent (mPollEvent);
    mPollEvent = NULL;
    goto Error;
  }

  if (mLog != NULL) {
    mLog ("\n=USBHID ready vid=04e8 pid=6861\n");
  }

  return EFI_SUCCESS;

Error:
  mActive = FALSE;
  if (mControllerTouched) {
    UsbStopController ();
  }

  if (mPhySaved) {
    UsbRestoreRegisters (mPhyState, ARRAY_SIZE (mPhyState));
    mPhySaved = FALSE;
  }

  if (mTransferPage != NULL) {
    gBS->FreePages ((EFI_PHYSICAL_ADDRESS)(UINTN)mTransferPage, 1);
    mTransferPage = NULL;
  }

  if (mEventPage != NULL) {
    gBS->FreePages ((EFI_PHYSICAL_ADDRESS)(UINTN)mEventPage, 1);
    mEventPage = NULL;
  }

  return Status;
}

VOID
Star2LteUsbDebugWriteByte (
  IN UINT8  Byte
  )
{
  UINT32  Head;
  UINT32  Tail;

  if (!mActive || mPolling) {
    return;
  }

  Head = mLogHead;
  Tail = mLogTail;
  if ((Head - Tail) >= USB_LOG_RING_SIZE) {
    mLogDrops++;
    return;
  }

  mLogRing[Head & (USB_LOG_RING_SIZE - 1u)] = Byte;
  MemoryFence ();
  mLogHead = Head + 1u;
}

VOID
Star2LteUsbDebugQuiesce (
  VOID
  )
{
  if (!mActive) {
    return;
  }

  mActive = FALSE;
  MemoryFence ();
  UsbStopController ();

  if (mPhySaved) {
    UsbRestoreRegisters (mPhyState, ARRAY_SIZE (mPhyState));
    mPhySaved = FALSE;
  }
}

#endif
