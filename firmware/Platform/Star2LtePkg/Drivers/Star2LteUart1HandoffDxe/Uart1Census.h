/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#ifndef STAR2LTE_UART1_CENSUS_H
#define STAR2LTE_UART1_CENSUS_H
#include <stdint.h>

#define U1_CLOCK_WORDS 16u
#define U1_PIN_WORDS 4u
#define U1_UART_WORDS 13u
#define U1_RECORD_VERSION 1u
#define U1_TABLE_BYTES 412u
#define U1_CLOCK_MASK 0xffffu
#define U1_PIN_MASK 0xfu
#define U1_UART_MASK 0x1fffu
#define U1_CLOCKS_PAIRED 1u
#define U1_PINS_PAIRED 2u
#define U1_UART_PAIRED 4u
#define U1_IDLE_SHAPE 8u
#define U1_PIN_FUNCTION 16u

enum U1_RESULT {
    U1_OK, U1_DISABLED, U1_ORDER, U1_AUTHORITY, U1_IO_ERROR,
    U1_DRIFT, U1_CLOCK_REFUSED
};
typedef struct U1_RECORD {
    uint32_t Size, Version, Stage, Result, Flags, Reads;
    uint32_t FailureAddress, FailureMask, FailureValue;
    uint32_t ClockValid[2], PinValid[2], UartValid[2];
    uint32_t ClockDiff, PinDiff, UartDiff;
    uint32_t NominalInputHz[2], RouteRefusals[2];
    uint32_t Clocks[2][U1_CLOCK_WORDS];
    uint32_t Pins[2][U1_PIN_WORDS];
    uint32_t Uart[2][U1_UART_WORDS];
} U1_RECORD;
typedef char U1RecordMustBe352[sizeof(U1_RECORD) == 352 ? 1 : -1];
typedef struct U1_IO {
    void *Context;
    int (*Authorize)(void *);
    int (*Read)(void *, uint32_t, uint32_t *);
} U1_IO;

#ifdef __cplusplus
extern "C" {
#endif
extern const uint32_t U1ClockAddresses[U1_CLOCK_WORDS];
extern const uint32_t U1PinAddresses[U1_PIN_WORDS];
extern const uint32_t U1UartAddresses[U1_UART_WORDS];
void U1Initialize(U1_RECORD *);
uint32_t U1ClockRefusals(const uint32_t *);
int U1ControllerReadable(const U1_RECORD *);
uint32_t U1Collect(U1_RECORD *, const U1_IO *, uint32_t);
#ifdef __cplusplus
}
#endif
#endif
