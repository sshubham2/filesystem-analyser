using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FsAnalyser.Core.Tests;

/// <summary>Win32 calls that only the fixtures need. None of this ships in Core.</summary>
internal static class TestNative
{
    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    public static void SetSparse(SafeFileHandle file, string path)
    {
        if (!DeviceIoControl(file, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"FSCTL_SET_SPARSE on {path} failed: {new Win32Exception(error).Message}");
        }
    }
}
