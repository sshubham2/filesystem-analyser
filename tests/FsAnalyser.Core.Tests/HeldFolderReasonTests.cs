using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;
using Microsoft.Win32.SafeHandles;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A folder that refuses the scan's last-access open is opened again plainly, and the record holds that plain open's
/// error, never the refused one (SynchronousIoCancel.Open, SL-089 criterion 2). The default-suite twin of the window case
/// T-009, on the same fixture in the same order: another program holds h open sharing no read first, and only then does
/// h, beside c, refuse FILE_WRITE_ATTRIBUTES (SL-085 measured the other order giving no row). It asks no last-access time
/// to be back-dated, so it does not lean on what the volume records.
/// </summary>
public class HeldFolderReasonTests
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const uint KeepOpen = TestNative.FILE_LIST_DIRECTORY | TestNative.FILE_WRITE_ATTRIBUTES;
    private const uint PlainOpen = TestNative.FILE_LIST_DIRECTORY;

    /// <summary>
    /// h is recorded once, with the sharing violation its plain open met: the row the window words 'in use by another
    /// program', not 'access denied'. c, refused the same way and held by nobody, is listed in full and is no row; p,
    /// refused nothing, is no row. The fixture is checked before the scan, open by open, so the test cannot pass on a
    /// volume or a path where the last-access open is never refused; and the scan's own counters show it asked to keep
    /// the last-access time of both and was refused by both (review m4), so the plain open it fell back to is the one
    /// recorded.
    /// </summary>
    [Fact]
    public void AHeldFolderThatRefusesTheLastAccessOpenIsRecordedWithThePlainOpensSharingViolation()
    {
        using var tree = new TempTree();
        string h = tree.Folder("h");
        tree.File(@"h\one.txt", 10);
        string c = tree.Folder("c");
        tree.File(@"c\two.txt", 20);
        string p = tree.Folder("p");
        tree.File(@"p\three.txt", 30);

        using SafeFileHandle holder = TestNative.HoldFolderSharingNoRead(h); // the holder first, then the deny
        tree.Deny(h, TestNative.FILE_WRITE_ATTRIBUTES);
        tree.Deny(c, TestNative.FILE_WRITE_ATTRIBUTES);
        Assert.Equal((ErrorAccessDenied, ErrorSharingViolation), (TestNative.FolderOpenError(h, KeepOpen), TestNative.FolderOpenError(h, PlainOpen)));
        Assert.Equal((ErrorAccessDenied, 0), (TestNative.FolderOpenError(c, KeepOpen), TestNative.FolderOpenError(c, PlainOpen)));
        Assert.Equal((0, 0), (TestNative.FolderOpenError(p, KeepOpen), TestNative.FolderOpenError(p, PlainOpen)));

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal([(h, ErrorSharingViolation)], Scans.UnreadableByPath(result));
        Assert.Equal(UnreadableKind.NothingListed, Assert.Single(result.Unreadable).Kind);
        Assert.True(result.LastAccessAsked - result.LastAccessKept >= 2,
            $"the scan asked to keep {result.LastAccessAsked} folders' last-access time and kept {result.LastAccessKept}: h and c must both have refused it");
        ScanStore store = result.Store;
        foreach ((string folder, long bytes) in new[] { (c, 20L), (p, 30L) })
        {
            int entry = Scans.Find(result, folder);
            Assert.Equal(EntryFlags.None, store.Flags(entry) & EntryFlags.Unreadable);
            Assert.Equal(1, store.ChildCount(entry));
            Assert.Equal(bytes, Rolled.Of(store, entry).Size);
        }
    }
}
