/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include <Uefi.h>
#include <PiDxe.h>
#include <Guid/DxeServices.h>
#include <Protocol/AcpiTable.h>
#include "Hsi40CensusBackend.h"
#ifndef H40_WIN11_RESET_TRIAL
#define H40_WIN11_RESET_TRIAL 0
#endif
#ifndef H40_WIN11_COLD_TRIAL
#define H40_WIN11_COLD_TRIAL 0
#endif
#if (H40_WIN11_COLD_TRIAL != 0 && H40_WIN11_COLD_TRIAL != 1) || (H40_WIN11_COLD_TRIAL && H40_WIN11_RESET_TRIAL)
#error Select exactly one explicit initialization or reset trial
#endif
#if H40_WIN11_RESET_TRIAL != 0 && H40_WIN11_RESET_TRIAL != 1
#error Select only the default census or explicit reversible reset observation
#endif
#if H40_WIN11_RESET_TRIAL || H40_WIN11_COLD_TRIAL
#include "Hsi40ColdTrial.h"
#if H40_WIN11_RESET_TRIAL
#if !H40_COLD_RESET_ONLY || H40_RESET_PIN_DRIVE != 2
#error Windows 11 reset observation requires reset-only writes and the measured drive-2 profile
#endif
#else
#if H40_COLD_RESET_ONLY || !H40_COLD_WIN11
#error Windows 11 initialization requires the measured full cold profile
#endif
#endif
#endif
#include "Hsi40Win11Authority.h"
#include "Hsi40Win11Policy.h"
#include "Hsi40Win11Producer.h"
#include <string.h>

static const EFI_GUID AcpiGuid=EFI_ACPI_TABLE_PROTOCOL_GUID;
static const EFI_GUID DxeGuid=DXE_SERVICES_TABLE_GUID;
static const EFI_GUID Acpi20Guid={0x8868e871,0xe4f1,0x11d3,{0xbc,0x22,0x00,0x80,0xc7,0x3c,0x88,0x81}};
static const EFI_GUID ReadyGuid={0x7ce88fb3,0x4bd7,0x4679,{0x87,0xa8,0xa8,0xd8,0xde,0xe5,0x0d,0x2b}};
static struct {
    EFI_BOOT_SERVICES *Bs;
    EFI_SYSTEM_TABLE *System;
    EFI_ACPI_TABLE_PROTOCOL *Acpi;
    EFI_DXE_SERVICES *Dxe;
    UINT64 Map[2048];
    UINTN MapBytes,DescriptorBytes;
    UINT8 Tables[6][4096];
    H40_W11_TABLE Root[5],Dsdt;
    H40_W11_AUTHORITY Authority;
    UINT32 Stage,Entries,Attempted;
    BOOLEAN Fired;
} w11;

static UINT32 Read32(const UINT8 *p)
{
    return p[0]|((UINT32)p[1]<<8)|((UINT32)p[2]<<16)|((UINT32)p[3]<<24);
}
static UINT64 Read64(const UINT8 *p){return Read32(p)|((UINT64)Read32(p+4)<<32);}
static VOID Put32(UINT8 *p,UINT32 value)
{
    UINTN i;for(i=0;i<4;++i)p[i]=(UINT8)(value>>(8*i));
}
static BOOLEAN SumZero(const UINT8 *p,UINTN bytes)
{
    UINT8 sum=0;while(bytes--)sum=(UINT8)(sum+*p++);return sum==0;
}
static BOOLEAN Mapped(UINT64 address,UINTN bytes,UINT32 kind)
{
    UINTN offset;
    if(!address||!bytes||address+bytes<address)return FALSE;
    for(offset=0;offset<w11.MapBytes;offset+=w11.DescriptorBytes){
        EFI_MEMORY_DESCRIPTOR *d=(VOID *)((UINT8 *)w11.Map+offset);
        UINT64 end;
        if(d->NumberOfPages>~(UINT64)0/4096)continue;
        end=d->PhysicalStart+d->NumberOfPages*4096;
        if(end<d->PhysicalStart)continue;
        if((kind==2?(d->Type==EfiBootServicesData||d->Type==EfiRuntimeServicesData):
            kind==1?(d->Type==EfiBootServicesCode||d->Type==EfiBootServicesData):
                    (d->Type==EfiACPIReclaimMemory||d->Type==EfiACPIMemoryNVS))&&
           address>=d->PhysicalStart&&address+bytes<=end)return TRUE;
    }
    return FALSE;
}
static EFI_STATUS CopyTable(UINT64 address,UINT8 *copy,UINTN maximum,UINTN *bytes)
{
    const UINT8 *p=(const UINT8 *)(UINTN)address;UINT32 length;
    if(!Mapped(address,36,FALSE))return EFI_ACCESS_DENIED;
    length=Read32(p+4);
    if(length<36||length>maximum||!Mapped(address,length,FALSE))return EFI_BAD_BUFFER_SIZE;
    memcpy(copy,p,length);*bytes=length;
    return SumZero(copy,length)?EFI_SUCCESS:EFI_CRC_ERROR;
}
static EFI_STATUS Capture(VOID)
{
    EFI_STATUS status;UINTN key,index,bytes=0;UINT32 version,seen=0;
    const UINT8 *rsdp=NULL;UINT8 rsdpCopy[36],xsdt[36+64*8];UINT64 dsdt=0;
    w11.Stage=1;w11.MapBytes=sizeof(w11.Map);
    status=w11.Bs->GetMemoryMap(&w11.MapBytes,(VOID *)w11.Map,&key,&w11.DescriptorBytes,&version);
    if(status!=EFI_SUCCESS)return status;
    if(version!=EFI_MEMORY_DESCRIPTOR_VERSION||
       w11.DescriptorBytes<sizeof(EFI_MEMORY_DESCRIPTOR)||w11.DescriptorBytes>128||
       w11.MapBytes>sizeof(w11.Map)||w11.MapBytes%w11.DescriptorBytes)return EFI_BAD_BUFFER_SIZE;
    w11.Stage=2;
    if(w11.System->NumberOfTableEntries>64)return EFI_BAD_BUFFER_SIZE;
    if(!Mapped((UINTN)w11.System->ConfigurationTable,
               w11.System->NumberOfTableEntries*sizeof(EFI_CONFIGURATION_TABLE),2))return EFI_ACCESS_DENIED;
    for(index=0;index<w11.System->NumberOfTableEntries;++index){
        EFI_CONFIGURATION_TABLE *entry=&w11.System->ConfigurationTable[index];
        if(!memcmp(&entry->VendorGuid,&Acpi20Guid,sizeof(EFI_GUID))){
            if(seen&1)return EFI_ACCESS_DENIED;
            seen|=1;
            rsdp=entry->VendorTable;
        }
        if(!memcmp(&entry->VendorGuid,&DxeGuid,sizeof(EFI_GUID))){
            if(seen&2)return EFI_ACCESS_DENIED;
            seen|=2;
            w11.Dxe=entry->VendorTable;
        }
    }
    if(seen!=3||!w11.Dxe||!Mapped((UINTN)w11.Dxe,sizeof(*w11.Dxe),TRUE)||
       w11.Dxe->Hdr.Signature!=DXE_SERVICES_SIGNATURE||
       w11.Dxe->Hdr.HeaderSize<sizeof(*w11.Dxe)||!w11.Dxe->GetMemorySpaceDescriptor)return EFI_ACCESS_DENIED;
    w11.Stage=3;
    if(!Mapped((UINTN)rsdp,sizeof(rsdpCopy),FALSE))return EFI_ACCESS_DENIED;
    memcpy(rsdpCopy,rsdp,sizeof(rsdpCopy));
    if(memcmp(rsdpCopy,"RSD PTR ",8)||rsdpCopy[15]!=2||Read32(rsdpCopy+20)!=36||
       !SumZero(rsdpCopy,20)||!SumZero(rsdpCopy,36))return EFI_CRC_ERROR;
    w11.Stage=4;
    status=CopyTable(Read64(rsdpCopy+24),xsdt,sizeof(xsdt),&bytes);
    if(status!=EFI_SUCCESS)return status;
    if(memcmp(xsdt,"XSDT",4)||(bytes-36)%8)return EFI_INVALID_PARAMETER;
    w11.Entries=(UINT32)((bytes-36)/8);
    if(w11.Entries!=5)return EFI_ACCESS_DENIED;
    w11.Stage=5;
    for(index=0;index<5;++index){
        status=CopyTable(Read64(xsdt+36+8*index),w11.Tables[index],4096,&bytes);
        if(status!=EFI_SUCCESS)return status;
        w11.Root[index].Data=w11.Tables[index];w11.Root[index].Bytes=bytes;
        if(!memcmp(w11.Tables[index],"FACP",4)){
            if(dsdt||bytes!=276)return EFI_ACCESS_DENIED;
            dsdt=Read64(w11.Tables[index]+140);
            if(!dsdt)dsdt=Read32(w11.Tables[index]+40);
        }
    }
    w11.Stage=6;
    status=CopyTable(dsdt,w11.Tables[5],4096,&bytes);
    if(status!=EFI_SUCCESS)return status;
    w11.Dsdt.Data=w11.Tables[5];w11.Dsdt.Bytes=bytes;
    w11.Stage=7;
    return H40Win11Authority(&H40Win11Policy,w11.Root,5,w11.Dsdt,1,&w11.Authority)==H40_W11_OK?
        EFI_SUCCESS:EFI_ACCESS_DENIED;
}
static EFI_STATUS Publish(EFI_STATUS capture,EFI_STATUS census)
{
    UINT8 table[112]={0},sum=0;UINTN i,key=0;
    memcpy(table,"H4WA",4);Put32(table+4,sizeof(table));table[8]=1;
    memcpy(table+10,H40_WIN11_COLD_TRIAL?"RWOA  H4WINC1 ":H40_WIN11_RESET_TRIAL?"RWOA  H4WINR1 ":"RWOA  H4WINA1 ",14);
    Put32(table+24,1);Put32(table+28,0x57464847);Put32(table+32,1);
    memcpy(table+36,H40Win11Producer,16);
    Put32(table+52,(UINT32)capture);Put32(table+56,(UINT32)(capture>>32));
    memcpy(table+60,&w11.Authority,sizeof(w11.Authority));
    Put32(table+92,(UINT32)census);Put32(table+96,(UINT32)(census>>32));
    Put32(table+100,w11.Attempted);Put32(table+104,w11.Stage);Put32(table+108,w11.Entries);
    for(i=0;i<sizeof(table);++i)sum=(UINT8)(sum+table[i]);table[9]=(UINT8)(0-sum);
    return w11.Acpi->InstallAcpiTable(w11.Acpi,table,sizeof(table),&key);
}
static VOID EFIAPI Ready(EFI_EVENT event,VOID *context)
{
    EFI_STATUS capture,census=EFI_NOT_STARTED,status;EFI_TPL old;(void)context;
    if(w11.Fired)return;
    w11.Fired=TRUE;
    memset(&w11.Authority,0,sizeof(w11.Authority));
    w11.Authority.Size=sizeof(w11.Authority);w11.Authority.Version=1;w11.Authority.Result=H40_W11_ARGUMENT;
    status=w11.Bs->CloseEvent(event);
    if(status!=EFI_SUCCESS){(void)Publish(status,census);return;}
    old=w11.Bs->RaiseTPL(TPL_NOTIFY);
    capture=Capture();
    if(capture==EFI_SUCCESS){
        w11.Stage=8;w11.Attempted=1;
#if H40_WIN11_RESET_TRIAL || H40_WIN11_COLD_TRIAL
        census=H40ColdTrialAndPublish(w11.Dxe,w11.Bs,w11.Acpi,H40Win11Producer,TRUE);
#else
        census=H40CensusAndPublish(w11.Dxe,w11.Acpi,H40Win11Producer,TRUE);
#endif
    }
    w11.Bs->RestoreTPL(old);
    /* Failure to publish remains missing evidence, never a successful census. */
    (void)Publish(capture,census);
}
EFI_STATUS EFIAPI H40Win11Entry(EFI_HANDLE image,EFI_SYSTEM_TABLE *system)
{
    EFI_STATUS status;EFI_EVENT event;(void)image;
    if(w11.Bs)return EFI_ALREADY_STARTED;
    if(!system||!system->BootServices)return EFI_INVALID_PARAMETER;
    w11.Bs=system->BootServices;w11.System=system;
    if(!w11.Bs->LocateProtocol||!w11.Bs->CreateEventEx||!w11.Bs->GetMemoryMap||
       !w11.Bs->RaiseTPL||!w11.Bs->RestoreTPL||!w11.Bs->CloseEvent)return EFI_INVALID_PARAMETER;
    status=w11.Bs->LocateProtocol((EFI_GUID *)&AcpiGuid,NULL,(VOID **)&w11.Acpi);
    if(status!=EFI_SUCCESS)return status;
    if(!w11.Acpi||!w11.Acpi->InstallAcpiTable)return EFI_UNSUPPORTED;
    return w11.Bs->CreateEventEx(EVT_NOTIFY_SIGNAL,TPL_CALLBACK,Ready,NULL,(EFI_GUID *)&ReadyGuid,&event);
}
