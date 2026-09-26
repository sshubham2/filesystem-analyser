using System.Globalization;

namespace FsAnalyser.App;

/// <summary>
/// The words and the bar of a folder tree row (SL-044): sizes as the rest of the window writes them, and the
/// row's share of its parent's space on disk, as Core computed it (ChildRow.Share).
/// </summary>
internal static class FolderTreeRowText
{
    /// <summary>The width of the track a share bar is drawn in, in device-independent pixels.</summary>
    internal const double BarTrack = 60;

    internal static string Size(long bytes) => MainWindow.FormatSize(bytes);

    /// <summary>
    /// The share as a percentage to one decimal place, a midpoint rounded away from zero (review m2: shares of
    /// 4 KiB multiples such as 1/16 are exact binary midpoints, 6.25), in the culture the sizes are written in.
    /// A share of 0, which is every child's when the parent takes nothing on disk, reads "0.0 %", and so does a
    /// share too small to show at one decimal.
    /// </summary>
    internal static string Share(double share) =>
        Math.Round(share * 100, 1, MidpointRounding.AwayFromZero).ToString("F1", CultureInfo.CurrentCulture) + " %";

    /// <summary>
    /// The bar: the share of the track. 0 draws nothing; any other share draws at least one pixel, so a row that
    /// takes some space never shows an empty bar, whatever its percentage rounds to.
    /// </summary>
    internal static double BarWidth(double share) =>
        share > 0 ? Math.Clamp(share * BarTrack, 1, BarTrack) : 0;
}
