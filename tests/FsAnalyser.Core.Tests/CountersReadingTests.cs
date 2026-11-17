using FsAnalyser.App;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The scanning panel's counters line and the window hook's reading of its folder count (SC-044, SL-001). A count of one is written
/// in the singular ('1 file', '1 folder', '1 folder unreadable') as everywhere else, and the hook, which stops a scan at a folder
/// count, must still read it. The hook's FoldersIn is compiled here as source, with no WPF in it.
/// </summary>
public class CountersReadingTests
{
    [Fact]
    public void ACountOfOneIsWrittenInTheSingularEverywhereTheCountersWriteOne()
    {
        Assert.Equal("1 file, 1 folder, 0 bytes (0 bytes on disk), 1 folder unreadable", FigureText.Counters(1, 1, 0, 0, 1));
        Assert.Equal("1 file, 1 folder, 0 bytes (0 bytes on disk)", FigureText.Counters(1, 1, 0, 0, 0));
        Assert.Equal("0 files, 0 folders, 0 bytes (0 bytes on disk)", FigureText.Counters(0, 0, 0, 0, 0));
    }

    [Fact]
    public void ACountOfTwoOrMoreIsWrittenAsItAlwaysWas()
    {
        Assert.Equal("2 files, 3 folders, 0 bytes (0 bytes on disk), 2 folders unreadable", FigureText.Counters(2, 3, 0, 0, 2));
    }

    [Theory]
    [InlineData("1 file, 1 folder, 64.0 KB (64.0 KB on disk)", 1)]
    [InlineData("1 file, 1 folder, 64.0 KB (64.0 KB on disk), 1 folder unreadable", 1)]
    [InlineData("0 files, 0 folders, 0 bytes (0 bytes on disk)", 0)]
    [InlineData("1 file, 0 folders, 0 bytes (0 bytes on disk)", 0)]
    [InlineData("0 files, 1 folder, 0 bytes (0 bytes on disk)", 1)]
    [InlineData("12 files, 3 folders, 2.0 KB (4.0 KB on disk)", 3)]
    [InlineData("4,77,757 files, 4,022 folders, 108.8 GB (108.8 GB on disk), 1 folder unreadable", 4022)]
    [InlineData("1,234 files, 5,678 folders, 3.2 GB (3.2 GB on disk), 12 folders unreadable", 5678)]
    [InlineData("", 0)]
    [InlineData("Reading C:\\", 0)]
    public void TheHookReadsTheFolderCountWhateverTheSeparatorAndWhateverFollowsIt(string counters, int folders) =>
        Assert.Equal(folders, CountersReading.FoldersIn(counters));

    /// <summary>What the counters write is what the hook reads, at every count the singular and the plural meet.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(999)]
    public void TheHookReadsBackTheFolderCountTheCountersWrote(int folders)
    {
        foreach (int unreadable in new[] { 0, 1, 2 })
            Assert.Equal(folders, CountersReading.FoldersIn(FigureText.Counters(1, folders, 0, 0, unreadable)));
    }
}
