using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A folder deleted during the scan is gone, not unreadable (SL-005). The walk lists a folder, queues its subfolders and opens each
/// one later; one deleted in that window fails its open with 2 (not found), or 3 (path not found) when its parent went too. It
/// is counted in <see cref="ScanResult.FoldersVanished"/> and stays in the store as a listed, empty folder, never in
/// <see cref="ScanResult.Unreadable"/>, the live unreadable count or the top-level rows' unreadable figures. The same codes from a
/// path that stopped resolving above the folder (a drive letter removed, the root itself deleted) are not a deleted folder: the
/// scan root is opened too, and when it does not open the folder stays unreadable (review M1, VolumeLossCorpus.json). Each
/// deletion is made from <see cref="ScanOptions.AfterFolder"/>, on the scan's own thread, once the folder's parent has been
/// listed and before the walk pops the folder, so it is deterministic.
/// </summary>
public class VanishedFolderTests
{
    private const int AccessDenied = 5;
    private const uint FileListDirectory = 0x1;

    [Fact]
    public void AFolderDeletedWithItsSubfoldersCountsOnceAndIsAListedEmptyFolder()
    {
        using var tree = new TempTree();
        tree.Folder(@"a\b");
        tree.Folder(@"z\inner");
        tree.File(@"z\inner\f.txt", 5);
        string z = tree.PathOf("z");
        var progress = new ScanProgress();

        ScanResult result = Scanner.Scan(tree.Root, progress, CancellationToken.None, new ScanOptions { AfterFolder = DeleteAfter(1, z) });

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.False(Directory.Exists(z));
        // Counted once: the folder whose open failed, not the folder below it that the scan never saw.
        Assert.Equal(1, result.FoldersVanished);
        Assert.Empty(result.Unreadable);
        Assert.Equal(0, progress.Unreadable);
        int entry = Scans.Find(result, z);
        // A listed folder with nothing in it, not one still to be listed, which a stopped scan would read as unfinished.
        Assert.Equal(0, result.Store.ChildCount(entry));
        Assert.False(result.Store.NotListed(entry));
        Assert.Equal(EntryFlags.None, result.Store.Flags(entry));
        Assert.Equal(3, result.Totals.Folders); // a, a\b and z: the root is not counted
        Assert.Equal(-1, FindPath(result, tree.PathOf(@"z\inner")));
        TopLevelRow row = result.TopLevel!.Rows.Single(r => r.Name == "z");
        Assert.Equal((TopLevelState.Done, (UnreadableKind?)null, 0L), (row.State, row.FailedKind, row.UnreadableBelow));
        Scans.AssertChargedBytesAreTheTotals(result);
    }

    [Fact]
    public void AFolderUnderADeletedParentIsGoneAndItsRowCountsNoUnreadableBelow()
    {
        using var tree = new TempTree();
        tree.Folder(@"a\b");
        tree.Folder(@"y\w");
        tree.File(@"y\one.txt", 5);
        string y = tree.PathOf("y"), w = tree.PathOf(@"y\w");
        var progress = new ScanProgress();
        bool deleted = false;

        // After y itself has been listed: y\w is queued, and its parent is gone, so its open fails with 3.
        ScanResult result = Scanner.Scan(tree.Root, progress, CancellationToken.None, new ScanOptions
        {
            AfterFolder = _ =>
            {
                if (deleted || progress.CurrentFolder != y)
                    return;
                Directory.Delete(y, recursive: true);
                deleted = true;
            },
        });

        Assert.True(deleted, "setup: y was never listed");
        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(1, result.FoldersVanished);
        Assert.Empty(result.Unreadable);
        Assert.Equal(0, progress.Unreadable);
        int entry = Scans.Find(result, w);
        Assert.False(result.Store.NotListed(entry));
        Assert.Equal(EntryFlags.None, result.Store.Flags(entry));
        TopLevelRow row = result.TopLevel!.Rows.Single(r => r.Name == "y");
        Assert.Equal((TopLevelState.Done, (UnreadableKind?)null, 0L), (row.State, row.FailedKind, row.UnreadableBelow));
        // The listed size of y's file is kept: it vanished too, before its size was read.
        Assert.Equal(1, result.VanishedBeforeProbe);
    }

    [Fact]
    public void AStoppedScanCountsTheFoldersThatVanishedBeforeTheStop()
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.Folder(@"z\inner");
        string z = tree.PathOf("z");
        using var cts = new CancellationTokenSource();
        var progress = new ScanProgress();
        Action<int> deleteAtRoot = DeleteAfter(1, z);

        ScanResult result = Scanner.Scan(tree.Root, progress, cts.Token, new ScanOptions
        {
            AfterFolder = n =>
            {
                deleteAtRoot(n);
                if (progress.CurrentFolder == z)
                    cts.Cancel(); // at z's own AfterFolder, after the walk counted it as gone
            },
        });
        Scans.WaitForAbortedCloses();

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Equal(1, result.FoldersVanished);
        Assert.Empty(result.Unreadable);
    }

    [Fact]
    public void FilesAndFoldersThatVanishKeepTheirOwnCounts()
    {
        using var tree = new TempTree();
        tree.Folder(@"a\b");
        tree.Folder(@"y\w");
        tree.File(@"y\g.txt", 7);
        tree.File(@"a\kept.txt", 9);
        string y = tree.PathOf("y");
        var progress = new ScanProgress();

        ScanResult result = Scanner.Scan(tree.Root, progress, CancellationToken.None, new ScanOptions
        {
            AfterFolder = _ =>
            {
                if (progress.CurrentFolder == y && Directory.Exists(y))
                    Directory.Delete(y, recursive: true);
            },
        });

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(1, result.FoldersVanished); // y\w
        Assert.Equal(1, result.VanishedBeforeProbe); // y\g.txt
        Assert.Equal(0, result.ProbeFailures);
        Assert.Empty(result.Unreadable);
    }

    /// <summary>
    /// The record is not simply empty: a folder refused by a deny ACE (5, NothingListed) stays in it beside a deleted one, and a
    /// folder whose listing failed partway (995, ListedPartway) stays in it in a scan of its own with nothing deleted.
    /// </summary>
    [Fact]
    public void RefusedAndPartlyReadFoldersAreStillRecordedBesideADeletedOne()
    {
        using (var tree = new TempTree())
        {
            string denied = tree.Folder("a-denied");
            tree.File(@"a-denied\not-seen.txt", 10);
            tree.Folder(@"z\inner");
            tree.Deny(denied, FileListDirectory);
            string z = tree.PathOf("z");
            var progress = new ScanProgress();

            ScanResult result = Scanner.Scan(tree.Root, progress, CancellationToken.None, new ScanOptions { AfterFolder = DeleteAfter(1, z) });

            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(1, result.FoldersVanished);
            UnreadableFolder record = Assert.Single(result.Unreadable);
            Assert.Equal((denied, AccessDenied, UnreadableKind.NothingListed), (result.Store.GetPath(record.Entry), record.Win32Error, record.Kind));
            Assert.Equal(1, progress.Unreadable);
            Assert.Equal((TopLevelState.Unreadable, UnreadableKind.NothingListed),
                (result.TopLevel!.Rows.Single(r => r.Name == "a-denied").State, result.TopLevel.Rows.Single(r => r.Name == "a-denied").FailedKind));
            Assert.Equal((TopLevelState.Done, (UnreadableKind?)null), (result.TopLevel.Rows.Single(r => r.Name == "z").State, result.TopLevel.Rows.Single(r => r.Name == "z").FailedKind));
        }

        using (var tree = new TempTree())
        {
            string big = tree.Folder("b-big");
            for (int i = 0; i < 2000; i++)
                tree.File($@"b-big\{i:D4}{new string('n', 92)}.txt", 1); // 100-character names: several buffers
            tree.File("c.txt", 1);

            ScanResult partway = TopLevelRowTextTests.ScanFailingPartway(tree, [big]);

            Assert.Equal(ScanStatus.Completed, partway.Status);
            UnreadableFolder record = Assert.Single(partway.Unreadable);
            Assert.Equal((big, UnreadableKind.ListedPartway), (partway.Store.GetPath(record.Entry), record.Kind));
            Assert.Equal(0, partway.FoldersVanished);
        }
    }

    [Fact]
    public void AMissingRootIsRootUnreadableAndCountsNoVanishedFolder()
    {
        using var tree = new TempTree();

        ScanResult result = Scanner.Scan(tree.PathOf("nope"), new ScanProgress(), CancellationToken.None);

        Assert.Equal(ScanStatus.RootUnreadable, result.Status);
        Assert.Equal(2, result.RootError);
        Assert.Equal(0, result.FoldersVanished);
        Assert.Empty(result.Unreadable);
    }

    /// <summary>
    /// Review M1, measured here with a letter this test makes and removes itself: the path above the queued folders stops
    /// resolving, every open fails the same way a deleted folder's does (2 or 3), and the scan root fails to open too. The folders
    /// stay unreadable, as before, and none is called deleted.
    /// </summary>
    [Fact]
    public void FoldersUnderADriveLetterRemovedMidScanStayUnreadable()
    {
        using var tree = new TempTree();
        tree.Folder(@"sub\a");
        tree.Folder(@"sub\b");
        tree.Folder(@"sub\c");
        using var drive = new SubstDrive(tree.PathOf("sub"));
        var progress = new ScanProgress();

        ScanResult result = Scanner.Scan(drive.Root, progress, CancellationToken.None, new ScanOptions
        {
            AfterFolder = n =>
            {
                if (n == 1)
                    drive.Remove();
            },
        });

        // No assertion on VanishedBeforeProbe: the probe pass counts files it cannot reach through the removed letter as gone.
        string said = string.Join("; ", result.Unreadable.Select(u => $"{result.Store.GetPath(u.Entry)} ({u.Win32Error}, {u.Kind})"));
        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.True(result.FoldersVanished == 0, $"folders under a removed drive letter were counted deleted ({result.FoldersVanished}); unreadable: {said}");
        Assert.True(result.Unreadable.Count == 3, $"the three queued folders stay unreadable; unreadable: {said}");
        Assert.All(result.Unreadable, u => Assert.Equal(UnreadableKind.NothingListed, u.Kind));
        Assert.Equal(3, progress.Unreadable);
    }

    /// <summary>
    /// The root deleted mid-scan, with its folders queued: the same codes from the folders and the root's open fails, so they stay
    /// unreadable (the safe side, as before).
    /// </summary>
    [Fact]
    public void AScanRootDeletedMidScanLeavesTheQueuedFoldersUnreadable()
    {
        using var tree = new TempTree();
        tree.Folder(@"scan\a");
        tree.Folder(@"scan\b");
        string scanRoot = tree.PathOf("scan");

        ScanResult result = Scanner.Scan(scanRoot, new ScanProgress(), CancellationToken.None, new ScanOptions { AfterFolder = DeleteAfter(1, scanRoot) });

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(0, result.FoldersVanished);
        Assert.Equal(2, result.Unreadable.Count);
    }

    /// <summary>
    /// A Stop that lands in the root check decides nothing (review M1, critic on its fix): the folder is neither stamped nor
    /// counted, nothing of it is committed, and the scan ends Cancelled, before or during the call alike.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStopInTheRootCheckStampsNothingAndCountsNothing(bool during)
    {
        using var tree = new TempTree();
        tree.Folder(@"z\inner");
        string z = tree.PathOf("z");
        using var cts = new CancellationTokenSource();
        SynchronousIoCancel? guard = null;

        ScanResult result = Scans.WithGuard(FirstPageFailure.OnThisThread(g => guard = g), () => Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, new ScanOptions
        {
            AfterFolder = n =>
            {
                if (n != 1)
                    return;
                Directory.Delete(z, recursive: true);
                // z's open is the guard's next call and the check of the scan root the one after it.
                Scans.CancelAt(guard!, cts, guard!.Calls + 2, during);
            },
        }));
        Scans.WaitForAbortedCloses();

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Equal(0, result.FoldersVanished);
        Assert.Empty(result.Unreadable);
        int entry = Scans.Find(result, z);
        Assert.True(result.Store.NotListed(entry), "a folder whose check a Stop aborted is not committed");
        Assert.Equal(EntryFlags.None, result.Store.Flags(entry));
    }

    /// <summary>Deletes <paramref name="folder"/> once folder <paramref name="n"/> has been listed (the root is 1).</summary>
    private static Action<int> DeleteAfter(int n, string folder) => listed =>
    {
        if (listed == n && Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    };

    private static int FindPath(ScanResult result, string path)
    {
        for (int e = 0; e < result.Store.Count; e++)
        {
            if (string.Equals(result.Store.GetPath(e), path, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return -1;
    }
}
