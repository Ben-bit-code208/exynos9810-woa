/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include "Hsi40Census.h"
#include <string.h>

const uint32_t H40CensusPinAddresses[H40_CENSUS_PIN_WORDS]={
    0x14220180,0x14220184,0x14220188,0x1422018c,
    0x142201a0,0x142201a4,0x142201a8,0x142201ac
};
/* Keep the repeated first-failure serializer shared in the fixed-size FV. */
__attribute__((noinline))
static uint32_t Fail(H40_CENSUS_RECORD *r,uint32_t result,uint32_t address,uint32_t expected,uint32_t observed)
{
    if(!r->Result){r->Result=result;r->FailureAddress=address;r->Expected=expected;r->Observed=observed;}
    return result;
}
void H40CensusInitialize(H40_CENSUS_RECORD *r)
{
    memset(r,0,sizeof(*r));r->Size=sizeof(*r);r->Version=H40_CENSUS_VERSION;
}
static int Sample(H40_CENSUS_RECORD *r,const H40_CENSUS_IO *io,const uint32_t *addresses,
    uint32_t base,unsigned count,uint32_t *values,uint32_t *valid,const uint32_t *before,uint32_t *diff)
{
    unsigned i;
    for(i=0;i<count;++i){
        uint32_t address=base+addresses[i];
        ++r->Reads;
        if(io->Read(io->Context,address,&values[i])){
            Fail(r,H40_IO_ERROR,address,0,0);return 0;
        }
        *valid|=1u<<i;
        if(before&&values[i]!=before[i])*diff|=1u<<i;
    }
    if(before&&*diff){
        for(i=0;i<count;++i)if(*diff&(1u<<i)){
            Fail(r,H40_DRIFT,base+addresses[i],before[i],values[i]);break;
        }
        return 0;
    }
    return 1;
}
uint32_t H40CensusCollect(H40_CENSUS_RECORD *r,const H40_CENSUS_IO *io,uint32_t enabled)
{
    unsigned pass;
    if(!enabled)return H40_DISABLED;
    if(!r)return H40_ORDER_ERROR;
    if(enabled!=1||r->Size!=sizeof(*r)||r->Version!=H40_CENSUS_VERSION||r->Stage||
       !io||!io->Authorize||!io->Read)return Fail(r,H40_ORDER_ERROR,0,1,enabled);
    r->Stage=1;
    if(io->Authorize(io->Context)!=1)return Fail(r,H40_AUTHORITY,0,1,0);
    for(pass=0;pass<2;++pass){
        r->Stage=2+pass;
        if(!Sample(r,io,H40ClockAddresses,0,H40_CLOCK_WORDS,r->Clocks[pass],&r->ClockValid[pass],
                   pass?r->Clocks[0]:NULL,&r->ClockDiff))return r->Result;
        if(!H40ClockRouteValid(r->Clocks[pass]))return Fail(r,H40_CLOCK_ERROR,0,200000000,0);
        r->InputClockHz=200000000;
        r->Flags|=pass?H40_CENSUS_CLOCK_AFTER:H40_CENSUS_CLOCK_BEFORE;
        ++r->Reads;
        if(io->Read(io->Context,H40_USI_SELECTOR_ADDRESS,&r->UsiSelector[pass]))
            return Fail(r,H40_IO_ERROR,H40_USI_SELECTOR_ADDRESS,0,0);
        r->UsiValidMask|=1u<<pass;
        if(pass&&r->UsiSelector[pass]!=r->UsiSelector[0]){
            r->UsiDiff=1;
            return Fail(r,H40_DRIFT,H40_USI_SELECTOR_ADDRESS,r->UsiSelector[0],r->UsiSelector[pass]);
        }
        if(!Sample(r,io,H40CensusPinAddresses,0,H40_CENSUS_PIN_WORDS,r->Pins[pass],&r->PinValid[pass],
                   pass?r->Pins[0]:NULL,&r->PinDiff))return r->Result;
        if(!Sample(r,io,H40ControllerOffsets,0x14360000,H40_CONTROLLER_WORDS,
                   r->Controller[pass],&r->ControllerValid[pass],pass?r->Controller[0]:NULL,&r->ControllerDiff))
            return r->Result;
    }
    r->Flags|=H40_CENSUS_PAIRED;
    if(r->UsiSelector[0]==4&&H40InitialControllerValid(r->Controller[0]))r->Flags|=H40_CENSUS_IDLE_SHAPE;
    if((r->Pins[0][0]&15)==2&&(r->Pins[0][4]&15)==2&&
       !(r->Pins[0][2]&15)&&!(r->Pins[0][3]&15)&&!(r->Pins[0][6]&15)&&!(r->Pins[0][7]&15))
        r->Flags|=H40_CENSUS_PIN_FUNCTION;
    if((r->Pins[0][1]&1)&&(r->Pins[0][5]&1))r->Flags|=H40_CENSUS_PIN_DATA_HIGH;
    r->Stage=4;
    return H40_OK;
}
