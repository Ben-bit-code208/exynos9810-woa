// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using S9Woa.Installer.Core.Host;
using S9Woa.Installer.Core.Recovery;
using S9Woa.Installer.Core.Stages;

namespace S9Woa.Installer.Core.Tests;

public class RecoveryAndStateTests
{
    [Fact]
    public void BuildsUcr1Cdb()
    {
        var cdb = RecoveryTicketProtocol.BuildCdb(RecoveryAction.Arm, 0x11223344);
        Assert.Equal(
            new byte[] { 0xD4, 0x98, 0x10, 0x43, 0x52, 0x53, 0x54, 0x01, 0x11, 0x22, 0x33, 0x44, 0, 0, 0, 0 },
            cdb);
        Assert.Throws<ArgumentException>(() => RecoveryTicketProtocol.BuildCdb(RecoveryAction.Query, 5));
        Assert.Throws<ArgumentException>(() => RecoveryTicketProtocol.BuildCdb(RecoveryAction.Arm, 0));
        Assert.NotEqual(0u, RecoveryTicketProtocol.NewToken());
    }

    [Fact]
    public void ParsesUcr1Reply()
    {
        var b = new byte[32];
        uint[] words = [0x31524355, 1, 32, 0, 1, 0xABCD, 300, 1];
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), words[i]);
        }
        var r = RecoveryTicketProtocol.ParseReply(b);
        Assert.Equal(new RecoveryTicketReply(RecoveryTicketStatus.Ok, RecoveryTicketState.Armed, 0xABCD, 300, true), r);

        b[0] = 0;
        Assert.Throws<FormatException>(() => RecoveryTicketProtocol.ParseReply(b));
        Assert.Throws<FormatException>(() => RecoveryTicketProtocol.ParseReply(new byte[16]));
    }

    private sealed class FakeHost : IHostEnvironment
    {
        public bool IsAdministrator { get; init; } = true;
        public Version OsVersion { get; init; } = new(10, 0, 26100);
        public Architecture OsArchitecture { get; init; } = Architecture.X64;
        public long Free { get; init; } = 200L << 30;
        public long FreeBytes(string path) => Free;
        public string? AdbPath { get; init; } = @"C:\adb.exe";
        public bool UsbDriver { get; init; } = true;
        public bool ServiceExists(string name) => UsbDriver;
    }

    [Fact]
    public void HostPreflightGates()
    {
        Assert.False(HostPreflight.Evaluate(new FakeHost(), @"C:\w").HasBlockers());
        Assert.True(HostPreflight.Evaluate(new FakeHost { IsAdministrator = false }, @"C:\w").HasBlockers());
        Assert.True(HostPreflight.Evaluate(new FakeHost { Free = 10L << 30 }, @"C:\w").HasBlockers());
        Assert.True(HostPreflight.Evaluate(new FakeHost { AdbPath = null }, @"C:\w").HasBlockers());
        Assert.True(HostPreflight.Evaluate(new FakeHost { OsArchitecture = Architecture.X86 }, @"C:\w").HasBlockers());
        var usb = HostPreflight.Evaluate(new FakeHost { UsbDriver = false }, @"C:\w");
        Assert.False(usb.HasBlockers());
        Assert.Equal(CheckSeverity.Warning, usb.Single(c => c.Id == "usbdrv").Severity);
    }

    [Fact]
    public void StateRoundTripsAndResumes()
    {
        var dir = Directory.CreateTempSubdirectory("s9woa-state").FullName;
        try
        {
            var s = new InstallState { DeviceSerial = "ABC" };
            s.Set("host", StageStatus.Done);
            s.Set("identify", StageStatus.Done);
            s.Save(dir);

            var loaded = InstallState.Load(dir);
            Assert.Equal("ABC", loaded.DeviceSerial);
            Assert.Equal(StageStatus.Done, loaded.StatusOf("identify"));
            Assert.Equal("unlock", loaded.NextStage()!.Id);
            Assert.Contains("\"Done\"", File.ReadAllText(Path.Combine(dir, "state.json")), StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => loaded.Set("nope", StageStatus.Done));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CatalogIdsAreUnique() =>
        Assert.Equal(StageCatalog.All.Count, StageCatalog.All.Select(s => s.Id).Distinct().Count());
}
