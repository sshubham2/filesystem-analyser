using System;
using System.Linq;

/// <summary>
/// How the hook reads the scanning panel's counters line (CountersText), with no WPF in it, so that Core.Tests compiles this file
/// as source and a test settles what the stop trigger reads (SC-044, SL-001).
/// </summary>
/// <remarks>
/// The line is "1,234 files, 5,678 folders, 3.2 GB (3.2 GB on disk), 2 folders unreadable", and with a count of one
/// "1 file, 1 folder, ..., 1 folder unreadable": FigureText.Counters writes each count through FigureText.Count. The folder
/// count is the number between the last ', ' before the first ' folder' and that ' folder', which is always the counters' own:
/// whatever 'N folders unreadable' follows comes after it.
/// </remarks>
internal static class CountersReading
{
    /// <summary>The folder count in a counters line, whatever its thousands separator; 0 when the line holds none.</summary>
    internal static int FoldersIn(string counters)
    {
        int at = counters.IndexOf(" folder", StringComparison.Ordinal);
        if (at < 0)
            return 0;
        int start = counters.LastIndexOf(", ", at, StringComparison.Ordinal);
        string digits = new(counters[(start < 0 ? 0 : start + 2)..at].Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : 0;
    }
}
