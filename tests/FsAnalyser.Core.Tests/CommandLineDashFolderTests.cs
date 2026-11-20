namespace FsAnalyser.Core.Tests;

// A quoted root ending in a backslash, then a quoted report path whose folder name starts with "--" after a space. Windows merges
// the root with the start of the report path and leaves the rest of the path standing as a token that starts with "--"; that token
// is a piece of a path the person quoted, not an option, and the app's only option is --report. The line as typed accounts for it.
public class CommandLineDashFolderTests
{
    [Theory]
    [InlineData(@"""D:\Data\"" --report ""C:\out --old\r.json""", @"D:\Data\", @"C:\out --old\r.json")]
    [InlineData(@"""D:\My Data\"" --report ""C:\Reports --2024\r.json""", @"D:\My Data\", @"C:\Reports --2024\r.json")]
    public void AQuotedReportPathWithAFolderStartingWithTwoDashesIsRepairedAsTyped(string tail, string root, string report)
    {
        string[] argv = TestNative.SplitArguments(tail);
        Assert.Contains(argv, a => a.Contains('"')); // Windows merged the root with what follows

        string[]? got = CommandLine.Arguments(argv, "app.exe " + tail);

        Assert.NotNull(got);
        Assert.Equal([root, "--report", report], got);
    }
}
