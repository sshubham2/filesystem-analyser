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
}
