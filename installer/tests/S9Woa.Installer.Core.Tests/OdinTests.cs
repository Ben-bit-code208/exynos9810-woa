// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Text;
using S9Woa.Installer.Core.Deploy.Odin;
using S9Woa.Installer.Core.Toolset;

namespace S9Woa.Installer.Core.Tests;

public class OdinTests
{
    private static byte[] BuildPit(params (string Name, int Id, int BinaryType, string File)[] parts)
    {
        var pit = new byte[28 + parts.Length * 132];
        BinaryPrimitives.WriteUInt32LittleEndian(pit, Pit.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(pit.AsSpan(4), parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var e = pit.AsSpan(28 + i * 132);
            BinaryPrimitives.WriteInt32LittleEndian(e, parts[i].BinaryType);
            BinaryPrimitives.WriteInt32LittleEndian(e[4..], 8); // UFS
            BinaryPrimitives.WriteInt32LittleEndian(e[8..], parts[i].Id);
            BinaryPrimitives.WriteInt32LittleEndian(e[24..], 14080);
            Encoding.ASCII.GetBytes(parts[i].Name).CopyTo(e[36..]);
            Encoding.ASCII.GetBytes(parts[i].File).CopyTo(e[68..]);
        }
        return pit;
    }

    private static readonly byte[] PhonePit = BuildPit(
        ("BOOT", 10, 0, "boot.img"), ("RECOVERY", 11, 0, "recovery.img"), ("RADIO", 12, 1, "modem.bin"),
        ("SYSTEM", 18, 0, "system.img"), ("USERDATA", 25, 0, "userdata.img"));

    /// <summary>Simulates a Samsung bootloader in Download mode.</summary>
    private sealed class FakePhone(short version, byte[] pit, int failEndCode = 0, bool refuseEndSession = false) : IOdinTransport
    {
        private readonly Queue<byte> _out = new();
        private int _partsLeft;
        private int _partIndex;

        public int? FilePartSize { get; private set; }
        public long? TotalBytes { get; private set; }
        public MemoryStream Received { get; } = new();
        public List<(int RealSize, int BinaryType, int DeviceType, int Id, int Last)> Ends { get; } = [];
        public List<string> Commands { get; } = [];

        public void Write(ReadOnlySpan<byte> data)
        {
            if (data.SequenceEqual("ODIN"u8))
            {
                Reply("LOKE"u8);
                return;
            }
            if (_partsLeft > 0)
            {
                Assert.Equal(FilePartSize ?? 128 * 1024, data.Length);
                Received.Write(data);
                Respond(0x00, _partIndex++);
                _partsLeft--;
                return;
            }
            Assert.Equal(OdinSession.ControlPacketSize, data.Length);
            var type = BinaryPrimitives.ReadInt32LittleEndian(data);
            var request = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
            var arg = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
            Commands.Add($"{type:X2}/{request:X2}");
            switch ((type, request))
            {
                case (0x64, 0x00):
                    Assert.Equal(int.MaxValue, arg);
                    Respond(0x64, version << 16);
                    break;
                case (0x64, 0x05):
                    FilePartSize = arg;
                    Respond(0x64, 0);
                    break;
                case (0x64, 0x02):
                    TotalBytes = BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
                    Respond(0x64, 0);
                    break;
                case (0x65, 0x01):
                    Respond(0x65, pit.Length);
                    break;
                case (0x65, 0x02):
                    // Every part is padded to 500 bytes, as some bootloaders do.
                    var part = new byte[OdinSession.PitPartSize];
                    pit.AsSpan(arg * 500, Math.Min(500, pit.Length - arg * 500)).CopyTo(part);
                    Reply(part);
                    break;
                case (0x65, 0x03):
                case (0x66, 0x00):
                case (0x67, 0x01):
                    Respond(type, 0);
                    break;
                case (0x67, 0x00):
                    // The SM-G965F (G965FXXUHFVG4) refuses the closing handshake with -1 after a good flash.
                    Respond(refuseEndSession ? unchecked((int)0xFFFFFFFF) : type, refuseEndSession ? -1 : 0);
                    break;
                case (0x66, 0x02):
                    _partsLeft = arg / (FilePartSize ?? 128 * 1024);
                    _partIndex = 0;
                    Assert.Equal(0, arg % (FilePartSize ?? 128 * 1024));
                    Respond(0x66, 0);
                    break;
                case (0x66, 0x03):
                    Assert.Equal(0, arg);
                    Ends.Add((BinaryPrimitives.ReadInt32LittleEndian(data[12..]), BinaryPrimitives.ReadInt32LittleEndian(data[16..]),
                        BinaryPrimitives.ReadInt32LittleEndian(data[20..]), BinaryPrimitives.ReadInt32LittleEndian(data[24..]),
                        BinaryPrimitives.ReadInt32LittleEndian(data[28..])));
                    if (failEndCode != 0)
                    {
                        Respond(unchecked((int)0xFFFFFFFF), failEndCode);
                    }
                    else
                    {
                        Respond(0x66, 0);
                    }
                    break;
                default:
                    throw new InvalidOperationException($"unexpected packet {type:X2}/{request:X2}");
            }
        }

        private void Respond(int type, int value)
        {
            var r = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(r, type);
            BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(4), value);
            Reply(r);
        }

        private void Reply(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                _out.Enqueue(b);
            }
        }

        public void ReadExactly(Span<byte> buffer, TimeSpan timeout)
        {
            if (_out.Count < buffer.Length)
            {
                throw new TimeoutException();
            }
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] = _out.Dequeue();
            }
        }

        public void DiscardInput() => _out.Clear();

        public void Dispose()
        {
        }
    }

    [Fact]
    public void ParsesThePit()
    {
        var entries = Pit.Parse(PhonePit);
        Assert.Equal(5, entries.Count);
        var recovery = Pit.Find(entries, "recovery");
        Assert.NotNull(recovery);
        Assert.Equal(11, recovery.Identifier);
        Assert.Equal("recovery.img", recovery.FlashFileName);
        Assert.True(recovery.IsApplicationProcessor);
        Assert.False(Pit.Find(entries, "RADIO")!.IsApplicationProcessor);
        Assert.Throws<InvalidDataException>(() => Pit.Parse(new byte[64]));
    }

    private static string WriteImage(int length)
    {
        var file = Path.GetTempFileName();
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        File.WriteAllBytes(file, bytes);
        return file;
    }

    private static OdinTwrpFlasher Flasher(FakePhone phone) =>
        new(new EmptyRegistry(), () => ["COM4"], _ => phone);

    [Theory]
    [InlineData(3, 1024 * 1024, 2 * 1024 * 1024 + 12345)]
    [InlineData(0, 128 * 1024, 300_000)]
    [InlineData(3, 1024 * 1024, 42_670_080)] // the real TWRP image size: two sequences
    public void FlashesRecoveryByPitIdentifier(short version, int partSize, int length)
    {
        var phone = new FakePhone(version, PhonePit);
        var image = WriteImage(length);
        try
        {
            var log = new List<string>();
            Flasher(phone).Flash("COM4", image, new SyncProgress(log.Add));

            Assert.Equal(version >= 2 ? (int?)partSize : null, phone.FilePartSize);
            Assert.Equal(length, phone.TotalBytes);
            var sent = phone.Received.ToArray();
            Assert.Equal(0, sent.Length % partSize);
            Assert.True(File.ReadAllBytes(image).AsSpan().SequenceEqual(sent.AsSpan(0, length)));
            Assert.All(sent.Skip(length), b => Assert.Equal(0, b));

            Assert.Equal(length, phone.Ends.Sum(e => e.RealSize));
            Assert.All(phone.Ends, e => Assert.Equal((0, 8, 11), (e.BinaryType, e.DeviceType, e.Id)));
            Assert.Equal(1, phone.Ends[^1].Last);
            Assert.All(phone.Ends.SkipLast(1), e => Assert.Equal(0, e.Last));
            Assert.Equal("67/00", phone.Commands[^1]);
            Assert.DoesNotContain("67/01", phone.Commands);
            Assert.Contains(log, l => l.Contains("100%", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void ARefusedEndSessionMeansTheImageWasRejected()
    {
        var phone = new FakePhone(3, PhonePit, refuseEndSession: true);
        var image = WriteImage(3 * 1024 * 1024);
        try
        {
            var e = Assert.Throws<OdinException>(() => Flasher(phone).Flash("COM4", image, null));
            Assert.Contains("Only official released binaries", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void AsksTheBootloaderToStartTwrpAndReboots()
    {
        var pit = BuildPit(("BOOT", 10, 0, "boot.img"), ("RECOVERY", 11, 0, "recovery.img"), ("MISC", 15, 0, "misc.bin"));
        var phone = new FakePhone(3, pit);
        var image = WriteImage(3 * 1024 * 1024 + 7);
        try
        {
            var restarted = Flasher(phone).Flash("COM4", image, null);

            Assert.True(restarted);
            Assert.Equal([11, 15], phone.Ends.Select(e => e.Id).Distinct());
            var misc = phone.Ends.Single(e => e.Id == 15);
            Assert.Equal(4096, misc.RealSize);
            Assert.Equal(1, misc.Last);
            var sent = phone.Received.ToArray();
            var bcbStart = 4 * 1024 * 1024; // the RECOVERY image padded to whole 1 MiB parts
            Assert.Equal("boot-recovery", System.Text.Encoding.ASCII.GetString(sent, bcbStart, 13));
            Assert.Equal(3 * 1024 * 1024 + 7 + 4096, phone.TotalBytes);
            Assert.Equal(["67/00", "67/01"], phone.Commands.TakeLast(2));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void ExplainsAnAuthenticationRefusal()
    {
        var phone = new FakePhone(3, PhonePit, failEndCode: -5);
        var image = WriteImage(4096);
        try
        {
            var e = Assert.Throws<OdinException>(() => Flasher(phone).Flash("COM4", image, null));
            Assert.Contains("KG/RMM", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void RefusesAPitWithoutRecovery()
    {
        var phone = new FakePhone(3, BuildPit(("BOOT", 10, 0, "boot.img")));
        var image = WriteImage(4096);
        try
        {
            Assert.Throws<OdinException>(() => Flasher(phone).Flash("COM4", image, null));
            Assert.Empty(phone.Ends);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void RejectsAPhoneThatIsNotInDownloadMode()
    {
        var phone = new NotLoke();
        Assert.Throws<OdinException>(() => new OdinSession(phone).Handshake());
    }

    private sealed class NotLoke : IOdinTransport
    {
        public void Write(ReadOnlySpan<byte> data) { }
        public void ReadExactly(Span<byte> buffer, TimeSpan timeout) => "AT\r\n"u8.CopyTo(buffer);
        public void DiscardInput() { }
        public void Dispose() { }
    }

    [Fact]
    public void FindsOnlyThePresentDownloadModePort()
    {
        var registry = new MapRegistry(new()
        {
            [@"SYSTEM\CurrentControlSet\Enum\USB\VID_04E8&PID_685D&Modem\7&1\Device Parameters"] = "COM4",
            [@"SYSTEM\CurrentControlSet\Enum\USB\VID_04E8&PID_6860&Modem\7&2\Device Parameters"] = "COM5",
        });
        Assert.Null(DownloadModePort.Find(registry, ["COM5"]));
        Assert.Equal("COM4", DownloadModePort.Find(registry, ["COM1", "COM4"]));
    }

    private sealed class EmptyRegistry : IRegistryReader
    {
        public bool KeyExists(string p) => false;
        public IReadOnlyList<string> SubKeyNames(string p) => [];
        public string? GetString(string p, string name) => null;
    }

    private sealed class MapRegistry(Dictionary<string, string> portNames) : IRegistryReader
    {
        public bool KeyExists(string p) => portNames.Keys.Any(k => k.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        public IReadOnlyList<string> SubKeyNames(string p) => portNames.Keys
            .Where(k => k.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[(p.Length + 1)..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        public string? GetString(string p, string name) =>
            name == "PortName" && portNames.TryGetValue(p, out var v) ? v : null;
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
