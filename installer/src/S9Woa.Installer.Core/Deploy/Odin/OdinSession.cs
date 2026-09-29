// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Text;

namespace S9Woa.Installer.Core.Deploy.Odin;

/// <summary>A byte pipe to a phone in Download mode (the Samsung driver's COM port, or a test fake).</summary>
public interface IOdinTransport : IDisposable
{
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Fills <paramref name="buffer"/> completely or throws <see cref="TimeoutException"/>.</summary>
    void ReadExactly(Span<byte> buffer, TimeSpan timeout);

    /// <summary>Drops any bytes already received and not yet read.</summary>
    void DiscardInput();
}

public sealed class OdinException(string message) : IOException(message);

/// <summary>Fills the argument bytes (offset 8 onwards) of a control packet.</summary>
public delegate void PacketFill(Span<byte> packet);

/// <summary>
/// Samsung's Download-mode ("Odin"/"LOKE") protocol: 1024-byte little-endian control packets,
/// 8-byte responses whose first byte is 0xFF on failure, a PIT dump in 500-byte parts, and files
/// sent in sequences of fixed-size parts. Implemented from the protocol as documented by the
/// open-source Heimdall and Thor projects.
/// </summary>
public sealed class OdinSession
{
    public const int ControlPacketSize = 1024;
    public const int ResponseSize = 8;
    public const int PitPartSize = 500;

    private const int Session = 0x64;
    private const int PitFile = 0x65;
    private const int FileTransfer = 0x66;
    private const int EndSessionType = 0x67;

    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);
    private readonly IOdinTransport _transport;

    public OdinSession(IOdinTransport transport) => _transport = transport;

    /// <summary>Protocol version the bootloader reported when the session began.</summary>
    public int ProtocolVersion { get; private set; }

    public int FilePartSize { get; private set; } = 128 * 1024;

    public int PartsPerSequence { get; private set; } = 240;

    public TimeSpan SequenceTimeout { get; private set; } = TimeSpan.FromSeconds(30);

    public void Handshake()
    {
        _transport.DiscardInput();
        _transport.Write("ODIN"u8);
        Span<byte> reply = stackalloc byte[4];
        _transport.ReadExactly(reply, ControlTimeout);
        if (!reply.SequenceEqual("LOKE"u8))
        {
            throw new OdinException($"The phone answered \"{Encoding.ASCII.GetString(reply)}\" instead of LOKE; it is not in Download mode.");
        }
    }

    public void BeginSession()
    {
        // Offer the highest protocol version; the bootloader answers with the one it speaks.
        var reply = Control(Session, 0x00, "Begin session", p => BinaryPrimitives.WriteInt32LittleEndian(p[8..], int.MaxValue));
        ProtocolVersion = BinaryPrimitives.ReadInt16LittleEndian(reply.AsSpan(6));
        if (ProtocolVersion >= 2)
        {
            FilePartSize = 1024 * 1024;
            PartsPerSequence = 30;
            SequenceTimeout = TimeSpan.FromMinutes(2);
            var size = FilePartSize;
            Control(Session, 0x05, "Set file part size", p => BinaryPrimitives.WriteInt32LittleEndian(p[8..], size));
        }
    }

    public void SetTotalBytes(long total) =>
        Control(Session, 0x02, "Set total bytes", p => BinaryPrimitives.WriteInt64LittleEndian(p[8..], total));

    public byte[] DumpPit()
    {
        var reply = Control(PitFile, 0x01, "Request PIT");
        var size = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(4));
        if (size is <= 0 or > 1024 * 1024)
        {
            throw new OdinException($"The phone reported a {size}-byte PIT.");
        }
        var pit = new byte[size];
        var parts = (size + PitPartSize - 1) / PitPartSize;
        for (var i = 0; i < parts; i++)
        {
            var index = i;
            SendControl(PitFile, 0x02, p => BinaryPrimitives.WriteInt32LittleEndian(p[8..], index));
            _transport.ReadExactly(pit.AsSpan(i * PitPartSize, Math.Min(PitPartSize, size - i * PitPartSize)), ControlTimeout);
        }
        // The final part may be padded (or followed by an empty transfer); drop anything left over.
        Thread.Sleep(200);
        _transport.DiscardInput();
        Control(PitFile, 0x03, "End PIT transfer");
        return pit;
    }

    /// <summary>
    /// Writes <paramref name="length"/> bytes from <paramref name="data"/> to the partition
    /// described by <paramref name="entry"/>. <paramref name="progress"/> receives bytes sent.
    /// </summary>
    public void FlashPartition(Stream data, long length, PitEntry entry, Action<long>? progress = null)
    {
        if (!entry.IsApplicationProcessor)
        {
            throw new NotSupportedException($"{entry.PartitionName} is a modem partition; only AP partitions are supported.");
        }
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
        Control(FileTransfer, 0x00, "Start file transfer");

        var sequenceBytes = (long)FilePartSize * PartsPerSequence;
        var sequences = (int)((length + sequenceBytes - 1) / sequenceBytes);
        var part = new byte[FilePartSize];
        long sent = 0;
        for (var s = 0; s < sequences; s++)
        {
            var last = s == sequences - 1;
            var realSize = (int)Math.Min(sequenceBytes, length - s * sequenceBytes);
            var parts = (realSize + FilePartSize - 1) / FilePartSize;
            var alignedSize = parts * FilePartSize;
            Control(FileTransfer, 0x02, $"Start sequence {s + 1}/{sequences}",
                p => BinaryPrimitives.WriteInt32LittleEndian(p[8..], alignedSize));

            for (var j = 0; j < parts; j++)
            {
                Array.Clear(part);
                var want = (int)Math.Min(FilePartSize, length - sent);
                data.ReadExactly(part, 0, want);
                _transport.Write(part);
                var reply = ReadResponse(0x00, $"File part {j + 1}/{parts}", ControlTimeout);
                var acked = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(4));
                if (acked != j)
                {
                    throw new OdinException($"The phone acknowledged part {acked} while part {j} was sent.");
                }
                sent += want;
                progress?.Invoke(sent);
            }

            SendControl(FileTransfer, 0x03, p =>
            {
                BinaryPrimitives.WriteInt32LittleEndian(p[8..], 0); // destination: phone (AP)
                BinaryPrimitives.WriteInt32LittleEndian(p[12..], realSize);
                BinaryPrimitives.WriteInt32LittleEndian(p[16..], entry.BinaryType);
                BinaryPrimitives.WriteInt32LittleEndian(p[20..], entry.DeviceType);
                BinaryPrimitives.WriteInt32LittleEndian(p[24..], entry.Identifier);
                BinaryPrimitives.WriteInt32LittleEndian(p[28..], last ? 1 : 0);
                // 32: EFS clear, 36: bootloader update; both off.
            });
            ReadResponse(FileTransfer, $"Write {entry.PartitionName} (sequence {s + 1}/{sequences})", SequenceTimeout, isWrite: true);
        }
    }

    /// <summary>Ends the session without rebooting: the phone stays in Download mode.</summary>
    public void EndSession() => Control(EndSessionType, 0x00, "End session");

    public void Reboot() => Control(EndSessionType, 0x01, "Reboot");

    internal static byte[] Packet(int type, int request, PacketFill? fill = null)
    {
        var p = new byte[ControlPacketSize];
        BinaryPrimitives.WriteInt32LittleEndian(p, type);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4), request);
        fill?.Invoke(p);
        return p;
    }

    private void SendControl(int type, int request, PacketFill? fill = null) =>
        _transport.Write(Packet(type, request, fill));

    private byte[] Control(int type, int request, string what, PacketFill? fill = null)
    {
        SendControl(type, request, fill);
        return ReadResponse(type, what, ControlTimeout);
    }

    private byte[] ReadResponse(int expectedType, string what, TimeSpan timeout, bool isWrite = false)
    {
        var reply = new byte[ResponseSize];
        try
        {
            _transport.ReadExactly(reply, timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{what}: the phone did not answer within {timeout.TotalSeconds:0} s.");
        }
        if (reply[0] == 0xFF)
        {
            throw new OdinException($"{what} failed: {Describe(BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(4)), isWrite)}");
        }
        var type = BinaryPrimitives.ReadInt32LittleEndian(reply);
        if (type != expectedType)
        {
            throw new OdinException($"{what}: unexpected reply 0x{type:X2} (expected 0x{expectedType:X2}).");
        }
        return reply;
    }

    internal const string OfficialBinariesOnlyHelp =
        "If the phone shows \"Only official released binaries are allowed to be flashed\", its KG/RMM state blocks custom images: "
        + "boot Android, connect to the internet, make sure Developer options > OEM unlocking is on, then try again.";

    internal static string Describe(int code, bool isWrite) => (code, isWrite) switch
    {
        (-5, true) => $"the phone refused the image (authentication). {OfficialBinariesOnlyHelp}",
        (-7, true) => "ext4 error (code -7).",
        (-6, true) => "the image does not fit the partition (code -6).",
        (-4, true) => "writing to storage failed (code -4).",
        (-3, true) => "erasing storage failed (code -3).",
        (-2, true) => "the partition is write-protected (code -2).",
        _ => $"the phone reported error {code}.",
    };
}
