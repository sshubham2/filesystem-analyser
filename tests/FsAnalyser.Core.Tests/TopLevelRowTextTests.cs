using FsAnalyser.App;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The words of a results row (SL-039) for folders the scan could not read, on rows a real scan built (HF-002). A
/// folder that could not be opened has nothing in it counted; one whose listing failed partway keeps what it listed,
/// and that is counted, so it reads as the folder tree reads it (FolderTreeNode.Note): not read in full. The partway
/// failure is a 995 Windows returns by itself at a folder's third listing buffer, fed in as CancelledFolderTests
/// feeds it; the folder that cannot be opened is denied its listing.
/// </summary>
public class TopLevelRowTextTests
{
    private const uint FileListDirectory = 0x1;
    private const int ThirdBuffer = 4; // a folder's open, then its first two buffers

    [Fact]
    public void AFolderListedPartwayIsNotReadInFullAndOneNeverOpenedIsNotOpened()
    {
        using var tree = new TempTree();
        string denied = tree.Folder("a-denied");
        tree.File(@"a-denied\not-seen.txt", 1000);
        tree.Folder("b-big");
        tree.Folder(@"c-top\big");
        for (int i = 0; i < 2000; i++)
        {
            tree.File($@"b-big\{i:D4}{new string('n', 92)}.txt", 1); // 100-character names: several buffers
            tree.File($@"c-top\big\{i:D4}{new string('n', 92)}.txt", 1);
        }
        string deniedInside = tree.Folder(@"c-top\denied");
        tree.File(@"c-top\denied\not-seen.txt", 1000);
        tree.File(@"c-top\one.txt", 11);
        tree.Deny(denied, FileListDirectory);
        tree.Deny(deniedInside, FileListDirectory);

        ScanResult result = ScanFailingPartway(tree, [tree.PathOf("b-big"), tree.PathOf(@"c-top\big")]);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal([5, 995, 995, 5], result.Unreadable.Select(u => u.Win32Error));
        TopLevelRow notOpened = Named(result, "a-denied"), partway = Named(result, "b-big"), top = Named(result, "c-top");

        // Both are unreadable, and only the one listed partway has anything counted. The row carries the kind the scanner
        // stamped, the one the list reads off the record (SL-106), and words it as the list does: 'only partly read'.
        Assert.Equal((TopLevelState.Unreadable, 0L, UnreadableKind.NothingListed), (notOpened.State, notOpened.Files, notOpened.FailedKind));
        Assert.Equal((TopLevelState.Unreadable, UnreadableKind.ListedPartway), (partway.State, partway.FailedKind));
        Assert.Equal(
            [UnreadableKind.NothingListed, UnreadableKind.ListedPartway, UnreadableKind.ListedPartway, UnreadableKind.NothingListed],
            result.Unreadable.Select(u => u.Kind));
        Assert.InRange(partway.Files, 1, 1999);
        Assert.Equal("a-denied  (could not be opened)", TopLevelRowText.TitleOf(notOpened));
        Assert.Equal("b-big  (only partly read)", TopLevelRowText.TitleOf(partway));

        // Below a row that finished, one of each: the count covers both, and claims of neither more than is true. A
        // failure below a row's own folder leaves the row's own kind alone: the row is Done, with none.
        Assert.Equal((TopLevelState.Done, 2L, (UnreadableKind?)null), (top.State, top.UnreadableBelow, top.FailedKind));
        Assert.InRange(top.Files, 2, 2000);
        Assert.EndsWith("; 2 folders inside could not be read (in full)", TopLevelRowText.FiguresOf(top));
    }

    /// <summary>
    /// Every kind is worded on the row as the list and the tree word it, from the kind alone (SL-106). And an Unreadable
    /// row that somehow carries no kind, which the tracker never builds, is worded 'could not be read' and not left as its
    /// bare name, which is how the list words a Done row: a folder that failed must not read as one measured in full.
    /// </summary>
    [Theory]
    [InlineData(UnreadableKind.NothingListed, "a-row  (could not be opened)")]
    [InlineData(UnreadableKind.OpenedNothingListed, "a-row  (opened, but nothing could be listed)")]
    [InlineData(UnreadableKind.ListedPartway, "a-row  (only partly read)")]
    [InlineData(null, "a-row  (could not be read)")]
    [InlineData((UnreadableKind)99, "a-row  (could not be read)")]
    public void AnUnreadableRowIsWordedFromItsKindAndNeverAsItsBareName(UnreadableKind? kind, string expected)
    {
        var row = new TopLevelRow("a-row", @"C:\a-row", EntryKind.Folder, TopLevelState.Unreadable, 0, 0, 0, 1, 0) { FailedKind = kind };

        Assert.Equal(expected, TopLevelRowText.TitleOf(row));
        Assert.Equal(UnreadableRows.KindText(kind), expected["a-row  (".Length..^1]); // the list's and the tree's words
    }

    // Scans the tree with a 995 at the third listing buffer of each of the named folders. Which folder a call lists is
    // read off the order an untouched scan exits them in; the scan thread's calls are counted from the last folder that
    // exited, so a folder cut short does not move where the next one's buffers fall. UnreadableFolderTests feeds the
    // engine's record of such a scan to the unreadable-folders list (SL-087).
    internal static ScanResult ScanFailingPartway(TempTree tree, string[] partway)
    {
        using var live = new CancellationTokenSource(); // never cancelled: the 995 is Windows', not the guard's
        var order = new List<string>();
        var progress = new ScanProgress();
        Scanner.Scan(tree.Root, progress, live.Token, new ScanOptions { AfterFolder = _ => order.Add(progress.CurrentFolder!) });
        var cut = new HashSet<int>(partway.Select(path => order.IndexOf(path)));
        Assert.DoesNotContain(-1, cut);

        var ends = new List<int>();
        SynchronousIoCancel? guard = null;
        SynchronousIoCancel.Created = g =>
        {
            guard = g;
            g.OverrideQueryError = (call, error) =>
                cut.Contains(ends.Count) && call == ends[^1] + ThirdBuffer ? SynchronousIoCancel.Aborted : error;
        };
        try
        {
            return Scanner.Scan(tree.Root, new ScanProgress(), live.Token, new ScanOptions { AfterFolder = _ => ends.Add(guard!.Calls) });
        }
        finally
        {
            SynchronousIoCancel.Created = null;
        }
    }

    private static TopLevelRow Named(ScanResult result, string name) => result.TopLevel!.Rows.Single(r => r.Name == name);
}
