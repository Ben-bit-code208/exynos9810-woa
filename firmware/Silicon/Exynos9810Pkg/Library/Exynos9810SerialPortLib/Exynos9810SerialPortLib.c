/** @file
  Minimal Samsung/Exynos UART SerialPortLib for Exynos 9810 (SKELETON).

  GOAL: Milestone 1 — print a character so you know the UEFI payload runs.

  This assumes sboot already configured the UART clock and pinmux (it printed
  its own boot log), so we do NOT touch clocks/baud here — we only poll status
  and push bytes. Once you have sign-of-life, add proper Initialize() that
  programs ULCON/UCON/UFCON/UBRDIV for a known baud.

  Register offsets are the standard Samsung S3C/Exynos UART layout. The BASE
  address is the only thing you must supply (PcdSerialRegisterBase) — get it
  from the DT serial node (docs/02-memory-map.md).
**/

#include <Base.h>
#include <Library/SerialPortLib.h>
#include <Library/BaseLib.h>
#include <Library/PcdLib.h>
#include <Library/IoLib.h>

//
// Samsung/Exynos UART register offsets.
//
#define EXYNOS_UART_ULCON     0x00  // Line control
#define EXYNOS_UART_UCON      0x04  // Control
#define EXYNOS_UART_UFCON     0x08  // FIFO control
#define EXYNOS_UART_UTRSTAT   0x10  // TX/RX status
#define EXYNOS_UART_UFSTAT    0x18  // FIFO status
#define EXYNOS_UART_UTXH      0x20  // Transmit buffer
#define EXYNOS_UART_URXH      0x24  // Receive buffer

#define UTRSTAT_TX_EMPTY      (1u << 1)  // Transmit buffer register empty
#define UTRSTAT_RX_READY      (1u << 0)  // Receive buffer data ready

#define ULCON_8N1             0x3   // 8 data bits, no parity, 1 stop bit
#define UCON_TXRX_POLL        0x5   // TX + RX in interrupt/polling mode

STATIC
UINTN
GetUartBase (
  VOID
  )
{
  // TODO-VERIFY: provided via Silicon/Exynos9810Pkg PcdSerialRegisterBase.
  return (UINTN)FixedPcdGet64 (PcdSerialRegisterBase);
}

/**
  Program the UART for a known 8N1 line format in polling mode.

  Deliberately conservative: sboot already configured the UART clock and baud
  divisor (it printed its own boot log over this port), so we do NOT touch
  UFCON/UBRDIV/UFRACVAL — reprogramming the baud wrong is the fastest way to turn
  readable output into garbage. We only force ULCON (8N1) and UCON (TX/RX on).
  Matches the standalone m1-hello payload so both agree.
**/
RETURN_STATUS
EFIAPI
SerialPortInitialize (
  VOID
  )
{
  UINTN  Base;

  Base = GetUartBase ();
  if (Base == 0) {
    // Base not verified yet — skip init rather than poke physical address 0.
    return RETURN_SUCCESS;
  }

  MmioWrite32 (Base + EXYNOS_UART_ULCON, ULCON_8N1);
  MmioWrite32 (Base + EXYNOS_UART_UCON,  UCON_TXRX_POLL);
  // UFCON / UBRDIV / UFRACVAL left as sboot configured them.
  return RETURN_SUCCESS;
}

UINTN
EFIAPI
SerialPortWrite (
  IN UINT8  *Buffer,
  IN UINTN   NumberOfBytes
  )
{
  UINTN  Base;
  UINTN  Index;

  if (Buffer == NULL) {
    return 0;
  }

  Base = GetUartBase ();
  if (Base == 0) {
    return 0;  // not configured yet
  }

  for (Index = 0; Index < NumberOfBytes; Index++) {
    // Wait until the transmit buffer is empty — BOUNDED so a UART that is not
    // TX-ready in this boot context can never hang the whole firmware (a real
    // risk during bring-up: PeilessSec writes its banner here before MMU).
    // Bound kept SMALL (~8k) so that when the UART never drains (no cable in this
    // chainloaded context), each dropped byte costs microseconds, not ~2 ms — a
    // verbose DEBUG/ASSERT burst can never stall the boot for seconds.
    UINT32  Spin = 0;
    while ((MmioRead32 (Base + EXYNOS_UART_UTRSTAT) & UTRSTAT_TX_EMPTY) == 0) {
      if (++Spin > 8000u) {
        return Index;  // give up; drop the rest rather than spin forever
      }
      CpuPause ();
    }
    MmioWrite32 (Base + EXYNOS_UART_UTXH, Buffer[Index]);
  }

  return NumberOfBytes;
}

UINTN
EFIAPI
SerialPortRead (
  OUT UINT8  *Buffer,
  IN  UINTN   NumberOfBytes
  )
{
  UINTN  Base;
  UINTN  Index;

  if (Buffer == NULL) {
    return 0;
  }

  Base = GetUartBase ();
  if (Base == 0) {
    return 0;
  }

  for (Index = 0; Index < NumberOfBytes; Index++) {
    UINT32  Spin = 0;
    while ((MmioRead32 (Base + EXYNOS_UART_UTRSTAT) & UTRSTAT_RX_READY) == 0) {
      if (++Spin > 2000000u) {
        return Index;  // bounded: never hang waiting for input that never comes
      }
      CpuPause ();
    }
    Buffer[Index] = (UINT8)MmioRead32 (Base + EXYNOS_UART_URXH);
  }

  return NumberOfBytes;
}

BOOLEAN
EFIAPI
SerialPortPoll (
  VOID
  )
{
  UINTN  Base;

  Base = GetUartBase ();
  if (Base == 0) {
    return FALSE;
  }

  return (BOOLEAN)((MmioRead32 (Base + EXYNOS_UART_UTRSTAT) & UTRSTAT_RX_READY) != 0);
}

RETURN_STATUS
EFIAPI
SerialPortSetControl (
  IN UINT32  Control
  )
{
  return RETURN_UNSUPPORTED;
}

RETURN_STATUS
EFIAPI
SerialPortGetControl (
  OUT UINT32  *Control
  )
{
  if (Control == NULL) {
    return RETURN_INVALID_PARAMETER;
  }
  *Control = 0;
  return RETURN_SUCCESS;
}

RETURN_STATUS
EFIAPI
SerialPortSetAttributes (
  IN OUT UINT64              *BaudRate,
  IN OUT UINT32              *ReceiveFifoDepth,
  IN OUT UINT32              *Timeout,
  IN OUT EFI_PARITY_TYPE     *Parity,
  IN OUT UINT8               *DataBits,
  IN OUT EFI_STOP_BITS_TYPE  *StopBits
  )
{
  // TODO: program ULCON/UCON/UFCON/UBRDIV for a real baud once needed.
  return RETURN_SUCCESS;
}
