// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using S9Woa.Installer.Core.Recovery;

namespace S9Woa.Installer.Core.Tests;

public class TicketServiceTests
{
    /// <summary>Simulates the UFS driver's UCR1 ticket state machine on one drive.</summary>
    private sealed class FakeUfsTransport : IUcr1Transport
    {
        private RecoveryTicketState _state = RecoveryTicketState.Idle;
        private uint _token;
        public bool Ready { get; init; } = true;
        public int Arms { get; private set; }

        public byte[] SendCdb(byte[] cdb, int replyLength)
        {
            var action = (RecoveryAction)cdb[7];
            var token = BinaryPrimitives.ReadUInt32BigEndian(cdb.AsSpan(8));
            var status = RecoveryTicketStatus.Ok;
            switch (action)
            {
                case RecoveryAction.Query:
                    break;
                case RecoveryAction.Arm:
                    if (_state == RecoveryTicketState.Armed)
                    {
                        status = RecoveryTicketStatus.Busy;
                    }
                    else
                    {
                        _state = RecoveryTicketState.Armed;
                        _token = token;
                        Arms++;
                    }
                    break;
                case RecoveryAction.Cancel:
                    if (token != _token)
                    {
                        status = RecoveryTicketStatus.WrongToken;
                    }
                    else
                    {
                        _state = RecoveryTicketState.Idle;
                        _token = 0;
                    }
                    break;
            }
            return Encode(status, _state, _token, Ready);
        }

        public void Dispose() { }

        private static byte[] Encode(RecoveryTicketStatus status, RecoveryTicketState state, uint token, bool ready)
        {
            var b = new byte[RecoveryTicketProtocol.ReplyBytes];
            uint[] w = [RecoveryTicketProtocol.Signature, RecoveryTicketProtocol.Version, RecoveryTicketProtocol.ReplyBytes,
                (uint)status, (uint)state, token, 300, ready ? 1u : 0u];
            for (var i = 0; i < w.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), w[i]);
            }
            return b;
        }
    }

    /// <summary>A drive that is not ours: it throws like a device rejecting a vendor CDB.</summary>
    private sealed class RejectingTransport : IUcr1Transport
    {
        public byte[] SendCdb(byte[] cdb, int replyLength) => throw new IOException("illegal request");
        public void Dispose() { }
    }

    private sealed class MapFactory : IUcr1TransportFactory
    {
        private readonly Dictionary<int, Func<IUcr1Transport?>> _map;
        public MapFactory(int count, Dictionary<int, Func<IUcr1Transport?>> map)
        {
            PhysicalDriveCount = count;
            _map = map;
        }
        public int PhysicalDriveCount { get; }
        public IUcr1Transport? Open(int index) => _map.TryGetValue(index, out var f) ? f() : null;
    }

    private sealed class FakeRestart : ISystemRestart
    {
        public int Count { get; private set; }
        public string? Reason { get; private set; }
        public void Restart(string reason) { Count++; Reason = reason; }
    }

    [Fact]
    public void FindsTheTicketDriveAmongOthers()
    {
        var ufs = new FakeUfsTransport();
        var factory = new MapFactory(4, new()
        {
            [0] = () => new RejectingTransport(),
            [2] = () => ufs,
            // drives 1 and 3 cannot be opened
        });
        var svc = new RecoveryTicketService(factory, new FakeRestart());

        var dev = svc.FindDevice();
        Assert.NotNull(dev);
        Assert.Equal(2, dev!.PhysicalDrive);
        Assert.True(dev.Reply.Ready);
    }

    [Fact]
    public void ReturnsNullWhenNoDriveAnswers()
    {
        var factory = new MapFactory(3, new() { [0] = () => new RejectingTransport() });
        Assert.Null(new RecoveryTicketService(factory, new FakeRestart()).FindDevice());
    }

    [Fact]
    public void ArmThenRestartArmsOnceAndRestarts()
    {
        var ufs = new FakeUfsTransport();
        var restart = new FakeRestart();
        var svc = new RecoveryTicketService(new MapFactory(1, new() { [0] = () => ufs }), restart);

        svc.RestartToRecovery();

        Assert.Equal(1, ufs.Arms);
        Assert.Equal(1, restart.Count);
        Assert.Contains("TWRP", restart.Reason);
    }

    [Fact]
    public void DoesNotRestartWhenNoDevice()
    {
        var restart = new FakeRestart();
        var svc = new RecoveryTicketService(new MapFactory(2, new()), restart);
        Assert.Throws<RecoveryTicketException>(() => svc.RestartToRecovery());
        Assert.Equal(0, restart.Count);
    }

    [Fact]
    public void DoesNotArmWhenDriverNotReady()
    {
        var ufs = new FakeUfsTransport { Ready = false };
        var restart = new FakeRestart();
        var svc = new RecoveryTicketService(new MapFactory(1, new() { [0] = () => ufs }), restart);
        Assert.Throws<RecoveryTicketException>(() => svc.RestartToRecovery());
        Assert.Equal(0, ufs.Arms);
        Assert.Equal(0, restart.Count);
    }

    [Fact]
    public void ArmIsRejectedWhenAlreadyArmed()
    {
        var ufs = new FakeUfsTransport();
        var svc = new RecoveryTicketService(new MapFactory(1, new() { [0] = () => ufs }), new FakeRestart());
        svc.Arm(0);
        Assert.Throws<RecoveryTicketException>(() => svc.Arm(0));
    }
}
