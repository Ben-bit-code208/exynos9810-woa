// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Security.Cryptography;
using System.Text;
using S9Woa.Installer.Core.Deploy;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Tests;

public class DeployTests
{
    /// <summary>Simulates a device in TWRP: named partitions with synthetic content, dd, sha256sum, push/pull.</summary>
    private sealed class FakeTwrpRunner : IProcessRunner
    {
        private readonly Dictionary<string, byte[]> _fs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _links;
        public List<string> Shell { get; } = [];

        public FakeTwrpRunner(IReadOnlyDictionary<string, byte[]> partitions, IReadOnlyDictionary<string, string> links)
        {
            _links = new(links);
            foreach (var (name, content) in partitions)
            {
                _fs[$"/dev/block/by-name/{name}"] = content;
            }
        }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            // arguments = ["-s", serial, verb, ...]
            var verb = arguments[2];
            if (verb == "shell")
            {
                var cmd = arguments[3];
                Shell.Add(cmd);
                return Task.FromResult(RunShell(cmd));
            }
            if (verb == "pull")
            {
                var src = arguments[3];
                var dst = arguments[4];
                if (!_fs.TryGetValue(src, out var content))
                {
                    return Task.FromResult(new ProcessResult(1, "", "no such file"));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllBytes(dst, content);
                return Task.FromResult(new ProcessResult(0, "1 file pulled", ""));
            }
            if (verb == "push")
            {
                _fs[arguments[4]] = File.ReadAllBytes(arguments[3]);
                return Task.FromResult(new ProcessResult(0, "1 file pushed", ""));
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }

        private ProcessResult RunShell(string cmd)
        {
            if (cmd.StartsWith("ls -l /dev/block/by-name", StringComparison.Ordinal))
            {
                var sb = new StringBuilder();
                foreach (var (name, node) in _links)
                {
                    sb.Append($"lrwxrwxrwx 1 root root 21 1970-01-01 00:00 {name} -> {node}\n");
                }
                return new ProcessResult(0, sb.ToString(), "");
            }
            if (cmd.StartsWith("blockdev --getsize64", StringComparison.Ordinal))
            {
                var path = cmd.Split(' ')[^1];
                return _fs.TryGetValue(path, out var c) ? new ProcessResult(0, $"{c.Length}\n", "") : new ProcessResult(1, "", "no dev");
            }
            if (cmd.StartsWith("mkdir", StringComparison.Ordinal) || cmd.StartsWith("rm ", StringComparison.Ordinal))
            {
                return new ProcessResult(0, "", "");
            }
            if (cmd.Contains("| sha256sum", StringComparison.Ordinal))
            {
                var ifPath = Between(cmd, "if=", " ");
                return _fs.TryGetValue(ifPath, out var c) ? new ProcessResult(0, $"{Hex(c)}  -\n", "") : new ProcessResult(1, "", "no file");
            }
            if (cmd.StartsWith("sha256sum ", StringComparison.Ordinal))
            {
                var path = cmd["sha256sum ".Length..].Trim();
                return _fs.TryGetValue(path, out var c)
                    ? new ProcessResult(0, $"{Hex(c)}  {path}\n", "")
                    : new ProcessResult(1, "", "no file");
            }
            if (cmd.StartsWith("dd if=", StringComparison.Ordinal))
            {
                var ifPath = Between(cmd, "if=", " ");
                var ofPath = Between(cmd, "of=", " ");
                if (_fs.TryGetValue(ifPath, out var c))
                {
                    _fs[ofPath] = c;
                    return new ProcessResult(0, "", "");
                }
                return new ProcessResult(1, "", "dd: no input");
            }
            return new ProcessResult(0, "", "");
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
    public void ParsesPartitionLinksAndSums()
    {
        var map = TwrpClient.ParsePartitionLinks(
            "lrwxrwxrwx 1 root root 21 1970 efs -> /dev/block/sda3\n" +
            "lrwxrwxrwx 1 root root 21 1970 boot -> /dev/block/sdd1\n");
        Assert.Equal("/dev/block/sda3", map["efs"]);
        Assert.Equal("/dev/block/sdd1", map["boot"]);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            TwrpClient.ParseSha256("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  -"));
        Assert.Null(TwrpClient.ParseSha256("short"));
    }

    [Fact]
    public async Task ListsAndSizesPartitions()
    {
        var runner = new FakeTwrpRunner(
            new Dictionary<string, byte[]> { ["efs"] = Encoding.ASCII.GetBytes("efs-data") },
            new Dictionary<string, string> { ["efs"] = "/dev/block/sda3" });
        var twrp = new TwrpClient(@"C:\adb.exe", "SER1", runner);
        Assert.Equal("/dev/block/sda3", (await twrp.ListPartitionsAsync())["efs"]);
        Assert.Equal(8, await twrp.PartitionSizeAsync("efs"));
    }

    [Fact]
    public async Task BacksUpAndVerifiesIdentityPartitions()
    {
        var dir = Directory.CreateTempSubdirectory("s9woa-backup").FullName;
        try
        {
            var runner = new FakeTwrpRunner(
                new Dictionary<string, byte[]>
                {
                    ["efs"] = Encoding.ASCII.GetBytes(new string('E', 4096)),
                    ["param"] = Encoding.ASCII.GetBytes(new string('P', 2048)),
                },
                new Dictionary<string, string> { ["efs"] = "/dev/block/sda3", ["param"] = "/dev/block/sda7" });
            var twrp = new TwrpClient(@"C:\adb.exe", "SER1", runner);
            var manifest = await new BackupService(twrp).BackupAsync(dir, "SM-G965F", "SER1");

            Assert.Equal(2, manifest.Partitions.Count);
            Assert.True(File.Exists(Path.Combine(dir, "efs.img")));
            Assert.True(File.Exists(Path.Combine(dir, "backup-manifest.json")));
            var efs = manifest.Partitions.Single(p => p.Name == "efs");
            Assert.Equal(4096, efs.Bytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(new string('E', 4096)))).ToLowerInvariant(), efs.Sha256);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task BackupFailsWhenEfsMissing()
    {
        var dir = Directory.CreateTempSubdirectory("s9woa-backup").FullName;
        try
        {
            var runner = new FakeTwrpRunner(
                new Dictionary<string, byte[]> { ["param"] = [1, 2, 3] },
                new Dictionary<string, string> { ["param"] = "/dev/block/sda7" });
            var twrp = new TwrpClient(@"C:\adb.exe", "SER1", runner);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new BackupService(twrp).BackupAsync(dir, "SM-G965F", "SER1"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
