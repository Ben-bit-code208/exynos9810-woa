/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#ifndef STAR2LTE_GPIO_HANDOFF_CORE_H
#define STAR2LTE_GPIO_HANDOFF_CORE_H
#include "GpioHandoffProfile.h"
typedef unsigned int GH_U32;
typedef unsigned char GH_U8;
typedef unsigned long long GH_U64;
typedef char GhU32MustBe4[sizeof(GH_U32) == 4 ? 1 : -1];
#define GH_OK 0u
#define GH_OFF 1u
#define GH_AUTHORITY 2u
#define GH_ELECTRICAL 3u
#define GH_READBACK 4u
#define GH_PUBLISH 5u
#define GH_IO_BASE 0x14050000ULL
#define GH_A0_MASK 0x900u
#define GH_A1_MASK 0x904u
#define GH_RECORD_VERSION 1u
typedef struct {
    GH_U32 Con, Pull, Drive, Eint, Filter0, Filter1, Mask;
} GH_STATE;
typedef struct {
    GH_U32 Size, Version, Status, Policy;
    GH_U32 AuthorityVerified, BeforeValid, AfterValid, AttemptedWrites;
    GH_U32 FailureOffset, FailureReadback, CallbackOrdinal, AuthorityFailure;
    GH_U8 CandidateId[16];
    GH_STATE Before[2], After[2];
} GH_RECORD;
typedef struct {
    void *Context;
    GH_U32 (*Read)(void *, GH_U32);
    void (*Write)(void *, GH_U32, GH_U32);
} GH_IO;
typedef char GhRecordMustBe176[sizeof(GH_RECORD) == 176 ? 1 : -1];
GH_U32 GhHandoff(GH_U32 Mode, GH_U32 AuthorityVerified, const GH_IO *Io, GH_RECORD *Record);
GH_U32 GhWriteMask(GH_U32 Mode, GH_U32 Offset);
GH_U32 GhWriteBits(GH_U32 Offset);
#endif
