using System.Diagnostics;
using FsAnalyser.Core.Scanning;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// A share that was reachable and then stopped answering mid-scan, as after Wi-Fi loss, a VPN drop or
/// resume from sleep. The session is established and the listing goes out, but the server never answers
/// it, nor anything after it.
/// </summary>
/// <remarks>
/// Opt-in, and left out of the default run by the test project's VSTestTestCaseFilter. It runs an
/// SMBLibrary server on 127.0.0.1 on a spare port, with a test user, and reaches it through a
/// non-persistent New-SmbMapping -TcpPort (Windows 11 24H2 and later). It needs no elevation and changes
/// nothing that outlives it: the mapping is removed and the server stopped when the test ends. The rig is
/// Rig/SmbRig.cs, shared with tests/window/StallRig; it waits for any other rig run on this machine.
/// <para>Measured on this PC (SL-024 build 3): this is the one shape Cancel cannot cut short. The SMB
/// client holds the unanswered listing, and the CancelSynchronousIo aimed at it, until it gives up on the
/// server: the scan returned 61.9 and 66.0 s after Cancel in two runs, the canceller's single CancelSynchronousIo blocked
/// all that time. Cancel() itself still returned in about 1 ms, because it only starts the canceller.
/// So the bound asserted here is the client's own limit, not criterion (1)'s 2 s, which the design does
/// not promise for a stalled session.</para>
/// </remarks>
public class StalledShareTests(ITestOutputHelper output)
{
    /// <summary>The SMB client's session timeout is 60 s by default; 62 to 66 s was measured, with margin here.</summary>
    private static readonly TimeSpan ClientGivesUp = TimeSpan.FromSeconds(90);

    [Fact]
    [Trait("Network", "SMB-LOOPBACK")]
    public void CancellingAScanOfAShareThatStoppedAnsweringNeverHangsInTheClose()
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.File(@"a\one.txt", 11);
        tree.Folder("hung");
        for (int i = 0; i < 5; i++)
            tree.File($@"hung\file{i}.txt", 100);
        tree.Folder("z");
        tree.File(@"z\last.txt", 33);
        using var share = new LoopbackShare(tree.Root);
        share.Store.StopAnsweringAtTheListingOf("hung");

        using var cts = new CancellationTokenSource();
        var progress = new ScanProgress();
        SynchronousIoCancel? guard = null;
        ScanResult? result = null;
        Exception? scanError = null;
        var clock = Stopwatch.StartNew();
        TimeSpan returned = default;
        var scan = new Thread(() =>
        {
            SynchronousIoCancel.Created = g => guard = g;
            try
            {
                result = Scanner.Scan(share.RemotePath, progress, cts.Token);
            }
            catch (Exception e)
            {
                scanError = e;
            }
            finally
            {
                SynchronousIoCancel.Created = null;
                returned = clock.Elapsed;
            }
        }) { IsBackground = true };
        scan.Start();

        Assert.True(share.Store.Stalled.Wait(TimeSpan.FromSeconds(30)),
            $"the scan never sent the listing of hung; it ended {returned.TotalMilliseconds:F0} ms in with {result?.Status}, root error {result?.RootError}, {scanError?.Message}\nThe server's log:\n  {share.ServerLog}");
        Thread.Sleep(500);
        Assert.True(scan.IsAlive, $"VOID: the scan ended by itself {returned.TotalMilliseconds:F0} ms in while the server was not answering; no stalled call was exercised");

        var cancelClock = Stopwatch.StartNew();
        cts.Cancel();
        TimeSpan cancelTook = cancelClock.Elapsed;
        bool within2s = scan.Join(TimeSpan.FromSeconds(2));
        bool freed = within2s || scan.Join(ClientGivesUp - TimeSpan.FromSeconds(2));
        TimeSpan sinceCancel = cancelClock.Elapsed;
        int closesPending = SynchronousIoCancel.PendingCloses;
        bool closeDoneWithin1s = SpinWait.SpinUntil(() => SynchronousIoCancel.PendingCloses == 0, TimeSpan.FromSeconds(1));
        output.WriteLine($"server stalled {share.Store.StalledAt.TotalMilliseconds:F0} ms after it started; Cancel() took {cancelTook.TotalMilliseconds:F1} ms; " +
            $"scan returned: {freed}, {sinceCancel.TotalMilliseconds:F0} ms after Cancel (within 2 s: {within2s}); " +
            $"CancelSynchronousIo attempts {guard?.CancelAttempts}, delivered {guard?.CancelsDelivered}; " +
            $"closes handed off {guard?.ClosesHandedOff}, pending when the scan returned {closesPending}, done within 1 s after {closeDoneWithin1s}; " +
            $"result {result?.Status}, root error {result?.RootError}, unreadable [{string.Join(",", result?.Unreadable.Select(u => u.Win32Error) ?? [])}], error {scanError?.Message}");

        Assert.True(cancelTook < TimeSpan.FromMilliseconds(250), $"Cancel() took {cancelTook.TotalMilliseconds:F0} ms on the calling thread");
        Assert.True(freed, $"the scan was still blocked {ClientGivesUp.TotalSeconds:F0} s after Cancel on a share that stopped answering");
        Assert.Null(scanError);
        Assert.True(guard!.CancelAttempts >= 1, "the canceller never tried to interrupt the stalled listing");

        // The stalled folder is not passed off as read: the scan says it was stopped, and the folder holds
        // nothing, as a folder never reached. Whatever the client finally returned for the listing, a cancel
        // had been issued during it, so it is discarded (criterion 2: under a requested cancel, cancellation,
        // not an unreadable folder).
        Assert.Equal(ScanStatus.Cancelled, result!.Status);
        Assert.Equal(0, result.RootError);
        Assert.Empty(result.Unreadable);
        string hung = share.RemotePath + @"\hung";
        Assert.Equal(0, result.Store.ChildCount(Scans.Find(result, hung)));
        Assert.Equal(1, result.Store.ChildCount(Scans.Find(result, share.RemotePath + @"\a")));
        Assert.Equal(share.RemotePath + @"\a", progress.CurrentFolder); // hung was never reported listed
        Assert.DoesNotContain(Scans.ByPath(result.Store).Keys, path => path.StartsWith(hung + @"\", StringComparison.Ordinal));
        TraversalTests.AssertChildrenAreContiguous(result.Store);

        // The stalled folder's handle was closed on a pool thread, never on the scan thread: once the listing
        // came back, nothing else kept the scan.
        Assert.True(guard.ClosesHandedOff >= 1, "the aborted listing's handle was not handed to the background close");
    }
}
