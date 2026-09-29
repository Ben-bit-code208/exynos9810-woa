// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace S9Woa.Installer.Core.Twrp;

/// <summary>
/// Routes USB-connected TWRP "Power Off" through Samsung's <c>sec_reboot</c> hook
/// with a four-byte change to one branch in the recovery kernel, so the GUI
/// "Turn off" action powers the phone down cleanly instead of hanging. The patch
/// is gated on the kernel's own SHA-256 plus the exact bytes at the patch site,
/// so it refuses anything but the known-good star2lte TWRP 3.7.0_9-0 kernel. The
/// boot image id is recomputed by the caller (<see cref="AndroidBootImage.Serialize"/>).
/// Mirrors the reference Python <c>poweroff.py</c>.
/// </summary>
public static class PowerOffRoutePatch
{
    public const string KernelSha256 = "1228ba84942d1b16f565c42af242eaf1ce084eb373f71cfa40fee74a7e7ef594";

    private const int SecRebootOffset = 0x00A912CC;
    private const int SecPowerOffOffset = 0x00A916C4;
    private const int RestartCallOffset = 0x00A91ACC;
    private const uint RestartCallBefore = 0xD63F0040; // blr x2
    private const uint RestartCallAfter = 0x97FFFE00;  // bl sec_reboot

    private static readonly byte[] SecRebootSig = Convert.FromHexString(
        "fd7bbca9e203002afd030091f35301a9f55b02a9f30301aadf4203d5e11c00b4");
    private static readonly byte[] SecPowerOffSig = Convert.FromHexString(
        "fd7bb9a900500090010080d2fd03009100403e91f55b02a9f35301a9f76303a9f96b04a90097ff97");

    /// <summary>True if this is the known kernel and the patch site is unmodified.</summary>
    public static bool IsPatchable(byte[] kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        if (Convert.ToHexString(SHA256.HashData(kernel)).ToLowerInvariant() != KernelSha256)
        {
            return false;
        }
        if (!SigAt(kernel, SecRebootOffset, SecRebootSig) || !SigAt(kernel, SecPowerOffOffset, SecPowerOffSig))
        {
            return false;
        }
        return RestartCallOffset + 4 <= kernel.Length
            && BinaryPrimitives.ReadUInt32LittleEndian(kernel.AsSpan(RestartCallOffset)) == RestartCallBefore;
    }

    /// <summary>Apply the patch, refusing anything but the known kernel.</summary>
    public static byte[] Patch(byte[] kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        var digest = Convert.ToHexString(SHA256.HashData(kernel)).ToLowerInvariant();
        if (digest != KernelSha256)
        {
            throw new InvalidOperationException(
                $"Kernel SHA-256 {digest[..16]}... is not the known star2lte TWRP kernel; refusing to patch.");
        }
        if (!SigAt(kernel, SecRebootOffset, SecRebootSig))
        {
            throw new InvalidOperationException("sec_reboot signature mismatch.");
        }
        if (!SigAt(kernel, SecPowerOffOffset, SecPowerOffSig))
        {
            throw new InvalidOperationException("sec_power_off signature mismatch.");
        }
        var actual = BinaryPrimitives.ReadUInt32LittleEndian(kernel.AsSpan(RestartCallOffset));
        if (actual != RestartCallBefore)
        {
            throw new InvalidOperationException($"Restart call precondition failed: 0x{actual:X8}.");
        }
        var encoded = EncodeBl(RestartCallOffset, SecRebootOffset);
        if (encoded != RestartCallAfter)
        {
            throw new InvalidOperationException($"Derived BL mismatch: 0x{encoded:X8}.");
        }

        var patched = (byte[])kernel.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(RestartCallOffset), encoded);
        for (var i = 0; i < kernel.Length; i++)
        {
            var changed = kernel[i] != patched[i];
            var inSite = i >= RestartCallOffset && i < RestartCallOffset + 4;
            if (changed != inSite)
            {
                throw new InvalidOperationException("Patch changed unexpected bytes.");
            }
        }
        return patched;
    }

    private static bool SigAt(byte[] data, int offset, byte[] sig) =>
        offset + sig.Length <= data.Length && data.AsSpan(offset, sig.Length).SequenceEqual(sig);

    private static uint EncodeBl(int source, int target)
    {
        var delta = target - source;
        if (delta % 4 != 0)
        {
            throw new InvalidOperationException("BL target is not instruction-aligned.");
        }
        var imm = delta / 4;
        if (imm < -(1 << 25) || imm >= (1 << 25))
        {
            throw new InvalidOperationException("BL target is out of range.");
        }
        return 0x94000000u | ((uint)imm & 0x03FFFFFF);
    }
}
