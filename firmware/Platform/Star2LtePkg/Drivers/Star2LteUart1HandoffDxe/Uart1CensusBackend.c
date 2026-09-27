/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include "Uart1CensusBackend.h"
#include <string.h>

typedef struct {
    U1_RECORD Record;
    BOOLEAN Authority;
} U1_CONTEXT;
static const UINT64 Bases[3] = {0x10800000, 0x14050000, 0x10840000};
static const UINT64 Lengths[3] = {0x4000, 0x1000, 0x1000};
static BOOLEAN Span(const EFI_GCD_MEMORY_SPACE_DESCRIPTOR *d, UINTN i)
{
    return !d->ImageHandle && !d->DeviceHandle && d->BaseAddress <= Bases[i] &&
        d->BaseAddress + d->Length >= d->BaseAddress &&
        Bases[i] + Lengths[i] <= d->BaseAddress + d->Length;
}
EFI_STATUS U1PrepareMemory(EFI_DXE_SERVICES *dxe)
{
    EFI_GCD_MEMORY_SPACE_DESCRIPTOR d;
    EFI_STATUS status;
    UINTN i;
    UINT32 missing = 0;
    if (!dxe || dxe->Hdr.Signature != DXE_SERVICES_SIGNATURE ||
        dxe->Hdr.HeaderSize < OFFSET_OF(EFI_DXE_SERVICES, GetMemorySpaceDescriptor) +
            sizeof(dxe->GetMemorySpaceDescriptor) || !dxe->GetMemorySpaceDescriptor)
        return EFI_INVALID_PARAMETER;
    /* Inspect every range before making even a GCD registration change. */
    for (i = 0; i < 3; ++i) {
        memset(&d, 0, sizeof(d));
        status = dxe->GetMemorySpaceDescriptor(Bases[i], &d);
        if (status != EFI_SUCCESS) return status;
        if (!Span(&d, i)) return EFI_ACCESS_DENIED;
        if (d.GcdMemoryType == EfiGcdMemoryTypeNonExistent) {
            if (d.Attributes || d.Capabilities) return EFI_ACCESS_DENIED;
            missing |= 1u << i;
        } else if (d.GcdMemoryType != EfiGcdMemoryTypeMemoryMappedIo ||
                   d.Attributes != EFI_MEMORY_UC || !(d.Capabilities & EFI_MEMORY_UC))
            return EFI_ACCESS_DENIED;
    }
    if (missing && (dxe->Hdr.HeaderSize < OFFSET_OF(EFI_DXE_SERVICES, SetMemorySpaceAttributes) +
            sizeof(dxe->SetMemorySpaceAttributes) || !dxe->AddMemorySpace ||
            !dxe->SetMemorySpaceAttributes)) return EFI_ACCESS_DENIED;
    for (i = 0; i < 3; ++i) {
        if (missing & (1u << i)) {
            status = dxe->AddMemorySpace(EfiGcdMemoryTypeMemoryMappedIo, Bases[i], Lengths[i], EFI_MEMORY_UC);
            if (status != EFI_SUCCESS) return status;
            status = dxe->SetMemorySpaceAttributes(Bases[i], Lengths[i], EFI_MEMORY_UC);
            if (status != EFI_SUCCESS) return status;
        }
    }
    for (i = 0; i < 3; ++i) {
        memset(&d, 0, sizeof(d));
        status = dxe->GetMemorySpaceDescriptor(Bases[i], &d);
        if (status != EFI_SUCCESS) return status;
        if (!Span(&d, i) || d.GcdMemoryType != EfiGcdMemoryTypeMemoryMappedIo ||
            d.Attributes != EFI_MEMORY_UC || !(d.Capabilities & EFI_MEMORY_UC))
            return EFI_ACCESS_DENIED;
    }
    return EFI_SUCCESS;
}
static int Authorize(void *opaque)
{
    return ((U1_CONTEXT *)opaque)->Authority ? 1 : 0;
}
static int Read(void *opaque, uint32_t address, uint32_t *value)
{
    U1_CONTEXT *c = opaque;
    UINTN i;
    BOOLEAN allowed = FALSE;
    if (!c->Authority) return -1;
    for (i = 0; i < U1_CLOCK_WORDS; ++i)
        if (address == U1ClockAddresses[i]) allowed = TRUE;
    for (i = 0; i < U1_PIN_WORDS; ++i)
        if (address == U1PinAddresses[i]) allowed = TRUE;
    for (i = 0; i < U1_UART_WORDS; ++i)
        if (address == U1UartAddresses[i] && U1ControllerReadable(&c->Record)) allowed = TRUE;
    if (!allowed) return -1;
#ifdef U1_HOST_TEST
    extern int U1TestRead(UINT32, UINT32 *);
    return U1TestRead(address, value);
#else
    __asm__ volatile("dsb sy" ::: "memory");
    *value = *(volatile UINT32 *)(UINTN)address;
    __asm__ volatile("dsb sy" ::: "memory");
    return 0;
#endif
}
static void Put32(UINT8 *p, UINT32 value)
{
    UINTN i;
    for (i = 0; i < 4; ++i) p[i] = (UINT8)(value >> (8 * i));
}
EFI_STATUS U1CensusAndPublish(EFI_DXE_SERVICES *dxe, EFI_ACPI_TABLE_PROTOCOL *acpi,
                             CONST UINT8 producer[16], BOOLEAN authority)
{
    U1_CONTEXT c;
    U1_IO io = {&c, Authorize, Read};
    EFI_STATUS gcd, status;
    UINT8 table[U1_TABLE_BYTES], sum = 0, nonzero = 0;
    UINTN i, key = 0;
    if (!authority || !producer || !acpi || !acpi->InstallAcpiTable) return EFI_ACCESS_DENIED;
    for (i = 0; i < 16; ++i) nonzero |= producer[i];
    if (!nonzero) return EFI_INVALID_PARAMETER;
    memset(&c, 0, sizeof(c));
    U1Initialize(&c.Record);
    gcd = U1PrepareMemory(dxe);
    c.Authority = gcd == EFI_SUCCESS;
    if (c.Authority) (void)U1Collect(&c.Record, &io, 1);
    else c.Record.Result = U1_AUTHORITY;
    memset(table, 0, sizeof(table));
    memcpy(table, "U1CS", 4);
    Put32(table + 4, sizeof(table));
    table[8] = 1;
    memcpy(table + 10, "RWOA  U1COLD1 ", 14);
    Put32(table + 24, 1);
    Put32(table + 28, 0x57464847);
    Put32(table + 32, 1);
    memcpy(table + 36, producer, 16);
    Put32(table + 52, (UINT32)gcd);
    Put32(table + 56, (UINT32)((UINT64)gcd >> 32));
    memcpy(table + 60, &c.Record, sizeof(c.Record));
    for (i = 0; i < sizeof(table); ++i) sum = (UINT8)(sum + table[i]);
    table[9] = (UINT8)(0 - sum);
    status = acpi->InstallAcpiTable(acpi, table, sizeof(table), &key);
    if (status != EFI_SUCCESS) return status;
    if (gcd != EFI_SUCCESS) return gcd;
    return c.Record.Result ? EFI_DEVICE_ERROR : EFI_SUCCESS;
}
