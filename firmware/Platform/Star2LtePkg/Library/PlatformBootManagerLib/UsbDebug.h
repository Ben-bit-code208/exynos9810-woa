/** @file
  Native USB diagnostic profiles for Star2Lte.
**/

#ifndef STAR2LTE_USB_DEBUG_H_
#define STAR2LTE_USB_DEBUG_H_

#include <Uefi.h>

#define STAR2LTE_USB_DEBUG_OFF       0
#define STAR2LTE_USB_DEBUG_SNAPSHOT  1
#define STAR2LTE_USB_DEBUG_HID       2

#ifndef STAR2LTE_USB_DEBUG_MODE
#define STAR2LTE_USB_DEBUG_MODE STAR2LTE_USB_DEBUG_OFF
#endif

typedef
VOID
(*STAR2LTE_USB_DEBUG_LOG)(
  IN CONST CHAR8  *Message
  );

#if STAR2LTE_USB_DEBUG_MODE >= STAR2LTE_USB_DEBUG_SNAPSHOT
VOID
Star2LteUsbDebugSnapshot (
  IN STAR2LTE_USB_DEBUG_LOG  Log
  );
#endif

#if STAR2LTE_USB_DEBUG_MODE == STAR2LTE_USB_DEBUG_HID
EFI_STATUS
Star2LteUsbDebugStart (
  IN STAR2LTE_USB_DEBUG_LOG  Log
  );

VOID
Star2LteUsbDebugWriteByte (
  IN UINT8  Byte
  );

VOID
Star2LteUsbDebugQuiesce (
  VOID
  );
#endif

#endif
