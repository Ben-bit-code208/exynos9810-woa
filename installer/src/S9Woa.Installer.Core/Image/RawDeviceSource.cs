// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace S9Woa.Installer.Core.Image;

/// <summary>
/// A read-only <see cref="IRawDiskSource"/> over a raw device such as <c>\\.\PhysicalDrive3</c>
/// or <c>\\.\W:</c>. The length comes from <c>IOCTL_DISK_GET_LENGTH_INFO</c>; reads must be
/// sector aligned, which the exporter's 1 MiB chunks at 4 KiB-aligned offsets are. Windows-only.
/// </summary>
public sealed class RawDeviceSource : IRawDiskSource, IDisposable
{
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private readonly string _path;

    public RawDeviceSource(string devicePath)
    {
        _path = devicePath;
        using var handle = Open();
        Length = QueryLength(handle);
    }

    public static RawDeviceSource ForPhysicalDrive(int number) => new($@"\\.\PhysicalDrive{number}");

    public static RawDeviceSource ForVolume(char driveLetter) => new($@"\\.\{char.ToUpperInvariant(driveLetter)}:");

    public long Length { get; }

    public Stream OpenRead(long offset)
    {
        var stream = new FileStream(Open(), FileAccess.Read, bufferSize: 0);
        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }
        return stream;
    }

    public void Dispose() { }

    private SafeFileHandle Open()
    {
        var handle = CreateFileW(_path, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not open {_path} (error {error}).");
        }
        return handle;
    }

    private long QueryLength(SafeFileHandle handle)
    {
        var buffer = new byte[8];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (!DeviceIoControl(handle, IoctlDiskGetLengthInfo, IntPtr.Zero, 0,
                    pinned.AddrOfPinnedObject(), (uint)buffer.Length, out _, IntPtr.Zero))
            {
                throw new IOException($"Could not read the length of {_path} (error {Marshal.GetLastWin32Error()}).");
            }
            return BitConverter.ToInt64(buffer);
        }
        finally
        {
            pinned.Free();
        }
    }

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
        uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
