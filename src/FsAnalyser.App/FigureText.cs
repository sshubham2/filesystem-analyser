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
