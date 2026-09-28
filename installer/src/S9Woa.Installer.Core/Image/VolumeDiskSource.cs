// SPDX-License-Identifier: BSD-2-Clause-Patent
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace S9Woa.Installer.Core.Image;

/// <summary>
/// A <see cref="IRawDiskSource"/> over a mounted volume such as <c>\\.\W:</c>.
/// Reads the whole volume's raw bytes; the length comes from
/// <c>IOCTL_DISK_GET_LENGTH_INFO</c>. Windows-only.
/// </summary>
public sealed class VolumeDiskSource : IRawDiskSource, IDisposable
{
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private readonly string _volumePath;

    public VolumeDiskSource(char driveLetter)
    {
        _volumePath = $@"\\.\{char.ToUpperInvariant(driveLetter)}:";
        using var handle = Open();
        Length = QueryLength(handle);
    }

    public long Length { get; }

    public Stream OpenRead(long offset)
    {
        var handle = Open();
        var stream = new FileStream(handle, FileAccess.Read);
        if (offset > 0)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }
        return stream;
    }

    public void Dispose() { }

    private SafeFileHandle Open()
    {
        var handle = CreateFileW(_volumePath, GenericRead, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException($"Could not open volume {_volumePath} (error {Marshal.GetLastWin32Error()}).");
        }
        return handle;
    }

    private static long QueryLength(SafeFileHandle handle)
    {
        var buffer = new byte[8];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            if (!DeviceIoControl(handle, IoctlDiskGetLengthInfo, IntPtr.Zero, 0,
                    pinned.AddrOfPinnedObject(), (uint)buffer.Length, out _, IntPtr.Zero))
            {
                throw new IOException($"Could not read volume length (error {Marshal.GetLastWin32Error()}).");
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
