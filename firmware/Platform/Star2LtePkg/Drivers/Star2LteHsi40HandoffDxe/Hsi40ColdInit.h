/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#ifndef STAR2LTE_HSI40_COLD_INIT_H
#define STAR2LTE_HSI40_COLD_INIT_H
#include "Hsi40Census.h"
#ifndef H40_RESET_PIN_DRIVE
#define H40_RESET_PIN_DRIVE 0
#endif
#if H40_RESET_PIN_DRIVE != 0 && H40_RESET_PIN_DRIVE != 2
#error Reset observation only supports the exact measured legacy or Windows 11 drive profile
#endif
#ifndef H40_RESET_CONTROL
#define H40_RESET_CONTROL 0
#endif
#if H40_RESET_CONTROL != 0 && H40_RESET_CONTROL != 8
#error Reset observation requires an explicit released idle-control value
#endif
#ifndef H40_COLD_WIN11
#define H40_COLD_WIN11 0
#endif
#if H40_COLD_WIN11 != 0 && H40_COLD_WIN11 != 1
#error Select only an explicit cold-initialization profile
#endif
#if H40_COLD_WIN11 && (H40_RESET_PIN_DRIVE != 2 || H40_RESET_CONTROL != 8)
#error Windows 11 cold initialization requires the measured drive-2 and control-8 profile
#endif
#define H40_COLD_BEFORE_VALID 1u
#define H40_COLD_TOUCHED 2u
#define H40_COLD_HELD 4u
#define H40_COLD_PUBLISHED 8u
#define H40_COLD_RESTORED 16u
#define H40_COLD_QUARANTINED 32u
#define H40_COLD_AUTHORITY_LOST 64u
#define H40_COLD_INFRASTRUCTURE_LOST 128u
#define H40_COLD_RESET_OBSERVED 256u
#define H40_COLD_RESET_TIMEOUT 12u
typedef struct H40_COLD_IO {
    void *Context;
    int (*Authorize)(void *);
    int (*Read)(void *,uint32_t,uint32_t *);
    int (*Write)(void *,uint32_t,uint32_t);
    int (*StallOneUs)(void *);
} H40_COLD_IO;
typedef struct H40_COLD_RECORD {
    uint32_t Size,Version,Stage,Result,Flags,Reads,Writes,WriteReturns,Stalls,ResetPolls,PotentialMask;
    uint32_t RestoreResult,FailureAddress,Expected,Observed,RestoreAddress,RestoreExpected,RestoreObserved;
    H40_CENSUS_RECORD Before,After,Restored;
    uint32_t Reset[H40_CONTROLLER_WORDS],ResetValid;
} H40_COLD_RECORD;
#ifdef __cplusplus
extern "C" {
#endif
void H40ColdInitialize(H40_COLD_RECORD *);
uint32_t H40ColdPrepare(H40_COLD_RECORD *,const H40_COLD_IO *,uint32_t);
uint32_t H40ColdObserveReset(H40_COLD_RECORD *,const H40_COLD_IO *,uint32_t);
uint32_t H40ColdRestoreReset(H40_COLD_RECORD *,const H40_COLD_IO *);
uint32_t H40ColdAbortBeforePublication(H40_COLD_RECORD *,const H40_COLD_IO *);
uint32_t H40ColdCommitPublication(H40_COLD_RECORD *);
#ifdef __cplusplus
}
#endif
#endif
