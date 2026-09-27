/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#ifndef STAR2LTE_HSI40_HANDOFF_CORE_H
#define STAR2LTE_HSI40_HANDOFF_CORE_H
#include <stdint.h>

#define H40_CLOCK_WORDS 14u
#define H40_PIN_WORDS 6u
#define H40_CONTROLLER_WORDS 20u
#define H40_CLOCK_VALID 1u
#define H40_PINS_VALID 2u
#define H40_IDLE_VALID 4u
#define H40_HELD 8u
#define H40_RESTORED 16u
#define H40_PUBLISHED 32u

enum H40_RESULT {
    H40_OK,H40_DISABLED,H40_AUTHORITY,H40_IO_ERROR,H40_CLOCK_ERROR,H40_PIN_ERROR,
    H40_CONTROLLER_ERROR,H40_WRITE_ERROR,H40_DRIFT,H40_ORDER_ERROR,H40_RESTORE_ERROR
};
typedef struct H40_RECORD {
    uint32_t Size,Version,Stage,Result,Flags,Reads,Writes,WriteReturns;
    uint32_t ClockValid[2],PinValid[2],BeforeValid,PreparedValid,RestoredValid;
    uint32_t PotentialMask,RestoredMask,FailureAddress,Expected,Observed;
    uint64_t InputClockHz;
    uint32_t Clocks[2][H40_CLOCK_WORDS],Pins[2][H40_PIN_WORDS];
    uint32_t Before[H40_CONTROLLER_WORDS],Prepared[H40_CONTROLLER_WORDS],Restored[H40_CONTROLLER_WORDS];
} H40_RECORD;
typedef struct H40_IO {
    void *Context;
    int (*Authorize)(void *);
    int (*Read)(void *,uint32_t,uint32_t *);
    int (*Write)(void *,uint32_t,uint32_t);
} H40_IO;

extern const uint32_t H40ClockAddresses[H40_CLOCK_WORDS];
extern const uint32_t H40PinAddresses[H40_PIN_WORDS];
extern const uint32_t H40ControllerOffsets[H40_CONTROLLER_WORDS];

#ifdef __cplusplus
extern "C" {
#endif
void H40Initialize(H40_RECORD *);
int H40ClockRouteValid(const uint32_t *);
int H40InitialControllerValid(const uint32_t *);
uint32_t H40Prepare(H40_RECORD *,const H40_IO *,uint32_t);
uint32_t H40AbortBeforePublication(H40_RECORD *,const H40_IO *);
uint32_t H40CommitPublication(H40_RECORD *);
#ifdef __cplusplus
}
#endif
#endif
