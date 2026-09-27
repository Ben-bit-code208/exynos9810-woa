/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include "Hsi40HandoffCore.h"
#include <string.h>

const uint32_t H40ClockAddresses[H40_CLOCK_WORDS]={
    0x1a240220,0x1a2418ec,0x1a241028,0x1a241824,0x1a242028,
    0x14200100,0x14201004,0x14201018,0x14202014,0x14201814,
    0x142020a8,0x142020ac,0x14202074,0x14203050
};
const uint32_t H40PinAddresses[H40_PIN_WORDS]={
    0x14220180,0x14220188,0x1422018c,0x142201a0,0x142201a8,0x142201ac
};
const uint32_t H40ControllerOffsets[H40_CONTROLLER_WORDS]={
    0,4,8,0x20,0x24,0x30,0x40,0x44,0x48,0x50,
    0x54,0x58,0x5c,0x60,0x64,0x68,0x6c,0x70,0xc4,0xc8
};
static const uint32_t Indices[3]={7,1,0};
static const uint32_t Values[3]={0,0,8};

static uint32_t Fail(H40_RECORD *r,uint32_t result,uint32_t address,uint32_t expected,uint32_t observed)
{
    if(!r->Result){
        r->Result=result;r->FailureAddress=address;r->Expected=expected;r->Observed=observed;
    }
    return result;
}
void H40Initialize(H40_RECORD *r)
{
    memset(r,0,sizeof(*r));
    r->Size=sizeof(*r);r->Version=1;
}
static int Read(H40_RECORD *r,const H40_IO *io,uint32_t address,uint32_t *value)
{
    ++r->Reads;
    if(io->Read(io->Context,address,value)){
        Fail(r,H40_IO_ERROR,address,0,0);return 0;
    }
    return 1;
}
static int Gate(uint32_t value)
{
    return value&0x100000u?!!(value&0x200000u):!(value&0x10000000u);
}
int H40ClockRouteValid(const uint32_t *v)
{
    /* Exact observed route: 26 MHz * 400 / 13, then /2, /2, /1. */
    static const struct {uint32_t Index,Mask,Value;} route[]={
        {0,0xa3ff3f97u,0xa1900d10u},{1,0x10001u,1},{2,0x10001u,1},{3,0x1000fu,1},
        {5,0x90u,0x10},{6,0x10001u,0},{7,0x10001u,1},{9,0x1000fu,0}
    };
    static const unsigned char gates[]={4,8,10,11,12};
    unsigned i;
    for(i=0;i<sizeof(route)/sizeof(route[0]);++i)
        if((v[route[i].Index]&route[i].Mask)!=route[i].Value)return 0;
    if((v[13]&3u)!=2)return 0;
    for(i=0;i<sizeof(gates);++i)if(!Gate(v[gates[i]]))return 0;
    return 1;
}
static int ClockRoute(H40_RECORD *r,const uint32_t *v)
{
    if(!H40ClockRouteValid(v))return 0;
    r->InputClockHz=200000000;return 1;
}
static int Infrastructure(H40_RECORD *r,const H40_IO *io,unsigned pass)
{
    unsigned i;
    r->ClockValid[pass]=0;r->PinValid[pass]=0;
    for(i=0;i<H40_CLOCK_WORDS;++i){
        if(!Read(r,io,H40ClockAddresses[i],&r->Clocks[pass][i]))return 0;
        r->ClockValid[pass]|=1u<<i;
        if(pass&&r->Clocks[pass][i]!=r->Clocks[0][i]){
            Fail(r,H40_DRIFT,H40ClockAddresses[i],r->Clocks[0][i],r->Clocks[pass][i]);return 0;
        }
    }
    if(!ClockRoute(r,r->Clocks[pass])){Fail(r,H40_CLOCK_ERROR,0,200000000,0);return 0;}
    r->Flags|=H40_CLOCK_VALID;
    for(i=0;i<H40_PIN_WORDS;++i){
        uint32_t expected=i==0||i==3?2u:0u;
        if(!Read(r,io,H40PinAddresses[i],&r->Pins[pass][i]))return 0;
        r->PinValid[pass]|=1u<<i;
        if((r->Pins[pass][i]&15u)!=expected){
            Fail(r,H40_PIN_ERROR,H40PinAddresses[i],expected,r->Pins[pass][i]);return 0;
        }
        if(pass&&r->Pins[pass][i]!=r->Pins[0][i]){
            Fail(r,H40_DRIFT,H40PinAddresses[i],r->Pins[0][i],r->Pins[pass][i]);return 0;
        }
    }
    r->Flags|=H40_PINS_VALID;return 1;
}
static int Controller(H40_RECORD *r,const H40_IO *io,uint32_t *values,uint32_t *valid,int prepared)
{
    unsigned i;
    for(i=0;i<H40_CONTROLLER_WORDS;++i){
        uint32_t expected=r->Before[i];
        if(!Read(r,io,0x14360000+H40ControllerOffsets[i],&values[i]))return 0;
        *valid|=1u<<i;
        if(values==r->Before)continue;
        if(prepared){if(i==0)expected=8;if(i==1||i==7)expected=0;}
        if(values[i]!=expected){
            Fail(r,H40_DRIFT,0x14360000+H40ControllerOffsets[i],expected,values[i]);return 0;
        }
    }
    return 1;
}
int H40InitialControllerValid(const uint32_t *v)
{
    static const unsigned char indices[]={2,3,4,5,6,8,13,14,15,16,18,19};
    static const uint32_t values[]={0xffffff,0,0,0x01000100,0x980120ff,0xff,
        0x01f0ff00,0x030003e0,0x00210000,0,0,1};
    uint32_t trigger=v[1]>>16;
    unsigned i;
    if((v[0]&~0xc0u)!=8||(v[1]!=0&&(trigger>8||v[1]!=(3u|(trigger<<16)|(trigger<<4))))||
       (v[7]&~0x3ffffu)||(v[9]&0x30000u))return 0;
    for(i=0;i<sizeof(indices);++i)if(v[indices[i]]!=values[i])return 0;
    return 1;
}
static int Write(H40_RECORD *r,const H40_IO *io,unsigned index,uint32_t value)
{
    uint32_t address=0x14360000+H40ControllerOffsets[Indices[index]],observed=0;
    ++r->Writes;
    if(io->Write(io->Context,address,value)){
        Fail(r,H40_WRITE_ERROR,address,value,0);return 0;
    }
    ++r->WriteReturns;
    if(!Read(r,io,address,&observed))return 0;
    if(observed!=value){Fail(r,H40_WRITE_ERROR,address,value,observed);return 0;}
    return 1;
}
uint32_t H40AbortBeforePublication(H40_RECORD *r,const H40_IO *io)
{
    unsigned i;int failed=0;uint32_t value=0;
    if(!io||!io->Read||!io->Write||r->BeforeValid!=0xfffff||
       !(r->Flags&H40_IDLE_VALID)||(r->Flags&H40_PUBLISHED))
        return Fail(r,H40_ORDER_ERROR,0,0,r->Flags);
    r->Stage=5;
    if(!Infrastructure(r,io,1))return H40_RESTORE_ERROR;
    if(!Read(r,io,0x14360044,&value)||(value&0x80000000u)||
       !Read(r,io,0x14360050,&value)||(value&0x30000u)||
       !Read(r,io,0x14360000,&value)||(value&0x80000000u)||
       !Read(r,io,0x14360020,&value)||value)
        return Fail(r,H40_RESTORE_ERROR,0,0,value);
    for(i=3;i>0;--i){
        unsigned n=i-1;
        if(!(r->PotentialMask&(1u<<n)))continue;
        if(Write(r,io,n,r->Before[Indices[n]])){
            r->PotentialMask&=~(1u<<n);r->RestoredMask|=1u<<n;
        }else failed=1;
    }
    if(!Controller(r,io,r->Restored,&r->RestoredValid,0))failed=1;
    if(failed||r->PotentialMask)return Fail(r,H40_RESTORE_ERROR,0,0,r->PotentialMask);
    r->Flags=(r->Flags&~H40_HELD)|H40_RESTORED;
    return H40_OK;
}
uint32_t H40Prepare(H40_RECORD *r,const H40_IO *io,uint32_t enabled)
{
    unsigned i;
    if(!enabled)return H40_DISABLED;
    if(enabled!=1||r->Size!=sizeof(*r)||r->Version!=1||r->Stage||!io||!io->Authorize||!io->Read||!io->Write)
        return Fail(r,H40_ORDER_ERROR,0,0,enabled);
    r->Stage=1;
    if(io->Authorize(io->Context)!=1)return Fail(r,H40_AUTHORITY,0,1,0);
    if(!Infrastructure(r,io,0))return r->Result;
    r->Stage=2;
    if(!Controller(r,io,r->Before,&r->BeforeValid,0))return r->Result;
    if(!H40InitialControllerValid(r->Before))return Fail(r,H40_CONTROLLER_ERROR,0,0,0);
    r->Flags|=H40_IDLE_VALID;r->Stage=3;
    for(i=0;i<3;++i){
        r->PotentialMask|=1u<<i;
        if(!Write(r,io,i,Values[i]))goto rollback;
    }
    if(!Controller(r,io,r->Prepared,&r->PreparedValid,1))goto rollback;
    if(!Infrastructure(r,io,1))return r->Result;
    r->Flags|=H40_HELD;r->Stage=4;
    return H40_OK;
rollback:
    (void)H40AbortBeforePublication(r,io);
    return r->Result;
}
uint32_t H40CommitPublication(H40_RECORD *r)
{
    if(r->Result||r->Stage!=4||!(r->Flags&H40_HELD)||(r->Flags&H40_PUBLISHED))
        return Fail(r,H40_ORDER_ERROR,0,4,r->Stage);
    r->Flags|=H40_PUBLISHED;
    return H40_OK;
}
