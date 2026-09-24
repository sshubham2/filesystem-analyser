using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core.Tests;

public class CancellationTests
{
    [Fact]
    public void CancellingAfterTheThirdFolderStopsWithWhatWasRead()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 10; i++)
        {
            tree.Folder($"f{i}");
            tree.File($@"f{i}\file.bin", 10);
        }
        using var cts = new CancellationTokenSource();
        var listed = new List<int>();
        var options = new ScanOptions
        {
            AfterFolder = n =>
            {
                listed.Add(n);
                if (n == 3)
                    cts.Cancel();
            },
        };
        var progress = new ScanProgress();

        ScanResult result = Scanner.Scan(tree.Root, progress, cts.Token, options);

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Equal([1, 2, 3], listed); // the root, f0, f1
        Assert.Equal(new ScanTotals(Files: 2, Folders: 10, Links: 0, Bytes: 20, BytesOnDisk: result.Totals.BytesOnDisk), result.Totals);
        Assert.Equal(1 + 10 + 2, result.Store.Count);
        Assert.Equal(1, result.Store.ChildCount(Scans.Find(result, tree.PathOf("f1"))));
        Assert.Equal(0, result.Store.ChildCount(Scans.Find(result, tree.PathOf("f2"))));
        Assert.Equal(2, progress.Files);
        Assert.Equal(10, progress.Folders);
        Assert.Equal(tree.PathOf("f1"), progress.CurrentFolder);
    }

    [Fact]
    public void AnAlreadyCancelledTokenReadsNothing()
    {
        using var tree = new TempTree();
        tree.File("file.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var listed = new List<int>();

        ScanResult result = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, new ScanOptions { AfterFolder = listed.Add });

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Empty(listed);
        Assert.Equal(0, result.Store.Count);
        Assert.Equal(default, result.Totals);
    }
}
