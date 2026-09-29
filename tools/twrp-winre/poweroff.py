# SPDX-License-Identifier: BSD-2-Clause-Patent
"""Route USB-connected TWRP "Power Off" through Samsung's normal reboot hook.

Stock TWRP on this device, when connected over USB, does not actually power the
phone off from the GUI "Turn off" action - it drops into a state that looks hung.
A four-byte change to one branch in the recovery kernel sends that path through
Samsung's `sec_reboot` hook instead, which powers down cleanly.

This is a byte patch on the KERNEL, which the WinRE build carries across from the
official TWRP image untouched, so it is gated on the kernel's own SHA-256 plus
the exact bytes at the patch site: it refuses to touch anything that is not the
known-good star2lte TWRP 3.7.0_9-0 kernel. The boot image's SHA-1 id is
recomputed by the caller after patching (BootImage.serialize(refresh_id=True)).

The offsets and signatures are the ones measured on the deployed device (the
research `patch_twrp_poweroff_route.py`); here they gate the *kernel* rather than
a whole 68 MiB partition image, because the WinRE build starts from the 42 MiB
official file, not a partition dump.
"""

from __future__ import annotations

import hashlib
import struct

# The star2lte TWRP 3.7.0_9-0 recovery kernel (identical in the official image
# and in the deployed research build - verified byte-for-byte).
KERNEL_SHA256 = "1228ba84942d1b16f565c42af242eaf1ce084eb373f71cfa40fee74a7e7ef594"

# All offsets are relative to the start of the kernel image.
SEC_REBOOT_OFFSET = 0x00A912CC
SEC_POWER_OFF_OFFSET = 0x00A916C4
RESTART_CALL_OFFSET = 0x00A91ACC
RESTART_CALL_BEFORE = 0xD63F0040  # blr x2
RESTART_CALL_AFTER = 0x97FFFE00   # bl sec_reboot

SEC_REBOOT_SIG = bytes.fromhex(
    "fd7bbca9e203002afd030091f35301a9"
    "f55b02a9f30301aadf4203d5e11c00b4"
)
SEC_POWER_OFF_SIG = bytes.fromhex(
    "fd7bb9a900500090010080d2fd030091"
    "00403e91f55b02a9f35301a9f76303a9"
    "f96b04a90097ff97"
)


class PatchError(RuntimeError):
    pass


def _encode_bl(source: int, target: int) -> int:
    delta = target - source
    if delta % 4 != 0:
        raise PatchError("BL target is not instruction-aligned")
    imm = delta // 4
    if not -(1 << 25) <= imm < (1 << 25):
        raise PatchError("BL target is out of range")
    return 0x94000000 | (imm & 0x03FFFFFF)


def is_patchable(kernel: bytes) -> bool:
    """True if this is the known kernel and the patch site is unmodified."""
    if hashlib.sha256(kernel).hexdigest() != KERNEL_SHA256:
        return False
    if kernel[SEC_REBOOT_OFFSET:SEC_REBOOT_OFFSET + len(SEC_REBOOT_SIG)] != SEC_REBOOT_SIG:
        return False
    if kernel[SEC_POWER_OFF_OFFSET:SEC_POWER_OFF_OFFSET + len(SEC_POWER_OFF_SIG)] != SEC_POWER_OFF_SIG:
        return False
    actual = struct.unpack_from("<I", kernel, RESTART_CALL_OFFSET)[0]
    return actual == RESTART_CALL_BEFORE


def patch_kernel(kernel: bytes) -> bytes:
    """Apply the power-off route patch, refusing anything but the known kernel."""
    digest = hashlib.sha256(kernel).hexdigest()
    if digest != KERNEL_SHA256:
        raise PatchError(
            f"kernel sha256 {digest[:16]}... is not the known star2lte TWRP kernel; "
            "refusing to patch")
    if kernel[SEC_REBOOT_OFFSET:SEC_REBOOT_OFFSET + len(SEC_REBOOT_SIG)] != SEC_REBOOT_SIG:
        raise PatchError("sec_reboot signature mismatch")
    if kernel[SEC_POWER_OFF_OFFSET:SEC_POWER_OFF_OFFSET + len(SEC_POWER_OFF_SIG)] != SEC_POWER_OFF_SIG:
        raise PatchError("sec_power_off signature mismatch")
    actual = struct.unpack_from("<I", kernel, RESTART_CALL_OFFSET)[0]
    if actual != RESTART_CALL_BEFORE:
        raise PatchError(f"restart call precondition failed: 0x{actual:08X}")

    encoded = _encode_bl(RESTART_CALL_OFFSET, SEC_REBOOT_OFFSET)
    if encoded != RESTART_CALL_AFTER:
        raise PatchError(f"derived BL mismatch: 0x{encoded:08X}")

    out = bytearray(kernel)
    struct.pack_into("<I", out, RESTART_CALL_OFFSET, encoded)
    changed = [i for i in range(len(kernel)) if kernel[i] != out[i]]
    if changed != list(range(RESTART_CALL_OFFSET, RESTART_CALL_OFFSET + 4)):
        raise PatchError("patch changed unexpected bytes")
    return bytes(out)
