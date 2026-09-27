// SPDX-License-Identifier: BSD-2-Clause-Patent
namespace S9Woa.Installer.Core.Recovery;

/// <summary>Raised when a Restart-to-TWRP request cannot be completed.</summary>
public sealed class RecoveryTicketException : Exception
{
    public RecoveryTicketException(string message) : base(message) { }
}

/// <summary>A physical drive that answered the UCR1 query, with its last reply.</summary>
public sealed record TicketDevice(int PhysicalDrive, RecoveryTicketReply Reply);

/// <summary>
/// Drives the Exynos9810 UFS "clean recovery" (UCR1) ticket: it finds the disk
/// whose storage driver understands the vendor CDB, arms a one-shot ticket, and
/// asks Windows to restart. The armed driver reboots the phone into TWRP instead
/// of back into Windows. Runs inside Windows on the phone; on any other machine
/// no drive answers and <see cref="FindDevice"/> returns null.
/// </summary>
public sealed class RecoveryTicketService
{
    private readonly IUcr1TransportFactory _factory;
    private readonly ISystemRestart _restart;

    public RecoveryTicketService(IUcr1TransportFactory factory, ISystemRestart restart)
    {
        _factory = factory;
        _restart = restart;
    }

    /// <summary>Scans the physical drives and returns the first that answers a UCR1 query.</summary>
    public TicketDevice? FindDevice(CancellationToken ct = default)
    {
        for (var i = 0; i < _factory.PhysicalDriveCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var reply = TryQuery(i);
            if (reply is not null)
            {
                return new TicketDevice(i, reply);
            }
        }
        return null;
    }

    private RecoveryTicketReply? TryQuery(int drive)
    {
        IUcr1Transport? transport = null;
        try
        {
            transport = _factory.Open(drive);
            if (transport is null)
            {
                return null;
            }
            var raw = transport.SendCdb(RecoveryTicketProtocol.BuildCdb(RecoveryAction.Query, 0), RecoveryTicketProtocol.ReplyBytes);
            return RecoveryTicketProtocol.ParseReply(raw);
        }
        catch (Exception e) when (e is IOException or FormatException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // Not our device, or it rejected the vendor command. Keep scanning.
            return null;
        }
        finally
        {
            transport?.Dispose();
        }
    }

    /// <summary>Arms a fresh ticket on the given drive and returns the resulting state.</summary>
    public TicketDevice Arm(int physicalDrive, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var transport = _factory.Open(physicalDrive)
            ?? throw new RecoveryTicketException($"Could not open PhysicalDrive{physicalDrive}.");
        try
        {
            var token = RecoveryTicketProtocol.NewToken();
            var raw = transport.SendCdb(RecoveryTicketProtocol.BuildCdb(RecoveryAction.Arm, token), RecoveryTicketProtocol.ReplyBytes);
            var reply = RecoveryTicketProtocol.ParseReply(raw);
            if (reply.Status != RecoveryTicketStatus.Ok)
            {
                throw new RecoveryTicketException(Describe(reply.Status));
            }
            if (reply.State != RecoveryTicketState.Armed)
            {
                throw new RecoveryTicketException("The storage driver did not confirm the recovery ticket.");
            }
            return new TicketDevice(physicalDrive, reply);
        }
        catch (Exception e) when (e is IOException or FormatException or System.ComponentModel.Win32Exception)
        {
            throw new RecoveryTicketException($"The storage driver rejected the recovery ticket: {e.Message}");
        }
        finally
        {
            transport.Dispose();
        }
    }

    /// <summary>Cancels any armed ticket on the given drive.</summary>
    public void Cancel(int physicalDrive, uint token, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var transport = _factory.Open(physicalDrive)
            ?? throw new RecoveryTicketException($"Could not open PhysicalDrive{physicalDrive}.");
        try
        {
            transport.SendCdb(RecoveryTicketProtocol.BuildCdb(RecoveryAction.Cancel, token), RecoveryTicketProtocol.ReplyBytes);
        }
        catch (Exception e) when (e is IOException or FormatException or System.ComponentModel.Win32Exception)
        {
            throw new RecoveryTicketException($"Could not cancel the recovery ticket: {e.Message}");
        }
        finally
        {
            transport.Dispose();
        }
    }

    /// <summary>
    /// Finds the UFS disk, arms a ticket, and restarts Windows into TWRP.
    /// Throws <see cref="RecoveryTicketException"/> if the driver is absent or refuses.
    /// </summary>
    public void RestartToRecovery(CancellationToken ct = default)
    {
        var device = FindDevice(ct)
            ?? throw new RecoveryTicketException(
                "No disk on this PC supports Restart to TWRP. This tool only works while running Windows on the phone, "
                + "with the Exynos9810 storage driver installed.");
        if (!device.Reply.Ready)
        {
            throw new RecoveryTicketException(
                "The storage driver reports it is not ready to schedule a recovery restart. Try again in a moment.");
        }
        Arm(device.PhysicalDrive, ct);
        _restart.Restart("Restarting to TWRP recovery (Galaxy S9+ Windows installer).");
    }

    private static string Describe(RecoveryTicketStatus status) => status switch
    {
        RecoveryTicketStatus.NotReady => "The storage driver is not ready to schedule a recovery restart.",
        RecoveryTicketStatus.Busy => "A recovery restart is already scheduled.",
        RecoveryTicketStatus.WrongToken => "The recovery ticket token was rejected.",
        _ => "The storage driver returned an unexpected status.",
    };
}
