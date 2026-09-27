// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace S9Woa.Installer.Core.Recovery;

/// <summary>
/// A channel that exchanges a raw SCSI CDB with one storage device and returns
/// the data-in reply. Abstracted so the recovery-ticket orchestration can be
/// unit-tested without real hardware.
/// </summary>
public interface IUcr1Transport : IDisposable
{
    /// <summary>Sends <paramref name="cdb"/> and reads back <paramref name="replyLength"/> data-in bytes.</summary>
    byte[] SendCdb(byte[] cdb, int replyLength);
}

/// <summary>Opens per-physical-drive transports and reports how many drives to scan.</summary>
public interface IUcr1TransportFactory
{
    /// <summary>Best-effort upper bound of \\.\PhysicalDriveN indices to probe.</summary>
    int PhysicalDriveCount { get; }

    /// <summary>Opens a transport for one physical drive, or null if it can't be opened.</summary>
    IUcr1Transport? Open(int physicalDriveIndex);
}

/// <summary>Requests a normal Windows restart. The UFS driver's shutdown path does the rest.</summary>
public interface ISystemRestart
{
    void Restart(string reason);
}

/// <summary>
/// Sends the UCR1 vendor CDB to a physical drive with
/// <c>IOCTL_SCSI_PASS_THROUGH_DIRECT</c>. Windows-only; requires elevation.
/// </summary>
public sealed class ScsiPassThroughTransport : IUcr1Transport
{
    private const int SenseLength = 32;
    private const byte ScsiIoctlDataIn = 1;
    private const uint IoctlScsiPassThroughDirect = 0x4D014;

    private readonly SafeFileHandle _handle;

    private ScsiPassThroughTransport(SafeFileHandle handle) => _handle = handle;

    public static ScsiPassThroughTransport? Open(int physicalDriveIndex)
    {
        var handle = CreateFileW($@"\\.\PhysicalDrive{physicalDriveIndex}",
            GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return new ScsiPassThroughTransport(handle);
    }

    public byte[] SendCdb(byte[] cdb, int replyLength)
    {
        ArgumentNullException.ThrowIfNull(cdb);
        if (cdb.Length is 0 or > 16)
        {
            throw new ArgumentException("CDB must be 1..16 bytes.", nameof(cdb));
        }

        var reply = new byte[replyLength];
        var dataHandle = GCHandle.Alloc(reply, GCHandleType.Pinned);
        try
        {
            var request = new ScsiPassThroughDirectWithSense
            {
                Sptd = new ScsiPassThroughDirect
                {
                    Length = (ushort)Marshal.SizeOf<ScsiPassThroughDirect>(),
                    CdbLength = (byte)cdb.Length,
                    SenseInfoLength = SenseLength,
                    DataIn = ScsiIoctlDataIn,
                    DataTransferLength = (uint)replyLength,
                    TimeOutValue = 10,
                    DataBuffer = dataHandle.AddrOfPinnedObject(),
                    SenseInfoOffset = (uint)Marshal.OffsetOf<ScsiPassThroughDirectWithSense>(nameof(ScsiPassThroughDirectWithSense.Sense)),
                    Cdb = new byte[16],
                },
                Sense = new byte[SenseLength],
            };
            Array.Copy(cdb, request.Sptd.Cdb, cdb.Length);

            var size = Marshal.SizeOf<ScsiPassThroughDirectWithSense>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(request, buffer, false);
                if (!DeviceIoControl(_handle, IoctlScsiPassThroughDirect, buffer, (uint)size, buffer, (uint)size, out _, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SCSI pass-through failed.");
                }
                var outStruct = Marshal.PtrToStructure<ScsiPassThroughDirectWithSense>(buffer);
                if (outStruct.Sptd.ScsiStatus != 0)
                {
                    throw new IOException($"Device rejected the vendor command (SCSI status 0x{outStruct.Sptd.ScsiStatus:X2}).");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return reply;
        }
        finally
        {
            dataHandle.Free();
        }
    }

    public void Dispose() => _handle.Dispose();

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPassThroughDirect
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPassThroughDirectWithSense
    {
        public ScsiPassThroughDirect Sptd;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = SenseLength)]
        public byte[] Sense;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
        uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}

/// <summary>Opens <see cref="ScsiPassThroughTransport"/> handles over the local physical drives.</summary>
public sealed class ScsiPassThroughTransportFactory : IUcr1TransportFactory
{
    public ScsiPassThroughTransportFactory(int physicalDriveCount = 16) => PhysicalDriveCount = physicalDriveCount;

    public int PhysicalDriveCount { get; }

    public IUcr1Transport? Open(int physicalDriveIndex) => ScsiPassThroughTransport.Open(physicalDriveIndex);
}

/// <summary>Restarts Windows with <c>shutdown.exe /r</c>.</summary>
public sealed class ShutdownExeRestart : ISystemRestart
{
    public void Restart(string reason)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe")
        {
            ArgumentList = { "/r", "/t", "0", "/c", reason },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
