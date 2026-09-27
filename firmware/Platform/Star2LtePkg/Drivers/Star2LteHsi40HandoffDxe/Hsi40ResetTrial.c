/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include "Hsi40ColdInit.h"
#include <string.h>

typedef struct {H40_COLD_RECORD *r;const H40_COLD_IO *io;int restoring;} RESET;
static int Failed(RESET *c,uint32_t result,uint32_t address,uint32_t expected,uint32_t observed)
{
    uint32_t *code=c->restoring?&c->r->RestoreResult:&c->r->Result;
    if(!*code){
        *code=result;
        if(c->restoring){c->r->RestoreAddress=address;c->r->RestoreExpected=expected;c->r->RestoreObserved=observed;}
        else{c->r->FailureAddress=address;c->r->Expected=expected;c->r->Observed=observed;}
    }
    return 0;
}
static int Owned(void *opaque)
{
    RESET *c=opaque;
    if(!(c->r->Flags&(H40_COLD_AUTHORITY_LOST|H40_COLD_INFRASTRUCTURE_LOST))&&
       c->io->Authorize(c->io->Context)==1)return 1;
    if(c->r->Flags&H40_COLD_TOUCHED)c->r->Flags|=H40_COLD_AUTHORITY_LOST|H40_COLD_QUARANTINED;
    return Failed(c,H40_AUTHORITY,0,1,0);
}
static int Read(void *opaque,uint32_t address,uint32_t *value)
{
    RESET *c=opaque;
    if(!Owned(c))return -1;
    ++c->r->Reads;
    if(!c->io->Read(c->io->Context,address,value))return 0;
    Failed(c,H40_IO_ERROR,address,0,0);return -1;
}
static int Drift(RESET *c,uint32_t address,uint32_t expected,uint32_t observed)
{
    if(c->r->Flags&H40_COLD_TOUCHED)c->r->Flags|=H40_COLD_INFRASTRUCTURE_LOST|H40_COLD_QUARANTINED;
    return Failed(c,H40_DRIFT,address,expected,observed);
}
static int Snapshot(RESET *c,H40_CENSUS_RECORD *s)
{
    H40_CENSUS_IO io={c,Owned,Read};unsigned i;uint32_t result;
    H40CensusInitialize(s);result=H40CensusCollect(s,&io,1);
    if(result!=H40_OK){
        if(result==H40_DRIFT||result==H40_CLOCK_ERROR)
            return Drift(c,s->FailureAddress,s->Expected,s->Observed);
        return Failed(c,result,s->FailureAddress,s->Expected,s->Observed);
    }
    for(i=0;i<8;++i){
        uint32_t expected=i==0||i==1||i==4||i==5?1u:i==3||i==7?H40_RESET_PIN_DRIVE:0u;
        if(s->Pins[0][i]!=expected)return Drift(c,H40CensusPinAddresses[i],expected,s->Pins[0][i]);
    }
    if((c->r->Flags&H40_COLD_BEFORE_VALID)&&memcmp(s->Clocks,c->r->Before.Clocks,sizeof(s->Clocks)))
        return Drift(c,0,0,0);
    return 1;
}
static int Step(RESET *c,uint32_t address,uint32_t value,uint32_t selector,uint32_t reset,uint32_t potential,int poll)
{
    H40_CENSUS_RECORD s;unsigned i,j;uint32_t observed=0;
    if(!Snapshot(c,&s))return 0;
    if(s.UsiSelector[0]!=selector)return Drift(c,H40_USI_SELECTOR_ADDRESS,selector,s.UsiSelector[0]);
    if(s.Controller[0][18]!=reset)return Drift(c,0x143600c4,reset,s.Controller[0][18]);
    if(!reset){
        const unsigned indices[]={3,7,9};const uint32_t masks[]={0xffffffffu,0x80000000u,0x30000u};
        for(i=0;i<3;++i)if(s.Controller[0][indices[i]]&masks[i])
            return Failed(c,H40_CONTROLLER_ERROR,0x14360000+H40ControllerOffsets[indices[i]],0,
                          s.Controller[0][indices[i]]&masks[i]);
    }
    if(address==0x14360000){
        uint32_t expected=(value&0x80000000u)?H40_RESET_CONTROL:(H40_RESET_CONTROL|0x80000000u);
        if(s.Controller[0][0]!=expected)return Failed(c,H40_CONTROLLER_ERROR,address,expected,s.Controller[0][0]);
    }
    if(!Owned(c))return 0;
    c->r->Flags|=H40_COLD_TOUCHED;c->r->PotentialMask|=potential;++c->r->Writes;
    if(c->io->Write(c->io->Context,address,value))return Failed(c,H40_WRITE_ERROR,address,value,0);
    ++c->r->WriteReturns;
    for(i=0;i<(poll?32u:1u);++i){
        for(j=0;j<14;++j){
            if(Read(c,H40ClockAddresses[j],&observed))return 0;
            if(observed!=c->r->Before.Clocks[0][j])return Drift(c,H40ClockAddresses[j],c->r->Before.Clocks[0][j],observed);
        }
        if(poll)++c->r->ResetPolls;
        if(Read(c,address,&observed))return 0;
        if(observed==value)return 1;
        if(poll&&i<31){
            ++c->r->Stalls;
            if(!Owned(c)||c->io->StallOneUs(c->io->Context))
                return Failed(c,H40_IO_ERROR,address,value,observed);
        }
    }
    return Failed(c,poll?H40_COLD_RESET_TIMEOUT:H40_WRITE_ERROR,address,value,observed);
}
uint32_t H40ColdRestoreReset(H40_COLD_RECORD *r,const H40_COLD_IO *io)
{
    RESET c={r,io,1};H40_CENSUS_RECORD s;
    if(!r||!io||!io->Authorize||!io->Read||!io->Write||!io->StallOneUs||
       !(r->Flags&H40_COLD_BEFORE_VALID)||(r->Flags&(H40_COLD_HELD|H40_COLD_PUBLISHED))||
       (r->PotentialMask&~7u))return H40_ORDER_ERROR;
    if(r->Flags&(H40_COLD_AUTHORITY_LOST|H40_COLD_INFRASTRUCTURE_LOST))goto quarantine;
    r->Stage=10;r->RestoreResult=r->RestoreAddress=r->RestoreExpected=r->RestoreObserved=0;
    if(!Snapshot(&c,&s))goto quarantine;
    if((s.UsiSelector[0]!=r->Before.UsiSelector[0]&&s.UsiSelector[0]!=4)||
       ((r->PotentialMask&6u)&&s.UsiSelector[0]!=4)){
        Drift(&c,H40_USI_SELECTOR_ADDRESS,4,s.UsiSelector[0]);goto quarantine;
    }
    if(s.Controller[0][18]>1){Drift(&c,0x143600c4,1,s.Controller[0][18]);goto quarantine;}
    if(r->PotentialMask&6u){
        if(!Step(&c,0x143600c4,1,4,s.Controller[0][18],0,1))goto quarantine;
        r->PotentialMask&=~6u;
    }
    if(r->PotentialMask&1u){
        if(!Step(&c,H40_USI_SELECTOR_ADDRESS,r->Before.UsiSelector[0],s.UsiSelector[0],1,0,0))goto quarantine;
        r->PotentialMask&=~1u;
    }
    if(!Snapshot(&c,&r->Restored))goto quarantine;
    if(memcmp(r->Before.Controller,r->Restored.Controller,sizeof(r->Before.Controller))||
       memcmp(r->Before.UsiSelector,r->Restored.UsiSelector,sizeof(r->Before.UsiSelector))){
        Failed(&c,H40_RESTORE_ERROR,0,0,0);goto quarantine;
    }
    r->Flags=(r->Flags&~H40_COLD_QUARANTINED)|H40_COLD_RESTORED;
    return H40_OK;
quarantine:
    r->Flags|=H40_COLD_QUARANTINED;
    if(!r->RestoreResult)r->RestoreResult=H40_RESTORE_ERROR;
    return H40_RESTORE_ERROR;
}
uint32_t H40ColdObserveReset(H40_COLD_RECORD *r,const H40_COLD_IO *io,uint32_t enabled)
{
    RESET c={r,io,0};unsigned i;
    if(!enabled)return H40_DISABLED;
    if(!r||enabled!=1||r->Size!=sizeof(*r)||r->Version!=1||r->Stage||
       !io||!io->Authorize||!io->Read||!io->Write||!io->StallOneUs)return H40_ORDER_ERROR;
    r->Stage=1;
    if(!Snapshot(&c,&r->Before))return r->Result;
    if(r->Before.UsiSelector[0]!=0&&r->Before.UsiSelector[0]!=4){
        Failed(&c,H40_CONTROLLER_ERROR,H40_USI_SELECTOR_ADDRESS,4,r->Before.UsiSelector[0]);return r->Result;
    }
    for(i=0;i<20;++i)if(r->Before.Controller[0][i]!=(i>=18?1u:0u)){
        Failed(&c,H40_CONTROLLER_ERROR,0x14360000+H40ControllerOffsets[i],i>=18?1u:0u,r->Before.Controller[0][i]);
        return r->Result;
    }
    r->Flags|=H40_COLD_BEFORE_VALID;r->Stage=2;
    if(r->Before.UsiSelector[0]!=4&&!Step(&c,H40_USI_SELECTOR_ADDRESS,4,0,1,1,0))goto rollback;
    r->Stage=3;
    if(!Step(&c,0x143600c4,0,4,1,2,1))goto rollback;
    r->Stage=4;
    if(!Step(&c,0x14360000,H40_RESET_CONTROL|0x80000000u,4,0,4,0)||
       !Step(&c,0x14360000,H40_RESET_CONTROL,4,0,4,1))goto rollback;
    r->Stage=5;
    if(!Snapshot(&c,&r->After))goto rollback;
    if(r->After.UsiSelector[0]!=4){
        Drift(&c,H40_USI_SELECTOR_ADDRESS,4,r->After.UsiSelector[0]);goto rollback;
    }
    for(i=0;i<=18;i+=18)if(r->After.Controller[0][i]!=(i==0?H40_RESET_CONTROL:0u)){
        Drift(&c,0x14360000+H40ControllerOffsets[i],i==0?H40_RESET_CONTROL:0u,r->After.Controller[0][i]);goto rollback;
    }
    memcpy(r->Reset,r->After.Controller[0],sizeof(r->Reset));r->ResetValid=0xfffff;
    r->Flags|=H40_COLD_RESET_OBSERVED;return H40_OK;
rollback:
    if(r->Flags&H40_COLD_TOUCHED)(void)H40ColdRestoreReset(r,io);
    return r->Result;
}
