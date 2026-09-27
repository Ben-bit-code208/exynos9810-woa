/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include "Hsi40CensusBackend.h"
#include <string.h>

typedef struct {
    H40_CENSUS_RECORD Record;
    BOOLEAN Authority;
} H40_CENSUS_CONTEXT;
static int Authorize(void *opaque){return ((H40_CENSUS_CONTEXT *)opaque)->Authority?1:0;}
static int Read(void *opaque,uint32_t address,uint32_t *value)
{
    H40_CENSUS_CONTEXT *c=opaque;
    UINTN i;
    BOOLEAN clock=FALSE,pin=FALSE,controller=FALSE;
    if(!c->Authority)return -1;
    for(i=0;i<H40_CLOCK_WORDS;++i)if(address==H40ClockAddresses[i])clock=TRUE;
    for(i=0;i<H40_CENSUS_PIN_WORDS;++i)if(address==H40CensusPinAddresses[i])pin=TRUE;
    for(i=0;i<H40_CONTROLLER_WORDS;++i)if(address==0x14360000+H40ControllerOffsets[i])controller=TRUE;
    if(!clock&&!pin&&!controller&&address!=H40_USI_SELECTOR_ADDRESS)return -1;
    if(!clock&&(!(c->Record.Flags&H40_CENSUS_CLOCK_BEFORE)||
       (c->Record.Stage==3&&!(c->Record.Flags&H40_CENSUS_CLOCK_AFTER))))return -1;
#ifdef H40_HOST_TEST
    extern int H40TestRead(UINT32,UINT32 *);
    return H40TestRead(address,value);
#else
    __asm__ volatile("dsb sy" ::: "memory");
    *value=*(volatile UINT32 *)(UINTN)address;
    return 0;
#endif
}
static void Put32(UINT8 *p,UINT32 value)
{
    UINTN i;for(i=0;i<4;++i)p[i]=(UINT8)(value>>(8*i));
}
EFI_STATUS H40CensusAndPublish(EFI_DXE_SERVICES *dxe,EFI_ACPI_TABLE_PROTOCOL *acpi,
    CONST UINT8 producer[16],BOOLEAN authority)
{
    H40_CENSUS_CONTEXT c;
    H40_CENSUS_IO io={&c,Authorize,Read};
    EFI_STATUS gcd,status;
    UINT8 table[60+sizeof(H40_CENSUS_RECORD)],sum=0;
    UINTN i,key=0;
    if(!authority||!producer||!acpi||!acpi->InstallAcpiTable)return EFI_ACCESS_DENIED;
    memset(&c,0,sizeof(c));H40CensusInitialize(&c.Record);
    gcd=H40PrepareCensusMemory(dxe);c.Authority=gcd==EFI_SUCCESS;
    if(c.Authority)(void)H40CensusCollect(&c.Record,&io,1);
    else c.Record.Result=H40_AUTHORITY;
    memset(table,0,sizeof(table));memcpy(table,"H4CS",4);Put32(table+4,sizeof(table));table[8]=1;
    memcpy(table+10,"RWOA  ",6);memcpy(table+16,"H4COLD2 ",8);
    Put32(table+24,2);Put32(table+28,0x57464847);Put32(table+32,2);
    memcpy(table+36,producer,16);Put32(table+52,(UINT32)gcd);Put32(table+56,(UINT32)(gcd>>32));
    memcpy(table+60,&c.Record,sizeof(c.Record));
    for(i=0;i<sizeof(table);++i)sum=(UINT8)(sum+table[i]);table[9]=(UINT8)(0-sum);
    status=acpi->InstallAcpiTable(acpi,table,sizeof(table),&key);
    if(status!=EFI_SUCCESS)return status;
    if(gcd!=EFI_SUCCESS)return gcd;
    return c.Record.Result?EFI_DEVICE_ERROR:EFI_SUCCESS;
}
