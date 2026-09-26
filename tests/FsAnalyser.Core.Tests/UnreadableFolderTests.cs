using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

public class UnreadableFolderTests
{
    private const uint FileListDirectory = 0x1;

    [Fact]
    public void AFolderThatCannotBeListedIsRecordedAndItsSiblingsAreCounted()
    {
        using var tree = new TempTree();
        tree.Folder("a-readable");
        tree.File(@"a-readable\one.txt", 11);
        string denied = tree.Folder("b-denied");
        tree.File(@"b-denied\not-seen.txt", 1000);
        tree.Folder("c-readable");
        tree.File(@"c-readable\two.txt", 22);
        tree.Deny(denied, FileListDirectory);

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        ScanStore store = result.Store;
        int entry = Scans.Find(result, denied);
        Assert.Equal(new UnreadableFolder(entry, 5), Assert.Single(result.Unreadable)); // ERROR_ACCESS_DENIED
        Assert.Equal(EntryFlags.Unreadable, store.Flags(entry));
        Assert.Equal(0, store.ChildCount(entry));
        // A leaf that adds nothing to the folder above it (SL-042): the root holds its two siblings' files only.
        Assert.Equal(default, Rolled.Of(store, entry));
        Assert.Empty(store.ChildrenLargestFirst(entry));
        Assert.Equal(new Rolled(33, result.Totals.BytesOnDisk, 2), Rolled.Of(store, 0));
        Assert.Equal(new ScanTotals(Files: 2, Folders: 3, Links: 0, Bytes: 33, BytesOnDisk: result.Totals.BytesOnDisk), result.Totals);
        Scans.Find(result, tree.PathOf(@"a-readable\one.txt"));
        Scans.Find(result, tree.PathOf(@"c-readable\two.txt"));
    }
}
