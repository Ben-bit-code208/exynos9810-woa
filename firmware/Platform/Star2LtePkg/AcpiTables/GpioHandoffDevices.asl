// SPDX-License-Identifier: BSD-2-Clause-Patent
// Installed only by the opt-in DXE callback after a verified masked handoff.
#include "../Drivers/Star2LteGpioHandoffDxe/GpioHandoffProfile.h"
#ifndef STAR2LTE_USB2_PHY_PREP
#define STAR2LTE_USB2_PHY_PREP 0
#endif
#ifndef STAR2LTE_USB2_CORE_PREP
#define STAR2LTE_USB2_CORE_PREP 0
#endif
#if STAR2LTE_USB2_CORE_PREP != 0 && STAR2LTE_USB2_CORE_PREP != 1
#error STAR2LTE_USB2_CORE_PREP must be zero or one.
#endif
#if STAR2LTE_USB2_CORE_PREP && !STAR2LTE_USB2_PHY_PREP
#error Stopped-core preparation requires the separate PHY RW profile.
#endif
#if STAR2LTE_USB2_PHY_PREP != 0 && STAR2LTE_USB2_PHY_PREP != 1
#error STAR2LTE_USB2_PHY_PREP must be zero or one.
#endif
#if STAR2LTE_USB2_PHY_PREP && !STAR2LTE_USB2_ACCESS_PROBE
#error PHY preparation requires the eight-resource USB2 profile.
#endif
#ifndef STAR2LTE_GPU_POWER_OBSERVER
#define STAR2LTE_GPU_POWER_OBSERVER 0
#endif
#ifndef STAR2LTE_AUD_POWER_OBSERVER
#define STAR2LTE_AUD_POWER_OBSERVER 0
#endif
#ifndef STAR2LTE_AUD_STATE_OBSERVER
#define STAR2LTE_AUD_STATE_OBSERVER 0
#endif
#ifndef STAR2LTE_AUD_CPU_HOLD
#define STAR2LTE_AUD_CPU_HOLD 0
#endif
#if STAR2LTE_AUD_CPU_HOLD != 0 && STAR2LTE_AUD_CPU_HOLD != 1
#error STAR2LTE_AUD_CPU_HOLD must be zero or one.
#endif
#if STAR2LTE_AUD_CPU_HOLD && STAR2LTE_GPIO_HANDOFF_MODE != 0 && STAR2LTE_GPIO_HANDOFF_MODE != 4
#error AUD CPU request ownership requires the corrected policy-four cohort.
#endif
#if STAR2LTE_AUD_STATE_OBSERVER != 0 && STAR2LTE_AUD_STATE_OBSERVER != 1
#error STAR2LTE_AUD_STATE_OBSERVER must be zero or one.
#endif
#if STAR2LTE_AUD_STATE_OBSERVER && STAR2LTE_GPIO_HANDOFF_MODE != 0 && STAR2LTE_GPIO_HANDOFF_MODE != 4
#error AUD state observation requires the corrected policy-four cohort.
#endif
#if STAR2LTE_AUD_POWER_OBSERVER != 0 && STAR2LTE_AUD_POWER_OBSERVER != 1
#error STAR2LTE_AUD_POWER_OBSERVER must be zero or one.
#endif
#if STAR2LTE_AUD_POWER_OBSERVER && STAR2LTE_GPIO_HANDOFF_MODE != 0 && STAR2LTE_GPIO_HANDOFF_MODE != 4
#error AUD power observation requires the corrected policy-four cohort.
#endif
#ifndef STAR2LTE_GPU_CLOCK_OBSERVER
#define STAR2LTE_GPU_CLOCK_OBSERVER 0
#endif
#ifndef STAR2LTE_GPU_PATH_OBSERVER
#define STAR2LTE_GPU_PATH_OBSERVER 0
#endif
#ifndef STAR2LTE_GPU_ID_OBSERVER
#define STAR2LTE_GPU_ID_OBSERVER 0
#endif
#ifndef STAR2LTE_GPU_CENSUS_OBSERVER
#define STAR2LTE_GPU_CENSUS_OBSERVER 0
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
#if (STAR2LTE_GPU_POWER_OBSERVER + STAR2LTE_GPU_CLOCK_OBSERVER + STAR2LTE_GPU_PATH_OBSERVER + STAR2LTE_GPU_ID_OBSERVER + STAR2LTE_GPU_CENSUS_OBSERVER + STAR2LTE_AUD_POWER_OBSERVER + STAR2LTE_AUD_STATE_OBSERVER + STAR2LTE_AUD_CPU_HOLD) > 1
#error G3D diagnostic devices cannot share the exclusive PMU/CMU pages
#endif
DefinitionBlock ("", "SSDT", 2, "RWOA", "GPIOV3", 1)
{
#if STAR2LTE_GPIO_HANDOFF_MODE == 3 || STAR2LTE_GPIO_HANDOFF_MODE == 4
    External (\_SB.I2C0, DeviceObj)
    Scope (\_SB)
    {
        Device (GPA1)
        {
            Name (_HID, "RWOA0001")
            Name (_UID, Zero)
            Name (_CRS, ResourceTemplate ()
            {
                Memory32Fixed (ReadWrite, 0x14050000, 0x1000)
                Interrupt (ResourceConsumer, Level, ActiveHigh, Exclusive) { 76 }
                Interrupt (ResourceConsumer, Level, ActiveHigh, Exclusive) { 82 }
            })
            Method (_STA, 0) { Return (0x0F) }
        }
        Device (TSP0)
        {
            Name (_HID, "RWOA0761")
            Name (_UID, Zero)
            Name (TGMD, One)
            Name (_DEP, Package (2) { \_SB.I2C0, \_SB.GPA1 })
            Name (_CRS, ResourceTemplate ()
            {
                I2cSerialBusV2 (0x0048, ControllerInitiated, 400000, AddressingMode7Bit,
                    "\\_SB.I2C0", 0x00, ResourceConsumer, , Exclusive)
                GpioInt (Level, ActiveLow, Exclusive, PullDefault, 0,
                    "\\_SB.GPA1", 0, ResourceConsumer, ,) { 0 }
            })
            Method (_STA, 0) { Return (0x0F) }
        }
        Device (BTNS)
        {
            Name (_HID, "RWOA0003")
            Name (_UID, Zero)
            Name (_DEP, Package (1) { \_SB.GPA1 })
            Name (_CRS, ResourceTemplate ()
            {
                GpioIo (Exclusive, PullDefault, 0, 0, IoRestrictionInputOnly,
                    "\\_SB.GPA1", 0, ResourceConsumer) { 11, 12 }
            })
            Method (_STA, 0) { Return (0x0F) }
        }
#if STAR2LTE_AUD_CPU_HOLD
        Device (AUCH)
        {
            Name (_HID, "RWOA0AD2")
            Name (_UID, Zero)
            Name (_CRS, ResourceTemplate ()
            {
                Memory32Fixed (ReadOnly, 0x14064004, 4)
                Memory32Fixed (ReadWrite, 0x1406415C, 4)
                Memory32Fixed (ReadOnly, 0x14064160, 4)
                Memory32Fixed (ReadOnly, 0x14064164, 4)
            })
            Method (_STA, 0) { Return (0x0F) }
        }
#elif STAR2LTE_AUD_STATE_OBSERVER
        Device (AUCS)
        {
            Name (_HID, "RWOA0AD1")
            Name (_UID, Zero)
            Name (_CRS, ResourceTemplate ()
            {
                Memory32Fixed (ReadOnly, 0x14064000, 0x1000)
            })
            Method (_STA, 0) { Return (0x0F) }
        }
#elif STAR2LTE_AUD_POWER_OBSERVER
        Device (AUPO)
        {
            Name (_HID, "RWOA0AD0")
            Name (_UID, Zero)
            Name (_CRS, ResourceTemplate ()
            {
                Memory32Fixed (ReadOnly, 0x14064000, 0x1000)
            })
            Method (_STA, 0) { Return (0x0F) }
        }
#elif STAR2LTE_GPU_POWER_OBSERVER
        Device (G3PO)
        {
            Name (_HID, "RWOA03D0")
            Name (_UID, Zero)
            Name (_CRS, ResourceTemplate ()
            {
                Memory32Fixed (ReadOnly, 0x14064000, 0x1000)
            })
            Method (_STA, 0) { Return (0x0F) }
        }
#elif STAR2LTE_GPU_CLOCK_OBSERVER
        Device (G3CO)
        {
            Name (_HID, "RWOA03D1")
            Name (_UID, Zero)
            Name (_CRS, ResourceTemplate ()
            {
                Memory32Fixed (ReadOnly, 0x14064000, 0x1000)
                Memory32Fixed (ReadOnly, 0x17400000, 0x1000)
            })
            Method (_STA, 0) { Return (0x0F) }
        }
#elif STAR2LTE_GPU_PATH_OBSERVER
        Device (G3DO)
        {
            Name (_HID, "RWOA03D2")
            Name (_UID, Zero)
            Name (_CRS, ResourceTemplate ()
            {
                Memory32Fixed (ReadOnly, 0x14064000, 0x1000)
                Memory32Fixed (ReadOnly, 0x17400000, 0x3000)
            })
            Method (_STA, 0) { Return (0x0F) }
        }
#elif STAR2LTE_GPU_ID_OBSERVER
        Device (G3ID)
        {
            Name (_HID, "RWOA03D3")
            Name (_UID, Zero)
            Name (_CRS, ResourceTemplate ()
            {
                Memory32Fixed (ReadOnly, 0x14064000, 0x1000)
                Memory32Fixed (ReadOnly, 0x17400000, 0x3000)
                Memory32Fixed (ReadOnly, 0x17500000, 0x1000)
            })
            Method (_STA, 0) { Return (0x0F) }
        }
#elif STAR2LTE_GPU_CENSUS_OBSERVER
        Device (G3CS)
        {
            Name (_HID, "RWOA03D4")
            Name (_UID, Zero)
            Name (_CRS, ResourceTemplate ()
            {
                Memory32Fixed (ReadOnly, 0x14064000, 0x1000)
                Memory32Fixed (ReadOnly, 0x17400000, 0x3000)
                Memory32Fixed (ReadOnly, 0x17500000, 0x1000)
                Memory32Fixed (ReadOnly, 0x17501000, 0x1000)
                Memory32Fixed (ReadOnly, 0x17502000, 0x1000)
            })
            Method (_STA, 0) { Return (0x0F) }
        }
#endif
#if STAR2LTE_ACPM_FRAMEWORK_LOOPBACK
        Device (A4LB)
        {
            Name (_HID, "RWOA0A04")
            Name (_UID, Zero)
            Name (_CRS, ResourceTemplate ()
            {
                Memory32Fixed (ReadWrite, 0x14100000, 0x1000)
                Memory32Fixed (ReadOnly, 0x0203E000, 0x1000)
                Memory32Fixed (ReadOnly, 0x02040000, 0x1000)
                Memory32Fixed (ReadWrite, 0x02041000, 0x1000)
#if STAR2LTE_USB2_ACCESS_PROBE
                Memory32Fixed (ReadWrite, 0x1406072C, 0x0004)
                Memory32Fixed (ReadOnly, 0x11000000, 0x4000)
#if STAR2LTE_USB2_CORE_PREP
                Memory32Fixed (ReadWrite, 0x10C00000, 0x10000)
#else
                Memory32Fixed (ReadOnly, 0x10C00000, 0x10000)
#endif
#if STAR2LTE_USB2_PHY_PREP
                Memory32Fixed (ReadWrite, 0x11100000, 0x0200)
#else
                Memory32Fixed (ReadOnly, 0x11100000, 0x0200)
#endif
#endif
            })
            Method (_STA, 0) { Return (0x0F) }
        }
#endif
    }
#endif
}
