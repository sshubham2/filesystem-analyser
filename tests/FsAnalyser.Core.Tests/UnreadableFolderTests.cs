using FsAnalyser.App;
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
        // ERROR_ACCESS_DENIED at its open: nothing of it was listed (SL-087).
        Assert.Equal(new UnreadableFolder(entry, 5, UnreadableKind.NothingListed), Assert.Single(result.Unreadable));
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

    /// <summary>
    /// The engine records which of the two happened (SL-087, U-103): a folder whose listing failed at its third buffer,
    /// fed in as TopLevelRowTextTests feeds it, is ListedPartway with what it listed in the store when the record is
    /// written; one denied its listing is NothingListed. The unreadable-folders list reads each kind off the record.
    /// </summary>
    [Fact]
    public void AFolderListedPartwayIsRecordedAsListedPartwayAndTheListSaysSo()
    {
        using var tree = new TempTree();
        string denied = tree.Folder("a-denied");
        tree.File(@"a-denied\not-seen.txt", 1000);
        string big = tree.Folder("b-big");
        for (int i = 0; i < 2000; i++)
            tree.File($@"b-big\{i:D4}{new string('n', 92)}.txt", 1); // 100-character names: several buffers
        tree.File("c.txt", 1);
        tree.Deny(denied, FileListDirectory);

        ScanResult result = TopLevelRowTextTests.ScanFailingPartway(tree, [big]);

        Assert.Equal(ScanStatus.Completed, result.Status);
        int deniedEntry = Scans.Find(result, denied), bigEntry = Scans.Find(result, big);
        Assert.Equal(
            [new UnreadableFolder(deniedEntry, 5, UnreadableKind.NothingListed), new UnreadableFolder(bigEntry, SynchronousIoCancel.Aborted, UnreadableKind.ListedPartway)],
            result.Unreadable);
        Assert.InRange(result.Store.ChildCount(bigEntry), 1, 1999);
        Assert.Equal(0, result.Store.ChildCount(deniedEntry));

        IReadOnlyList<UnreadableRow> rows = UnreadableRows.Build(result.Unreadable, result.Store.GetPath);

        Assert.Equal([$"{denied}  —  could not be opened: access denied", $"{big}  —  only partly read: {UnreadableRows.Reason(995)}"],
            rows.Select(r => r.Text));
        Assert.EndsWith("(Windows error 995)", rows[1].Reason);
    }
}
