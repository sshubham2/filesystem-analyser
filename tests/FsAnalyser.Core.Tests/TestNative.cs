using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FsAnalyser.Core.Tests;

/// <summary>Win32 calls that only the fixtures need. None of this ships in Core.</summary>
internal static class TestNative
{
    private const uint FSCTL_SET_SPARSE = 0x000900C4;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlIn(SafeFileHandle device, uint ioControlCode, byte[] inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    // A folder's opens as the scanner makes them (SynchronousIoCancel.Open), for fixtures that must show which of its two
    // opens a folder refuses before a scan is asked about it (SL-089).
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>
    /// A folder held open as another program can hold one (SL-085, SL-089): able to list it, and sharing write and delete
    /// but not read, so any later open that asks to list it meets a sharing violation (32) once its access check has
    /// passed. Close the handle to let go.
    /// </summary>
    public static SafeFileHandle HoldFolderSharingNoRead(string folder)
    {
        SafeFileHandle held = CreateFileW(@"\\?\" + folder, FILE_LIST_DIRECTORY, FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (held.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not hold {folder} open.");
        return held;
    }

    /// <summary>
    /// The Win32 error a folder open with <paramref name="access"/> gets, with every share mode and backup semantics as
    /// the scanner asks, or 0 when it opens; the handle is closed at once and nothing is listed.
    /// </summary>
    public static int FolderOpenError(string folder, uint access)
    {
        using SafeFileHandle opened = CreateFileW(@"\\?\" + folder, access, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        return opened.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

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

    /// <summary>
    /// Sets a reparse point with a Microsoft tag (REPARSE_DATA_BUFFER: tag, data length, reserved, then
    /// <paramref name="data"/>) on the file <paramref name="file"/> was opened on for writing. The file keeps its data.
    /// </summary>
    public static void SetReparsePoint(SafeFileHandle file, string path, uint tag, byte[] data)
    {
        var buffer = new byte[8 + data.Length];
        BitConverter.GetBytes(tag).CopyTo(buffer, 0);
        BitConverter.GetBytes(checked((ushort)data.Length)).CopyTo(buffer, 4);
        data.CopyTo(buffer, 8);
        if (!DeviceIoControlIn(file, FSCTL_SET_REPARSE_POINT, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"FSCTL_SET_REPARSE_POINT (tag 0x{tag:X8}) on {path} failed: {new Win32Exception(error).Message}");
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
