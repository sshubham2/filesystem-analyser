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

    private const uint SPI_GETWORKAREA = 0x0030;

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out Rect rect, uint winIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>The primary monitor's work area, as SPI_GETWORKAREA reports it: what WPF's SystemParameters.WorkArea reads.</summary>
    public static (int Left, int Top, int Right, int Bottom) PrimaryWorkArea() =>
        SystemParametersInfo(SPI_GETWORKAREA, 0, out Rect rect, 0)
            ? (rect.Left, rect.Top, rect.Right, rect.Bottom)
            : throw new Win32Exception(Marshal.GetLastWin32Error());

    public static void SetSparse(SafeFileHandle file, string path)
    {
        if (!DeviceIoControl(file, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"FSCTL_SET_SPARSE on {path} failed: {new Win32Exception(error).Message}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetThreadErrorMode();

    [DllImport("kernel32.dll", EntryPoint = "SetThreadErrorMode", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadErrorModeNative(uint newMode, out uint oldMode);

    /// <summary>The calling thread's own error mode (SEM_* bits), as GetThreadErrorMode reports it. The process's default is not in it.</summary>
    public static uint ThreadErrorMode() => GetThreadErrorMode();

    /// <summary>Sets the calling thread's error mode and returns the one it replaced.</summary>
    public static uint SetThreadErrorMode(uint newMode) =>
        SetThreadErrorModeNative(newMode, out uint oldMode)
            ? oldMode
            : throw new Win32Exception(Marshal.GetLastWin32Error());

    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string path, out uint high);

    /// <summary>What <paramref name="path"/> takes on disk, as the volume reports it, compressed or not.</summary>
    public static long SizeOnDisk(string path)
    {
        uint low = GetCompressedFileSize(path, out uint high);
        int error = Marshal.GetLastWin32Error();
        if (low == uint.MaxValue && error != 0)
            throw new Win32Exception(error, $"GetCompressedFileSize on {path} failed: {new Win32Exception(error).Message}");
        return ((long)high << 32) | low;
    }
}
