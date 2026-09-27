// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;

namespace S9Woa.Installer.Core.Recovery;

public enum RecoveryAction : byte
{
    Query = 0,
    Arm = 1,
    Cancel = 2,
}

public enum RecoveryTicketState : uint
{
    Idle = 0,
    Armed = 1,
    Committed = 2,
}

public enum RecoveryTicketStatus : uint
{
    Ok = 0,
    NotReady = 1,
    Busy = 2,
    WrongToken = 3,
}

public sealed record RecoveryTicketReply(RecoveryTicketStatus Status, RecoveryTicketState State, uint Token,
    uint LifetimeSeconds, bool Ready);

/// <summary>
/// Encoder/decoder for the Exynos9810Ufs "clean recovery" (UCR1) vendor CDB.
/// Arming a ticket and then requesting a normal Windows restart makes the UFS
/// driver's shutdown path reboot the phone into recovery (TWRP).
/// Mirrors <c>drivers/Exynos9810Ufs/Exynos9810UfsDiag.h</c>.
/// </summary>
public static class RecoveryTicketProtocol
{
    public const byte Opcode = 0xD4;
    public const ushort Subcode = 0x9810;
    public const uint Confirm = 0x43525354;   // 'CRST'
    public const uint Signature = 0x31524355; // 'UCR1'
    public const uint Version = 1;
    public const int ReplyBytes = 32;
    public const int CdbBytes = 16;

    public static byte[] BuildCdb(RecoveryAction action, uint token)
    {
        if ((action == RecoveryAction.Query) != (token == 0))
        {
            throw new ArgumentException("QUERY requires token 0; ARM/CANCEL require a nonzero token.", nameof(token));
        }
        var cdb = new byte[CdbBytes];
        cdb[0] = Opcode;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(1), Subcode);
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(3), Confirm);
        cdb[7] = (byte)action;
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(8), token);
        return cdb;
    }

    public static RecoveryTicketReply ParseReply(ReadOnlySpan<byte> reply)
    {
        if (reply.Length != ReplyBytes)
        {
            throw new FormatException($"UCR1 reply must be {ReplyBytes} bytes, got {reply.Length}.");
        }
        uint[] w = new uint[8];
        for (var i = 0; i < w.Length; i++)
        {
            w[i] = BinaryPrimitives.ReadUInt32LittleEndian(reply[(i * 4)..]);
        }
        if (w[0] != Signature || w[1] != Version || w[2] != ReplyBytes)
        {
            throw new FormatException("Not a UCR1 v1 reply; the installed storage driver does not support Restart to TWRP.");
        }
        return new RecoveryTicketReply((RecoveryTicketStatus)w[3], (RecoveryTicketState)w[4], w[5], w[6], w[7] != 0);
    }

    public static uint NewToken()
    {
        Span<byte> b = stackalloc byte[4];
        uint token;
        do
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(b);
            token = BinaryPrimitives.ReadUInt32LittleEndian(b);
        } while (token == 0);
        return token;
    }
}
