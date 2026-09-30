// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Security.Cryptography;
using S9Woa.Installer.Core.Deploy;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Tests;

public class TransferTests
{
    private const long Mib = 1024 * 1024;

    /// <summary>Models one partition as a mutable byte array plus SD files, honouring windowed dd/hash.</summary>
    private sealed class FakePartitionRunner : IProcessRunner
    {
        private readonly Dictionary<string, byte[]> _sd = new(StringComparer.Ordinal);
        public byte[] Partition { get; }
        public string PartitionName { get; }
        public bool Mounted { get; set; }
        public bool StickyMount { get; init; }

        public FakePartitionRunner(string name, long sizeBytes)
        {
            // The phone's by-name links are upper case; callers use lower case on purpose.
            PartitionName = name.ToUpperInvariant();
            Partition = new byte[sizeBytes];
        }

        public int Pushes { get; private set; }
        public int Hashes { get; private set; }
        public List<string> PushTargets { get; } = [];
        public List<string> Shells { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var verb = arguments[2];
            if (verb == "push")
            {
                _sd[arguments[4]] = File.ReadAllBytes(arguments[3]);
                PushTargets.Add(arguments[4]);
                Pushes++;
                return Ok("pushed");
            }
            var cmd = arguments[3];
            Shells.Add(cmd);
            var dev = $"/dev/block/by-name/{PartitionName}";
            if (cmd.StartsWith("ls -l /dev/block/by-name", StringComparison.Ordinal))
            {
                return Ok($"lrwxrwxrwx 1 root root 21 2026-09-29 12:00 {PartitionName} -> /dev/block/sda25\n");
            }
            if (cmd.StartsWith("blockdev --getsize64", StringComparison.Ordinal))
            {
                return cmd.EndsWith(dev, StringComparison.Ordinal)
                    ? Ok($"{Partition.Length}\n")
                    : Task.FromResult(new ProcessResult(1, "", "No such file or directory"));
            }
            if (cmd.Contains("/proc/mounts", StringComparison.Ordinal))
            {
                return Ok(Mounted ? "yes\n" : "no\n");
            }
            if (cmd.StartsWith("mkdir", StringComparison.Ordinal) || cmd.StartsWith("rm ", StringComparison.Ordinal))
            {
                return Ok("");
            }
            if (cmd.StartsWith("umount", StringComparison.Ordinal))
            {
                if (!StickyMount)
                {
                    Mounted = false;
                }
                return Ok("");
            }
            if (cmd.StartsWith("dd if=", StringComparison.Ordinal) && cmd.Contains($"of={dev}", StringComparison.Ordinal))
            {
                var sd = Between(cmd, "if=", " ");
                if (sd == "/dev/zero")
                {
                    var zseek = long.Parse(Between(cmd, "seek=", " "));
                    var zcount = long.Parse(Between(cmd, "count=", " "));
                    Array.Clear(Partition, (int)(zseek * Mib), (int)(zcount * Mib));
                    return Ok("");
                }
                var src = _sd[sd];
                if (cmd.Contains("seek=", StringComparison.Ordinal))
                {
                    var seek = long.Parse(Between(cmd, "seek=", " "));
                    var count = long.Parse(Between(cmd, "count=", " "));
                    Array.Copy(src, 0, Partition, seek * Mib, count * Mib);
                }
                else
                {
                    Array.Copy(src, 0, Partition, 0, src.Length);
                }
                return Ok("");
            }
            if (cmd.StartsWith($"dd if={dev}", StringComparison.Ordinal) && cmd.Contains("sha256sum", StringComparison.Ordinal))
            {
                Hashes++;
                var skip = long.Parse(Between(cmd, "skip=", " "));
                var count = long.Parse(Between(cmd, "count=", " "));
                var slice = Partition.AsSpan((int)(skip * Mib), (int)(count * Mib)).ToArray();
                return Ok($"{Hex(slice)}  -\n");
            }
            if (cmd.StartsWith("head -c", StringComparison.Ordinal) && cmd.Contains(dev, StringComparison.Ordinal) && cmd.Contains("sha256sum", StringComparison.Ordinal))
            {
                var n = long.Parse(Between(cmd, "head -c ", " "));
                return Ok($"{Hex(Partition.AsSpan(0, (int)n).ToArray())}  -\n");
            }
            return Ok("");

            Task<ProcessResult> Ok(string o) => Task.FromResult(new ProcessResult(0, o, ""));
        }

        private static string Between(string s, string start, string end)
        {
            var i = s.IndexOf(start, StringComparison.Ordinal) + start.Length;
            var j = s.IndexOf(end, i, StringComparison.Ordinal);
            return j < 0 ? s[i..] : s[i..j];
        }

        private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    }

    [Fact]
    public async Task WritesRawImageInWindowsAndVerifies()
    {
        var image = Path.GetTempFileName();
        try
        {
            var bytes = new byte[5 * Mib];
            new Random(7).NextBytes(bytes);
            await File.WriteAllBytesAsync(image, bytes);

            var runner = new FakePartitionRunner("userdata", 8 * Mib);
            var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);
            await new TransferService(twrp).WriteRawImageAsync("userdata", image, new RawWriteOptions { ChunkMiB = 2 }, "/data");

            Assert.Equal(bytes, runner.Partition.AsSpan(0, 5 * (int)Mib).ToArray());
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task InstallStatusRidesOnTheChunkWritesWithNoExtraRoundTrips()
    {
        var image = Path.GetTempFileName();
        try
        {
            var bytes = new byte[4 * Mib];
            new Random(21).NextBytes(bytes);
            await File.WriteAllBytesAsync(image, bytes);

            var runner = new FakePartitionRunner("userdata", 6 * Mib);
            var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);
            await new TransferService(twrp).WriteRawImageAsync("userdata", image, new RawWriteOptions { ChunkMiB = 1 });
            Assert.Equal(bytes, runner.Partition.AsSpan(0, 4 * (int)Mib).ToArray());

            // Exactly the staging mkdir and the four chunk writes carry the status; no command
            // exists only to update the phone's screen.
            var carriers = runner.Shells.Where(c => c.Contains("s9r=$?", StringComparison.Ordinal)).ToList();
            Assert.Equal(5, carriers.Count);
            Assert.StartsWith("mkdir -p /tmp/s9woa", carriers[0], StringComparison.Ordinal);
            Assert.All(carriers.Skip(1), c => Assert.StartsWith("dd if=", c, StringComparison.Ordinal));
            Assert.All(carriers, c => Assert.EndsWith("exit $s9r", c, StringComparison.Ordinal));
            Assert.DoesNotContain(runner.Shells, c => c.StartsWith("rm -rf", StringComparison.Ordinal) && c.Contains("s9r=$?", StringComparison.Ordinal));

            var percents = carriers.Select(c => Status(c)["percent"]).ToList();
            Assert.Equal(["0", "25", "50", "75", "100"], percents);
            Assert.Equal("Copying Windows", Status(carriers[^1])["label"]);
            Assert.StartsWith("100% complete", Status(carriers[^1])["detail"], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void StatusSuffixKeepsTheExitCodeAndWritesAtomically()
    {
        var suffix = TwrpClient.StatusSuffix(WinReStatus.BootFiles());
        Assert.StartsWith("s9r=$?; ", suffix, StringComparison.Ordinal);
        Assert.EndsWith("exit $s9r", suffix, StringComparison.Ordinal);
        Assert.Contains("> /tmp/s9woa/status.tmp && mv -f /tmp/s9woa/status.tmp /tmp/s9woa/status", suffix, StringComparison.Ordinal);
        Assert.Contains(">/dev/null 2>&1", suffix, StringComparison.Ordinal);
        Assert.Equal("Writing boot files", Status("x; " + suffix)["label"]);

        // Long values are cut to what an Android property holds.
        var longLabel = new WinReStatus("x", new string('a', 200)).ToFileContents();
        Assert.Contains("label=" + new string('a', WinReStatus.MaxValueBytes) + "\n", longLabel, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Status(string command)
    {
        var b64 = System.Text.RegularExpressions.Regex.Match(command, @"echo ([A-Za-z0-9+/=]+) \| base64 -d").Groups[1].Value;
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2))
            .ToDictionary(kv => kv[0], kv => kv[1]);
    }

    [Fact]
    public async Task ZeroWindowsAreFilledOnThePhoneNotPushed()
    {
        var image = Path.GetTempFileName();
        try
        {
            // MiB 0 data, MiB 1-2 zero, MiB 3 data; the phone partition starts full of stale bytes.
            var bytes = new byte[4 * Mib];
            new Random(9).NextBytes(bytes.AsSpan(0, (int)Mib));
            new Random(10).NextBytes(bytes.AsSpan(3 * (int)Mib, (int)Mib));
            await File.WriteAllBytesAsync(image, bytes);

            var runner = new FakePartitionRunner("userdata", 6 * Mib);
            new Random(11).NextBytes(runner.Partition);
            var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);
            await new TransferService(twrp).WriteRawImageAsync("USERDATA", image, new RawWriteOptions { ChunkMiB = 1 });

            Assert.Equal(bytes, runner.Partition.AsSpan(0, 4 * (int)Mib).ToArray());
            Assert.Equal(2, runner.Pushes); // only the two data windows crossed USB
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task RefusesUnknownPartition()
    {
        var image = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(image, new byte[1 * Mib]);
            var runner = new FakePartitionRunner("userdata", 8 * Mib);
            var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new TransferService(twrp).WriteRawImageAsync("system", image, new RawWriteOptions()));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Theory]
    [InlineData("/sbin/mkfs.fat", "/sbin/mkfs.fat -F 32 -S 4096 -s 1 -n ESP /dev/block/by-name/CACHE")]
    [InlineData("/system/bin/newfs_msdos", "/system/bin/newfs_msdos -F 32 -S 4096 -c 1 -L ESP /dev/block/by-name/CACHE")]
    public void FormatsEspAsFat32With4KSectorsAndClusters(string tool, string expected) =>
        Assert.Equal(expected, BootFilesService.FormatCommand(tool, "/dev/block/by-name/CACHE", "ESP"));

    [Fact]
    public async Task RefusesImageLargerThanPartition()
    {
        var image = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(image, new byte[4 * Mib]);
            var runner = new FakePartitionRunner("userdata", 2 * Mib);
            var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new TransferService(twrp).WriteRawImageAsync("userdata", image, new RawWriteOptions()));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task RefusesWhenTargetCannotBeUnmounted()
    {
        var image = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(image, new byte[1 * Mib]);
            var runner = new FakePartitionRunner("userdata", 8 * Mib) { Mounted = true, StickyMount = true };
            var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new TransferService(twrp).WriteRawImageAsync("userdata", image, new RawWriteOptions(), "/data"));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task AutoUnmountsMountedTargetThenWrites()
    {
        var image = Path.GetTempFileName();
        try
        {
            var bytes = new byte[2 * Mib];
            new Random(5).NextBytes(bytes);
            await File.WriteAllBytesAsync(image, bytes);
            var runner = new FakePartitionRunner("userdata", 8 * Mib) { Mounted = true };
            var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);
            await new TransferService(twrp).WriteRawImageAsync("userdata", image, new RawWriteOptions { ChunkMiB = 1 }, "/data");
            Assert.False(runner.Mounted);
            Assert.Equal(bytes, runner.Partition.AsSpan(0, 2 * (int)Mib).ToArray());
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task SkipsNtfsFreeSpaceAndStagesInRam()
    {
        var image = Path.GetTempFileName();
        try
        {
            // Used: MiB 0 (boot, MFT, bitmap) and clusters 1024-1029 (MiB 4); the last MiB always goes.
            var bytes = NtfsTestImage.Build(8, [(1024, 6)]);
            await File.WriteAllBytesAsync(image, bytes);

            var runner = new FakePartitionRunner("userdata", 10 * Mib);
            new Random(12).NextBytes(runner.Partition);
            var stale = runner.Partition.ToArray();
            var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);
            var log = new List<string>();
            await new TransferService(twrp).WriteRawImageAsync("userdata", image, new RawWriteOptions { ChunkMiB = 2 }, log: new SyncProgress(log.Add));

            foreach (var mib in new[] { 0, 4, 7 })
            {
                Assert.Equal(bytes.AsSpan(mib * (int)Mib, (int)Mib).ToArray(), runner.Partition.AsSpan(mib * (int)Mib, (int)Mib).ToArray());
            }
            foreach (var mib in new[] { 1, 2, 3, 5, 6 })
            {
                Assert.Equal(stale.AsSpan(mib * (int)Mib, (int)Mib).ToArray(), runner.Partition.AsSpan(mib * (int)Mib, (int)Mib).ToArray());
            }
            Assert.Equal(3, runner.Pushes);
            Assert.All(runner.PushTargets, t => Assert.StartsWith("/tmp/s9woa/slot", t, StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("Writing 3 MiB of 8 MiB", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task VerificationCanBeTurnedOff()
    {
        var image = Path.GetTempFileName();
        try
        {
            var bytes = new byte[6 * Mib];
            new Random(13).NextBytes(bytes);
            await File.WriteAllBytesAsync(image, bytes);
            var runner = new FakePartitionRunner("userdata", 8 * Mib);
            var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);

            await new TransferService(twrp).WriteRawImageAsync("userdata", image, new RawWriteOptions { ChunkMiB = 2, Verify = false });
            Assert.Equal(bytes, runner.Partition.AsSpan(0, 6 * (int)Mib).ToArray());
            Assert.Equal(0, runner.Hashes);

            await new TransferService(twrp).WriteRawImageAsync("userdata", image, new RawWriteOptions { ChunkMiB = 2 });
            Assert.Equal(3, runner.Hashes);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task AlternatesTwoStagingSlotsAcrossManyChunks()
    {
        var image = Path.GetTempFileName();
        try
        {
            var bytes = new byte[7 * Mib];
            new Random(14).NextBytes(bytes);
            await File.WriteAllBytesAsync(image, bytes);
            var runner = new FakePartitionRunner("userdata", 7 * Mib);
            var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);
            await new TransferService(twrp).WriteRawImageAsync("userdata", image, new RawWriteOptions { ChunkMiB = 1 });

            Assert.Equal(bytes, runner.Partition);
            Assert.Equal(["/tmp/s9woa/slot0.bin", "/tmp/s9woa/slot1.bin"], runner.PushTargets.Distinct().Order());
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Theory]
    [InlineData(10, 4, "0+1 3+4 7+1 9+1")]
    [InlineData(5, 2, "0+2 2+2 4+1")]
    public void PlansChunksAroundTheNeededMiBs(long total, long chunk, string expected)
    {
        Func<long, bool>? needed = total == 10 ? m => m is >= 3 and <= 7 : null;
        var plan = TransferPlanner.Plan(total, chunk, needed);
        Assert.Equal(expected, string.Join(' ', plan.Select(c => $"{c.OffsetMiB}+{c.CountMiB}")));
    }

    [Fact]
    public void ReportsRateAndTimeLeft()
    {
        var text = TransferService.Progress("USERDATA", 2048, 10240, 0, verified: true, TimeSpan.FromSeconds(64));
        Assert.Equal("USERDATA: 2.0 of 10.0 GiB written and verified, 34 MB/s, about 5 min left", text);
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [Fact]
    public async Task WritesWholePartitionForUefi()
    {
        var image = Path.GetTempFileName();
        try
        {
            var bytes = new byte[512 * 1024];
            new Random(3).NextBytes(bytes);
            await File.WriteAllBytesAsync(image, bytes);
            var runner = new FakePartitionRunner("boot", 4 * Mib);
            var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);
            await new TransferService(twrp).WriteWholePartitionAsync("boot", image);
            Assert.Equal(bytes, runner.Partition.AsSpan(0, bytes.Length).ToArray());
        }
        finally
        {
            File.Delete(image);
        }
    }
}
