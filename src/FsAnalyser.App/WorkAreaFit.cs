namespace FsAnalyser.App;

/// <summary>
/// How the window fits itself to the work area of the monitor it opened on (SL-044, HF-019): no taller than that work
/// area, and moved up, if its foot is below it, so that the whole of it is on screen. The work area is that monitor's,
/// never the primary's by default: measured against the primary's, a window Windows placed on a monitor below it was
/// moved up onto the primary, and one on a taller monitor was cut to the primary's height.
/// </summary>
/// <remarks>
/// No WPF and no window, in device-independent pixels: it is also compiled into Core.Tests, which has neither, so it is
/// fed monitors this PC does not have. The window keeps its MinHeight whatever this returns (SL-044 review M2).
/// </remarks>
internal static class WorkAreaFit
{
    /// <summary>The window's top and height inside the work area from <paramref name="areaTop"/> to <paramref name="areaBottom"/>.</summary>
    internal static (double Top, double Height) Fit(double top, double height, double areaTop, double areaBottom)
    {
        height = Math.Min(height, areaBottom - areaTop);
        return (top + height > areaBottom ? Math.Max(areaTop, areaBottom - height) : top, height);
    }
}
