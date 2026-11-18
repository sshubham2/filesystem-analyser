using FsAnalyser.App;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The kind a failed folder is stamped with (SL-106, closing SL-087's review M2): from the phase the reader saw, in the one
/// rule Scanner.FailedKind. Before it, a folder that opened and failed its first listing page was recorded exactly as one
/// whose open was refused, from ChildCount alone.
/// </summary>
public class ScannerFailedKindTests
{
    private const int AccessDenied = 5;
    private const int SharingViolation = 32;
    private const int NetworkOrShare = 59;
    private const int CloudMetadataCorrupt = 363;
    private const int CantAccessFile = 1920;
    private const int Aborted = SynchronousIoCancel.Aborted; // 995

    /// <summary>
    /// The rule over every shape it takes: no error is no kind, whatever was listed; entries kept make the folder listed
    /// partway whatever else is true; with none, the open decides, and never the code. The refusals the project has met
    /// (5, 32, 363, 1920) stay NothingListed, as the 27 real code-363 folders measured on this PC, whose open fails.
    /// </summary>
    [Theory]
    [InlineData(0, true, 0, null)]
    [InlineData(0, false, 0, null)]
    [InlineData(0, true, 7, null)]
    [InlineData(AccessDenied, false, 0, UnreadableKind.NothingListed)]
    [InlineData(SharingViolation, false, 0, UnreadableKind.NothingListed)]
    [InlineData(CloudMetadataCorrupt, false, 0, UnreadableKind.NothingListed)]
    [InlineData(CantAccessFile, false, 0, UnreadableKind.NothingListed)]
    [InlineData(NetworkOrShare, true, 0, UnreadableKind.OpenedNothingListed)]
    [InlineData(AccessDenied, true, 0, UnreadableKind.OpenedNothingListed)] // SMB variant 1: CREATE answered, the first QUERY_DIRECTORY refused
    [InlineData(Aborted, true, 0, UnreadableKind.OpenedNothingListed)] // a 995 Windows returned with no cancel issued, on the first page
    [InlineData(NetworkOrShare, true, 3, UnreadableKind.ListedPartway)]
    [InlineData(Aborted, true, 3, UnreadableKind.ListedPartway)]
    [InlineData(AccessDenied, false, 3, UnreadableKind.ListedPartway)] // entries kept are partway, whichever way the flag came
    public void TheKindFollowsThePhaseTheReaderSawAndNeverTheCode(int error, bool opened, int listed, UnreadableKind? expected) =>
        Assert.Equal(expected, Scanner.FailedKind(error, opened, listed));

    /// <summary>
    /// A folder is gone, not unreadable (SL-005), only when its open was refused (nothing opened) with a code the file probe also
    /// calls gone: 2 or 3. An opened folder that later reports either is a listing failure; 5 is a refusal or a delete still
    /// pending, 87 a folder a file replaced, 995 a cancel; each keeps today's kind. The code set is SizeProbe.IsGone's.
    /// </summary>
    [Theory]
    [InlineData(2, false, true)]
    [InlineData(3, false, true)]
    [InlineData(2, true, false)]
    [InlineData(3, true, false)]
    [InlineData(0, false, false)]
    [InlineData(AccessDenied, false, false)]
    [InlineData(SharingViolation, false, false)]
    [InlineData(NetworkOrShare, false, false)]
    [InlineData(87, true, false)]
    [InlineData(CloudMetadataCorrupt, false, false)]
    [InlineData(CantAccessFile, false, false)]
    [InlineData(Aborted, false, false)]
    public void FolderVanishedIsTrueOnlyForARefusedOpenThatFoundNoFolderOrNoPath(int error, bool opened, bool expected)
    {
        Assert.Equal(expected, Scanner.FolderVanished(error, opened));
        Assert.Equal(!opened && SizeProbe.IsGone(error), Scanner.FolderVanished(error, opened)); // the one definition of gone
    }

    /// <summary>
    /// The reader's bit, which the stamp reads: true once the last Read's open returned a handle, even when the listing
    /// then failed with nothing kept; false for a refused open; cleared by the next Read, so a refusal after an open is not
    /// stamped as one that opened.
    /// </summary>
    [Fact]
    public void TheReaderSaysWhetherItsLastReadGotAHandle()
    {
        using var tree = new TempTree();
        string refused = tree.Folder("refused");
        tree.File(@"refused\one.txt", 5);
        string plain = tree.Folder("plain");
        tree.File(@"plain\one.txt", 5);
        tree.Deny(refused, 0x1); // FILE_LIST_DIRECTORY: the open is refused
        using var guard = new SynchronousIoCancel(CancellationToken.None);
        using var reader = new DirectoryReader(guard);

        Assert.False(reader.Opened); // nothing read yet
        Assert.Equal(AccessDenied, reader.Read(LongPath.ToExtended(refused), fullClass: false, keepLastAccess: false));
        Assert.False(reader.Opened);

        Assert.Equal(0, reader.Read(LongPath.ToExtended(plain), fullClass: false, keepLastAccess: false));
        Assert.True(reader.Opened);
        Assert.Equal(1, reader.Count);

        guard.OverrideQueryError = (_, real) => real == 0 ? NetworkOrShare : real; // every page that came back is reported failed
        Assert.Equal(NetworkOrShare, reader.Read(LongPath.ToExtended(plain), fullClass: false, keepLastAccess: false));
        Assert.True(reader.Opened); // the open returned a handle; the first page failed
        Assert.Equal(0, reader.Count);

        Assert.Equal(AccessDenied, reader.Read(LongPath.ToExtended(refused), fullClass: false, keepLastAccess: false));
        Assert.False(reader.Opened); // cleared, not left over from the read before
    }

    /// <summary>
    /// The 995 both ways, through the real scanner, with the first listing page of one folder reporting it. Returned by
    /// Windows with no cancel issued it is an error like any other: the folder opened and listed nothing. With Cancel
    /// issued during that same call it is the guard's own abort, which commits nothing and stamps nothing: the scan
    /// ends Cancelled with no record of the folder.
    /// </summary>
    [Fact]
    public void AnUnaskedFirstPage995IsOpenedNothingListedAndACancelledOneLeavesNoRecord()
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\one.txt", 5);
        string b = tree.Folder("b");
        tree.File(@"b\one.txt", 5);
        tree.Folder("c");
        tree.File(@"c\one.txt", 5);
        (int call, _) = FirstPageFailure.CallOf(tree, b);

        (ScanResult unasked, int injected) = FirstPageFailure.Scan(tree, call, Aborted);

        Assert.Equal((ScanStatus.Completed, 1), (unasked.Status, injected));
        UnreadableFolder record = Assert.Single(unasked.Unreadable);
        Assert.Equal((b, Aborted, UnreadableKind.OpenedNothingListed), (unasked.Store.GetPath(record.Entry), record.Win32Error, record.Kind));
        Assert.Equal(0, unasked.Store.ChildCount(record.Entry));
        TopLevelRow row = unasked.TopLevel!.Rows.Single(r => r.Name == "b");
        Assert.Equal((UnreadableKind.OpenedNothingListed, "b  (opened, but nothing could be listed)"), (row.FailedKind, TopLevelRowText.TitleOf(row)));

        using var cts = new CancellationTokenSource();
        ScanResult cancelled = Scans.WithGuard(FirstPageFailure.OnThisThread(g => Scans.CancelAt(g, cts, call, during: true)),
            () => Scanner.Scan(tree.Root, new ScanProgress(), cts.Token));
        Scans.WaitForAbortedCloses();

        Assert.Equal(ScanStatus.Cancelled, cancelled.Status);
        Assert.Empty(cancelled.Unreadable);
    }
}

/// <summary>
/// Reports an error for one folder's first listing page, as an SMB server that answers CREATE and then fails QUERY_DIRECTORY
/// does, through the guard's own test seam (SynchronousIoCancel.OverrideQueryError, set through Created). No real I/O makes
/// a local folder open and then fail (lesson #309), and FailListingOf refuses page 1 by design. The seam is a static, so it
/// is handed only the guards the calling thread creates, which are its own scan's: a scan another test runs at the same
/// moment, or a probe worker, is not given it.
/// </summary>
internal static class FirstPageFailure
{
    /// <summary>
    /// Scans <paramref name="tree"/> unchanged and says which of the scan thread's guarded calls is the first listing page of
    /// <paramref name="folder"/>: the first query after the folder before it exited. The unchanged scan is returned too.
    /// </summary>
    internal static (int Call, ScanResult Dry) CallOf(TempTree tree, string folder)
    {
        var order = new List<string>();
        var exits = new List<int>();
        var queries = new List<int>();
        var progress = new ScanProgress();
        SynchronousIoCancel? scanGuard = null;
        ScanResult dry = Scans.WithGuard(
            OnThisThread(g =>
            {
                if (scanGuard is not null)
                    return;
                scanGuard = g;
                g.OverrideQueryError = (call, error) =>
                {
                    queries.Add(call);
                    return error;
                };
            }),
            () => Scanner.Scan(tree.Root, progress, CancellationToken.None, new ScanOptions
            {
                AfterFolder = _ =>
                {
                    order.Add(progress.CurrentFolder!);
                    exits.Add(scanGuard!.Calls);
                },
            }));
        Assert.Equal(ScanStatus.Completed, dry.Status);
        int at = order.IndexOf(folder);
        Assert.True(at > 0, $"{folder} was not among the folders the scan exited: {string.Join(" | ", order)}");
        return (queries.First(call => call > exits[at - 1] && call <= exits[at]), dry);
    }

    /// <summary>Scans <paramref name="tree"/> with <paramref name="error"/> reported in place of the page at <paramref name="call"/>, and how often it was.</summary>
    internal static (ScanResult Result, int Injected) Scan(TempTree tree, int call, int error)
    {
        int injected = 0;
        bool armed = false;
        ScanResult result = Scans.WithGuard(
            OnThisThread(g =>
            {
                if (armed)
                    return;
                armed = true;
                g.OverrideQueryError = (n, real) =>
                {
                    if (n != call || real != 0)
                        return real;
                    injected++;
                    return error;
                };
            }),
            () => Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None));
        return (result, injected);
    }

    /// <summary>
    /// <paramref name="arm"/> as a Created seam that runs only for a guard created on the thread that calls this, which is
    /// the scan's: Created runs on the thread that makes the guard.
    /// </summary>
    internal static Action<SynchronousIoCancel> OnThisThread(Action<SynchronousIoCancel> arm)
    {
        int scanThread = Environment.CurrentManagedThreadId;
        return guard =>
        {
            if (Environment.CurrentManagedThreadId == scanThread)
                arm(guard);
        };
    }
}
