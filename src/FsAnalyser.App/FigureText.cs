namespace FsAnalyser.App;

/// <summary>
/// How the window writes a size and a count, wherever it shows one: the outcome, the results list, the folder tree
/// and the file kinds list. One formatter, so no two places write the same figure two ways (HF-012).
/// </summary>
/// <remarks>
/// No WPF and no window: it is also compiled into Core.Tests, beside the row words that call it.
/// </remarks>
internal static class FigureText
{
    internal static string Count(long n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    /// <summary>
    /// The scanning panel's counters line (SL-044): files, folders, size and size on disk, and the folders that could not be read
    /// once there are any. Every count through <see cref="Count"/>, so one file or one folder is '1 file' and '1 folder' here as
    /// everywhere else. The window cases' hook reads the folder count back from this text (CountersReading.FoldersIn).
    /// </summary>
    internal static string Counters(long files, long folders, long bytes, long onDisk, long unreadable) =>
        $"{Count(files, "file")}, {Count(folders, "folder")}, {FormatSize(bytes)} ({FormatSize(onDisk)} on disk)" +
        (unreadable > 0 ? $", {Count(unreadable, "folder")} unreadable" : "");

    internal static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} bytes" : $"{value:N1} {units[unit]}";
    }
}
