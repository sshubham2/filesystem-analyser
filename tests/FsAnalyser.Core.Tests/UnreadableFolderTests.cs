using FsAnalyser.App;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

public class UnreadableFolderTests
{
    private const uint FileListDirectory = 0x1;
    private const uint FileWriteAttributes = 0x100;

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

    /// <summary>
    /// A folder that opened and failed its first page is told apart from one whose open was refused (SL-106), on every
    /// surface that words it, in one scan: the refused open (error 5) keeps NothingListed and 'could not be opened'; the
    /// folder that opened and failed its first page with 59 is OpenedNothingListed, worded 'opened, but nothing could be
    /// listed' by the list and the top-level row alike.
    /// </summary>
    [Fact]
    public void AFolderThatOpenedAndFailedItsFirstPageIsOpenedNothingListedBesideARefusedOne()
    {
        using var tree = new TempTree();
        string refused = tree.Folder("a-refused");
        tree.File(@"a-refused\not-seen.txt", 5);
        string opened = tree.Folder("b-opened");
        tree.File(@"b-opened\one.txt", 5);
        tree.Deny(refused, FileListDirectory);
        (int call, _) = FirstPageFailure.CallOf(tree, opened);

        (ScanResult result, int injected) = FirstPageFailure.Scan(tree, call, 59);

        Assert.Equal((ScanStatus.Completed, 1), (result.Status, injected));
        int refusedEntry = Scans.Find(result, refused), openedEntry = Scans.Find(result, opened);
        Assert.Equal(
            [new UnreadableFolder(refusedEntry, 5, UnreadableKind.NothingListed), new UnreadableFolder(openedEntry, 59, UnreadableKind.OpenedNothingListed)],
            result.Unreadable);
        Assert.Equal((0, 0), (result.Store.ChildCount(refusedEntry), result.Store.ChildCount(openedEntry)));
        Assert.Equal(
            [$"{refused}  —  could not be opened: access denied", $"{opened}  —  opened, but nothing could be listed: network or share error"],
            UnreadableRows.Build(result.Unreadable, result.Store.GetPath).Select(r => r.Text));
        Assert.Equal(
            ["a-refused  (could not be opened)", "b-opened  (opened, but nothing could be listed)"],
            result.TopLevel!.Rows.Where(r => r.State == TopLevelState.Unreadable).Select(TopLevelRowText.TitleOf));
    }

    /// <summary>
    /// The open that counts is the one that returned a handle. A folder whose keep-last-access open is refused (deny
    /// WriteAttributes) is opened by the plain open it falls back to, so a first page that then fails is the folder
    /// opened, not refused. The dry run proves the fallback ran before 59 is injected: the scan asked to keep this
    /// folder's last-access time, and was refused, while the folder listed in full. Without that, a stamp taken from the
    /// first attempt, or a scan that never asked, would pass this too.
    /// </summary>
    [Fact]
    public void AFolderOpenedByTheFallbackAfterItsKeepOpenWasRefusedIsOpenedNothingListedWhenItsFirstPageFails()
    {
        using var tree = new TempTree();
        tree.Folder("a-plain");
        tree.File(@"a-plain\one.txt", 5);
        string fallback = tree.Folder("b-no-write-attributes");
        tree.File(@"b-no-write-attributes\one.txt", 5);
        tree.Folder("c-plain");
        tree.File(@"c-plain\one.txt", 5);
        tree.Deny(fallback, FileWriteAttributes); // no inheritance: only this folder refuses to keep its last-access time
        (int call, ScanResult dry) = FirstPageFailure.CallOf(tree, fallback);

        Assert.Empty(dry.Unreadable); // the plain open listed it in full
        Assert.True(dry.LastAccessAsked >= 2, $"the scan asked to keep {dry.LastAccessAsked} folders' last-access time: it must ask for the root and this folder");
        Assert.Equal(1, dry.LastAccessAsked - dry.LastAccessKept); // this folder alone refused

        (ScanResult result, int injected) = FirstPageFailure.Scan(tree, call, 59);

        Assert.Equal((ScanStatus.Completed, 1), (result.Status, injected));
        Assert.Equal(1, result.LastAccessAsked - result.LastAccessKept); // refused again: the open that returned the handle was the plain one
        int entry = Scans.Find(result, fallback);
        Assert.Equal(new UnreadableFolder(entry, 59, UnreadableKind.OpenedNothingListed), Assert.Single(result.Unreadable));
        Assert.Equal(0, result.Store.ChildCount(entry));
    }
}
