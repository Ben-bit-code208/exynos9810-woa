/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include "Hsi40HandoffBackend.h"
#include <string.h>

typedef struct {
    H40_RECORD Record;
    BOOLEAN Authority;
} H40_CONTEXT;
static const UINT64 Bases[5]={0x1a240000,0x14200000,0x14220000,0x14360000,0x14212000};
static const UINT64 Lengths[5]={0x3000,0x4000,0x1000,0x1000,0x1000};

static void Clear(VOID *p,UINTN bytes)
{
    (void)memset(p,0,bytes);
}
static void Copy(VOID *p,const VOID *source,UINTN bytes)
{
    (void)memcpy(p,source,bytes);
}
static void Put32(UINT8 *p,UINT32 value)
{
    UINTN i;for(i=0;i<4;++i)p[i]=(UINT8)(value>>(i*8));
}
static void Put64(UINT8 *p,UINT64 value)
{
    Put32(p,(UINT32)value);Put32(p+4,(UINT32)(value>>32));
}
static BOOLEAN ValidSsdt(CONST VOID *data,UINTN bytes)
{
    const UINT8 *p=data;UINT8 sum=0;UINTN i;
    if(!p||bytes<36||bytes>4096||p[0]!='S'||p[1]!='S'||p[2]!='D'||p[3]!='T'||
       ((UINT32)p[4]|((UINT32)p[5]<<8)|((UINT32)p[6]<<16)|((UINT32)p[7]<<24))!=bytes)return FALSE;
    for(i=0;i<bytes;++i)sum=(UINT8)(sum+p[i]);
    return sum==0;
}
static BOOLEAN Span(const EFI_GCD_MEMORY_SPACE_DESCRIPTOR *d,UINTN i)
{
    return !d->ImageHandle&&!d->DeviceHandle&&d->BaseAddress<=Bases[i]&&
        d->BaseAddress+d->Length>=d->BaseAddress&&
        Bases[i]+Lengths[i]<=d->BaseAddress+d->Length;
}
static EFI_STATUS PrepareMemory(EFI_DXE_SERVICES *dxe,UINTN count)
{
    EFI_GCD_MEMORY_SPACE_DESCRIPTOR d;
    EFI_STATUS status;
    UINTN i;
    if(!dxe||dxe->Hdr.Signature!=DXE_SERVICES_SIGNATURE||
       dxe->Hdr.HeaderSize<OFFSET_OF(EFI_DXE_SERVICES,GetMemorySpaceDescriptor)+sizeof(dxe->GetMemorySpaceDescriptor)||
       !dxe->GetMemorySpaceDescriptor)return EFI_INVALID_PARAMETER;
    for(i=0;i<count;++i){
        Clear(&d,sizeof(d));status=dxe->GetMemorySpaceDescriptor(Bases[i],&d);
        if(status!=EFI_SUCCESS)return status;
        if(!Span(&d,i))return EFI_ACCESS_DENIED;
        if(d.GcdMemoryType==EfiGcdMemoryTypeNonExistent){
            if(d.Attributes||d.Capabilities||!dxe->AddMemorySpace||!dxe->SetMemorySpaceAttributes)
                return EFI_ACCESS_DENIED;
            status=dxe->AddMemorySpace(EfiGcdMemoryTypeMemoryMappedIo,Bases[i],Lengths[i],EFI_MEMORY_UC);
            if(status!=EFI_SUCCESS)return status;
            status=dxe->SetMemorySpaceAttributes(Bases[i],Lengths[i],EFI_MEMORY_UC);
            if(status!=EFI_SUCCESS)return status;
        }else if(d.GcdMemoryType!=EfiGcdMemoryTypeMemoryMappedIo||d.Attributes!=EFI_MEMORY_UC||
                 !(d.Capabilities&EFI_MEMORY_UC))return EFI_ACCESS_DENIED;
        Clear(&d,sizeof(d));status=dxe->GetMemorySpaceDescriptor(Bases[i],&d);
        if(status!=EFI_SUCCESS)return status;
        if(!Span(&d,i)||d.GcdMemoryType!=EfiGcdMemoryTypeMemoryMappedIo||d.Attributes!=EFI_MEMORY_UC||
           !(d.Capabilities&EFI_MEMORY_UC))return EFI_ACCESS_DENIED;
    }
    return EFI_SUCCESS;
}
EFI_STATUS H40PrepareMemory(EFI_DXE_SERVICES *dxe){return PrepareMemory(dxe,4);}
EFI_STATUS H40PrepareCensusMemory(EFI_DXE_SERVICES *dxe){return PrepareMemory(dxe,5);}
static int Authorize(void *opaque){return ((H40_CONTEXT *)opaque)->Authority?1:0;}
static BOOLEAN AllowedRead(UINT32 address)
{
    UINTN i;
    for(i=0;i<H40_CLOCK_WORDS;++i)if(address==H40ClockAddresses[i])return TRUE;
    for(i=0;i<H40_PIN_WORDS;++i)if(address==H40PinAddresses[i])return TRUE;
    for(i=0;i<H40_CONTROLLER_WORDS;++i)if(address==0x14360000+H40ControllerOffsets[i])return TRUE;
    return FALSE;
}
#ifdef H40_HOST_TEST
extern int H40TestRead(UINT32,UINT32 *);
extern int H40TestWrite(UINT32,UINT32);
#endif
static int Read(void *opaque,uint32_t address,uint32_t *value)
{
    H40_CONTEXT *c=opaque;
    if(!c->Authority||!AllowedRead(address))return -1;
    if(address>=0x14360000&&address<0x14361000&&
       (c->Record.Flags&(H40_CLOCK_VALID|H40_PINS_VALID))!=(H40_CLOCK_VALID|H40_PINS_VALID))return -1;
#ifdef H40_HOST_TEST
    return H40TestRead(address,value);
#else
    __asm__ volatile("dsb sy" ::: "memory");
    *value=*(volatile UINT32 *)(UINTN)address;
    return 0;
#endif
}
static int Write(void *opaque,uint32_t address,uint32_t value)
{
    H40_CONTEXT *c=opaque;
    UINT32 trigger=value>>16;
    BOOLEAN fifo=value==0||(trigger<=8&&value==(3u|(trigger<<16)|(trigger<<4)));
    if(!c->Authority||!(c->Record.Flags&H40_IDLE_VALID)||(c->Record.Flags&H40_PUBLISHED)||
       !((address==0x14360000&&(value&~0xc0u)==8)||
         (address==0x14360004&&fifo)||(address==0x14360044&&!(value&~0x3ffffu))))return -1;
#ifdef H40_HOST_TEST
    return H40TestWrite(address,value);
#else
    *(volatile UINT32 *)(UINTN)address=value;
    __asm__ volatile("dsb sy" ::: "memory");
    return 0;
#endif
}
EFI_STATUS H40PrepareAndPublish(EFI_DXE_SERVICES *dxe,EFI_ACPI_TABLE_PROTOCOL *acpi,
    CONST UINT8 producer[16],BOOLEAN authority,CONST VOID *ssdt,UINTN ssdtBytes)
{
    H40_CONTEXT c;
    H40_IO io={&c,Authorize,Read,Write};
    EFI_STATUS gcdStatus,deviceStatus=EFI_NOT_READY,recordStatus;
    UINTN deviceKey=~(UINTN)0,recordKey=0,i;
    UINT8 table[68+sizeof(H40_RECORD)],sum=0;
    if(!authority||!acpi||!acpi->InstallAcpiTable||!producer||!ValidSsdt(ssdt,ssdtBytes))return EFI_ACCESS_DENIED;
    Clear(&c,sizeof(c));H40Initialize(&c.Record);
    gcdStatus=H40PrepareMemory(dxe);
    c.Authority=gcdStatus==EFI_SUCCESS;
    if(c.Authority&&H40Prepare(&c.Record,&io,1)==H40_OK){
        deviceStatus=acpi->InstallAcpiTable(acpi,(VOID *)ssdt,ssdtBytes,&deviceKey);
        if(deviceStatus==EFI_SUCCESS){
            (void)H40CommitPublication(&c.Record);
        }else{
            BOOLEAN removed=FALSE;
            if(deviceKey!=~(UINTN)0&&acpi->UninstallAcpiTable)
                removed=acpi->UninstallAcpiTable(acpi,deviceKey)==EFI_SUCCESS;
            if(removed||(EFI_ERROR(deviceStatus)&&deviceKey==~(UINTN)0))
                (void)H40AbortBeforePublication(&c.Record,&io);
            if(!c.Record.Result)c.Record.Result=H40_ORDER_ERROR;
        }
    }else if(!c.Authority)c.Record.Result=H40_AUTHORITY;
    Clear(table,sizeof(table));Copy(table,"H4ST",4);Put32(table+4,sizeof(table));table[8]=1;
    Copy(table+10,"RWOA  ",6);Copy(table+16,"HSI40V3 ",8);
    Put32(table+24,1);Put32(table+28,0x57464847);Put32(table+32,1);
    Copy(table+36,producer,16);Put64(table+52,gcdStatus);Put64(table+60,deviceStatus);
    Copy(table+68,&c.Record,sizeof(c.Record));
    for(i=0;i<sizeof(table);++i)sum=(UINT8)(sum+table[i]);table[9]=(UINT8)(0-sum);
    recordStatus=acpi->InstallAcpiTable(acpi,table,sizeof(table),&recordKey);
    if(recordStatus!=EFI_SUCCESS)return recordStatus;
    if(gcdStatus!=EFI_SUCCESS)return gcdStatus;
    if(c.Record.Result)return EFI_DEVICE_ERROR;
    return deviceStatus;
}
