using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

public class LinkTests
{
    [Fact]
    public void JunctionsAndSymlinksAreRecordedAndNeverEntered()
    {
        using var tree = new TempTree();
        string outside = tree.Outside("target");
        string outsideFile = TempTree.WriteFile(Path.Combine(outside, "big.bin"), TempTree.MiB);
        string inside = tree.Folder("inside");
        string insideFile = tree.File(@"inside\real.txt", 100);

        string[] links =
        [
            tree.Junction("j-outside", outside),
            tree.Junction("j-inside", inside),
            tree.Junction(@"inside\j-ancestor", tree.Root), // a cycle
            tree.DirectorySymlink("d-outside", outside),
            tree.DirectorySymlink("d-inside", inside),
            tree.DirectorySymlink(@"inside\d-ancestor", tree.Root), // a cycle
            tree.DirectorySymlink("d-dangling", tree.PathOf("missing")),
            tree.FileSymlink("f-outside", outsideFile),
            tree.FileSymlink("f-inside", insideFile),
            tree.FileSymlink(@"inside\f-ancestor", tree.Root), // a file-type link aimed at a folder above it
        ];

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Empty(result.Unreadable);
        ScanStore store = result.Store;
        foreach (string link in links)
        {
            int entry = Scans.Find(result, link);
            Assert.Equal(EntryKind.Link, store.Kind(entry));
            Assert.Equal(0, store.ChildCount(entry));
            Assert.Equal(-1, store.FirstChild(entry));
            Assert.Equal(0, store.ChargedSize(entry));
            Assert.Equal(0, store.ChargedOnDisk(entry));
        }
        Assert.Empty(Scans.UnderLinks(store));
        Assert.Equal(1 + 1 + 1 + links.Length, store.Count); // root, inside, real.txt, the links
        Assert.Equal(new ScanTotals(Files: 1, Folders: 1, Links: links.Length, Bytes: 100, BytesOnDisk: result.Totals.BytesOnDisk), result.Totals);
        Assert.True(result.Totals.BytesOnDisk < TempTree.MiB, "nothing outside the root may be charged");
    }
}
