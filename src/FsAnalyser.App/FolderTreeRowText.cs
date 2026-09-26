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
    /// The share as a percentage, as the window writes every share (<see cref="ShareText.Percent"/>). A share of 0,
    /// which is every child's when the parent takes nothing on disk, reads "0.0 %", and so does a share too small to
    /// show at one decimal.
    /// </summary>
    internal static string Share(double share) => ShareText.Percent(share);

    /// <summary>
    /// The bar: the share of the track. 0 draws nothing; any other share draws at least one pixel, so a row that
    /// takes some space never shows an empty bar, whatever its percentage rounds to.
    /// </summary>
    internal static double BarWidth(double share) =>
        share > 0 ? Math.Clamp(share * BarTrack, 1, BarTrack) : 0;
}
