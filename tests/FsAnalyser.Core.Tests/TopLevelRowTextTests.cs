using FsAnalyser.App;
using FsAnalyser.Core.Scanning;

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

        // Both are unreadable, and only the one listed partway has anything counted.
        Assert.Equal((TopLevelState.Unreadable, 0L, false), (notOpened.State, notOpened.Files, notOpened.ListedPartway));
        Assert.Equal((TopLevelState.Unreadable, true), (partway.State, partway.ListedPartway));
        Assert.InRange(partway.Files, 1, 1999);
        Assert.Equal("a-denied  (could not be opened)", TopLevelRowText.TitleOf(notOpened));
        Assert.Equal("b-big  (could not be read in full)", TopLevelRowText.TitleOf(partway));

        // Below a row that finished, one of each: the count covers both, and claims of neither more than is true.
        Assert.Equal((TopLevelState.Done, 2L, false), (top.State, top.UnreadableBelow, top.ListedPartway));
        Assert.InRange(top.Files, 2, 2000);
        Assert.EndsWith("; 2 folders inside could not be read (in full)", TopLevelRowText.FiguresOf(top));
    }

    // Scans the tree with a 995 at the third listing buffer of each of the named folders. Which folder a call lists is
    // read off the order an untouched scan exits them in; the scan thread's calls are counted from the last folder that
    // exited, so a folder cut short does not move where the next one's buffers fall.
    private static ScanResult ScanFailingPartway(TempTree tree, string[] partway)
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
