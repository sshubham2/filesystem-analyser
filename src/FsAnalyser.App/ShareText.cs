using System.Globalization;

namespace FsAnalyser.App;

/// <summary>
/// How the window writes a share as a percentage, wherever it shows one: the folder tree's rows (SL-044) and the file
/// kinds list (SL-046). One formatter, so the two cannot come to write the same share two ways (SL-046 review m2).
/// </summary>
/// <remarks>
/// No WPF and no window: it is also compiled into Core.Tests, beside the file kinds list's words that call it.
/// </remarks>
internal static class ShareText
{
    /// <summary>
    /// The share as a percentage to one decimal place, a midpoint rounded away from zero (SL-044 review m2: shares of
    /// 4 KiB multiples such as 1/16 are exact binary midpoints, 6.25), in the culture the sizes are written in.
    /// </summary>
    internal static string Percent(double share) =>
        Math.Round(share * 100, 1, MidpointRounding.AwayFromZero).ToString("F1", CultureInfo.CurrentCulture) + " %";
}
