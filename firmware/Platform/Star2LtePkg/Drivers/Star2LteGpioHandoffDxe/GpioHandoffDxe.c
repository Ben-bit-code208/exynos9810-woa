/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include <Uefi.h>
#include <PiDxe.h>
#include <Guid/DxeServices.h>
#include <Protocol/AcpiTable.h>
#include "GpioHandoffCore.h"
#ifndef STAR2LTE_BCD_WRITE_OBSERVER
#define STAR2LTE_BCD_WRITE_OBSERVER 0
#endif
#ifndef STAR2LTE_BCD_BLOCK_OBSERVER
#define STAR2LTE_BCD_BLOCK_OBSERVER 0
#endif
#if STAR2LTE_BCD_BLOCK_OBSERVER
#include "../Star2LteBcdWriteObserver/BcdBlockObserver.h"
#elif STAR2LTE_BCD_WRITE_OBSERVER
#include "../Star2LteBcdWriteObserver/BcdWriteObserver.h"
#endif
#ifndef STAR2LTE_ACPM_FRAMEWORK_LOOPBACK
#define STAR2LTE_ACPM_FRAMEWORK_LOOPBACK 0
#endif
#if STAR2LTE_ACPM_FRAMEWORK_LOOPBACK != 0 && STAR2LTE_ACPM_FRAMEWORK_LOOPBACK != 1
#error STAR2LTE_ACPM_FRAMEWORK_LOOPBACK must be zero or one.
#endif
#if STAR2LTE_ACPM_FRAMEWORK_LOOPBACK && STAR2LTE_GPIO_HANDOFF_MODE != 0 && STAR2LTE_GPIO_HANDOFF_MODE != 4
#error ACPM loopback publication requires the corrected policy-four cohort.
#endif
#if STAR2LTE_USB_HANDOFF_OBSERVER
#include "../Star2LteUsbHandoffObserver/UsbHandoffObserver.h"
#endif
#if STAR2LTE_HSI40_HANDOFF
#if STAR2LTE_HSI40_COLD_CENSUS
#include "../Star2LteHsi40HandoffDxe/Hsi40CensusBackend.h"
#elif STAR2LTE_HSI40_COLD_TRIAL
#include "../Star2LteHsi40HandoffDxe/Hsi40ColdTrial.h"
#else
#include "../Star2LteHsi40HandoffDxe/Hsi40HandoffBackend.h"
#include "Hsi40DevicesGenerated.h"
#endif
#endif
#if STAR2LTE_UART1_COLD_CENSUS
#include "../Star2LteUart1HandoffDxe/Uart1CensusBackend.h"
#endif

#if STAR2LTE_GPIO_HANDOFF_MODE == 3 || STAR2LTE_GPIO_HANDOFF_MODE == 4
#include "GpioHandoffGenerated.h"
static EFI_BOOT_SERVICES *mBs;
static EFI_SYSTEM_TABLE *mSystem;
static EFI_ACPI_TABLE_PROTOCOL *mAcpi;
static BOOLEAN mAttempted;
static UINT32 mAuthorityFailure;
#if STAR2LTE_ACPM_FRAMEWORK_LOOPBACK
static EFI_DXE_SERVICES *mFrameworkDxe;
#endif
#if STAR2LTE_USB_HANDOFF_OBSERVER
static EFI_DXE_SERVICES *mUsbDxe;
#endif
#if STAR2LTE_HSI40_HANDOFF
static EFI_DXE_SERVICES *mHsi40Dxe;
#endif
#if STAR2LTE_UART1_COLD_CENSUS
static EFI_DXE_SERVICES *mUart1Dxe;
#endif
#define AUTH_FAIL(n) do {mAuthorityFailure=(n);return FALSE;} while(0)
static UINT64 mMapStorage[2048];
static UINTN mMapBytes, mDescriptorBytes;
static const EFI_GUID AcpiGuid = EFI_ACPI_TABLE_PROTOCOL_GUID;
static const EFI_GUID DxeGuid = DXE_SERVICES_TABLE_GUID;
static const EFI_GUID Acpi20Guid = {0x8868e871,0xe4f1,0x11d3,{0xbc,0x22,0x00,0x80,0xc7,0x3c,0x88,0x81}};
static const EFI_GUID ReadyGuid = {0x7ce88fb3,0x4bd7,0x4679,{0x87,0xa8,0xa8,0xd8,0xde,0xe5,0x0d,0x2b}};
typedef struct {
    UINT8 Header[36];
    GH_RECORD Record;
} GH_TABLE;
typedef char GhTableMustBe212[sizeof(GH_TABLE)==212?1:-1];

static BOOLEAN equal(const VOID *a,const VOID *b,UINTN count)
{
    const UINT8 *x=a,*y=b;
    while(count--) if(*x++!=*y++) return FALSE;
    return TRUE;
}
static UINT32 u32(const UINT8 *p)
{
    return p[0]|((UINT32)p[1]<<8)|((UINT32)p[2]<<16)|((UINT32)p[3]<<24);
}
static UINT64 u64(const UINT8 *p)
{
    return u32(p)|((UINT64)u32(p+4)<<32);
}
static VOID put32(UINT8 *p,UINT32 value)
{
    UINTN i;for(i=0;i<4;i++)p[i]=(UINT8)(value>>(8*i));
}
static BOOLEAN sum_zero(const UINT8 *p,UINTN count)
{
    UINT8 sum=0;while(count--)sum=(UINT8)(sum+*p++);return sum==0;
}
static BOOLEAN mapped(UINT64 address,UINTN bytes,BOOLEAN service)
{
    UINTN offset;
    if(!address||!bytes||address+bytes<address)return FALSE;
    for(offset=0;offset+mDescriptorBytes<=mMapBytes;offset+=mDescriptorBytes){
        EFI_MEMORY_DESCRIPTOR *d=(VOID *)((UINT8 *)mMapStorage+offset);
        UINT64 end;
        if(d->NumberOfPages>(~(UINT64)0)/4096)continue;
        end=d->PhysicalStart+d->NumberOfPages*4096;
        if(end<d->PhysicalStart)continue;
        /* The held SEC loader allocates DxeCore (including its .data services
           table) as BootServicesCode. This exception is services-only. */
        if((service ? (d->Type==EfiBootServicesCode||d->Type==EfiBootServicesData) :
            (d->Type==EfiACPIReclaimMemory||d->Type==EfiACPIMemoryNVS)) &&
            address>=d->PhysicalStart&&address+bytes<=end)return TRUE;
    }
    return FALSE;
}
static const UINT8 *table(UINT64 address,const CHAR8 signature[4],UINTN maximum)
{
    const UINT8 *p=(const UINT8 *)(UINTN)address;UINTN length;
    if(!mapped(address,36,FALSE)||!equal(p,signature,4))return NULL;
    length=u32(p+4);
    if(length<36||length>maximum||!mapped(address,length,FALSE)||!sum_zero(p,length))return NULL;
    return p;
}
#if STAR2LTE_ACPM_FRAMEWORK_LOOPBACK
static CONST EFI_PHYSICAL_ADDRESS FrameworkPages[4] = {
    0x14100000, 0x0203e000, 0x02040000, 0x02041000
};
static BOOLEAN framework_space(EFI_GCD_MEMORY_SPACE_DESCRIPTOR *space,UINTN index)
{
    UINT64 base=FrameworkPages[index];
    return !space->ImageHandle&&!space->DeviceHandle&&space->BaseAddress<=base&&
        space->Length>=0x1000&&space->BaseAddress+space->Length>=space->BaseAddress&&
        base+0x1000<=space->BaseAddress+space->Length;
}
static UINT32 prepare_framework_pages(EFI_DXE_SERVICES *dxe,BOOLEAN create)
{
    EFI_GCD_MEMORY_SPACE_DESCRIPTOR spaces[4];
    UINTN index,byte;
    UINT32 missing=0;
    EFI_STATUS status;
    if(!dxe||dxe->Hdr.HeaderSize<OFFSET_OF(EFI_DXE_SERVICES,GetMemorySpaceDescriptor)+
        sizeof(dxe->GetMemorySpaceDescriptor)||!dxe->GetMemorySpaceDescriptor)return 300;
    for(index=0;index<4;index++){
        for(byte=0;byte<sizeof(spaces[index]);byte++)((UINT8 *)&spaces[index])[byte]=0;
        status=dxe->GetMemorySpaceDescriptor(FrameworkPages[index],&spaces[index]);
        if(status!=EFI_SUCCESS)return 301+(UINT32)index*10;
        if(!framework_space(&spaces[index],index))return 302+(UINT32)index*10;
        if(spaces[index].GcdMemoryType==EfiGcdMemoryTypeNonExistent){
            if(!create||spaces[index].Attributes||spaces[index].Capabilities)return 303+(UINT32)index*10;
            missing|=1u<<index;
        }else if(spaces[index].GcdMemoryType!=EfiGcdMemoryTypeMemoryMappedIo||
                 spaces[index].Attributes!=EFI_MEMORY_UC||
                 !(spaces[index].Capabilities&EFI_MEMORY_UC))return 304+(UINT32)index*10;
    }
    if(missing&&(dxe->Hdr.HeaderSize<OFFSET_OF(EFI_DXE_SERVICES,SetMemorySpaceAttributes)+
        sizeof(dxe->SetMemorySpaceAttributes)||!dxe->AddMemorySpace||!dxe->SetMemorySpaceAttributes))return 340;
    for(index=0;index<4;index++){
        if(!(missing&(1u<<index)))continue;
        status=dxe->AddMemorySpace(EfiGcdMemoryTypeMemoryMappedIo,FrameworkPages[index],0x1000,EFI_MEMORY_UC);
        if(status!=EFI_SUCCESS)return 305+(UINT32)index*10;
        status=dxe->SetMemorySpaceAttributes(FrameworkPages[index],0x1000,EFI_MEMORY_UC);
        if(status!=EFI_SUCCESS)return 306+(UINT32)index*10;
    }
    for(index=0;index<4;index++){
        for(byte=0;byte<sizeof(spaces[index]);byte++)((UINT8 *)&spaces[index])[byte]=0;
        status=dxe->GetMemorySpaceDescriptor(FrameworkPages[index],&spaces[index]);
        if(status!=EFI_SUCCESS||!framework_space(&spaces[index],index)||
           spaces[index].GcdMemoryType!=EfiGcdMemoryTypeMemoryMappedIo||
           spaces[index].Attributes!=EFI_MEMORY_UC||
           !(spaces[index].Capabilities&EFI_MEMORY_UC))return 307+(UINT32)index*10;
    }
    return 0;
}
#include "Usb2AccessGcd.inc"
static VOID publish_framework_status(UINT32 failure,UINT32 attempted,EFI_STATUS devicesStatus,UINT32 choice)
{
    UINT8 data[76];UINTN index,key=0;UINT8 sum=0;
    for(index=0;index<sizeof(data);index++)data[index]=0;
    for(index=0;index<4;index++)data[index]="A4ST"[index];
    put32(data+4,sizeof(data));data[8]=1;
    for(index=0;index<6;index++)data[10+index]="RWOA  "[index];
    for(index=0;index<8;index++)data[16+index]="A4GCD   "[index];
    put32(data+24,1);put32(data+28,0x57464847);put32(data+32,1);
    for(index=0;index<16;index++)data[36+index]=GhCandidateId[index];
    put32(data+52,failure);put32(data+56,attempted);
    put32(data+60,(UINT32)devicesStatus);put32(data+64,(UINT32)((UINT64)devicesStatus>>32));
    put32(data+68,choice);
    for(index=0;index<sizeof(data);index++)sum=(UINT8)(sum+data[index]);
    data[9]=(UINT8)(0-sum);
    /* Missing A4ST is unavailable evidence, never a successful admission. */
    (void)mAcpi->InstallAcpiTable(mAcpi,data,sizeof(data),&key);
}
#endif
static BOOLEAN authority(BOOLEAN prepareMissingMmio)
{
    UINTN key, index, entries;UINT32 version;EFI_STATUS status;
    const UINT8 *rsdp=NULL,*xsdt,*fadt=NULL,*dsdt;
    EFI_DXE_SERVICES *dxe=NULL;
    EFI_GCD_MEMORY_SPACE_DESCRIPTOR space;
    UINT32 seen=0;
    mAuthorityFailure=0;mMapBytes=sizeof(mMapStorage);
    status=mBs->GetMemoryMap(&mMapBytes,(EFI_MEMORY_DESCRIPTOR *)mMapStorage,&key,&mDescriptorBytes,&version);
    if(EFI_ERROR(status)||mDescriptorBytes<sizeof(EFI_MEMORY_DESCRIPTOR)||mDescriptorBytes>128||
       mMapBytes>sizeof(mMapStorage)||mMapBytes%mDescriptorBytes!=0)AUTH_FAIL(1);
    if(mSystem->NumberOfTableEntries>64)AUTH_FAIL(201);
    for(index=0;index<mSystem->NumberOfTableEntries;index++){
        if(equal(&mSystem->ConfigurationTable[index].VendorGuid,&Acpi20Guid,sizeof(EFI_GUID))){
            if(rsdp)AUTH_FAIL(3);
            rsdp=mSystem->ConfigurationTable[index].VendorTable;
        }
        if(equal(&mSystem->ConfigurationTable[index].VendorGuid,&DxeGuid,sizeof(EFI_GUID))){
            if(dxe)AUTH_FAIL(202);
            dxe=mSystem->ConfigurationTable[index].VendorTable;
        }
    }
    /* Non-runtime MMIO need not appear in GetMemoryMap. */
    if(!dxe)AUTH_FAIL(203);
    if(!mapped((UINTN)dxe,sizeof(*dxe),TRUE))AUTH_FAIL(204);
    if(dxe->Hdr.Signature!=DXE_SERVICES_SIGNATURE)AUTH_FAIL(205);
    if(dxe->Hdr.HeaderSize<OFFSET_OF(EFI_DXE_SERVICES,GetMemorySpaceDescriptor)+
       sizeof(dxe->GetMemorySpaceDescriptor))AUTH_FAIL(206);
    if(!dxe->GetMemorySpaceDescriptor)AUTH_FAIL(207);
    if(!rsdp||!mapped((UINTN)rsdp,36,FALSE)||!equal(rsdp,"RSD PTR ",8)||
       rsdp[15]!=2||u32(rsdp+20)!=36||!sum_zero(rsdp,20)||!sum_zero(rsdp,36))AUTH_FAIL(4);
    xsdt=table(u64(rsdp+24),"XSDT",1024);
    if(!xsdt||(u32(xsdt+4)-36)%8)AUTH_FAIL(5);
    entries=(u32(xsdt+4)-36)/8;
    if(entries!=4)AUTH_FAIL(6); /* No earlier SSDT/raw-ALIVE owner may coexist. */
    for(index=0;index<entries;index++){
        UINT64 address=u64(xsdt+36+index*8);
        const UINT8 *p;
        if(!mapped(address,36,FALSE))AUTH_FAIL(7);
        p=(const UINT8 *)(UINTN)address;
        if(equal(p,"FACP",4)){
            if(seen&1)AUTH_FAIL(8);
            fadt=table(address,"FACP",sizeof(GhExpectedFadt));
            if(!fadt||u32(fadt+4)!=sizeof(GhExpectedFadt))AUTH_FAIL(9);
            seen|=1;
        }else if(equal(p,"APIC",4)){
            if(seen&2||!table(address,"APIC",sizeof(GhExpectedApic))||
               u32(p+4)!=sizeof(GhExpectedApic))AUTH_FAIL(11);
            seen|=2;
        }else if(equal(p,"GTDT",4)){
            if(seen&4||!table(address,"GTDT",sizeof(GhExpectedGtdt))||
               u32(p+4)!=sizeof(GhExpectedGtdt))AUTH_FAIL(12);
            seen|=4;
        }else if(equal(p,"PPTT",4)){
            if(seen&8||!table(address,"PPTT",sizeof(GhExpectedPptt))||
               u32(p+4)!=sizeof(GhExpectedPptt))AUTH_FAIL(13);
            seen|=8;
        }else AUTH_FAIL(14);
    }
    if(seen!=15||!fadt)AUTH_FAIL(15);
    dsdt=table(u64(fadt+140)?u64(fadt+140):u32(fadt+40),"DSDT",sizeof(GhExpectedDsdt));
    if(!dsdt||u32(dsdt+4)!=sizeof(GhExpectedDsdt))AUTH_FAIL(16);
    /* This is an ALIVE resource-ownership gate, not a new CPU policy oracle.
       The retained baseline publishes runtime parking data in CPU _MAT buffers
       and architectural tables. All AML outside those data buffers is exact. */
    for(index=0;index<sizeof(GhExpectedDsdt);index++)
        if(GhDsdtCompareMask[index]&&dsdt[index]!=GhExpectedDsdt[index])AUTH_FAIL(16);
    status=dxe->GetMemorySpaceDescriptor(GH_IO_BASE,&space);
    if(EFI_ERROR(status))AUTH_FAIL(208);
    if(space.ImageHandle)AUTH_FAIL(211);
    if(space.DeviceHandle)AUTH_FAIL(212);
    if(space.BaseAddress>GH_IO_BASE||
       space.Length<0x1000||space.BaseAddress+space.Length<space.BaseAddress||
       GH_IO_BASE+0x1000>space.BaseAddress+space.Length)AUTH_FAIL(213);
    if(space.GcdMemoryType==EfiGcdMemoryTypeNonExistent){
        if(!prepareMissingMmio)AUTH_FAIL(217);
        if(space.Attributes||space.Capabilities)AUTH_FAIL(218);
        if(dxe->Hdr.HeaderSize<OFFSET_OF(EFI_DXE_SERVICES,SetMemorySpaceAttributes)+
           sizeof(dxe->SetMemorySpaceAttributes)||!dxe->AddMemorySpace||
           !dxe->SetMemorySpaceAttributes)AUTH_FAIL(214);
        /* The exact ACPI graph is verified first. Register only this empty
           board-defined page; never reclassify RAM, reserved space or owners. */
        status=dxe->AddMemorySpace(EfiGcdMemoryTypeMemoryMappedIo,GH_IO_BASE,0x1000,EFI_MEMORY_UC);
        if(EFI_ERROR(status))AUTH_FAIL(215);
        status=dxe->SetMemorySpaceAttributes(GH_IO_BASE,0x1000,EFI_MEMORY_UC);
        if(EFI_ERROR(status))AUTH_FAIL(216);
        return authority(FALSE);
    }
    if(space.GcdMemoryType!=EfiGcdMemoryTypeMemoryMappedIo)
        AUTH_FAIL(230u+((UINT32)space.GcdMemoryType<(UINT32)EfiGcdMemoryTypeMaximum?
                       (UINT32)space.GcdMemoryType:9u));
    if(!(space.Attributes&EFI_MEMORY_UC))AUTH_FAIL(210);
#if STAR2LTE_ACPM_FRAMEWORK_LOOPBACK
    mFrameworkDxe=dxe;
#endif
#if STAR2LTE_USB_HANDOFF_OBSERVER
    mUsbDxe=dxe;
#endif
#if STAR2LTE_HSI40_HANDOFF
    mHsi40Dxe=dxe;
#endif
#if STAR2LTE_UART1_COLD_CENSUS
    mUart1Dxe=dxe;
#endif
    return TRUE;
}

#ifdef GH_HOST_TEST
extern GH_U32 GhTestRead(GH_U32 Offset);
extern void GhTestWrite(GH_U32 Offset,GH_U32 Value);
#endif
static GH_U32 read_register(void *context,GH_U32 offset)
{
    (void)context;
#ifdef GH_HOST_TEST
    return GhTestRead(offset);
#else
    return *(volatile GH_U32 *)(UINTN)(GH_IO_BASE+offset);
#endif
}
static void write_register(void *context,GH_U32 offset,GH_U32 value)
{
    GH_U32 mask=GhWriteMask(STAR2LTE_GPIO_HANDOFF_MODE,offset);
    if(!mask||((value^read_register(context,offset))&~mask)||
       (value&mask)!=GhWriteBits(offset))return;
#ifdef GH_HOST_TEST
    GhTestWrite(offset,value);
#else
    *(volatile GH_U32 *)(UINTN)(GH_IO_BASE+offset)=value;
    __asm__ volatile("dsb sy" ::: "memory");
#endif
}
static VOID EFIAPI ready(EFI_EVENT event,VOID *context)
{
    GH_TABLE observation;GH_IO io={NULL,read_register,write_register};
    EFI_TPL old;EFI_STATUS status;UINTN key=0,index;UINT8 sum=0;
#if STAR2LTE_USB_HANDOFF_OBSERVER || STAR2LTE_BCD_WRITE_OBSERVER || STAR2LTE_BCD_BLOCK_OBSERVER || STAR2LTE_HSI40_HANDOFF || STAR2LTE_UART1_COLD_CENSUS
    EFI_STATUS observationStatus;
#endif
#if STAR2LTE_ACPM_FRAMEWORK_LOOPBACK
    UINT32 frameworkFailure=341,frameworkAttempted=0,deviceChoice=1;
    const VOID *devices=GhBaseDeviceSsdt;UINTN devicesBytes=sizeof(GhBaseDeviceSsdt);
#endif
    (void)context;
    if(mAttempted)return;
    mAttempted=TRUE;mBs->CloseEvent(event);
    if(authority(TRUE)){
        old=mBs->RaiseTPL(TPL_HIGH_LEVEL);
        GhHandoff(STAR2LTE_GPIO_HANDOFF_MODE,1,&io,&observation.Record);
        mBs->RestoreTPL(old);
    }else GhHandoff(STAR2LTE_GPIO_HANDOFF_MODE,0,&io,&observation.Record);
    observation.Record.AuthorityFailure=mAuthorityFailure;
    for(index=0;index<16;index++)observation.Record.CandidateId[index]=GhCandidateId[index];
    for(index=0;index<36;index++)observation.Header[index]=0;
    for(index=0;index<4;index++)observation.Header[index]="GPH3"[index];
    put32(observation.Header+4,sizeof(observation));observation.Header[8]=1;
    for(index=0;index<6;index++)observation.Header[10+index]="RWOA  "[index];
    for(index=0;index<8;index++)observation.Header[16+index]="GPIOV3  "[index];
    put32(observation.Header+24,1);put32(observation.Header+28,0x57464847);put32(observation.Header+32,1);
    for(index=0;index<sizeof(observation);index++)sum=(UINT8)(sum+((UINT8 *)&observation)[index]);
    observation.Header[9]=(UINT8)(0-sum);
    status=mAcpi->InstallAcpiTable(mAcpi,&observation,sizeof(observation),&key);
#if STAR2LTE_USB_HANDOFF_OBSERVER || STAR2LTE_BCD_WRITE_OBSERVER || STAR2LTE_BCD_BLOCK_OBSERVER || STAR2LTE_HSI40_HANDOFF || STAR2LTE_UART1_COLD_CENSUS
    observationStatus=status;
#endif
    if(EFI_ERROR(status)||observation.Record.Status!=GH_OK)return;
    /* GPH3 describes the mask operation. SSDT presence separately records
       successful binding publication; neither claims Windows driver success. */
#if STAR2LTE_ACPM_FRAMEWORK_LOOPBACK
    if(status==EFI_SUCCESS){
        frameworkAttempted=1;frameworkFailure=prepare_framework_pages(mFrameworkDxe,TRUE);
#if STAR2LTE_USB2_ACCESS_PROBE
        if(!frameworkFailure)frameworkFailure=prepare_usb2_access(mFrameworkDxe);
#endif
        if(!frameworkFailure){devices=GhDeviceSsdt;devicesBytes=sizeof(GhDeviceSsdt);deviceChoice=2;}
    }
    /* Framework refusal cannot revoke the established GPIO/recovery cohort. */
    status=mAcpi->InstallAcpiTable(mAcpi,(VOID *)devices,devicesBytes,&key);
    publish_framework_status(frameworkFailure,frameworkAttempted,status,deviceChoice);
#else
    status=mAcpi->InstallAcpiTable(mAcpi,(VOID *)GhDeviceSsdt,sizeof(GhDeviceSsdt),&key);
#endif
#if STAR2LTE_USB_HANDOFF_OBSERVER
    if(observationStatus==EFI_SUCCESS&&status==EFI_SUCCESS)
        (void)UsboObserveAndPublish(mUsbDxe,mAcpi,GhCandidateId);
#endif
#if STAR2LTE_BCD_BLOCK_OBSERVER
    if(observationStatus==EFI_SUCCESS&&status==EFI_SUCCESS)
        (void)BcdBlockObserverInitialize(mSystem,mAcpi,GhCandidateId);
#elif STAR2LTE_BCD_WRITE_OBSERVER
    if(observationStatus==EFI_SUCCESS&&status==EFI_SUCCESS)
        (void)BcdObserverInitialize(mSystem,mAcpi,GhCandidateId);
#endif
#if STAR2LTE_HSI40_HANDOFF
    if(observationStatus==EFI_SUCCESS&&status==EFI_SUCCESS&&observation.Record.AuthorityVerified)
#if STAR2LTE_HSI40_COLD_CENSUS
        (void)H40CensusAndPublish(mHsi40Dxe,mAcpi,GhCandidateId,TRUE);
#elif STAR2LTE_HSI40_COLD_TRIAL
        (void)H40ColdTrialAndPublish(mHsi40Dxe,mBs,mAcpi,GhCandidateId,TRUE);
#else
        (void)H40PrepareAndPublish(mHsi40Dxe,mAcpi,GhCandidateId,TRUE,H40DeviceSsdt,sizeof(H40DeviceSsdt));
#endif
#endif
#if STAR2LTE_UART1_COLD_CENSUS
    if(observationStatus==EFI_SUCCESS&&status==EFI_SUCCESS&&observation.Record.AuthorityVerified)
        (void)U1CensusAndPublish(mUart1Dxe,mAcpi,GhCandidateId,TRUE);
#endif
    (void)status;
}

EFI_STATUS EFIAPI GpioHandoffEntry(EFI_HANDLE image,EFI_SYSTEM_TABLE *system)
{
    EFI_EVENT event;EFI_STATUS status;(void)image;
    mBs=system->BootServices;mSystem=system;mAttempted=FALSE;
#if STAR2LTE_ACPM_FRAMEWORK_LOOPBACK
    mFrameworkDxe=NULL;
#endif
#if STAR2LTE_USB_HANDOFF_OBSERVER
    mUsbDxe=NULL;
#endif
#if STAR2LTE_HSI40_HANDOFF
    mHsi40Dxe=NULL;
#endif
#if STAR2LTE_UART1_COLD_CENSUS
    mUart1Dxe=NULL;
#endif
    status=mBs->LocateProtocol((EFI_GUID *)&AcpiGuid,NULL,(VOID **)&mAcpi);
    if(EFI_ERROR(status))return status;
    return mBs->CreateEventEx(EVT_NOTIFY_SIGNAL,TPL_CALLBACK,ready,NULL,(EFI_GUID *)&ReadyGuid,&event);
}
#else
EFI_STATUS EFIAPI GpioHandoffEntry(EFI_HANDLE image,EFI_SYSTEM_TABLE *system)
{
    (void)image;(void)system;
    return EFI_UNSUPPORTED;
}
#endif

#ifndef GH_HOST_TEST
void *memcpy(void *out,const void *in,UINTN count)
{
    UINT8 *d=out;const UINT8 *s=in;while(count--)*d++=*s++;return out;
}
void *memset(void *out,int value,UINTN count)
{
    UINT8 *d=out;while(count--)*d++=(UINT8)value;return out;
}
#if STAR2LTE_HSI40_COLD_TRIAL
int memcmp(const void *left,const void *right,UINTN count)
{
    const UINT8 *a=left,*b=right;
    while(count--){if(*a!=*b)return (int)*a-(int)*b;++a;++b;}
    return 0;
}
#endif
#endif
