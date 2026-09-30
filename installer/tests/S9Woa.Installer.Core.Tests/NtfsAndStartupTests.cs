// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using S9Woa.Installer.Core.Deploy;
using S9Woa.Installer.Core.Processes;

namespace S9Woa.Installer.Core.Tests;

/// <summary>
/// The two failure modes found on the reference phone: the TWRP ntfs-3g FUSE self-deadlock (broken
/// from the host over the adb sync service) and garbage RWD1/P3 startup records (cleared before the
/// first boot so UEFI does not hang at the Samsung logo).
/// </summary>
public class NtfsAndStartupTests
{
    [Fact]
    public void EnumeratesOnlyNumericProcEntries()
    {
        var pids = TwrpClient.EnumerateProcPids(
            "-r--r--r-- 0 0 0 1\n-r--r--r-- 0 0 0 200\ndr-xr-xr-x 0 0 0 self\ndr-xr-xr-x 0 0 0 cpuinfo\n 0 0 0 3563\n").ToList();
        Assert.Equal(["1", "200", "3563"], pids);
    }

    // The failure seen mid-copy on 2026-09-30: the adb server kept running but stopped accepting for ~20 s.
    private const string DaemonUnreachable =
        "* daemon still not running\nadb: error: failed to get feature set: cannot connect to daemon at tcp:5037: "
        + "cannot connect to 127.0.0.1:5037: A connection attempt failed because the connected party did not properly respond (10060)\n";

    /// <summary>Fails the first <c>failures</c> calls with the given adb output, then succeeds.</summary>
    private sealed class FlakyRunner(int failures, string failure) : IProcessRunner
    {
        public int Calls { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Calls <= failures ? new ProcessResult(1, "", failure) : new ProcessResult(0, "ok\n", ""));
        }
    }

    private static TwrpClient Flaky(FlakyRunner runner) =>
        new(@"C:\adb.exe", "SER", runner) { AdbRetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero] };

    [Fact]
    public async Task RetriesACommandTheAdbServerNeverTookAndThenSucceeds()
    {
        var push = new FlakyRunner(2, DaemonUnreachable);
        await Flaky(push).PushAsync(Path.GetTempFileName(), "/tmp/s9woa/slot1.bin");
        Assert.Equal(3, push.Calls);

        // A shell command that never reached the phone is safe to send again as well.
        var shell = new FlakyRunner(1, DaemonUnreachable);
        Assert.Equal("ok\n", await Flaky(shell).ShellAsync("dd if=/tmp/s9woa/slot1.bin of=/dev/block/by-name/USERDATA"));
        Assert.Equal(2, shell.Calls);
    }

    [Fact]
    public async Task GivesUpAfterTheLastRetry()
    {
        var runner = new FlakyRunner(10, DaemonUnreachable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Flaky(runner).PushAsync(Path.GetTempFileName(), "/tmp/x"));
        Assert.Equal(4, runner.Calls); // the first attempt and three retries
    }

    [Fact]
    public async Task NeverRetriesAPhoneSideFailureOrAShellDroppedMidCommand()
    {
        // The command ran on the phone and failed: sending it again would not help.
        var remote = new FlakyRunner(1, "dd: /dev/block/by-name/USERDATA: No space left on device\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Flaky(remote).ShellAsync("dd if=a of=b"));
        Assert.Equal(1, remote.Calls);

        // A connection dropped mid-command may have run half of a shell command: not retried...
        var dropped = new FlakyRunner(1, "error: closed\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Flaky(dropped).ShellAsync("insmod /tmp/x.ko"));
        Assert.Equal(1, dropped.Calls);

        // ...but re-sending a push is harmless.
        var push = new FlakyRunner(1, "adb: error: failed to copy 'a' to '/tmp/a': protocol fault (couldn't read status): connection reset\n");
        await Flaky(push).PushAsync(Path.GetTempFileName(), "/tmp/a");
        Assert.Equal(2, push.Calls);
    }

    /// <summary>Fakes a TWRP whose shell hangs on the ntfs-3g deadlock; the OOM kill frees it.</summary>
    private sealed class DeadlockRunner(IReadOnlyDictionary<string, string> cmdlines) : IProcessRunner
    {
        public List<string> Pushes { get; } = [];
        public bool ShellHung { get; set; } = true;

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
        {
            var verb = args[2];
            switch (verb)
            {
                case "shell" when args[3] == "echo s9ok":
                    if (ShellHung)
                    {
                        throw new TimeoutException("adb shell hung");
                    }
                    return Task.FromResult(new ProcessResult(0, "s9ok\n", ""));
                case "shell":
                    return Task.FromResult(new ProcessResult(0, "", ""));
                case "ls" when args[3] == "/proc":
                    var sb = new StringBuilder();
                    foreach (var pid in cmdlines.Keys)
                    {
                        sb.Append($"-r--r--r-- 0 0 0 {pid}\n");
                    }
                    sb.Append("dr-xr-xr-x 0 0 0 self\n"); // non-numeric noise, must be ignored
                    return Task.FromResult(new ProcessResult(0, sb.ToString(), ""));
                case "pull":
                    var m = Regex.Match(args[3], @"/proc/(\d+)/cmdline");
                    if (m.Success && cmdlines.TryGetValue(m.Groups[1].Value, out var cl))
                    {
                        File.WriteAllBytes(args[4], Encoding.ASCII.GetBytes(cl.Replace(' ', '\0') + "\0"));
                        return Task.FromResult(new ProcessResult(0, "1 file pulled", ""));
                    }
                    return Task.FromResult(new ProcessResult(1, "", "no file"));
                case "push":
                    var content = File.Exists(args[3]) ? File.ReadAllText(args[3]) : "";
                    Pushes.Add($"{content} -> {args[4]}");
                    if (args[4] == "/proc/sysrq-trigger")
                    {
                        ShellHung = false; // the OOM killer reaped mount.ntfs; the shell answers again
                    }
                    return Task.FromResult(new ProcessResult(0, "1 file pushed", ""));
                default:
                    return Task.FromResult(new ProcessResult(0, "", ""));
            }
        }
    }

    [Fact]
    public async Task BreaksTheDeadlockByOomKillingTheNtfsDaemonOnly()
    {
        var runner = new DeadlockRunner(new Dictionary<string, string>
        {
            ["100"] = "/sbin/recovery",
            ["200"] = "/sbin/mount.ntfs /dev/block/sda25 /data",
            ["300"] = "/sbin/sh",
        });
        var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);

        Assert.True(await twrp.BreakNtfsDeadlockAsync());
        Assert.Contains("1000 -> /proc/200/oom_score_adj", runner.Pushes);
        Assert.Contains("f -> /proc/sysrq-trigger", runner.Pushes);
        // The recovery and shell processes are never targeted; only the ntfs-3g daemon is.
        Assert.DoesNotContain(runner.Pushes, p => p.Contains("/proc/100/", StringComparison.Ordinal));
        Assert.DoesNotContain(runner.Pushes, p => p.Contains("/proc/300/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureResponsiveBreaksAHungShellThenSucceeds()
    {
        var runner = new DeadlockRunner(new Dictionary<string, string>
        {
            ["200"] = "/sbin/mount.ntfs /dev/block/sda25 /data",
        });
        var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);

        Assert.True(await twrp.EnsureResponsiveAsync());
        Assert.Contains("f -> /proc/sysrq-trigger", runner.Pushes);
    }

    [Fact]
    public async Task EnsureResponsiveDoesNothingWhenTheShellIsHealthy()
    {
        var runner = new DeadlockRunner(new Dictionary<string, string>()) { ShellHung = false };
        var twrp = new TwrpClient(@"C:\adb.exe", "SER", runner);

        Assert.True(await twrp.EnsureResponsiveAsync());
        Assert.Empty(runner.Pushes); // no break was needed
    }

    // ---- garbage RWD1 / P3 startup records --------------------------------

    /// <summary>Fakes the evidence reader and the two clear modules against in-memory records.</summary>
    private sealed class StartupRunner : IProcessRunner
    {
        public byte[] Rwd1 { get; set; } = new byte[64];
        public byte[] P3 { get; set; } = new byte[128];
        public bool P3ClearFails { get; init; }
        public List<string> Inserted { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
        {
            var verb = args[2];
            if (verb == "pull")
            {
                if (args[3].EndsWith("rwd1-second", StringComparison.Ordinal))
                {
                    File.WriteAllBytes(args[4], Rwd1);
                    return Task.FromResult(new ProcessResult(0, "1 file pulled", ""));
                }
                if (args[3].EndsWith("p3-record", StringComparison.Ordinal))
                {
                    File.WriteAllBytes(args[4], P3);
                    return Task.FromResult(new ProcessResult(0, "1 file pulled", ""));
                }
                return Task.FromResult(new ProcessResult(1, "", "no file"));
            }
            if (verb == "shell")
            {
                var cmd = args[3];
                if (cmd.Contains("insmod /tmp/rwd1_clear_poc.ko authorize=CLEAR_INVALID_RWD1_SUPERVISED_V1", StringComparison.Ordinal))
                {
                    Inserted.Add("rwd1_clear");
                    Rwd1 = new byte[64];
                    return Task.FromResult(new ProcessResult(0, "S9WOA_CLEARED\n", ""));
                }
                if (cmd.Contains("insmod /tmp/pram_smp_clear_poc.ko", StringComparison.Ordinal))
                {
                    Inserted.Add("p3_clear");
                    if (P3ClearFails)
                    {
                        return Task.FromResult(new ProcessResult(0, "", "")); // insmod failed: no marker
                    }
                    P3 = new byte[128];
                    return Task.FromResult(new ProcessResult(0, "S9WOA_CLEARED\n", ""));
                }
                return Task.FromResult(new ProcessResult(0, "", "")); // reader insmod, debugfs, rm
            }
            return Task.FromResult(new ProcessResult(0, "1 file pushed", "")); // push
        }
    }

    private static string ModulesDir()
    {
        var dir = Directory.CreateTempSubdirectory("s9woa-mods").FullName;
        foreach (var m in new[] { "rwd1_evidence_reader.ko", "rwd1_clear_poc.ko", "pram_smp_clear_poc.ko", "rwd1_ack.ko" })
        {
            File.WriteAllBytes(Path.Combine(dir, m), [1]);
        }
        return dir;
    }

    private static byte[] Blob(int size, uint magic)
    {
        var b = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(b, magic);
        for (var i = 4; i < size; i++)
        {
            b[i] = (byte)(i * 7 + 1);
        }
        return b;
    }

    [Fact]
    public async Task ClearsBothStartupRecordsAndVerifiesTheReadBack()
    {
        var dir = ModulesDir();
        try
        {
            var runner = new StartupRunner { Rwd1 = Blob(64, 0xD959A710u), P3 = Blob(128, 0x530EA12Au) };
            var result = await new BootRouteService(new TwrpClient(@"C:\adb.exe", "SER", runner)).ClearStartupRecordsAsync(dir);

            Assert.NotNull(result);
            Assert.True(result.Ready);
            Assert.Equal(["recovery record", "startup record"], result.Cleared);
            Assert.False(result.Before.ReadyToStart);
            Assert.True(result.After!.ReadyToStart);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ClearsAP3RecordWhoseFirstWordIsZero()
    {
        var dir = ModulesDir();
        try
        {
            // The reference phone's logo hang: magic 0, stray bits further in.
            var p3 = new byte[128];
            BinaryPrimitives.WriteUInt32LittleEndian(p3.AsSpan(8), 0x00000800);
            var runner = new StartupRunner { P3 = p3 };
            var result = await new BootRouteService(new TwrpClient(@"C:\adb.exe", "SER", runner)).ClearStartupRecordsAsync(dir);

            Assert.True(result!.Ready);
            Assert.Equal(["startup record"], result.Cleared);
            Assert.DoesNotContain("rwd1_clear", runner.Inserted);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ClearsALeftOverWellFormedRwd1Record()
    {
        var dir = ModulesDir();
        try
        {
            var runner = new StartupRunner { Rwd1 = Blob(64, RecoveryRecord.Magic) };
            var result = await new BootRouteService(new TwrpClient(@"C:\adb.exe", "SER", runner)).ClearStartupRecordsAsync(dir);

            Assert.True(result!.Ready);
            Assert.Contains("rwd1_clear", runner.Inserted);
            Assert.DoesNotContain("p3_clear", runner.Inserted);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReportsWhenARecordDoesNotReadBackClear()
    {
        var dir = ModulesDir();
        try
        {
            var runner = new StartupRunner { P3 = Blob(128, 0x530EA12Au), P3ClearFails = true };
            var result = await new BootRouteService(new TwrpClient(@"C:\adb.exe", "SER", runner)).ClearStartupRecordsAsync(dir);

            Assert.False(result!.Ready);
            Assert.Empty(result.Cleared);
            Assert.False(result.After!.P3Clear);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LeavesCleanStartupRecordsUntouched()
    {
        var dir = ModulesDir();
        try
        {
            var runner = new StartupRunner();
            var result = await new BootRouteService(new TwrpClient(@"C:\adb.exe", "SER", runner)).ClearStartupRecordsAsync(dir);

            Assert.True(result!.Ready);
            Assert.Null(result.After);
            Assert.Empty(runner.Inserted);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
