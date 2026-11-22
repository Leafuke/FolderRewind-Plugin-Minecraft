using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MineRewind;

/// <summary>Holds the existing Java session marker open without admitting another data handle or deletion.</summary>
internal sealed class MinecraftStoppedWorldLock : IDisposable
{
    private const uint ReadData = 0x0001;
    private const uint ReadAttributes = 0x0080;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint FixedDrive = 3;
    private readonly string _path;
    private readonly LockIdentity _identity;
    private SafeFileHandle? _handle;

    private MinecraftStoppedWorldLock(string path, LockIdentity identity, SafeFileHandle handle)
    {
        _path = path;
        _identity = identity;
        _handle = handle;
    }

    internal static MinecraftStoppedWorldLock Open(string worldDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new IOException("Stopped-world protection requires Windows file sharing.");
        var path = Path.Combine(worldDirectory, "session.lock");
        if (GetDriveTypeW(Path.GetPathRoot(path)!) != FixedDrive)
            throw new IOException("Stopped-world protection requires a fixed local volume.");

        var handle = OpenMarker(path, ReadData | ReadAttributes, share: 0);
        try
        {
            var fileSystem = new StringBuilder(32);
            if (!GetVolumeInformationByHandleW(handle, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    fileSystem, (uint)fileSystem.Capacity) || !fileSystem.ToString().Equals("NTFS", StringComparison.Ordinal))
                throw new IOException("Stopped-world protection requires an NTFS lock file.");
            return new(path, ReadIdentity(handle), handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void RequireHeld()
    {
        var handle = _handle ?? throw new ObjectDisposedException(nameof(MinecraftStoppedWorldLock));
        // An attributes-only open verifies the path without asking to share file data with our own guard.
        using var current = OpenMarker(_path, ReadAttributes, ShareAll);
        if (ReadIdentity(handle) != _identity || ReadIdentity(current) != _identity)
            throw new IOException("The protected Minecraft session lock changed.");
    }

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();

    private static SafeFileHandle OpenMarker(string path, uint access, uint share)
    {
        var handle = CreateFileW(path, access, share, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("Could not hold or inspect Minecraft session.lock.", unchecked((int)0x80070000) | error);
    }

    private static LockIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("Could not identify Minecraft session.lock.", Marshal.GetHRForLastWin32Error());
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || info.LinkCount != 1)
            throw new IOException("Minecraft session.lock must be a regular file with a single name.");
        return new(info.VolumeSerial, info.IndexHigh, info.IndexLow);
    }

    private readonly record struct LockIdentity(uint Volume, uint High, uint Low);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint VolumeSerial, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPath);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, IntPtr volumeName, uint volumeNameSize,
        IntPtr serialNumber, IntPtr maximumComponentLength, IntPtr fileSystemFlags, StringBuilder fileSystemName, uint fileSystemNameSize);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
