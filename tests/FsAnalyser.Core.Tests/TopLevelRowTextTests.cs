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

    /// <summary>
    /// SL-113: the stopped list's second line for a row carrying the store's lower bound. Every figure on it is worded as a bound, from
    /// the bound alone (the row's own Bytes and OnDisk, the listing's, are not on it), the counts that only grow included; and the same
    /// row without a bound reads as it always did, byte for byte, so a final row and an unread row are not changed.
    /// </summary>
    [Fact]
    public void AMarkedRowWordsEveryFigureAsABoundAndAnUnmarkedRowReadsAsItAlwaysDid()
    {
        var bound = new AtLeastFigures(2048, 4096, 7);
        var folder = new TopLevelRow("big", @"C:\big", EntryKind.Folder, TopLevelState.Done, 900_000, 1_048_576, 12, 5, 0) { AtLeast = bound };
        Assert.Equal("at least 2.0 KB (at least 4.0 KB on disk) in at least 7 files and at least 4 folders", TopLevelRowText.FiguresOf(folder));

        // The numbers on the row are the listing's and never reach the line: they would be a false bound.
        var zero = new TopLevelRow("big", @"C:\big", EntryKind.Folder, TopLevelState.StoppedPartway, 900_000, 1_048_576, 12, 5, 2) { AtLeast = new AtLeastFigures(0, 0, 1) };
        Assert.Equal("at least 0 bytes (at least 0 bytes on disk) in at least 1 file and at least 4 folders; at least 2 folders inside could not be read (in full)",
            TopLevelRowText.FiguresOf(zero));
        Assert.DoesNotContain("900,000", TopLevelRowText.FiguresOf(zero));

        var one = new TopLevelRow("one", @"C:\one", EntryKind.Folder, TopLevelState.Done, 1, 1, 1, 2, 1) { AtLeast = new AtLeastFigures(1, 1, 1) };
        Assert.Equal("at least 1 bytes (at least 1 bytes on disk) in at least 1 file and at least 1 folder; at least 1 folder inside could not be read (in full)",
            TopLevelRowText.FiguresOf(one));

        var file = new TopLevelRow("f.bin", @"C:\f.bin", EntryKind.File, TopLevelState.Done, 4096, 4096, 1, 0, 0) { AtLeast = new AtLeastFigures(0, 0, 1) };
        Assert.Equal("at least 0 bytes (at least 0 bytes on disk)", TopLevelRowText.FiguresOf(file));

        // The same rows with no bound: today's text.
        Assert.Equal("878.9 KB (1.0 MB on disk) in 12 files and 4 folders", TopLevelRowText.FiguresOf(new TopLevelRow("big", @"C:\big", EntryKind.Folder, TopLevelState.Done, 900_000, 1_048_576, 12, 5, 0)));
        Assert.Equal("4.0 KB (4.0 KB on disk)", TopLevelRowText.FiguresOf(new TopLevelRow("f.bin", @"C:\f.bin", EntryKind.File, TopLevelState.Done, 4096, 4096, 1, 0, 0)));
        Assert.Equal("0 bytes (0 bytes on disk) in 0 files and 0 folders", TopLevelRowText.FiguresOf(new TopLevelRow("p", @"C:\p", EntryKind.Folder, TopLevelState.NotRead, 0, 0, 0, 1, 0)));
        Assert.Equal("p  (not read yet)", TopLevelRowText.TitleOf(new TopLevelRow("p", @"C:\p", EntryKind.Folder, TopLevelState.NotRead, 0, 0, 0, 1, 0)));

        // The title is the state's and is not changed by the bound.
        Assert.Equal("big  (stopped partway)", TopLevelRowText.TitleOf(zero));
        Assert.Equal("big", TopLevelRowText.TitleOf(folder));
    }

    /// <summary>The word is one constant and the composers take the bound whole, so no other value can be worded as one (SL-113).</summary>
    [Fact]
    public void TheWordIsOneConstantAndTheComposersTakeOnlyTheStoresBound()
    {
        Assert.Equal("at least", TopLevelRowText.AtLeastWord);
        var bound = new AtLeastFigures(1024, 2048, 3);
        Assert.Equal($"{TopLevelRowText.AtLeastWord} 1.0 KB", TopLevelRowText.AtLeastSizeText(bound));
        Assert.Equal($"{TopLevelRowText.AtLeastWord} 2.0 KB", TopLevelRowText.AtLeastOnDiskText(bound));
        Assert.Equal("at least 0 bytes", TopLevelRowText.AtLeastSizeText(default)); // default reads (0, 0, 0): still a true lower bound
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
