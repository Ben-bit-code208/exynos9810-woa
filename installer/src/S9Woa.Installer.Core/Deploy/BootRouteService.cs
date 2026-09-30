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
/// The retained startup records the firmware reads before Windows: the RWD1 recovery record
/// (64 bytes at 0xFED13D80) and the P3/SMP1 startup record (128 bytes at 0xFED13E80).
///
/// Both are judged on EVERY byte, never on the first word: the firmware's P3 gate halts - with
/// the watchdog disabled, so the phone sits on the Samsung logo for good - on anything but an
/// all-zero record (all-ones passes only under extra guards), and the record that hung the
/// reference phone had a zero first word with stray bits further in. Stock Android, a
/// Download-mode session, a power loss or a forced reset all leave such content behind. Any
/// left-over RWD1 record likewise sends the next start back to TWRP (stale owner or invalid
/// record). So before the installer starts Windows from TWRP, both must be all zero.
/// </summary>
public sealed record StartupRecords(byte[] Rwd1Bytes, byte[] P3Bytes)
{
    public const int P3Length = 128;
    public const uint P3Magic = 0x31504D53;          // "SMP1"
    public const uint P3CommitInProgress = 0x21504D53; // "SMP!"
    public const uint P3VersionLength = 0x00800001;

    /// <summary>The parsed RWD1 record, when its magic is present.</summary>
    public RecoveryRecord? Rwd1 => RecoveryRecord.Parse(Rwd1Bytes);

    /// <summary>RWD1 is all zero: the next start creates a fresh record and goes on to Windows.</summary>
    public bool Rwd1Clear => IsAllZero(Rwd1Bytes);

    /// <summary>P3 is all zero: the only state the firmware's startup gate accepts unconditionally.</summary>
    public bool P3Clear => IsAllZero(P3Bytes);

    /// <summary>Both records are clean, so the start from TWRP reaches Windows' boot manager.</summary>
    public bool ReadyToStart => Rwd1Clear && P3Clear;

    /// <summary>What the RWD1 bytes hold, in words, for the log.</summary>
    public string Rwd1Kind => Classify(Rwd1Bytes) switch
    {
        "zero" => "clear",
        "erased" => "erased (all ones)",
        _ when Rwd1 is { ChecksumOk: true } r => $"left over from an earlier start: {r}",
        _ => $"unreadable (first word 0x{FirstWord(Rwd1Bytes):X8}, {NonZero(Rwd1Bytes)} of {Rwd1Bytes.Length} bytes set)",
    };

    /// <summary>What the P3 bytes hold, in words, for the log.</summary>
    public string P3Kind => Classify(P3Bytes) switch
    {
        "zero" => "clear",
        "erased" => "erased (all ones)",
        _ when IsValidP3(P3Bytes) => "left over from an earlier start",
        _ when FirstWord(P3Bytes) == P3CommitInProgress => "torn (a write was interrupted)",
        _ => $"invalid (first word 0x{FirstWord(P3Bytes):X8}, {NonZero(P3Bytes)} of {P3Bytes.Length} bytes set)",
    };

    public static bool IsAllZero(ReadOnlySpan<byte> data) => !data.ContainsAnyExcept((byte)0);

    private static bool IsAllOnes(ReadOnlySpan<byte> data) => data.Length > 0 && !data.ContainsAnyExcept((byte)0xFF);

    private static string Classify(ReadOnlySpan<byte> data) => IsAllZero(data) ? "zero" : IsAllOnes(data) ? "erased" : "data";

    private static uint FirstWord(ReadOnlySpan<byte> data) => data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;

    private static int NonZero(ReadOnlySpan<byte> data)
    {
        var n = 0;
        foreach (var b in data)
        {
            if (b != 0)
            {
                n++;
            }
        }
        return n;
    }

    /// <summary>A structurally valid P3 record: magic, version/length and a zero XOR over all 32 words.</summary>
    internal static bool IsValidP3(ReadOnlySpan<byte> data)
    {
        if (data.Length < P3Length)
        {
            return false;
        }
        var xor = P3Magic;
        for (var i = 1; i < 32; i++)
        {
            xor ^= BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
        }
        return FirstWord(data) == P3Magic && BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) == P3VersionLength && xor == 0;
    }
}

/// <summary>What <see cref="BootRouteService.ClearStartupRecordsAsync"/> found and did.</summary>
public sealed record StartupClearResult(StartupRecords Before, StartupRecords? After, IReadOnlyList<string> Cleared)
{
    /// <summary>Both records were read back all zero after clearing (or were clean to begin with).</summary>
    public bool Ready => (After ?? Before).ReadyToStart;
}

/// <summary>
/// Clears what makes the phone start TWRP instead of Windows, or stop at the Samsung logo: the
/// Android bootloader control block in MISC (<c>boot-recovery</c>), a RECOVERY_PENDING watchdog
/// record (acknowledged with <c>rwd1_ack.ko</c>), and whatever is left in the retained startup
/// records (read with <c>rwd1_evidence_reader.ko</c>, zeroed with <c>rwd1_clear_poc.ko</c> and
/// <c>pram_smp_clear_poc.ko</c>, then read back). All modules are ours, built for the TWRP kernel;
/// without them only the MISC request is cleared.
/// </summary>
public sealed class BootRouteService
{
    public const string AckModule = "rwd1_ack.ko";
    public const string ReaderModule = "rwd1_evidence_reader.ko";

    /// <summary>Supervised clearer for a garbage RWD1 word (gated behind <see cref="ClearToken"/>).</summary>
    public const string Rwd1ClearModule = "rwd1_clear_poc.ko";

    /// <summary>Clearer for the P3/SMP1 startup record that gates UEFI at the Samsung logo.</summary>
    public const string P3ClearModule = "pram_smp_clear_poc.ko";

    /// <summary>The exact token <see cref="Rwd1ClearModule"/> requires, so it can never fire by accident.</summary>
    public const string ClearToken = "CLEAR_INVALID_RWD1_SUPERVISED_V1";

    private readonly TwrpClient _twrp;

    public BootRouteService(TwrpClient twrp) => _twrp = twrp;

    /// <summary>Reads the current record, or null when the reader module is unavailable or there is no valid record.</summary>
    public async Task<RecoveryRecord?> ReadRecordAsync(string? modulesDirectory, CancellationToken ct = default)
    {
        var blobs = await ReadEvidenceBlobsAsync(modulesDirectory, ["rwd1-second"], ct).ConfigureAwait(false);
        return blobs is null ? null : RecoveryRecord.Parse(blobs["rwd1-second"]);
    }

    /// <summary>
    /// Reads the retained startup records (the whole RWD1 and P3 records) that decide whether the
    /// phone boots Windows, hangs at the Samsung logo or returns to TWRP. Null when the reader
    /// module is unavailable or the records could not be read.
    /// </summary>
    public async Task<StartupRecords?> ReadStartupStateAsync(string? modulesDirectory, CancellationToken ct = default)
    {
        var blobs = await ReadEvidenceBlobsAsync(modulesDirectory, ["rwd1-second", "p3-record"], ct).ConfigureAwait(false);
        if (blobs is null || blobs["rwd1-second"].Length < RecoveryRecord.Bytes || blobs["p3-record"].Length < StartupRecords.P3Length)
        {
            return null;
        }
        return new StartupRecords(blobs["rwd1-second"], blobs["p3-record"]);
    }

    /// <summary>
    /// Loads the evidence reader once, copies the named debugfs blobs into tmpfs, unloads it, and
    /// pulls the copies to the PC. Binary records travel as files, never as decoded stdout.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, byte[]>?> ReadEvidenceBlobsAsync(
        string? modulesDirectory, IReadOnlyList<string> blobs, CancellationToken ct)
    {
        var module = modulesDirectory is null ? null : Path.Combine(modulesDirectory, ReaderModule);
        if (module is null || !File.Exists(module))
        {
            return null;
        }
        var stamp = Guid.NewGuid().ToString("N");
        var copy = string.Join(" && ", blobs.Select(b => $"cp /sys/kernel/debug/rwd1-evidence/{b} /tmp/s9woa-ev-{stamp}-{b}"));
        try
        {
            await _twrp.PushAsync(module, $"/tmp/{ReaderModule}", ct).ConfigureAwait(false);
            await _twrp.ShellAsync(
                "grep -q ' /sys/kernel/debug ' /proc/mounts || mount -t debugfs none /sys/kernel/debug; " +
                $"rmmod rwd1_evidence_reader 2>/dev/null; insmod /tmp/{ReaderModule} && {copy}; " +
                $"rmmod rwd1_evidence_reader 2>/dev/null; rm -f /tmp/{ReaderModule}; true", ct).ConfigureAwait(false);
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            try
            {
                foreach (var b in blobs)
                {
                    var local = Path.Combine(Path.GetTempPath(), $"s9woa-ev-{stamp}-{b}.bin");
                    try
                    {
                        await _twrp.PullAsync($"/tmp/s9woa-ev-{stamp}-{b}", local, ct).ConfigureAwait(false);
                        result[b] = await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        try { File.Delete(local); } catch (IOException) { }
                    }
                }
            }
            finally
            {
                await _twrp.ShellAsync($"rm -f /tmp/s9woa-ev-{stamp}-*; true", ct).ConfigureAwait(false);
            }
            return result;
        }
        catch (InvalidOperationException)
        {
            return null;
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

    /// <summary>
    /// Makes the next restart a real Windows start: clears the MISC boot request, acknowledges a
    /// pending recovery record, and clears the retained startup records (see
    /// <see cref="ClearStartupRecordsAsync"/>). Returns what the records held and whether both were
    /// read back clean, or null when the phone's records could not be read (no modules).
    /// </summary>
    public async Task<StartupClearResult?> ClearAsync(string? modulesDirectory, IProgress<string>? log = null, CancellationToken ct = default)
    {
        await ClearBootRequestAsync(log, ct).ConfigureAwait(false);
        var ack = modulesDirectory is null ? null : Path.Combine(modulesDirectory, AckModule);
        if (ack is null || !File.Exists(ack))
        {
            log?.Report("No recovery-record module in the toolset; if Windows keeps returning to TWRP, power the phone off fully once.");
            return null;
        }
        await _twrp.PushAsync(ack, $"/tmp/{AckModule}", ct).ConfigureAwait(false);
        var text = await _twrp.ShellAsync(
            $"rmmod rwd1_ack 2>/dev/null; insmod /tmp/{AckModule} && cat /proc/rwd1_ack; rmmod rwd1_ack 2>/dev/null; rm -f /tmp/{AckModule}; true", ct)
            .ConfigureAwait(false);
        var result = RecoveryAck.Parse(text);
        log?.Report(result switch
        {
            { Status: 0, Cleared: true } r => $"Acknowledged a pending recovery record (was 0x{r.StateBefore:X2}).",
            { Status: -1 or -117 } => "No pending recovery record to acknowledge.",
            { } r => $"The recovery record was not acknowledged (status {r.Status}).",
            null => "Could not read the recovery-record module's result.",
        });

        // The acknowledgement only handles a well-formed RECOVERY_PENDING record. Whatever else is
        // left in RWD1 or P3 - from stock Android, Download mode, a power loss, a forced reset or
        // an earlier start - hangs UEFI at the Samsung logo or sends the phone back to TWRP.
        return await ClearStartupRecordsAsync(modulesDirectory, log, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the retained startup records before a deliberate start from TWRP. The previous start
    /// is over by definition, so anything left in either record is stale: a non-zero P3 record halts
    /// UEFI at the Samsung logo with the watchdog off (it is judged on every byte - the record that
    /// hung the reference phone had a zero first word), and a left-over RWD1 record costs a round
    /// trip back to TWRP at best. Each is zeroed with its GPL module (RWD1 via the token-gated
    /// supervised clearer), then both are read back. Returns null when the records cannot be read.
    /// </summary>
    public async Task<StartupClearResult?> ClearStartupRecordsAsync(string? modulesDirectory, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (modulesDirectory is null)
        {
            return null;
        }
        var before = await ReadStartupStateAsync(modulesDirectory, ct).ConfigureAwait(false);
        if (before is null)
        {
            log?.Report("Could not read the phone's startup records; starting without checking them.");
            return null;
        }
        if (before.ReadyToStart)
        {
            log?.Report("Startup records are clear.");
            return new StartupClearResult(before, null, []);
        }

        var cleared = new List<string>();
        if (!before.Rwd1Clear)
        {
            log?.Report($"Recovery record: {before.Rwd1Kind}. Clearing it...");
            if (await RunClearModuleAsync(modulesDirectory, Rwd1ClearModule, $"authorize={ClearToken}", log, ct).ConfigureAwait(false))
            {
                cleared.Add("recovery record");
            }
        }
        if (!before.P3Clear)
        {
            log?.Report($"Startup record: {before.P3Kind}. Clearing it so UEFI does not stop at the Samsung logo...");
            if (await RunClearModuleAsync(modulesDirectory, P3ClearModule, "", log, ct).ConfigureAwait(false))
            {
                cleared.Add("startup record");
            }
        }

        var after = await ReadStartupStateAsync(modulesDirectory, ct).ConfigureAwait(false);
        var outcome = new StartupClearResult(before, after, cleared);
        log?.Report(outcome.Ready
            ? "Cleared the " + string.Join(" and the ", cleared) + "; both read back clear."
            : after is null
                ? "Could not read the startup records back after clearing them."
                : $"The startup records are still not clear (recovery record: {after.Rwd1Kind}; startup record: {after.P3Kind}).");
        return outcome;
    }

    /// <summary>
    /// Pushes and loads a one-shot clear module. Success is the module's own init result (insmod
    /// fails when it could not zero and verify the record); a dmesg line could be a previous run's.
    /// </summary>
    private async Task<bool> RunClearModuleAsync(string modulesDirectory, string module, string parameters,
        IProgress<string>? log, CancellationToken ct)
    {
        var local = Path.Combine(modulesDirectory, module);
        if (!File.Exists(local))
        {
            log?.Report($"The clearer {module} is missing from the installer's modules ({modulesDirectory}).");
            return false;
        }
        var name = Path.GetFileNameWithoutExtension(module);
        await _twrp.PushAsync(local, $"/tmp/{module}", ct).ConfigureAwait(false);
        var text = await _twrp.ShellAsync(
            $"rmmod {name} 2>/dev/null; insmod /tmp/{module} {parameters} 2>/dev/null && echo S9WOA_CLEARED; "
            + $"rmmod {name} 2>/dev/null; rm -f /tmp/{module}; true", ct).ConfigureAwait(false);
        return text.Contains("S9WOA_CLEARED", StringComparison.Ordinal);
    }
}
