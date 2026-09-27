// SPDX-License-Identifier: BSD-2-Clause-Patent
DefinitionBlock ("", "SSDT", 2, "RWOA  ", "HSI40V3 ", 1)
{
    Scope (\_SB)
    {
        Device (HS40)
        {
            Name (_HID, "RWOA0040")
            Name (_UID, 40)
            Name (_CCA, Zero)
            Method (_STA, 0, NotSerialized) { Return (0x0F) }
            Name (_CRS, ResourceTemplate ()
            {
                Memory32Fixed (ReadWrite, 0x14360000, 0x1000)
                Interrupt (ResourceConsumer, Level, ActiveHigh, Exclusive) { 38 }
            })
        }
        Device (MX40)
        {
            Name (_HID, "EXYN7705")
            Name (_UID, Zero)
            Method (_STA, 0, NotSerialized) { Return (0x0F) }
            Name (_CRS, ResourceTemplate ()
            {
                I2cSerialBusV2 (0x66, ControllerInitiated, 400000, AddressingMode7Bit,
                               "\\_SB.HS40", 0, ResourceConsumer, , Exclusive)
                I2cSerialBusV2 (0x25, ControllerInitiated, 400000, AddressingMode7Bit,
                               "\\_SB.HS40", 0, ResourceConsumer, , Exclusive)
            })
        }
    }
}
