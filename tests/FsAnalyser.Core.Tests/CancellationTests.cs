using System.Diagnostics;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

public class CancellationTests
{
    [Fact]
    public void CancellingAfterTheThirdFolderStopsWithWhatWasRead()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 10; i++)
        {
            tree.Folder($"f{i}");
            tree.File($@"f{i}\file.bin", 10);
        }
        using var cts = new CancellationTokenSource();
        var listed = new List<int>();
        var options = new ScanOptions
        {
            AfterFolder = n =>
            {
                listed.Add(n);
                if (n == 3)
                    cts.Cancel();
            },
        };
        var progress = new ScanProgress();

        ScanResult result = Scanner.Scan(tree.Root, progress, cts.Token, options);

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Equal([1, 2, 3], listed); // the root, f0, f1
        Assert.Equal(new ScanTotals(Files: 2, Folders: 10, Links: 0, Bytes: 20, BytesOnDisk: result.Totals.BytesOnDisk), result.Totals);
        Scans.AssertChargedBytesAreTheTotals(result);
        Assert.Equal(1 + 10 + 2, result.Store.Count);
        Assert.Equal(1, result.Store.ChildCount(Scans.Find(result, tree.PathOf("f1"))));
        Assert.Equal(0, result.Store.ChildCount(Scans.Find(result, tree.PathOf("f2"))));
        Assert.Equal(2, progress.Files);
        Assert.Equal(10, progress.Folders);
        Assert.Equal(tree.PathOf("f1"), progress.CurrentFolder);
    }

    [Fact]
    public void AnAlreadyCancelledTokenReadsNothing()
    {
        using var tree = new TempTree();
        tree.File("file.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var listed = new List<int>();

        ScanResult result = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, new ScanOptions { AfterFolder = listed.Add });

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Empty(listed);
        Assert.Equal(0, result.Store.Count);
        Assert.Equal(default, result.Totals);
    }

    /// <summary>
    /// The default suite's stand-in for an unreachable share: the scan's first call (the root's open) is
    /// stuck in a synchronous named-pipe wait that knows nothing of the token, inside the call's bracket.
    /// Cancel must return at once and the scan within 2 s, while the call is still stuck.
    /// </summary>
    [Fact]
    public void CancelReturnsFromAScanStuckInItsFirstCall()
    {
        using var tree = new TempTree();
        tree.File("file.bin", 10);
        using var cts = new CancellationTokenSource();
        using var server = SynchronousIoCancelTests.NewPipe(out _);
        using var stuck = new ManualResetEventSlim();
        SynchronousIoCancel? guard = null;
        ScanResult? result = null;
        Exception? scanError = null, standInError = null;
        var scan = new Thread(() =>
        {
            SynchronousIoCancel.Created = g =>
            {
                guard = g;
                g.BeforeCall = call =>
                {
                    if (call != 1)
                        return;
                    stuck.Set();
                    try
                    {
                        server.WaitForConnection();
                    }
                    catch (Exception e)
                    {
                        standInError = e;
                    }
                };
            };
            try
            {
                result = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token);
            }
            catch (Exception e)
            {
                scanError = e;
            }
            finally
            {
                SynchronousIoCancel.Created = null;
            }
        }) { IsBackground = true };
        scan.Start();
        Assert.True(stuck.Wait(TimeSpan.FromSeconds(5)), "the scan never reached its first call");
        Thread.Sleep(500);
        Assert.True(scan.IsAlive, "the stand-in returned by itself: no stuck call was exercised");

        var clock = Stopwatch.StartNew();
        cts.Cancel();
        TimeSpan cancelTook = clock.Elapsed;

        Assert.True(scan.Join(TimeSpan.FromSeconds(2)), "the scan was still stuck 2 s after Cancel");
        Assert.Null(scanError);
        Assert.True(cancelTook < TimeSpan.FromMilliseconds(250), $"Cancel() took {cancelTook.TotalMilliseconds:F0} ms");
        Assert.NotNull(standInError); // interrupted, not finished by itself
        Assert.True(guard!.CancelsDelivered >= 1);
        Assert.Equal(ScanStatus.Cancelled, result!.Status);
        Assert.Equal(0, result.RootError);
        Assert.Empty(result.Unreadable);
        Assert.Equal(0, result.Store.Count);
    }

    /// <summary>
    /// The real thing: the root is a share on TEST-NET-1 (192.0.2.0/24, never routed), whose open blocks
    /// about 21 s on this PC before failing with error 53. The last octet varies per run, in case the
    /// redirector remembers a failed address.
    /// </summary>
    /// <remarks>
    /// Opt-in, and left out of the default run by the test project's VSTestTestCaseFilter (SL-024, m5):
    /// a UNC path by IP address signs in with NTLM, so on a hostile network whoever answers on port 445
    /// receives this user's NTLMv2 response, and a network that fails fast turns the test red when the
    /// code is fine. That risk is accepted only when someone asks for the test. Run it by name with an
    /// explicit filter, which replaces the default one. It fails as VOID, and never passes, when the scan
    /// ends before the cancel: then no blocked call was exercised.
    /// </remarks>
    [Fact]
    [Trait("Network", "TEST-NET-1")]
    public void CancelReturnsFromAScanBlockedOnAnUnreachableShare()
    {
        AssertCancelFreesAScanBlockedOn(UnreachableShare());
    }

    /// <summary>The same unreachable address twice in one session: the second scan must block too.</summary>
    [Fact]
    [Trait("Network", "TEST-NET-1")]
    public void CancelReturnsFromTheSameUnreachableShareScannedTwice()
    {
        string share = UnreachableShare();
        AssertCancelFreesAScanBlockedOn(share);
        AssertCancelFreesAScanBlockedOn(share);
    }

    /// <summary>
    /// The guard brackets every call even for a token that can never be cancelled, so the counting-rule
    /// tests, which pass CancellationToken.None, run the path the app runs. A live token that is never
    /// cancelled must still change nothing: the same entries, flags, sizes, errors and probes.
    /// </summary>
    [Theory]
    [InlineData("unreadable folder")]
    [InlineData("stale hardlink")]
    [InlineData("probed, failed probe and link")]
    public void ALiveTokenThatIsNeverCancelledChangesNothing(string shape)
    {
        using var tree = new TempTree();
        switch (shape)
        {
            case "unreadable folder":
                tree.Folder("a-readable");
                tree.File(@"a-readable\one.txt", 11);
                string denied = tree.Folder("b-denied");
                tree.File(@"b-denied\not-seen.txt", 1000);
                tree.Deny(denied, 0x1); // FILE_LIST_DIRECTORY: error 5
                break;
            case "stale hardlink":
                tree.Folder("1-folder");
                tree.Folder("2-folder");
                string written = tree.File(@"2-folder\current.bin", TempTree.MiB);
                TempTree.Hardlink(tree.PathOf(@"1-folder\stale.bin"), written);
                TempTree.Append(written, 63 * TempTree.MiB);
                break;
            default:
                tree.SparseFile("large.bin", 64 * TempTree.MiB);
                string wofDenied = tree.TextFile("wof-denied.txt", TempTree.MiB);
                TempTree.WofCompress(wofDenied);
                tree.Deny(wofDenied, 0x100080); // SYNCHRONIZE | FILE_READ_ATTRIBUTES: the probe's open fails
                string target = tree.Outside("target");
                TempTree.WriteFile(Path.Combine(target, "outside.bin"), 100);
                tree.Junction("link", target);
                break;
        }

        ScanResult none = Scans.Run(tree.Root);
        using var live = new CancellationTokenSource();
        SynchronousIoCancel? guard = null;
        SynchronousIoCancel.Created = g => guard = g;
        ScanResult liveResult;
        try
        {
            liveResult = Scanner.Scan(tree.Root, new ScanProgress(), live.Token);
        }
        finally
        {
            SynchronousIoCancel.Created = null;
        }

        Assert.True(guard!.HasThreadHandle && guard.Calls > 0 && guard.CancelAttempts == 0);
        Assert.Equal(ScanStatus.Completed, none.Status);
        Assert.Equal(none.Status, liveResult.Status);
        Assert.Equal(none.Totals, liveResult.Totals);
        Assert.Equal(Scans.UnreadableByPath(none), Scans.UnreadableByPath(liveResult));
        Assert.Equal(none.Probes, liveResult.Probes);
        Assert.Equal(none.ProbeFailures, liveResult.ProbeFailures);
        // Whether a probe corrected a figure is a fact about the listing at that moment, and the first scan's probe
        // of the stale name made Windows refresh that name's directory entry (SL-045), so the second scan lists it
        // current. Everything else, sizes included, must be the same.
        Assert.Equal(WithoutCorrections(Scans.Snapshot(none.Store)), WithoutCorrections(Scans.Snapshot(liveResult.Store)));
        switch (shape)
        {
            case "unreadable folder":
                Assert.Equal(5, Assert.Single(none.Unreadable).Win32Error);
                break;
            case "stale hardlink":
                Assert.Equal(1, none.Probes);
                Assert.Equal(64 * TempTree.MiB, none.Totals.Bytes);
                Assert.Equal((1, 0), (none.ProbeCorrections, liveResult.ProbeCorrections));
                break;
            default:
                Assert.Equal(2, none.Probes);
                Assert.Equal(1, none.ProbeFailures);
                Assert.Equal(1, none.Totals.Links);
                break;
        }
    }

    private static Dictionary<string, Scans.EntryState> WithoutCorrections(Dictionary<string, Scans.EntryState> entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value with { Flags = e.Value.Flags & ~EntryFlags.ProbeCorrected }, StringComparer.Ordinal);

    private static string UnreachableShare() => $@"\\192.0.2.{Random.Shared.Next(1, 255)}\s";

    private static void AssertCancelFreesAScanBlockedOn(string root)
    {
        using var cts = new CancellationTokenSource();
        SynchronousIoCancel? guard = null;
        ScanResult? result = null;
        Exception? scanError = null;
        var clock = Stopwatch.StartNew();
        TimeSpan ended = default;
        var scan = new Thread(() =>
        {
            SynchronousIoCancel.Created = g => guard = g;
            try
            {
                result = Scanner.Scan(root, new ScanProgress(), cts.Token);
            }
            catch (Exception e)
            {
                scanError = e;
            }
            finally
            {
                SynchronousIoCancel.Created = null;
                ended = clock.Elapsed;
            }
        }) { IsBackground = true };
        scan.Start();

        if (scan.Join(TimeSpan.FromMilliseconds(500)))
        {
            Assert.Fail($"VOID: network failed fast after {ended.TotalMilliseconds:F0} ms, error {result?.RootError.ToString() ?? scanError?.Message}; " +
                $"no blocked call was exercised ({root}).");
        }
        var cancelClock = Stopwatch.StartNew();
        cts.Cancel();
        TimeSpan cancelTook = cancelClock.Elapsed;

        Assert.True(scan.Join(TimeSpan.FromSeconds(2)), $"the scan of {root} was still blocked 2 s after Cancel");
        Assert.Null(scanError);
        Assert.True(cancelTook < TimeSpan.FromMilliseconds(250), $"Cancel() took {cancelTook.TotalMilliseconds:F0} ms");
        Assert.True(guard!.CancelsDelivered >= 1,
            $"no CancelSynchronousIo found a call to cancel: the scan of {root} ended by itself {ended.TotalMilliseconds:F0} ms in, it was not interrupted");
        Assert.Equal(ScanStatus.Cancelled, result!.Status);
        Assert.Equal(0, result.RootError);
        Assert.Empty(result.Unreadable);
    }
}
