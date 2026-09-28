using System.Runtime.InteropServices;

namespace FsAnalyser.Core.Native;

/// <summary>A work area as Windows reports it: the rectangle's edges in device pixels, right and bottom exclusive.</summary>
public readonly record struct WorkArea(int Left, int Top, int Right, int Bottom);

/// <summary>
/// The work area of the monitor a window is on (HF-019): the window fits itself to it, where WPF's
/// SystemParameters.WorkArea only ever knows the primary monitor's. Nothing here reads a drive.
/// </summary>
public static partial class Monitors
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    /// <summary>
    /// The work area (the monitor less the taskbar and docked bars) of the monitor that holds most of
    /// <paramref name="window"/>, or the nearest one when it is on none, in the device pixels Windows gives the calling
    /// thread; null when Windows does not answer.
    /// </summary>
    public static WorkArea? WorkAreaOf(nint window)
    {
        nint monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            return null;
        return new WorkArea(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    // MONITORINFO: its size first, which GetMonitorInfo checks, then the whole monitor, its work area, and its flags.
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }
}
