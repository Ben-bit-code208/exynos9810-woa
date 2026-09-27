/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include "Hsi40ColdTrial.h"
#include <string.h>

typedef struct {
    H40_COLD_RECORD Record;
    EFI_BOOT_SERVICES *BootServices;
    BOOLEAN Authority,Closed;
} COLD_TRIAL;
static int Authorize(void *opaque)
{
    COLD_TRIAL *c=opaque;
    if(!c->Authority||c->Closed||
       (c->Record.Flags&(H40_COLD_PUBLISHED|H40_COLD_AUTHORITY_LOST|H40_COLD_INFRASTRUCTURE_LOST)))return 0;
#ifdef H40_COLD_HOST_TEST
    {
        extern int H40ColdHostOwned(void);
        if(H40ColdHostOwned()==1)return 1;
        c->Authority=FALSE;
        if(c->Record.Flags&H40_COLD_TOUCHED)
            c->Record.Flags|=H40_COLD_AUTHORITY_LOST|H40_COLD_QUARANTINED;
        return 0;
    }
#else
    return 1;
#endif
}
static int Read(void *opaque,uint32_t address,uint32_t *value)
{
    COLD_TRIAL *c=opaque;UINTN i;
    int clock=0,allowed=address==H40_USI_SELECTOR_ADDRESS;
    if(!Authorize(c))return -1;
    for(i=0;i<H40_CLOCK_WORDS;++i)if(address==H40ClockAddresses[i])clock=allowed=1;
    for(i=0;i<H40_CENSUS_PIN_WORDS;++i)if(address==H40CensusPinAddresses[i])allowed=1;
    for(i=0;i<H40_CONTROLLER_WORDS;++i)if(address==0x14360000+H40ControllerOffsets[i])allowed=1;
    if(!allowed||(!clock&&!(c->Record.Before.Flags&H40_CENSUS_CLOCK_BEFORE)))return -1;
#ifdef H40_COLD_HOST_TEST
    {
        extern int H40ColdHostRead(uint32_t,uint32_t *);
        return H40ColdHostRead(address,value);
    }
#else
    __asm__ volatile("dsb sy" ::: "memory");
    *value=*(volatile UINT32 *)(UINTN)address;
    return 0;
#endif
}
int H40ColdWriteAllowed(const H40_COLD_RECORD *r,uint32_t address,uint32_t value)
{
    if(!r||(r->Flags&H40_COLD_BEFORE_VALID)==0||
       (r->Flags&(H40_COLD_PUBLISHED|H40_COLD_AUTHORITY_LOST|H40_COLD_INFRASTRUCTURE_LOST)))return 0;
    if(r->Stage==10){
#if !H40_COLD_RESET_ONLY
        if(address==0x14220180||address==0x142201a0)return value==1;
#endif
        if(address==0x143600c4)return value==1;
        return address==H40_USI_SELECTOR_ADDRESS&&(r->Before.UsiSelector[0]==0||r->Before.UsiSelector[0]==4)&&
            value==r->Before.UsiSelector[0];
    }
    if(r->Stage==2)return address==H40_USI_SELECTOR_ADDRESS&&value==4;
    if(r->Stage==3)return address==0x143600c4&&value==0;
    if(r->Stage==4)return address==0x14360000&&
        (value==((H40_COLD_RESET_ONLY||H40_COLD_WIN11)?H40_RESET_CONTROL:0u)||
         value==(((H40_COLD_RESET_ONLY||H40_COLD_WIN11)?H40_RESET_CONTROL:0u)|0x80000000u));
#if H40_COLD_RESET_ONLY
    return 0;
#else
    if(r->Stage==5&&r->ResetValid==0xfffff){
#if H40_COLD_WIN11
        if(address==0x14360044||address==0x14360004)return value==0;
        if(address==0x14360048)return value==(r->Reset[8]&~0x80000000u);
#endif
        if(address==0x14360068)return value==((r->Reset[15]&~0x00ff0000u)|0x00210000u);
        if(address==0x14360064)return value==((r->Reset[14]&~0xffu)|0xe0u);
        if(address==0x14360060)return value==((r->Reset[13]&~0x00ff0000u)|0x00f00000u);
    }
    if(!H40_COLD_WIN11&&r->Stage==6&&r->ResetValid==0xfffff){
        if(address==0x14360000)return value==8;
        if(address==0x14360008)return value==0xffffff;
        if(address==0x14360040)return value==(r->Reset[6]|0x80000000u);
    }
    return r->Stage==7&&(address==0x14220180||address==0x142201a0)&&value==2;
#endif
}
static int Write(void *opaque,uint32_t address,uint32_t value)
{
    COLD_TRIAL *c=opaque;
    if(!Authorize(c)||!H40ColdWriteAllowed(&c->Record,address,value))return -1;
#ifdef H40_COLD_HOST_TEST
    {
        extern int H40ColdHostWrite(uint32_t,uint32_t);
        return H40ColdHostWrite(address,value);
    }
#else
    *(volatile UINT32 *)(UINTN)address=value;
    __asm__ volatile("dsb sy" ::: "memory");
    return 0;
#endif
}
static int Stall(void *opaque)
{
    COLD_TRIAL *c=opaque;
    if(!Authorize(c))return -1;
    return c->BootServices->Stall(1)==EFI_SUCCESS?0:-1;
}
static void Put32(UINT8 *p,UINT32 value)
{
    UINTN i;for(i=0;i<4;++i)p[i]=(UINT8)(value>>(8*i));
}
EFI_STATUS H40ColdTrialAndPublish(EFI_DXE_SERVICES *dxe,EFI_BOOT_SERVICES *bs,
    EFI_ACPI_TABLE_PROTOCOL *acpi,CONST UINT8 producer[16],BOOLEAN authority)
{
    COLD_TRIAL c;H40_COLD_IO io={&c,Authorize,Read,Write,Stall};
    EFI_STATUS gcd,status;UINT32 prepared=H40_AUTHORITY,restored=H40_DISABLED;
    UINT8 table[H40_COLD_TRIAL_TABLE_BYTES],sum=0;UINTN i,key=0;
    if(!authority||!producer||!bs||!bs->Stall||!acpi||!acpi->InstallAcpiTable)return EFI_ACCESS_DENIED;
    memset(&c,0,sizeof(c));H40ColdInitialize(&c.Record);c.BootServices=bs;
    gcd=H40PrepareCensusMemory(dxe);c.Authority=gcd==EFI_SUCCESS;
    if(c.Authority){
#if H40_COLD_RESET_ONLY
        prepared=H40ColdObserveReset(&c.Record,&io,1);
#else
        prepared=H40ColdPrepare(&c.Record,&io,1);
#endif
        if(prepared==H40_OK)restored=H40_COLD_RESET_ONLY?
            H40ColdRestoreReset(&c.Record,&io):H40ColdAbortBeforePublication(&c.Record,&io);
        else if(c.Record.Flags&H40_COLD_RESTORED)restored=H40_OK;
        else if(c.Record.Flags&H40_COLD_TOUCHED)restored=H40_RESTORE_ERROR;
    }else c.Record.Result=H40_AUTHORITY;
    c.Closed=TRUE;
    memset(table,0,sizeof(table));
    memcpy(table,H40_COLD_RESET_ONLY?"H4CR":"H4CT",4);Put32(table+4,sizeof(table));table[8]=1;
    memcpy(table+10,"RWOA  ",6);memcpy(table+16,H40_COLD_RESET_ONLY?"H4RESET1":"H4CINIT1",8);
    Put32(table+24,1);Put32(table+28,0x57464847);Put32(table+32,1);
    memcpy(table+36,producer,16);Put32(table+52,(UINT32)gcd);Put32(table+56,(UINT32)(gcd>>32));
    Put32(table+60,prepared);Put32(table+64,restored);
    memcpy(table+68,&c.Record,sizeof(c.Record));
    for(i=0;i<sizeof(table);++i)sum=(UINT8)(sum+table[i]);table[9]=(UINT8)(0-sum);
    status=acpi->InstallAcpiTable(acpi,table,sizeof(table),&key);
    if(status!=EFI_SUCCESS)return status;
    if(gcd!=EFI_SUCCESS)return gcd;
    return prepared==H40_OK&&restored==H40_OK?EFI_SUCCESS:EFI_DEVICE_ERROR;
}
