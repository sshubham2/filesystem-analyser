// Bug (SL-005): a folder deleted during the scan, after its parent was listed and before the walk opens it, is recorded as
// an UNREADABLE folder. The walk lists a folder, queues its subfolders on a stack (Scanner.Run.FolderDone, _pending) and opens
// each one later. A subfolder deleted in that window fails its open with ERROR_FILE_NOT_FOUND (2; 3 if its parent went too)
// with nothing opened. FailedKind maps any error with nothing opened to UnreadableKind.NothingListed, and FolderDone then
// flags the folder EntryFlags.Unreadable and adds it to ScanResult.Unreadable. So the live "N folders unreadable", the
// "Unreadable folders (N)" list ("could not be opened"), the "may be missing" captions and the "Rescan as administrator"
// offer all count a folder that is simply gone. Nothing was missing and elevation cannot help it. The probe pass already tells
// a vanished FILE apart (SizeProbe.IsGone, VanishedBeforeProbe); the walk has no such case for a folder.
//
// Expected: a folder that no longer exists when the walk comes to open it is not an unreadable folder: it is not in
// ScanResult.Unreadable and its entry is not flagged EntryFlags.Unreadable. The scan still completes, and the folder that was
// not deleted is listed as usual.
//
// Actual: the scan completes, but ScanResult.Unreadable holds the deleted folder with Win32Error 2 and
// Kind NothingListed, and its entry carries EntryFlags.Unreadable.
//
// What the test does and what it costs: a scratch tree under %TEMP%\fsa-tests of three folders (a, a\b, z\inner) and one file
// of 5 bytes, scanned with Scanner.Scan. The scan's own AfterFolder seam deletes z the moment the root has been listed
// (folder 1), when z is still on the walk's stack behind a, so z's open fails for the reason the bug is about. Nothing leaves
// the temp tree and it runs in well under a second. The worst case is a handful of tiny folders and one file, removed again
// by the tree's Dispose.

using FsAnalyser.Core;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;
using FsAnalyser.Core.Tests;

namespace SL005.VanishedFolderNotUnreadable.Tests;

public class VanishedFolderNotUnreadableTests
{
    [Fact]
    public void AFolderDeletedBetweenItsParentsListingAndItsOwnOpenIsNotAnUnreadableFolder()
    {
        using var tree = new TempTree();
        tree.Folder(@"a\b");
        string gone = tree.Folder(@"z\inner");
        tree.File(@"z\inner\f.txt", 5);
        string zPath = tree.PathOf("z");

        bool deleted = false;
        var options = new ScanOptions
        {
            // Called on the scan's own thread once folder n has been listed; the root is 1. The root's listing pushed a and z
            // (a is popped first), so z is queued and unopened here: delete it, as another program cleaning a temp or cache
            // folder would while the walk is busy elsewhere.
            AfterFolder = n =>
            {
                if (n != 1 || deleted)
                    return;
                Directory.Delete(zPath, recursive: true);
                deleted = true;
            },
        };

        ScanResult result = Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None, options);

        // Setup, so a failure below is the bug and not the fixture: z was deleted while queued, the scan finished, and the
        // walk did reach the folder that stayed (a\b).
        Assert.True(deleted, "setup: the AfterFolder seam never fired for the root");
        Assert.False(Directory.Exists(gone), "setup: the folder was not deleted");
        Assert.Equal(ScanStatus.Completed, result.Status);
        int zEntry = FindEntry(result, zPath);
        Assert.True(zEntry > 0, $"setup: the store has no entry for {zPath}: the root's listing did not record z");
        Assert.True(FindEntry(result, tree.PathOf(@"a\b")) > 0, "setup: the folder that stayed, a\\b, was not listed");

        // The bug: z is gone, not unreadable.
        string unreadable = string.Join("; ", result.Unreadable.Select(u =>
            $"{result.Store.GetPath(u.Entry)} (error {u.Win32Error}, {u.Kind})"));
        Assert.True(result.Unreadable.Count == 0,
            $"a folder deleted after its parent was listed and before the walk opened it was recorded as unreadable: {unreadable}. "
            + "It failed its open with ERROR_FILE_NOT_FOUND because it no longer exists; nothing in it can be missing from the "
            + "scan and elevation cannot open it, so it must not be in ScanResult.Unreadable (the 'N folders unreadable' count, "
            + "the 'Unreadable folders' list, the 'may be missing' captions and the 'Rescan as administrator' offer all read it).");
        Assert.False((result.Store.Flags(zEntry) & EntryFlags.Unreadable) != 0,
            "the vanished folder's entry is flagged EntryFlags.Unreadable");
    }

    private static int FindEntry(ScanResult result, string path)
    {
        for (int e = 1; e < result.Store.Count; e++)
        {
            if (string.Equals(result.Store.GetPath(e), path, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return -1;
    }
}
