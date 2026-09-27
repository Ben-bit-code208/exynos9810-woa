/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include "Hsi40ColdInit.h"
#include <string.h>

#define SELECTOR_BIT 1u
#define USI_BIT 2u
#define CONTROLLER_BIT 4u
#define SCL_BIT 8u
#define SDA_BIT 16u
typedef struct {
    H40_COLD_RECORD *Record;
    const H40_COLD_IO *Io;
    int Restoring;
} COLD_CONTEXT;
static uint32_t Fail(COLD_CONTEXT *c,uint32_t code,uint32_t address,uint32_t expected,uint32_t observed)
{
    H40_COLD_RECORD *r=c->Record;
    if(c->Restoring){
        if(!r->RestoreResult){
            r->RestoreResult=code;r->RestoreAddress=address;
            r->RestoreExpected=expected;r->RestoreObserved=observed;
        }
    }else if(!r->Result){
        r->Result=code;r->FailureAddress=address;r->Expected=expected;r->Observed=observed;
    }
    return code;
}
static int Owned(COLD_CONTEXT *c)
{
    if(c->Io->Authorize(c->Io->Context)==1)return 1;
    if(c->Record->Flags&H40_COLD_TOUCHED)
        c->Record->Flags|=H40_COLD_AUTHORITY_LOST|H40_COLD_QUARANTINED;
    Fail(c,H40_AUTHORITY,0,1,0);return 0;
}
static int Read(COLD_CONTEXT *c,uint32_t address,uint32_t *value)
{
    if(!Owned(c))return 0;
    ++c->Record->Reads;
    if(c->Io->Read(c->Io->Context,address,value)){
        Fail(c,H40_IO_ERROR,address,0,0);return 0;
    }
    return 1;
}
static int CensusAuthorize(void *opaque){return Owned(opaque);}
static int CensusRead(void *opaque,uint32_t address,uint32_t *value)
{
    return Read(opaque,address,value)?0:-1;
}
static int Census(COLD_CONTEXT *c,H40_CENSUS_RECORD *record)
{
    const H40_CENSUS_IO io={c,CensusAuthorize,CensusRead};
    uint32_t result;
    H40CensusInitialize(record);
    result=H40CensusCollect(record,&io,1);
    if(result!=H40_OK){
        Fail(c,result,record->FailureAddress,record->Expected,record->Observed);return 0;
    }
    return 1;
}
static int ClockFence(COLD_CONTEXT *c)
{
    uint32_t values[H40_CLOCK_WORDS];unsigned i;
    for(i=0;i<H40_CLOCK_WORDS;++i){
        if(!Read(c,H40ClockAddresses[i],&values[i]))return 0;
        if(values[i]!=c->Record->Before.Clocks[0][i]){
            if(c->Record->Flags&H40_COLD_TOUCHED)c->Record->Flags|=H40_COLD_INFRASTRUCTURE_LOST;
            Fail(c,H40_DRIFT,H40ClockAddresses[i],c->Record->Before.Clocks[0][i],values[i]);return 0;
        }
    }
    if(!H40ClockRouteValid(values)){
        if(c->Record->Flags&H40_COLD_TOUCHED)c->Record->Flags|=H40_COLD_INFRASTRUCTURE_LOST;
        Fail(c,H40_CLOCK_ERROR,0,200000000,0);return 0;
    }
    return 1;
}
static int PinsFence(COLD_CONTEXT *c,int allowPrepared)
{
    unsigned i;
    for(i=0;i<H40_CENSUS_PIN_WORDS;++i){
        uint32_t value=0,expected=c->Record->Before.Pins[0][i];
        if(!Read(c,H40CensusPinAddresses[i],&value))return 0;
        uint32_t bit=i==0?SCL_BIT:i==4?SDA_BIT:0;
        if(value!=expected&&!(allowPrepared&&bit&&(c->Record->PotentialMask&bit)&&value==2)){
            if(c->Record->Flags&H40_COLD_TOUCHED)c->Record->Flags|=H40_COLD_INFRASTRUCTURE_LOST;
            Fail(c,H40_DRIFT,H40CensusPinAddresses[i],expected,value);return 0;
        }
    }
    return 1;
}
static int ProtocolFence(COLD_CONTEXT *c,uint32_t target,uint32_t targetValue)
{
    uint32_t selector=0,usi=0,expected;
    if(!Read(c,H40_USI_SELECTOR_ADDRESS,&selector)||!Read(c,0x143600c4,&usi))return 0;
    expected=c->Restoring?c->Record->Before.UsiSelector[0]:
        c->Record->Stage==2?c->Record->Before.UsiSelector[0]:4;
    if((selector!=expected&&!(c->Restoring&&selector==4))||
       (c->Restoring&&selector!=4&&(c->Record->PotentialMask&(USI_BIT|CONTROLLER_BIT|SCL_BIT|SDA_BIT)))){
        if(c->Record->Flags&H40_COLD_TOUCHED)c->Record->Flags|=H40_COLD_INFRASTRUCTURE_LOST;
        Fail(c,H40_DRIFT,H40_USI_SELECTOR_ADDRESS,expected,selector);return 0;
    }
    expected=(target==H40_USI_SELECTOR_ADDRESS||(target==0x143600c4&&targetValue==0))?1u:0u;
    if(usi>1||(!c->Restoring&&usi!=expected)||(target==H40_USI_SELECTOR_ADDRESS&&usi!=1)||
       (c->Restoring&&(c->Record->PotentialMask&(SCL_BIT|SDA_BIT))&&usi!=0)){
        if(c->Record->Flags&H40_COLD_TOUCHED)c->Record->Flags|=H40_COLD_INFRASTRUCTURE_LOST;
        Fail(c,H40_DRIFT,0x143600c4,expected,usi);return 0;
    }
    return 1;
}
static int Write(COLD_CONTEXT *c,uint32_t address,uint32_t value,uint32_t potential,int poll)
{
    unsigned i;uint32_t observed=0;
    if(!ClockFence(c)||!PinsFence(c,1)||!ProtocolFence(c,address,value)||!Owned(c))return 0;
    c->Record->PotentialMask|=potential;c->Record->Flags|=H40_COLD_TOUCHED;
    ++c->Record->Writes;
    if(c->Io->Write(c->Io->Context,address,value)){
        Fail(c,H40_WRITE_ERROR,address,value,0);return 0;
    }
    ++c->Record->WriteReturns;
    for(i=0;i<(poll?32u:1u);++i){
        if(!ClockFence(c))return 0;
        if(poll)++c->Record->ResetPolls;
        if(!Read(c,address,&observed))return 0;
        if(observed==value)return 1;
        if(poll&&i+1<32){
            ++c->Record->Stalls;
            if(c->Io->StallOneUs(c->Io->Context)){
                Fail(c,H40_IO_ERROR,address,value,observed);return 0;
            }
        }
    }
    Fail(c,poll?H40_COLD_RESET_TIMEOUT:H40_WRITE_ERROR,address,value,observed);return 0;
}
static int ReadController(COLD_CONTEXT *c,uint32_t *values,uint32_t *valid)
{
    unsigned i;
    *valid=0;
    if(!ClockFence(c)||!ProtocolFence(c,0,0))return 0;
    for(i=0;i<H40_CONTROLLER_WORDS;++i){
        if(!Read(c,0x14360000+H40ControllerOffsets[i],&values[i]))return 0;
        *valid|=1u<<i;
    }
    return 1;
}
static int ColdShape(COLD_CONTEXT *c)
{
    const H40_CENSUS_RECORD *b=&c->Record->Before;unsigned i;
    if(b->UsiSelector[0]!=0&&b->UsiSelector[0]!=4){
        Fail(c,H40_CONTROLLER_ERROR,H40_USI_SELECTOR_ADDRESS,4,b->UsiSelector[0]);return 0;
    }
    for(i=0;i<H40_CENSUS_PIN_WORDS;++i){
        uint32_t expected=(i==0||i==1||i==4||i==5)?1u:
            H40_COLD_WIN11&&(i==3||i==7)?2u:0u;
        if(b->Pins[0][i]!=expected){
            Fail(c,H40_PIN_ERROR,H40CensusPinAddresses[i],expected,b->Pins[0][i]);return 0;
        }
    }
    for(i=0;i<H40_CONTROLLER_WORDS;++i){
        uint32_t expected=i==18||i==19?1u:0u;
        if(b->Controller[0][i]!=expected){
            Fail(c,H40_CONTROLLER_ERROR,0x14360000+H40ControllerOffsets[i],expected,b->Controller[0][i]);return 0;
        }
    }
    return 1;
}
static int Canonical(COLD_CONTEXT *c,const uint32_t *values)
{
    static const unsigned indices[]={0,1,2,3,4,5,6,7,8,13,14,15,16,18,19};
    static const uint32_t expected[]={8,0,0xffffff,0,0,0x01000100,0x980120ff,0,0xff,
        0x01f0ff00,0x030003e0,0x00210000,0,0,1};
    unsigned i;
    for(i=0;i<sizeof(indices)/sizeof(indices[0]);++i){
        unsigned index=indices[i];
        if(values[index]!=expected[i]){
            Fail(c,H40_CONTROLLER_ERROR,0x14360000+H40ControllerOffsets[index],expected[i],values[index]);return 0;
        }
    }
    if(values[9]&0x30000u){Fail(c,H40_CONTROLLER_ERROR,0x14360050,0,values[9]&0x30000u);return 0;}
#if H40_COLD_WIN11
    for(i=0;i<H40_CONTROLLER_WORDS;++i){
        if(i==0||i==1||i==2||i==6||i==7||i==8||i==13||i==14||i==15)continue;
        if(values[i]!=c->Record->Reset[i]){
            Fail(c,H40_DRIFT,0x14360000+H40ControllerOffsets[i],c->Record->Reset[i],values[i]);return 0;
        }
    }
#endif
    return 1;
}
void H40ColdInitialize(H40_COLD_RECORD *r)
{
    memset(r,0,sizeof(*r));r->Size=sizeof(*r);r->Version=1;
}
uint32_t H40ColdAbortBeforePublication(H40_COLD_RECORD *r,const H40_COLD_IO *io)
{
    COLD_CONTEXT c={r,io,1};
    uint32_t selector=0,usi=0,value=0;unsigned i;
    if(!r||!io||!io->Authorize||!io->Read||!io->Write||!io->StallOneUs)return H40_ORDER_ERROR;
    if(!(r->Flags&H40_COLD_BEFORE_VALID)||(r->Flags&H40_COLD_PUBLISHED))return H40_ORDER_ERROR;
    if(r->Flags&(H40_COLD_AUTHORITY_LOST|H40_COLD_INFRASTRUCTURE_LOST)){
        r->Flags|=H40_COLD_QUARANTINED;return H40_RESTORE_ERROR;
    }
    r->Stage=10;r->RestoreResult=0;r->RestoreAddress=r->RestoreExpected=r->RestoreObserved=0;
    if(!ClockFence(&c)||!PinsFence(&c,1))goto quarantine;
    if(!Read(&c,H40_USI_SELECTOR_ADDRESS,&selector)||!Read(&c,0x143600c4,&usi))goto quarantine;
    if((selector!=0&&selector!=4)||usi>1){Fail(&c,H40_RESTORE_ERROR,H40_USI_SELECTOR_ADDRESS,0,selector);goto quarantine;}
    if(selector==4&&usi==0){
        if(!Read(&c,0x14360020,&value)||value){Fail(&c,H40_RESTORE_ERROR,0x14360020,0,value);goto quarantine;}
        if(!Read(&c,0x14360044,&value)||(value&0x80000000u)){Fail(&c,H40_RESTORE_ERROR,0x14360044,0,value);goto quarantine;}
        if(!Read(&c,0x14360050,&value)||(value&0x30000u)){Fail(&c,H40_RESTORE_ERROR,0x14360050,0,value);goto quarantine;}
    }
    for(i=2;i>0;--i){
        uint32_t bit=i==2?SDA_BIT:SCL_BIT,address=i==2?0x142201a0:0x14220180;
        if(!(r->PotentialMask&bit))continue;
        if(!Write(&c,address,1,0,0))goto quarantine;
        r->PotentialMask&=~bit;
    }
    if(!PinsFence(&c,0))goto quarantine;
    if(r->PotentialMask&(USI_BIT|CONTROLLER_BIT)){
        if(!Write(&c,0x143600c4,1,0,1))goto quarantine;
        r->PotentialMask&=~(USI_BIT|CONTROLLER_BIT);
    }
    if(r->PotentialMask&SELECTOR_BIT){
        if(!Write(&c,H40_USI_SELECTOR_ADDRESS,r->Before.UsiSelector[0],0,0))goto quarantine;
        r->PotentialMask&=~SELECTOR_BIT;
    }
    if(!Census(&c,&r->Restored))goto quarantine;
    if(memcmp(r->Before.Clocks,r->Restored.Clocks,sizeof(r->Before.Clocks))||
       memcmp(r->Before.Pins,r->Restored.Pins,sizeof(r->Before.Pins))||
       memcmp(r->Before.Controller,r->Restored.Controller,sizeof(r->Before.Controller))||
       memcmp(r->Before.UsiSelector,r->Restored.UsiSelector,sizeof(r->Before.UsiSelector))){
        Fail(&c,H40_RESTORE_ERROR,0,0,0);goto quarantine;
    }
    r->Flags=(r->Flags&~(H40_COLD_HELD|H40_COLD_QUARANTINED))|H40_COLD_RESTORED;
    return H40_OK;
quarantine:
    r->Flags|=H40_COLD_QUARANTINED;
    if(!r->RestoreResult)r->RestoreResult=H40_RESTORE_ERROR;
    return H40_RESTORE_ERROR;
}
uint32_t H40ColdPrepare(H40_COLD_RECORD *r,const H40_COLD_IO *io,uint32_t enabled)
{
    COLD_CONTEXT c={r,io,0};
    uint32_t current[H40_CONTROLLER_WORDS],valid=0;unsigned i;
    if(!enabled)return H40_DISABLED;
    if(!r)return H40_ORDER_ERROR;
    if(enabled!=1||r->Size!=sizeof(*r)||r->Version!=1||r->Stage||
       !io||!io->Authorize||!io->Read||!io->Write||!io->StallOneUs)
        return Fail(&c,H40_ORDER_ERROR,0,1,enabled);
    r->Stage=1;
    if(!Census(&c,&r->Before)||!ColdShape(&c))return r->Result;
    r->Flags|=H40_COLD_BEFORE_VALID;
    if(!ClockFence(&c)||!PinsFence(&c,0))return r->Result;
    r->Stage=2;
    if(r->Before.UsiSelector[0]!=4&&!Write(&c,H40_USI_SELECTOR_ADDRESS,4,SELECTOR_BIT,0))goto rollback;
    r->Stage=3;
    if(!Write(&c,0x143600c4,0,USI_BIT,1))goto rollback;
    if(!ReadController(&c,current,&valid))goto rollback;
    if(current[0]!=(H40_COLD_WIN11?8u:0u)||current[3]||
       (H40_COLD_WIN11?(current[7]&0x80000000u):current[7])||(current[9]&0x30000u)){
        Fail(&c,H40_CONTROLLER_ERROR,0x14360000,H40_COLD_WIN11?8u:0u,current[0]);goto rollback;
    }
    r->Stage=4;
    if(!Write(&c,0x14360000,0x80000000u|(H40_COLD_WIN11?8u:0u),CONTROLLER_BIT,0)||
       !Write(&c,0x14360000,H40_COLD_WIN11?8u:0u,CONTROLLER_BIT,1))goto rollback;
    if(!ReadController(&c,r->Reset,&r->ResetValid))goto rollback;
#if H40_COLD_WIN11
    {
        static const uint32_t measured[20]={8,0x80083,0xffffff,0,0,0x01000100,0x980120ff,0x300ff,0x800000ff,
            0x80001,0x0180ff00,0x030003f8,0x00ff0000,0x0180ff00,0x030003f8,0x00ff0000,0,0xff0fffff,0,1};
        for(i=0;i<20;++i)if(r->Reset[i]!=measured[i]){
            Fail(&c,H40_CONTROLLER_ERROR,0x14360000+H40ControllerOffsets[i],measured[i],r->Reset[i]);goto rollback;
        }
    }
#endif
    r->Stage=5;
#if H40_COLD_WIN11
    if(!Write(&c,0x14360044,0,CONTROLLER_BIT,0)||!Write(&c,0x14360004,0,CONTROLLER_BIT,0)||
       !Write(&c,0x14360048,r->Reset[8]&~0x80000000u,CONTROLLER_BIT,0))goto rollback;
#endif
    if(!Write(&c,0x14360068,(r->Reset[15]&~0x00ff0000u)|0x00210000u,CONTROLLER_BIT,0)||
       !Write(&c,0x14360064,(r->Reset[14]&~0xffu)|0xe0u,CONTROLLER_BIT,0)||
       !Write(&c,0x14360060,(r->Reset[13]&~0x00ff0000u)|0x00f00000u,CONTROLLER_BIT,0))goto rollback;
    r->Stage=6;
#if !H40_COLD_WIN11
    if(!Write(&c,0x14360000,8,CONTROLLER_BIT,0)||
       !Write(&c,0x14360008,0xffffff,CONTROLLER_BIT,0)||
       !Write(&c,0x14360040,r->Reset[6]|0x80000000u,CONTROLLER_BIT,0))goto rollback;
#endif
    if(!ReadController(&c,current,&valid)||!Canonical(&c,current)||!ClockFence(&c)||!PinsFence(&c,0))goto rollback;
    r->Stage=7;
    if(!Write(&c,0x14220180,2,SCL_BIT,0)||!Write(&c,0x142201a0,2,SDA_BIT,0))goto rollback;
    r->Stage=8;
    if(!Census(&c,&r->After)||!Canonical(&c,r->After.Controller[0]))goto rollback;
    for(i=0;i<H40_CENSUS_PIN_WORDS;++i){
        uint32_t expected=(i==0||i==4)?2u:r->Before.Pins[0][i];
        if(r->After.Pins[0][i]!=expected){Fail(&c,H40_DRIFT,H40CensusPinAddresses[i],expected,r->After.Pins[0][i]);goto rollback;}
    }
    if(r->After.UsiSelector[0]!=4||memcmp(r->Before.Clocks,r->After.Clocks,sizeof(r->Before.Clocks))){
        Fail(&c,H40_DRIFT,H40_USI_SELECTOR_ADDRESS,4,r->After.UsiSelector[0]);goto rollback;
    }
    if(!Owned(&c))goto rollback;
    r->Stage=9;r->Flags|=H40_COLD_HELD;
    return H40_OK;
rollback:
    if(r->Flags&H40_COLD_TOUCHED)(void)H40ColdAbortBeforePublication(r,io);
    return r->Result;
}
uint32_t H40ColdCommitPublication(H40_COLD_RECORD *r)
{
    if(!r||r->Result||r->Stage!=9||!(r->Flags&H40_COLD_HELD)||
       (r->Flags&(H40_COLD_PUBLISHED|H40_COLD_QUARANTINED)))return H40_ORDER_ERROR;
    r->Flags|=H40_COLD_PUBLISHED;
    return H40_OK;
}
