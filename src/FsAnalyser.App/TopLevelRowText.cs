using FsAnalyser.Core;
using static FsAnalyser.App.FigureText;

namespace FsAnalyser.App;

/// <summary>
/// The two lines of a results row (SL-039): the name with how far it has been read, and its figures. A
/// converter, so only the rows on screen are ever put into words: the list virtualizes, and a root with
/// 100,000 entries formats a screenful per refresh, not 100,000 rows. No percentages: mid-scan and after
/// Cancel a share would be of an incomplete whole. A Done row is its bare name unless a correction moved its
/// figures after it settled, which it says (SL-063): Done promises the row was listed and measured, and the
/// mark is how a figure that changed since is told apart. A folder that could not be read in full is worded as the
/// unreadable-folders list and the folder tree word it, through the one function, UnreadableRows.KindText, from the
/// kind the scanner stamped: its open refused, opened and failed before its first entry, or listed partway (HF-002,
/// SL-106). A row in that state is never left as its bare name, which is how a Done row reads.
/// </summary>
/// <remarks>
/// The words are here and the converter that binds them is in TopLevelRowText.Converter.cs, so this half has no WPF
/// and is also compiled into Core.Tests, where its words are tested on rows a real scan built.
/// </remarks>
public sealed partial class TopLevelRowText
{
    internal static string TitleOf(TopLevelRow row) => row.State switch
    {
        TopLevelState.Reading => $"{row.Name}  (reading...)",
        TopLevelState.NotRead => $"{row.Name}  (not read yet)",
        TopLevelState.StoppedPartway => $"{row.Name}  (stopped partway)",
        TopLevelState.Unreadable => $"{row.Name}  ({UnreadableRows.KindText(row.FailedKind)})",
        TopLevelState.NotFollowed => $"{row.Name}  (link, not followed)",
        TopLevelState.NotScanned => $"{row.Name}  (size not counted: Windows container layers)",
        TopLevelState.Done when row.Revised => $"{row.Name}  (revised)",
        _ => row.Name,
    };

    internal static string FiguresOf(TopLevelRow row)
    {
        string size = $"{FormatSize(row.Bytes)} ({FormatSize(row.OnDisk)} on disk)";
        if (!row.IsFolder)
            return size;
        // The count holds folders that could not be opened and folders listed partway alike (HF-002).
        string unreadable = row.UnreadableBelow > 0
            ? $"; {Count(row.UnreadableBelow, "folder")} inside could not be read (in full)"
            : "";
        // A folder row counts its own folder; the words count what is inside it.
        return $"{size} in {Count(row.Files, "file")} and {Count(row.Folders - 1, "folder")}{unreadable}";
    }
}
