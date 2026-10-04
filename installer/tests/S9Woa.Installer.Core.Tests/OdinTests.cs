// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Text;
using S9Woa.Installer.Core.Deploy.Odin;
using S9Woa.Installer.Core.Device;
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
    private sealed class FakePhone(short version, byte[] pit, int failEndCode = 0, bool refuseEndSession = false, int refuseEndSessionFrom = int.MaxValue) : IOdinTransport
    {
        private readonly Queue<byte> _out = new();
        private int _partsLeft;
        private int _partIndex;
        private int _endSessions;

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
                    var refuse = refuseEndSession || ++_endSessions >= refuseEndSessionFrom;
                    Respond(refuse ? unchecked((int)0xFFFFFFFF) : type, refuse ? -1 : 0);
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

    private static OdinTwrpFlasher Flasher(FakePhone phone, bool startTwrp = true) =>
        new(new EmptyRegistry(), () => ["COM4"], _ => phone) { StartTwrpAfterFlash = startTwrp };

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
            Flasher(phone, startTwrp: false).Flash("COM4", image, new SyncProgress(log.Add));

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
    public void WritesTwrpToRecoveryAndBootInOneSessionThenRestarts()
    {
        var phone = new FakePhone(3, PhonePit);
        var length = 3 * 1024 * 1024 + 7;
        var image = WriteImage(length);
        var log = new List<string>();
        try
        {
            var restarted = Flasher(phone).Flash("COM4", image, new SyncProgress(log.Add));

            Assert.True(restarted);
            // RECOVERY (11) then BOOT (10), each the whole image, in a single session - never MISC,
            // which this phone's Download mode will not write.
            Assert.Equal([11, 10], phone.Ends.Select(e => e.Id).Distinct());
            Assert.All(phone.Ends.GroupBy(e => e.Id), g => Assert.Equal(length, g.Sum(e => e.RealSize)));
            Assert.Equal(2L * length, phone.TotalBytes);
            Assert.Equal(1, phone.Commands.Count(c => c == "64/00"));
            Assert.Equal(["67/00", "67/01"], phone.Commands.TakeLast(2));

            var sent = phone.Received.ToArray();
            var padded = 4 * 1024 * 1024; // each image padded to whole 1 MiB parts
            var bytes = File.ReadAllBytes(image);
            Assert.True(bytes.AsSpan().SequenceEqual(sent.AsSpan(0, length)));
            Assert.True(bytes.AsSpan().SequenceEqual(sent.AsSpan(padded, length)));
            Assert.Contains(log, l => l.Contains("BOOT", StringComparison.Ordinal));
            Assert.Equal(1, log.Count(l => l == "  100%"));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void AFullPartitionImageIsWrittenToBootWithoutItsPadding()
    {
        // A prebuilt WinRE recovery is the whole 65 MiB RECOVERY partition, 43 MiB of it image and
        // the rest zeros; BOOT is 55 MiB. RECOVERY gets every byte, BOOT only the used part.
        var phone = new FakePhone(3, PhonePit);
        var length = (int)DeviceCatalog.GalaxyS9Plus.RecoveryPartitionBytes;
        const int data = 43_284_306;   // the real prebuilt's last non-zero byte + 1
        const int used = 43_286_528;   // rounded up to a 4 KiB block
        var bytes = new byte[length];
        new Random(7).NextBytes(bytes.AsSpan(0, data));
        bytes[data - 1] = 0x5A;
        var image = Path.GetTempFileName();
        File.WriteAllBytes(image, bytes);
        try
        {
            Assert.True(Flasher(phone).Flash("COM4", image, null));

            Assert.Equal(length, phone.Ends.Where(e => e.Id == 11).Sum(e => e.RealSize));
            Assert.Equal(used, phone.Ends.Where(e => e.Id == 10).Sum(e => e.RealSize));
            Assert.Equal((long)length + used, phone.TotalBytes);
            var sent = phone.Received.ToArray();
            var recoveryParts = (length + 1024 * 1024 - 1) / (1024 * 1024) * 1024 * 1024;
            Assert.True(bytes.AsSpan(0, used).SequenceEqual(sent.AsSpan(recoveryParts, used)));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Theory]
    [InlineData(10_000, 0, 10_000)]          // no padding: the whole file, even off a block boundary
    [InlineData(3 * 1024 * 1024, 5000, 8192)] // data then zeros: rounded up to a 4 KiB block
    [InlineData(3 * 1024 * 1024, 0, 0)]      // all zeros
    public void UsedLengthDropsOnlyTrailingZeros(int length, int dataBytes, long expected)
    {
        var bytes = new byte[length];
        if (dataBytes > 0)
        {
            bytes[dataBytes - 1] = 1;
        }
        else if (expected == length)
        {
            Array.Fill(bytes, (byte)0xFF);
        }
        Assert.Equal(expected, OdinTwrpFlasher.UsedLength(new MemoryStream(bytes)));
    }

    [Fact]
    public void ARefusedSessionDoesNotRestartThePhone()
    {
        var phone = new FakePhone(3, PhonePit, refuseEndSession: true);
        var image = WriteImage(2 * 1024 * 1024);
        try
        {
            Assert.Throws<OdinException>(() => Flasher(phone).Flash("COM4", image, null));
            // A reboot now would start Android, which puts its own recovery back.
            Assert.DoesNotContain("67/01", phone.Commands);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void WithoutABootPartitionTheFlashStaysInDownloadMode()
    {
        var phone = new FakePhone(3, BuildPit(("RECOVERY", 11, 0, "recovery.img")));
        var image = WriteImage(2 * 1024 * 1024);
        var log = new List<string>();
        try
        {
            Assert.False(Flasher(phone).Flash("COM4", image, new SyncProgress(log.Add)));
            Assert.Equal([11], phone.Ends.Select(e => e.Id).Distinct());
            Assert.DoesNotContain("67/01", phone.Commands);
            Assert.Contains(log, l => l.Contains("stays in Download mode", StringComparison.Ordinal));
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

    [Theory]
    [InlineData("USERDATA", true)]
    [InlineData("userdata", true)]
    [InlineData("RECOVERY", false)]
    [InlineData("BOOT", false)]
    [InlineData("CACHE", false)]
    public void OnlyUserdataRequiresASparseImage(string name, bool expected) =>
        Assert.Equal(expected, OdinSession.RequiresSparseImage(name));

    [Fact]
    public void FlashingRawUserdataIsRejectedBeforeAnyWrite()
    {
        var phone = new FakePhone(3, PhonePit);
        var odin = new OdinSession(phone);
        odin.Handshake();
        odin.BeginSession();
        var userdata = Pit.Find(Pit.Parse(PhonePit), "USERDATA")!;
        var raw = new byte[256 * 1024]; // an NTFS/ext4/raw image: no sparse magic
        new Random(1).NextBytes(raw);

        var e = Assert.Throws<OdinException>(() => odin.FlashPartition(new MemoryStream(raw), raw.Length, userdata));
        Assert.Contains("sparse", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Invalid Magic Code", e.Message, StringComparison.Ordinal);
        Assert.Empty(phone.Received.ToArray()); // nothing was sent to the phone
    }

    [Fact]
    public void FlashingSparseUserdataIsAccepted()
    {
        var phone = new FakePhone(3, PhonePit);
        var odin = new OdinSession(phone);
        odin.Handshake();
        odin.BeginSession();
        var userdata = Pit.Find(Pit.Parse(PhonePit), "USERDATA")!;
        var sparse = new byte[256 * 1024];
        new byte[] { 0x3A, 0xFF, 0x26, 0xED }.CopyTo(sparse, 0); // Android sparse-image magic

        odin.FlashPartition(new MemoryStream(sparse), sparse.Length, userdata);
        Assert.Equal(sparse.Length, phone.Ends.Sum(x => x.RealSize));
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
