using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

public class RootTests
{
    [Fact]
    public void ARegularFileIsNotARoot()
    {
        using var tree = new TempTree();
        string file = tree.File("file.txt", 10);

        ScanResult result = Scans.Run(file);

        Assert.Equal(ScanStatus.RootUnreadable, result.Status);
        Assert.Equal(267, result.RootError); // ERROR_DIRECTORY, never 87
        Assert.Equal(0, result.Store.Count);
        Assert.False(result.FileIdsAvailable);
    }

    [Fact]
    public void AMissingRootIsUnreadableNotAnEmptySuccess()
    {
        using var tree = new TempTree();

        ScanResult result = Scans.Run(tree.PathOf("does-not-exist"));

        Assert.Equal(ScanStatus.RootUnreadable, result.Status);
        Assert.Equal(2, result.RootError); // ERROR_FILE_NOT_FOUND
        Assert.Equal(default, result.Totals);
    }

    [Fact]
    public void ABareDriveMeansTheDriveNotItsCurrentDirectory()
    {
        string drive = Path.GetPathRoot(Environment.SystemDirectory)![..2]; // "C:"
        Assert.StartsWith(drive, Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase);
        using var cts = new CancellationTokenSource();
        var options = new ScanOptions { AfterFolder = _ => cts.Cancel() }; // list the drive root only

        ScanResult result = Scanner.Scan(drive, new ScanProgress(), cts.Token, options);

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Scans.AssertChargedBytesAreTheTotals(result);
        ScanStore store = result.Store;
        Assert.Equal(drive + @"\", store.Name(0).ToString());
        var children = Enumerable.Range(store.FirstChild(0), store.ChildCount(0)).Select(i => store.Name(i).ToString()).ToList();
        Assert.Contains("Windows", children, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("FsAnalyser.Core.Tests.dll", children, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"relative\folder")]
    [InlineData("C:folder")]
    public void ARelativeOrEmptyRootThrows(string root)
    {
        var e = Assert.Throws<ArgumentException>(() => Scanner.Scan(root, new ScanProgress(), CancellationToken.None));
        Assert.Equal("root", e.ParamName); // the window shows 'Could not read' only for this ParamName
    }

    [Fact]
    public void AJunctionPickedAsTheRootIsOpenedThrough()
    {
        using var tree = new TempTree();
        string target = tree.Outside("picked-target");
        TempTree.WriteFile(Path.Combine(target, "one.bin"), 100);
        TempTree.WriteFile(Path.Combine(target, "two.bin"), 200);
        string junction = tree.Junction("picked", target);

        ScanResult result = Scans.Run(junction);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(EntryKind.Folder, result.Store.Kind(0));
        Assert.Equal(junction, result.Store.GetPath(0));
        Assert.Equal(new ScanTotals(Files: 2, Folders: 0, Links: 0, Bytes: 300, BytesOnDisk: result.Totals.BytesOnDisk), result.Totals);
    }
}
