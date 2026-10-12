// Bug (SL-106, deferred from the SL-087 review as M2 and m1): the scan does not keep which phase of a folder's read failed.
// DirectoryReader.Read returns the open's error and the first listing page's error through one int, and Scanner.FolderDone
// stamps UnreadableKind from ChildCount alone. So a folder that OPENED and then failed its first listing page, before any
// entry came back, is recorded exactly like a folder whose open was refused: NothingListed, ChildCount 0, and the
// unreadable-folders list, the folder tree and the results list's top-level row all word it "could not be opened", which
// is false for it.
//
// Expected: the record says the folder was opened. Its Kind is not the one a refused open gets, and the unreadable-folders
// list and the top-level row do not say "could not be opened" of it, while a folder whose open really was refused is still
// worded "could not be opened" on both. (The folder tree's note, FolderTreeNode.Note, reads the same rule again from
// ChildCount and is part of the same change; it needs WPF, so it is not asserted here.)
//
// Actual: both folders are recorded with the same Kind (NothingListed) and both are worded "could not be opened".
//
// What the test does and what it costs: a scan of a scratch tree under %TEMP%\fsa-tests of three folders and three files of
// a few bytes. One folder is denied its listing (FILE_LIST_DIRECTORY), so its open is refused with 5. A first scan, unchanged,
// records which guarded call is the first listing page of the folder 'b-opened'; a second scan has the guard's own test seam
// (SynchronousIoCancel.OverrideQueryError, set through the Created hook, as Core.Tests' CancelledFolderTests set it) report
// error 59 in place of that one page, as an SMB server that fails QUERY_DIRECTORY after CREATE does (SL-089's N-005 control
// measured s as code 59 'network or share error' worded 'could not be opened'). Nothing leaves the temp tree, and it runs in
// well under a second.

using FsAnalyser.App;
using FsAnalyser.Core;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Tests;

namespace SL106.OpenedThenFailedKind.Tests;

public class OpenedThenFailedKindTests
{
    private const uint FileListDirectory = 0x1;
    private const int NetworkOrShareError = 59; // ERROR_UNEXP_NET_ERR
    private const string NotOpened = "could not be opened";

    [Fact]
    public void AFolderThatOpenedAndFailedItsFirstPageIsNotWordedAsOneThatCouldNotBeOpened()
    {
        using var tree = new TempTree();
        string refused = tree.Folder("a-refused");
        tree.File(@"a-refused\not-seen.txt", 5);
        string opened = tree.Folder("b-opened");
        tree.File(@"b-opened\one.txt", 5);
        tree.Folder("c-plain");
        tree.File(@"c-plain\one.txt", 5);
        tree.Deny(refused, FileListDirectory);

        // First scan, nothing changed: which guarded call is the first listing page of b-opened? Each folder's calls are
        // the ones after the previous folder exited and up to its own exit, so the first query in that stretch is it.
        var order = new List<string>();
        var exits = new List<int>();
        var queries = new List<(int Call, int Error)>();
        var progress = new ScanProgress();
        SynchronousIoCancel? scanGuard = null;
        SynchronousIoCancel.Created = g =>
        {
            if (scanGuard is not null)
                return;
            scanGuard = g;
            g.OverrideQueryError = (call, error) =>
            {
                queries.Add((call, error));
                return error;
            };
        };
        try
        {
            ScanResult dry = Scanner.Scan(tree.Root, progress, CancellationToken.None, new ScanOptions
            {
                AfterFolder = _ =>
                {
                    order.Add(progress.CurrentFolder!);
                    exits.Add(scanGuard!.Calls);
                },
            });
            Assert.Equal(ScanStatus.Completed, dry.Status);
        }
        finally
        {
            SynchronousIoCancel.Created = null;
        }

        int at = order.IndexOf(opened);
        Assert.True(at > 0, $"b-opened was not among the folders the scan exited: {string.Join(" | ", order)}");
        (int Call, int Error) firstPage = queries.First(q => q.Call > exits[at - 1] && q.Call <= exits[at]);
        Assert.Equal(0, firstPage.Error); // the real first page of b-opened came back with its entry

        // Second scan: the same calls, with that one page reported as failed with 59. The folder's open succeeded.
        int injected = 0;
        scanGuard = null;
        SynchronousIoCancel.Created = g =>
        {
            if (scanGuard is not null)
                return;
            scanGuard = g;
            g.OverrideQueryError = (call, error) =>
            {
                if (call != firstPage.Call || error != 0)
                    return error;
                injected++;
                return NetworkOrShareError;
            };
        };
        ScanResult result;
        try
        {
            result = Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None);
        }
        finally
        {
            SynchronousIoCancel.Created = null;
        }

        // What the scan saw: the open was refused for one folder, and for the other it opened and then failed on its first
        // page with nothing listed. Neither is partway, so both have no children in the store.
        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(1, injected);
        Assert.Equal(2, result.Unreadable.Count);
        UnreadableFolder refusedRecord = Assert.Single(result.Unreadable, u => result.Store.GetPath(u.Entry) == refused);
        UnreadableFolder openedRecord = Assert.Single(result.Unreadable, u => result.Store.GetPath(u.Entry) == opened);
        Assert.Equal(5, refusedRecord.Win32Error);
        Assert.Equal(NetworkOrShareError, openedRecord.Win32Error);
        Assert.Equal(0, result.Store.ChildCount(refusedRecord.Entry));
        Assert.Equal(0, result.Store.ChildCount(openedRecord.Entry));

        // "Could not be opened" is still true of the folder that was refused, so the fix cannot be to reword both.
        IReadOnlyList<UnreadableRow> rows = UnreadableRows.Build(result.Unreadable, result.Store.GetPath);
        string refusedListText = rows.Single(r => r.Path == refused).Text;
        string openedListText = rows.Single(r => r.Path == opened).Text;
        TopLevelRow refusedTop = result.TopLevel!.Rows.Single(r => r.Name == "a-refused");
        TopLevelRow openedTop = result.TopLevel!.Rows.Single(r => r.Name == "b-opened");
        Assert.Contains(NotOpened, refusedListText);
        Assert.EndsWith($"({NotOpened})", TopLevelRowText.TitleOf(refusedTop));

        // The bug: the opened folder is recorded and worded as the refused one. It was opened, so none of these may say it
        // could not be.
        Assert.True(openedRecord.Kind != refusedRecord.Kind,
            $"b-opened opened and then failed its first listing page, but its record says '{openedRecord.Kind}', the same as the folder " +
            "whose open was refused: the scan does not tell the two phases apart");
        Assert.DoesNotContain(NotOpened, openedListText);
        Assert.DoesNotContain(NotOpened, TopLevelRowText.TitleOf(openedTop));
    }
}
