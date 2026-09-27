/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#ifndef STAR2LTE_HSI40_CENSUS_H
#define STAR2LTE_HSI40_CENSUS_H
#include "Hsi40HandoffCore.h"
#define H40_CENSUS_PIN_WORDS 8u
#define H40_CENSUS_VERSION 2u
#define H40_USI_SELECTOR_ADDRESS 0x14212030u
#define H40_CENSUS_CLOCK_BEFORE 1u
#define H40_CENSUS_CLOCK_AFTER 2u
#define H40_CENSUS_PAIRED 4u
#define H40_CENSUS_IDLE_SHAPE 8u
#define H40_CENSUS_PIN_FUNCTION 16u
#define H40_CENSUS_PIN_DATA_HIGH 32u
typedef struct H40_CENSUS_RECORD {
    uint32_t Size,Version,Stage,Result,Flags,Reads,FailureAddress,Expected,Observed;
    uint32_t ClockValid[2],PinValid[2],ControllerValid[2];
    uint32_t ClockDiff,PinDiff,ControllerDiff;
    uint64_t InputClockHz;
    uint32_t Clocks[2][H40_CLOCK_WORDS];
    uint32_t Pins[2][H40_CENSUS_PIN_WORDS];
    uint32_t Controller[2][H40_CONTROLLER_WORDS];
    uint32_t UsiSelector[2],UsiValidMask,UsiDiff;
} H40_CENSUS_RECORD;
typedef struct H40_CENSUS_IO {
    void *Context;
    int (*Authorize)(void *);
    int (*Read)(void *,uint32_t,uint32_t *);
} H40_CENSUS_IO;
extern const uint32_t H40CensusPinAddresses[H40_CENSUS_PIN_WORDS];
#ifdef __cplusplus
extern "C" {
#endif
void H40CensusInitialize(H40_CENSUS_RECORD *);
uint32_t H40CensusCollect(H40_CENSUS_RECORD *,const H40_CENSUS_IO *,uint32_t);
#ifdef __cplusplus
}
#endif
#endif
