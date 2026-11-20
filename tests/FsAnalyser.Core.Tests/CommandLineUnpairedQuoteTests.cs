namespace FsAnalyser.Core.Tests;

// A root typed with an unpaired backslash-quote and no opening quote: Windows splits the line into three arguments, and only
// the root carries a trailing quote, which RootArgument repairs. The repair must not join what Windows split.
public class CommandLineUnpairedQuoteTests
{
    [Fact]
    public void AnUnpairedQuoteAfterABackslashDoesNotJoinWhatWindowsSplit()
    {
        const string tail = @"D:\Data\"" --report out.json";
        string[] argv = TestNative.SplitArguments(tail);
        Assert.Equal([@"D:\Data""", "--report", "out.json"], argv);

        string[]? got = CommandLine.Arguments(argv, "app.exe " + tail);

        Assert.NotNull(got);
        Assert.Equal(3, got.Length);
        Assert.Equal(@"D:\Data\", CommandLine.RootArgument(got[0]));
        Assert.Equal("--report", got[1]);
        Assert.Equal("out.json", got[2]);
    }
}
