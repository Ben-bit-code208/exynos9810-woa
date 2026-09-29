// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace S9Woa.Installer.Core.Deploy;

/// <summary>
/// The UEFI's retained watchdog record (RWD1, 64 bytes at physical 0xFED13D80). Each boot
/// phase takes ownership in it and the Windows storage driver takes it over; if a boot stalls,
/// the next start finds a stale owner, marks the record RECOVERY_PENDING and routes to TWRP,
/// and keeps doing so until TWRP acknowledges it. Its state/phase/reason say where a boot died.
/// </summary>
public sealed record RecoveryRecord(uint Generation, uint State, uint Owner, uint Phase, uint Reason, uint ResetStatus, bool ChecksumOk)
{
    public const uint Magic = 0x31445752; // "RWD1"
    public const int Bytes = 64;
    public const uint RecoveryPending = 0xA0;
    /// <summary>The next start found the previous boot's owner still in place: nothing was attempted.</summary>
    public const uint StaleBootOwnerReason = 2;

    private static readonly Dictionary<uint, string> States = new()
    {
        [0x00] = "empty", [0x10] = "boot shim armed", [0x20] = "SEC", [0x30] = "DXE", [0x40] = "boot device selection",
        [0x50] = "ExitBootServices", [0x60] = "Windows running", [0x70] = "processor handoff", [0x80] = "shutting down",
        [0x90] = "controlled stop", [0xA0] = "recovery pending", [0xB0] = "acknowledged by TWRP", [0xE0] = "fatal, watchdog not petted",
    };

    private static readonly string[] Phases =
    [
        "none", "boot shim", "SEC", "DXE", "boot device selection", "starting boot manager", "ExitBootServices",
        "ACPI committed", "processor pre-branch", "Windows initializing", "Windows running", "Windows bugcheck",
        "Windows storage fatal", "Windows shutdown", "recovery route", "TWRP alive",
    ];

    private static readonly string[] Reasons =
    [
        "none", "watchdog reset", "stale boot owner", "stale processor handoff", "Windows did not pet the watchdog",
        "Windows bugcheck", "Windows storage fatal", "invalid record", "recovery retry", "controlled shutdown",
        "Windows requested recovery",
    ];

    public bool IsRecoveryPending => State == RecoveryPending;

    public string StateName => States.TryGetValue(State, out var s) ? s : $"0x{State:X2}";
    public string PhaseName => Phase < Phases.Length ? Phases[Phase] : $"0x{Phase:X}";
    public string ReasonName => Reason < Reasons.Length ? Reasons[Reason] : $"0x{Reason:X}";

    public override string ToString() => $"{StateName} (phase: {PhaseName}; reason: {ReasonName})";

    /// <summary>Parses a record; null when the bytes are empty, erased (all 0xFF) or not RWD1.</summary>
    public static RecoveryRecord? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Bytes || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            return null;
        }
        Span<uint> w = stackalloc uint[16];
        for (var i = 0; i < 16; i++)
        {
            w[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
        }
        var checksum = 0xA5A55A5Au;
        for (var i = 0; i < 12; i++)
        {
            checksum ^= w[i];
        }
        var ok = checksum == w[12] && w[13] == ~w[12] && w[3] == ~w[2];
        return new RecoveryRecord(w[2], w[4], w[5], w[6], w[7], w[8], ok);
    }
}

/// <summary>Result line of the <c>rwd1_ack</c> module (<c>/proc/rwd1_ack</c>).</summary>
public sealed partial record RecoveryAck(int Status, bool Cleared, uint StateBefore)
{
    [GeneratedRegex(@"rwd1_ack status=(-?\d+) empty=\d valid=\d cleared=(\d)(?: state_before=0x([0-9a-fA-F]+))?")]
    private static partial Regex LineRegex();

    public static RecoveryAck? Parse(string text)
    {
        var m = LineRegex().Match(text);
        if (!m.Success)
        {
            return null;
        }
        var state = m.Groups[3].Success ? Convert.ToUInt32(m.Groups[3].Value, 16) : 0u;
        return new RecoveryAck(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value == "1", state);
    }
}

/// <summary>
/// Clears what makes the phone start TWRP instead of Windows: the Android bootloader control
/// block in MISC (<c>boot-recovery</c>) and a RECOVERY_PENDING watchdog record. The record is
/// read and acknowledged with kernel modules built for the TWRP kernel (<c>rwd1_evidence_reader.ko</c>,
/// <c>rwd1_ack.ko</c>); without them only the MISC request is cleared.
/// </summary>
public sealed class BootRouteService
{
    public const string AckModule = "rwd1_ack.ko";
    public const string ReaderModule = "rwd1_evidence_reader.ko";
    private readonly TwrpClient _twrp;

    public BootRouteService(TwrpClient twrp) => _twrp = twrp;

    /// <summary>Android's <c>bootloader_message</c>: 32-byte command, 32-byte status, 768-byte recovery arguments.</summary>
    public static byte[] BootRecoveryMessage(int size = 4096)
    {
        var bcb = new byte[size];
        "boot-recovery"u8.CopyTo(bcb);
        "recovery\n"u8.CopyTo(bcb.AsSpan(64));
        return bcb;
    }

    /// <summary>Reads the current record, or null when the reader module is unavailable or there is no valid record.</summary>
    public async Task<RecoveryRecord?> ReadRecordAsync(string? modulesDirectory, CancellationToken ct = default)
    {
        var module = modulesDirectory is null ? null : Path.Combine(modulesDirectory, ReaderModule);
        if (module is null || !File.Exists(module))
        {
            return null;
        }
        var local = Path.Combine(Path.GetTempPath(), $"s9woa-rwd1-{Guid.NewGuid():N}.bin");
        try
        {
            await _twrp.PushAsync(module, $"/tmp/{ReaderModule}", ct).ConfigureAwait(false);
            await _twrp.ShellAsync(
                "grep -q ' /sys/kernel/debug ' /proc/mounts || mount -t debugfs none /sys/kernel/debug; " +
                $"rmmod rwd1_evidence_reader 2>/dev/null; insmod /tmp/{ReaderModule} && " +
                "cp /sys/kernel/debug/rwd1-evidence/rwd1-second /tmp/s9woa-rwd1.bin; " +
                $"rmmod rwd1_evidence_reader 2>/dev/null; rm -f /tmp/{ReaderModule}; true", ct).ConfigureAwait(false);
            await _twrp.PullAsync("/tmp/s9woa-rwd1.bin", local, ct).ConfigureAwait(false);
            return RecoveryRecord.Parse(await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(local); } catch (IOException) { }
        }
    }

    /// <summary>Clears Android's bootloader control block in MISC (e.g. the <c>boot-recovery</c> request).</summary>
    public async Task ClearBootRequestAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        var misc = await _twrp.ResolvePartitionNameAsync(PartitionMap.Misc, ct).ConfigureAwait(false);
        var command = (await _twrp.ShellAsync($"dd if={TwrpClient.ByName}/{misc} bs=32 count=1 2>/dev/null | tr -d '\\000'", ct)
            .ConfigureAwait(false)).Trim();
        if (command.Length == 0)
        {
            return;
        }
        log?.Report($"Clearing the \"{command}\" request in {misc}...");
        await _twrp.ShellAsync($"dd if=/dev/zero of={TwrpClient.ByName}/{misc} bs=2048 count=1 conv=notrunc,fsync 2>/dev/null; sync", ct)
            .ConfigureAwait(false);
    }

    /// <summary>Clears the MISC boot request and acknowledges a pending recovery record.</summary>
    public async Task ClearAsync(string? modulesDirectory, IProgress<string>? log = null, CancellationToken ct = default)
    {
        await ClearBootRequestAsync(log, ct).ConfigureAwait(false);
        var ack = modulesDirectory is null ? null : Path.Combine(modulesDirectory, AckModule);
        if (ack is null || !File.Exists(ack))
        {
            log?.Report("No recovery-record module in the toolset; if Windows keeps returning to TWRP, power the phone off fully once.");
            return;
        }
        await _twrp.PushAsync(ack, $"/tmp/{AckModule}", ct).ConfigureAwait(false);
        var text = await _twrp.ShellAsync(
            $"rmmod rwd1_ack 2>/dev/null; insmod /tmp/{AckModule} && cat /proc/rwd1_ack; rmmod rwd1_ack 2>/dev/null; rm -f /tmp/{AckModule}; true", ct)
            .ConfigureAwait(false);
        var result = RecoveryAck.Parse(text);
        log?.Report(result switch
        {
            { Status: 0, Cleared: true } r => $"Cleared a pending recovery record (was 0x{r.StateBefore:X2}).",
            { Status: -1 } => "No pending recovery record.",
            { Status: -117 } => "No valid recovery record (normal after a full power-off).",
            { } r => $"The recovery record was left as is (status {r.Status}).",
            null => "Could not read the recovery-record module's result.",
        });
    }
}
