using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MineRewind;

internal static class PhysicalWorldDirectory
{
    // Resolve the whole path, including ancestor junctions, and retain the filesystem's actual case.
    internal static string Resolve(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new DirectoryNotFoundException("The physical world directory is unavailable.");
        if (!OperatingSystem.IsWindows())
        {
            var resolved = RealPath(path, IntPtr.Zero);
            if (resolved == IntPtr.Zero) throw new IOException("Cannot resolve the world directory.");
            try { return Marshal.PtrToStringUTF8(resolved) ?? throw new IOException("Invalid physical path."); }
            finally { Free(resolved); }
        }
        const uint readAttributes = 0x80, shareAll = 7, openExisting = 3, backupSemantics = 0x02000000;
        using var handle = CreateFileW(path, readAttributes, shareAll, IntPtr.Zero, openExisting, backupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Cannot open the physical world directory.", Marshal.GetHRForLastWin32Error());
        var buffer = new char[512];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length >= buffer.Length)
        {
            buffer = new char[checked((int)length + 1)];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length >= buffer.Length)
            throw new IOException("Cannot resolve the physical world directory.", Marshal.GetHRForLastWin32Error());
        var final = new string(buffer, 0, (int)length);
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) final = @"\\" + final[8..];
        else if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
        return Path.TrimEndingDirectorySeparator(final);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);

    [DllImport("libc", EntryPoint = "realpath", ExactSpelling = true, BestFitMapping = false,
        ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    [DllImport("libc", EntryPoint = "free", ExactSpelling = true)]
    private static extern void Free(IntPtr pointer);
}
