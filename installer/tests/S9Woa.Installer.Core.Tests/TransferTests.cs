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

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var verb = arguments[2];
            if (verb == "push")
            {
                _sd[arguments[4]] = File.ReadAllBytes(arguments[3]);
                Pushes++;
                return Ok("pushed");
            }
            var cmd = arguments[3];
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
            await new TransferService(twrp).WriteRawImageAsync("userdata", image, windowMiB: 2, "/data");

            Assert.Equal(bytes, runner.Partition.AsSpan(0, 5 * (int)Mib).ToArray());
        }
        finally
        {
            File.Delete(image);
        }
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
            await new TransferService(twrp).WriteRawImageAsync("USERDATA", image, windowMiB: 1);

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
                new TransferService(twrp).WriteRawImageAsync("system", image, 1));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Theory]
    [InlineData("/sbin/mkfs.fat", "/sbin/mkfs.fat -F 32 -S 4096 -n SYSTEM /dev/block/by-name/SYSTEM")]
    [InlineData("/system/bin/newfs_msdos", "/system/bin/newfs_msdos -F 32 -S 4096 -L SYSTEM /dev/block/by-name/SYSTEM")]
    public void FormatsEspAsFat32With4KSectors(string tool, string expected) =>
        Assert.Equal(expected, BootFilesService.FormatCommand(tool, "/dev/block/by-name/SYSTEM"));

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
                new TransferService(twrp).WriteRawImageAsync("userdata", image, 1));
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
                new TransferService(twrp).WriteRawImageAsync("userdata", image, 1, "/data"));
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
            await new TransferService(twrp).WriteRawImageAsync("userdata", image, windowMiB: 1, "/data");
            Assert.False(runner.Mounted);
            Assert.Equal(bytes, runner.Partition.AsSpan(0, 2 * (int)Mib).ToArray());
        }
        finally
        {
            File.Delete(image);
        }
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
