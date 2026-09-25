using System.Globalization;
using System.Windows.Data;
using FsAnalyser.Core;

namespace FsAnalyser.App;

/// <summary>
/// The two lines of a results row (SL-039): the name with how far it has been read, and its figures. A
/// converter, so only the rows on screen are ever put into words: the list virtualizes, and a root with
/// 100,000 entries formats a screenful per refresh, not 100,000 rows. No percentages: mid-scan and after
/// Cancel a share would be of an incomplete whole.
/// </summary>
public sealed class TopLevelRowText : IValueConverter
{
    /// <summary>The figures line; otherwise the name line.</summary>
    public bool Figures { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not TopLevelRow row ? "" : Figures ? FiguresOf(row) : TitleOf(row);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string TitleOf(TopLevelRow row) => row.State switch
    {
        TopLevelState.Reading => $"{row.Name}  (reading...)",
        TopLevelState.NotRead => $"{row.Name}  (not read yet)",
        TopLevelState.StoppedPartway => $"{row.Name}  (stopped partway)",
        TopLevelState.Unreadable => $"{row.Name}  (could not be opened)",
        TopLevelState.NotFollowed => $"{row.Name}  (link, not followed)",
        _ => row.Name,
    };

    private static string FiguresOf(TopLevelRow row)
    {
        string size = $"{MainWindow.FormatSize(row.Bytes)} ({MainWindow.FormatSize(row.OnDisk)} on disk)";
        if (row.Kind != TopLevelKind.Folder)
            return size;
        string unreadable = row.UnreadableBelow > 0
            ? $"; {MainWindow.Count(row.UnreadableBelow, "folder")} inside could not be opened"
            : "";
        // A folder row counts its own folder; the words count what is inside it.
        return $"{size} in {MainWindow.Count(row.Files, "file")} and {MainWindow.Count(row.Folders - 1, "folder")}{unreadable}";
    }
}
