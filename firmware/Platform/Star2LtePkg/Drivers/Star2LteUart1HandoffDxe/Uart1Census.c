/* SPDX-License-Identifier: BSD-2-Clause-Patent */
#include "Uart1Census.h"
#include <string.h>

const uint32_t U1ClockAddresses[U1_CLOCK_WORDS] = {
    0x10800100, 0x10800108, 0x10800120, 0x10800128,
    0x10801814, 0x10802034, 0x108020c4, 0x108020c8,
    0x1080201c, 0x10803028, 0x10802054, 0x10802058,
    0x1080212c, 0x10802080, 0x10802084, 0x10803018
};
const uint32_t U1PinAddresses[U1_PIN_WORDS] = {
    0x140500a0, 0x140500a4, 0x140500a8, 0x140500ac
};
/* No data ports, read-clear error status, or modem-change status. */
const uint32_t U1UartAddresses[U1_UART_WORDS] = {
    0x10840000, 0x10840004, 0x10840008, 0x1084000c,
    0x10840010, 0x10840018, 0x10840028, 0x1084002c,
    0x10840030, 0x10840034, 0x10840038, 0x108400c4,
    0x108400c8
};
static uint32_t Fail(U1_RECORD *r, uint32_t result, uint32_t address,
                     uint32_t mask, uint32_t value)
{
    if (!r->Result) {
        r->Result = result;
        r->FailureAddress = address;
        r->FailureMask = mask;
        r->FailureValue = value;
    }
    return r->Result;
}
void U1Initialize(U1_RECORD *r)
{
    memset(r, 0, sizeof(*r));
    r->Size = sizeof(*r);
    r->Version = U1_RECORD_VERSION;
}
static int GatePasses(uint32_t v)
{
    return (v & (1u << 20)) ? (v & (1u << 21)) != 0 : !(v & (1u << 28));
}
uint32_t U1ClockRefusals(const uint32_t *v)
{
    unsigned i;
    uint32_t refused = 0;
    /* Only the undivided oscillator route is admitted in this first census.
       Both APB bridges must pass: this is conservative, not a topology claim. */
    for (i = 0; i < U1_CLOCK_WORDS; ++i) {
        int good;
        if (i == 0 || i == 2)
            good = !(v[i] & 0x90u);
        else if (i == 1 || i == 3)
            good = !(v[i] & (1u << 28));
        else if (i == 4)
            good = !(v[i] & 0x1001000fu);
        else if (i == 9 || i == 15)
            good = (v[i] & 7u) == 2u;
        else
            good = GatePasses(v[i]);
        if (!good || v[i] == UINT32_MAX) refused |= 1u << i;
    }
    return refused;
}
int U1ControllerReadable(const U1_RECORD *r)
{
    unsigned pass;
    if (!r || r->Result || r->Stage < 2 || r->Stage > 3) return 0;
    pass = r->Stage - 2;
    if (r->ClockValid[pass] != U1_CLOCK_MASK || r->PinValid[pass] != U1_PIN_MASK ||
        r->RouteRefusals[pass] || U1ClockRefusals(r->Clocks[pass])) return 0;
    return !pass || (!r->RouteRefusals[0] && !r->ClockDiff && !r->PinDiff);
}
static int Sample(U1_RECORD *r, const U1_IO *io, const uint32_t *addresses,
                  unsigned count, uint32_t *values, uint32_t *valid,
                  const uint32_t *before, uint32_t *diff)
{
    unsigned i;
    for (i = 0; i < count; ++i) {
        uint32_t value = 0;
        ++r->Reads;
        if (io->Read(io->Context, addresses[i], &value)) {
            Fail(r, U1_IO_ERROR, addresses[i], 0, 0);
            return 0;
        }
        values[i] = value;
        *valid |= 1u << i;
        if (before && value != before[i]) *diff |= 1u << i;
    }
    return 1;
}
static uint32_t Drift(U1_RECORD *r, const uint32_t *addresses, unsigned count,
                      const uint32_t *before, const uint32_t *after, uint32_t diff)
{
    unsigned i;
    for (i = 0; i < count; ++i)
        if (diff & (1u << i))
            return Fail(r, U1_DRIFT, addresses[i], before[i] ^ after[i], after[i]);
    return U1_OK;
}
static int Idle(const uint32_t *u)
{
    return u[0] == 3 && !(u[1] & 0x3fu) && !(u[2] & ~0x770u) &&
        !(u[3] & ~0xf1u) && (u[4] & 7u) == 6u && !(u[5] & 0x01ff01ffu) &&
        u[6] <= 0xffff && u[7] <= 15 && !u[8] && !u[9] &&
        u[10] == 15 && !(u[11] & 1u);
}
uint32_t U1Collect(U1_RECORD *r, const U1_IO *io, uint32_t enabled)
{
    unsigned pass, i;
    if (!enabled) return U1_DISABLED;
    if (!r) return U1_ORDER;
    if (enabled != 1 || r->Size != sizeof(*r) || r->Version != U1_RECORD_VERSION ||
        r->Stage || r->Result || !io || !io->Authorize || !io->Read)
        return Fail(r, U1_ORDER, 0, 0, enabled);
    r->Stage = 1;
    if (io->Authorize(io->Context) != 1) return Fail(r, U1_AUTHORITY, 0, 0, 0);
    for (pass = 0; pass < 2; ++pass) {
        r->Stage = 2 + pass;
        if (!Sample(r, io, U1ClockAddresses, U1_CLOCK_WORDS, r->Clocks[pass],
                    &r->ClockValid[pass], pass ? r->Clocks[0] : NULL, &r->ClockDiff))
            return r->Result;
        r->RouteRefusals[pass] = U1ClockRefusals(r->Clocks[pass]);
        if (!r->RouteRefusals[pass]) r->NominalInputHz[pass] = 26000000;
        if (!Sample(r, io, U1PinAddresses, U1_PIN_WORDS, r->Pins[pass],
                    &r->PinValid[pass], pass ? r->Pins[0] : NULL, &r->PinDiff))
            return r->Result;
        if (U1ControllerReadable(r) &&
            !Sample(r, io, U1UartAddresses, U1_UART_WORDS, r->Uart[pass],
                    &r->UartValid[pass], pass ? r->Uart[0] : NULL, &r->UartDiff))
            return r->Result;
    }
    r->Stage = 4;
    if (!r->ClockDiff) r->Flags |= U1_CLOCKS_PAIRED;
    if (!r->PinDiff) r->Flags |= U1_PINS_PAIRED;
    if (!r->UartDiff && r->UartValid[0] == U1_UART_MASK && r->UartValid[1] == U1_UART_MASK) {
        r->Flags |= U1_UART_PAIRED;
        if (Idle(r->Uart[0])) r->Flags |= U1_IDLE_SHAPE;
    }
    if ((r->Flags & U1_PINS_PAIRED) && (r->Pins[0][0] & 0xffffu) == 0x2222u)
        r->Flags |= U1_PIN_FUNCTION;
    if (Drift(r, U1ClockAddresses, U1_CLOCK_WORDS, r->Clocks[0], r->Clocks[1], r->ClockDiff) ||
        Drift(r, U1PinAddresses, U1_PIN_WORDS, r->Pins[0], r->Pins[1], r->PinDiff) ||
        Drift(r, U1UartAddresses, U1_UART_WORDS, r->Uart[0], r->Uart[1], r->UartDiff))
        return r->Result;
    for (i = 0; i < U1_CLOCK_WORDS; ++i) {
        if ((r->RouteRefusals[0] | r->RouteRefusals[1]) & (1u << i))
            return Fail(r, U1_CLOCK_REFUSED, U1ClockAddresses[i], 1u << i, r->Clocks[0][i]);
    }
    return U1_OK;
}
