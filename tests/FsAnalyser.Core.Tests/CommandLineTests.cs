namespace FsAnalyser.Core.Tests;

/// <summary>What the app does with its path argument after Windows has split the command line.</summary>
public class CommandLineTests
{
    [Theory]
    [InlineData("C:\"", @"C:\")]                           // typed as "C:\"
    [InlineData(@"D:\My Folder""", @"D:\My Folder\")]      // typed as "D:\My Folder\"
    [InlineData(@"\\server\share""", @"\\server\share\")]  // typed as "\\server\share\"
    [InlineData(@"C:\", @"C:\")]                           // typed unquoted
    [InlineData(@"C:\Users", @"C:\Users")]
    [InlineData(@"D:\My Folder", @"D:\My Folder")]         // typed as "D:\My Folder"
    public void AQuotedPathEndingInABackslashGetsItsBackslashBack(string argument, string root)
    {
        Assert.Equal(root, CommandLine.RootArgument(argument));
    }

    // SL-007. Every row below is a command line as typed (`tail`, what follows the program's name), the argv Windows' own
    // splitter gives it (TestNative.SplitArguments, shell32), and the arguments as typed, written out by hand beforehand.
    // The command line the process sees is the program's name and the tail.
    private static string[] Repair(string tail, string program = "app.exe")
    {
        string[]? repaired = CommandLine.Arguments(TestNative.SplitArguments(tail), program + " " + tail);
        Assert.NotNull(repaired); // a line Windows merged and the line as typed accounts for is never refused
        return repaired;
    }

    [Theory]
    [InlineData(@"""D:\Data\"" --report out.json", @"D:\Data"" --report out.json", @"D:\Data\", "--report", "out.json")]
    [InlineData(@"""C:\"" --report C:\r.json", @"C:"" --report C:\r.json", @"C:\", "--report", @"C:\r.json")]
    public void AQuotedRootEndingInABackslashThenReportIsSplitAsTyped(string tail, string mergedByWindows, params string[] asTyped)
    {
        // Windows' own split of the line is the ONE merged argument, which is the hang this slice fixes.
        Assert.Equal([mergedByWindows], TestNative.SplitArguments(tail));

        Assert.Equal(asTyped, Repair(tail));
    }

    [Fact]
    public void ASpacedRootAndASpacedReportPathKeepTheirSpaces()
    {
        const string tail = @"""D:\My Data\"" --report ""C:\out  dir\r.json""";

        // After the \" Windows flips the quoting of the rest of the line: 2 arguments, the report path split in two and
        // its run of two spaces lost. Only the line as typed holds the path.
        Assert.Equal([@"D:\My Data"" --report C:\out", @"dir\r.json"], TestNative.SplitArguments(tail));

        Assert.Equal([@"D:\My Data\", "--report", @"C:\out  dir\r.json"], Repair(tail));
    }

    [Fact]
    public void AQuotedRootEndingInABackslashAloneIsTheFolder()
    {
        const string tail = @"""D:\Data\""";

        // argv's only quote is the last character of its only argument: the gate opens on it all the same.
        Assert.Equal([@"D:\Data"""], TestNative.SplitArguments(tail));

        Assert.Equal([@"D:\Data\"], Repair(tail));
    }

    [Theory]
    [InlineData(@"""D:\Data"" --report out.json", @"D:\Data", "--report", "out.json")]
    [InlineData(@"""D:\Data\\"" --report out.json", @"D:\Data\", "--report", "out.json")]  // an even run: Windows halves it
    [InlineData(@"""D:\My Data"" --report ""C:\out dir\r.json""", @"D:\My Data", "--report", @"C:\out dir\r.json")]
    [InlineData(@"C:\ --report C:\r.json", @"C:\", "--report", @"C:\r.json")]               // unquoted: a backslash before no quote
    [InlineData(@"""C:\Program Files"" --report out.json", @"C:\Program Files", "--report", "out.json")]
    public void ALineWindowsSplitCleanlyIsReturnedAsItCame(string tail, params string[] expected)
    {
        string[] argv = TestNative.SplitArguments(tail);
        Assert.Equal(expected, argv); // the row's own premise: Windows' split is what a person meant, and holds no quote

        // The gate is closed: argv comes back as the same array, and a line that would split differently is never read.
        Assert.Same(argv, CommandLine.Arguments(argv, "app.exe " + tail));
        Assert.Same(argv, CommandLine.Arguments(argv, @"other.exe ""not that line\"" at all"));
        Assert.Same(argv, CommandLine.Arguments(argv, ""));
    }

    [Fact]
    public void ALineWithNoArgumentsIsReturnedAsItCame()
    {
        string[] argv = [];
        Assert.Same(argv, CommandLine.Arguments(argv, "app.exe"));
    }

    [Fact]
    public void ALineThatDoesNotMatchItsArgvIsRefusedOrLeftAlone()
    {
        const string tail = @"""D:\Data\"" --report out.json";
        string[] merged = TestNative.SplitArguments(tail);
        Assert.Equal([@"D:\Data"" --report out.json"], merged);

        // The process was not started from that line the way the repair reads it: `dotnet app.dll "D:\Data\" ...` has the dll
        // as the line's first argument and not in argv. A quote inside an argument that nothing accounts for: null.
        Assert.Null(CommandLine.Arguments(merged, "dotnet.exe app.dll " + tail));
        Assert.Null(CommandLine.Arguments(merged, "app.exe extra " + tail));
        Assert.Null(CommandLine.Arguments(merged, "app.exe"));
        Assert.Null(CommandLine.Arguments(merged, ""));
        Assert.Null(CommandLine.Arguments([@"a""b"], "app.exe c"));

        // Only a trailing quote: what RootArgument repairs for the root, argv as it is.
        string[] trailing = [@"D:\Data"""];
        Assert.Same(trailing, CommandLine.Arguments(trailing, "app.exe something else"));
        Assert.Same(trailing, CommandLine.Arguments(trailing, ""));
        string[] twoTrailing = ["--report", "out.json", @"D:\Data"""];
        Assert.Same(twoTrailing, CommandLine.Arguments(twoTrailing, "dotnet.exe app.dll --report out.json x"));

        // A trailing quote and an inner one in different arguments: the inner one decides.
        Assert.Null(CommandLine.Arguments([@"D:\Data""", @"E:\a""b"], "app.exe nothing"));
    }

    // The grammar the repair reads differently from Windows, or that it opens (lesson #791): one row each. The first column
    // is the program's name as the line starts, which the repair must skip by the C runtime's rule.
    public static TheoryData<string, string, string[]> TheGrammar() => new()
    {
        // two \" in one line: both close a quoted part
        { "app.exe", @"""D:\A\"" --report ""E:\out\""", [@"D:\A\", "--report", @"E:\out\"] },
        // an odd run of three before a quote is kept whole, as typed
        { "app.exe", @"""D:\A\\\"" --report out.json", [@"D:\A\\\", "--report", "out.json"] },
        // an even run elsewhere on a repaired line is halved, as Windows halves it
        { "app.exe", @"""D:\A\"" --report ""E:\out\\""", [@"D:\A\", "--report", @"E:\out\"] },
        // a tab separates arguments outside quotes, and does not inside them
        { "app.exe", "\"D:\\Data\\\"\t--report\tout.json", [@"D:\Data\", "--report", "out.json"] },
        { "app.exe", "\"D:\\My Data\\\"\t--report \"C:\\a\tb\\r.json\"", [@"D:\My Data\", "--report", "C:\\a\tb\\r.json"] },
        // the program's name: quoted with a space (runs to the next quote), and unquoted
        { @"""C:\Program Files\FsAnalyser\app.exe""", @"""D:\Data\"" --report out.json", [@"D:\Data\", "--report", "out.json"] },
        { @"C:\Tools\app.exe", @"""D:\Data\"" --report out.json", [@"D:\Data\", "--report", "out.json"] },
        // an empty quoted value is an argument
        { "app.exe", @"--report """" ""D:\Data\""", ["--report", "", @"D:\Data\"] },
        // a root with characters outside ASCII
        { "app.exe", @"""D:\Dätä\Ünï 日本\"" --report out.json", [@"D:\Dätä\Ünï 日本\", "--report", "out.json"] },
        // a share root and a long-path root: their leading backslashes are not an escape
        { "app.exe", @"""\\server\share\"" --report out.json", [@"\\server\share\", "--report", "out.json"] },
        { "app.exe", @"""\\?\C:\"" --report out.json", [@"\\?\C:\", "--report", "out.json"] },
        { "app.exe", @"""\\?\UNC\server\share\"" --report out.json", [@"\\?\UNC\server\share\", "--report", "out.json"] },
        // the root last, after --report
        { "app.exe", @"--report out.json ""D:\Data\""", ["--report", "out.json", @"D:\Data\"] },
        // a root that is not first
        { "app.exe", @"extra ""D:\Data\"" --report out.json", ["extra", @"D:\Data\", "--report", "out.json"] },
        // a bare drive in quotes and a spaced root, with the report unquoted
        { "app.exe", @"""C:\"" --report out.json", [@"C:\", "--report", "out.json"] },
        { "app.exe", @"""D:\My Data\"" --report out.json", [@"D:\My Data\", "--report", "out.json"] },
    };

    [Theory]
    [MemberData(nameof(TheGrammar))]
    public void TheGrammarTheRepairOpensIsSplitAsTyped(string program, string tail, string[] expected)
    {
        string[] argv = TestNative.SplitArguments(tail);
        Assert.Contains(argv, a => a.Contains('"')); // every row is one Windows merged: the gate is open

        Assert.Equal(expected, Repair(tail, program));
    }
}
