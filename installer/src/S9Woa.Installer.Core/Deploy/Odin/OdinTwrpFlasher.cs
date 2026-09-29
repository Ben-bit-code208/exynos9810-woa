// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.IO.Ports;
using S9Woa.Installer.Core.Toolset;

namespace S9Woa.Installer.Core.Deploy.Odin;

/// <summary>
/// Talks to Download mode through the COM port the Samsung USB driver creates for it
/// (the "SAMSUNG Mobile USB Modem" of VID 04E8 PID 685D), so no driver replacement is needed.
/// </summary>
public sealed class SerialOdinTransport : IOdinTransport
{
    private readonly SerialPort _port;
    private readonly byte[] _scratch = new byte[64 * 1024];

    public SerialOdinTransport(string portName)
    {
        _port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.RequestToSend,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 1000,
            WriteTimeout = (int)TimeSpan.FromMinutes(1).TotalMilliseconds,
            ReadBufferSize = 1024 * 1024,
            WriteBufferSize = 2 * 1024 * 1024,
        };
        try
        {
            _port.Open();
        }
        catch (UnauthorizedAccessException)
        {
            _port.Dispose();
            throw new OdinException($"{portName} is in use by another program. Close Samsung Smart Switch or Odin and try again.");
        }
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();
    }

    public void Write(ReadOnlySpan<byte> data) => _port.BaseStream.Write(data);

    public void ReadExactly(Span<byte> buffer, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        var got = 0;
        while (got < buffer.Length)
        {
            var left = deadline - Environment.TickCount64;
            if (left <= 0)
            {
                throw new TimeoutException($"Received {got} of {buffer.Length} bytes.");
            }
            _port.ReadTimeout = (int)Math.Min(left, 1000);
            try
            {
                var n = _port.Read(_scratch, 0, Math.Min(_scratch.Length, buffer.Length - got));
                _scratch.AsSpan(0, n).CopyTo(buffer[got..]);
                got += n;
            }
            catch (TimeoutException)
            {
            }
        }
    }

    public void DiscardInput() => _port.DiscardInBuffer();

    public void Dispose() => _port.Dispose();
}

/// <summary>Finds the COM port of a phone that is in Download mode right now.</summary>
public static class DownloadModePort
{
    private const string UsbEnum = @"SYSTEM\CurrentControlSet\Enum\USB";

    /// <summary>
    /// The port recorded for a VID 04E8 / PID 685D (Download mode) device instance that is present
    /// among <paramref name="presentPorts"/>; ports of the phone in Android mode (PID 6860) never match.
    /// </summary>
    public static string? Find(IRegistryReader registry, IReadOnlyCollection<string> presentPorts)
    {
        foreach (var device in registry.SubKeyNames(UsbEnum)
                     .Where(n => n.StartsWith(DownloadModeDriver.HardwarePrefix, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var instance in registry.SubKeyNames($@"{UsbEnum}\{device}"))
            {
                var port = registry.GetString($@"{UsbEnum}\{device}\{instance}\Device Parameters", "PortName");
                if (port is not null && presentPorts.Contains(port, StringComparer.OrdinalIgnoreCase))
                {
                    return port;
                }
            }
        }
        return null;
    }
}

/// <summary>
/// Flashes TWRP to RECOVERY over Samsung's own Download-mode protocol and USB driver: finds
/// RECOVERY in the phone's PIT, sends the image and ends the session without rebooting.
/// </summary>
public sealed class OdinTwrpFlasher : ITwrpFlasher
{
    private readonly IRegistryReader _registry;
    private readonly Func<IReadOnlyCollection<string>> _presentPorts;
    private readonly Func<string, IOdinTransport> _open;

    public OdinTwrpFlasher(IRegistryReader registry, Func<IReadOnlyCollection<string>>? presentPorts = null,
        Func<string, IOdinTransport>? open = null)
    {
        _registry = registry;
        _presentPorts = presentPorts ?? SerialPort.GetPortNames;
        _open = open ?? (port => new SerialOdinTransport(port));
    }

    public string Name => "the built-in Download-mode flasher";

    public string? FindPort() => DownloadModePort.Find(_registry, _presentPorts());

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(FindPort() is not null);

    public async Task FlashRecoveryAsync(string twrpImage, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(twrpImage))
        {
            throw new FileNotFoundException("TWRP image not found.", twrpImage);
        }
        var port = FindPort() ?? throw new InvalidOperationException("No phone in Download mode.");
        ct.ThrowIfCancellationRequested();
        // Not cancellable once started: stopping mid-write would leave RECOVERY half written.
        await Task.Run(() => Flash(port, twrpImage, log), CancellationToken.None).ConfigureAwait(false);
    }

    internal void Flash(string port, string twrpImage, IProgress<string>? log)
    {
        log?.Report($"Connecting to Download mode on {port}...");
        using var transport = _open(port);
        var odin = new OdinSession(transport);
        odin.Handshake();
        odin.BeginSession();
        log?.Report($"Download mode protocol v{odin.ProtocolVersion}, {odin.FilePartSize / 1024} KiB packets. Reading the partition table...");
        var pit = Pit.Parse(odin.DumpPit());
        var recovery = Pit.Find(pit, "RECOVERY")
            ?? throw new OdinException($"The phone's partition table ({pit.Count} partitions) has no RECOVERY partition.");
        log?.Report($"RECOVERY is partition {recovery.Identifier} ({recovery.FlashFileName}).");

        using var image = File.OpenRead(twrpImage);
        var length = image.Length;
        odin.SetTotalBytes(length);
        log?.Report($"Flashing {Path.GetFileName(twrpImage)} ({length / 1024} KiB) to RECOVERY...");
        var lastDecile = -1;
        odin.FlashPartition(image, length, recovery, sent =>
        {
            var decile = (int)(sent * 10 / length);
            if (decile != lastDecile)
            {
                lastDecile = decile;
                log?.Report($"  {decile * 10}%");
            }
        });
        odin.EndSession();
        log?.Report("TWRP flashed. The phone stays in Download mode until you restart it.");
    }
}
