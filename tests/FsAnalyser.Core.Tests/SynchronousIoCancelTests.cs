using System.Diagnostics;
using System.IO.Pipes;
using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The cancel guard on its own. A synchronous named-pipe wait with no client stands in for an open or a
/// listing blocked in the kernel: it is local, needs no elevation, and only CancelSynchronousIo frees it.
/// </summary>
public class SynchronousIoCancelTests
{
    private static readonly TimeSpan PromptCancel = TimeSpan.FromMilliseconds(250);

    [Fact]
    public void CancelInterruptsACallBlockedInTheKernel()
    {
        using var cts = new CancellationTokenSource();
        using NamedPipeServerStream server = NewPipe(out _);
        using var inCall = new ManualResetEventSlim();
        SynchronousIoCancel? guard = null;
        bool entered = false, issued = false;
        Exception? waitError = null;
        var blocked = new Thread(() =>
        {
            using var g = new SynchronousIoCancel(cts.Token);
            guard = g;
            entered = g.Enter();
            inCall.Set();
            try
            {
                server.WaitForConnection();
            }
            catch (Exception e)
            {
                waitError = e;
            }
            finally
            {
                issued = g.Exit();
            }
        }) { IsBackground = true };
        blocked.Start();
        Assert.True(inCall.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(200);

        var clock = Stopwatch.StartNew();
        cts.Cancel();
        TimeSpan cancelTook = clock.Elapsed;

        Assert.True(blocked.Join(TimeSpan.FromSeconds(2)), "the blocked call was still blocked 2 s after Cancel");
        Assert.True(entered);
        Assert.True(cancelTook < PromptCancel, $"Cancel() took {cancelTook.TotalMilliseconds:F0} ms on the calling thread");
        Assert.True(issued, "Exit did not report the cancel issued during the call");
        Assert.NotNull(waitError); // the wait was interrupted, it did not end by itself
        Assert.True(guard!.CancelsDelivered >= 1, "no CancelSynchronousIo found the blocked call");
    }

    /// <summary>
    /// A cancel that reaches the thread before its call reaches the kernel finds nothing (ERROR_NOT_FOUND)
    /// and is lost unless it is sent again. Here the thread stays in managed code between Enter and its wait
    /// until two cancels were sent and 100 ms more, so the first cancels all miss. Waited for, not timed: each
    /// attempt starts a thread of its own, and under load the first came more than 200 ms after Cancel
    /// (SL-055 validate), so a fixed sleep let it land on the wait.
    /// </summary>
    [Fact]
    public void CancelIsResentUntilACallThatHadNotReachedTheKernelIsInterrupted()
    {
        using var cts = new CancellationTokenSource();
        using NamedPipeServerStream server = NewPipe(out _);
        SynchronousIoCancel? guard = null;
        bool entered = false, issued = false, sentBeforeWait = false;
        Exception? waitError = null;
        TimeSpan waited = default;
        var blocked = new Thread(() =>
        {
            using var g = new SynchronousIoCancel(cts.Token);
            guard = g;
            entered = g.Enter();
            cts.Cancel();
            sentBeforeWait = SpinWait.SpinUntil(() => g.CancelAttempts >= 2, TimeSpan.FromSeconds(5));
            Thread.Sleep(100);
            var clock = Stopwatch.StartNew();
            try
            {
                server.WaitForConnection();
            }
            catch (Exception e)
            {
                waitError = e;
            }
            finally
            {
                waited = clock.Elapsed;
                issued = g.Exit();
            }
        }) { IsBackground = true };
        blocked.Start();

        Assert.True(blocked.Join(TimeSpan.FromSeconds(10)), "the wait was never interrupted: a cancel that missed was not sent again");
        Assert.True(entered);
        Assert.True(sentBeforeWait, $"{guard!.CancelAttempts} cancel(s) sent in 5 s before the wait: a cancel that missed was not sent again");
        Assert.True(waited < TimeSpan.FromSeconds(2), $"the wait returned {waited.TotalMilliseconds:F0} ms after it started");
        Assert.NotNull(waitError);
        Assert.True(issued);
        Assert.True(guard!.CancelsDelivered >= 1);
        Assert.True(guard.CancelAttempts > guard.CancelsDelivered,
            $"{guard.CancelAttempts} attempts, {guard.CancelsDelivered} delivered: the cancels sent before the wait should have found nothing");
    }

    /// <summary>
    /// A populating cloud folder blocks again after a cancel: the platform re-issues its fetch, and one
    /// cancel never freed it (spike 2). Here the call waits again after each interruption, three times in
    /// all, and the canceller must keep cancelling until the call leaves.
    /// </summary>
    [Fact]
    public void CancelIsResentWhileACallBlocksAgainAfterACancel()
    {
        const int Waits = 3;
        using var cts = new CancellationTokenSource();
        using var inCall = new ManualResetEventSlim();
        SynchronousIoCancel? guard = null;
        int interrupted = 0;
        bool issued = false;
        var blocked = new Thread(() =>
        {
            using var g = new SynchronousIoCancel(cts.Token);
            guard = g;
            g.Enter();
            inCall.Set();
            try
            {
                for (int i = 0; i < Waits; i++)
                {
                    using NamedPipeServerStream server = NewPipe(out _);
                    try
                    {
                        server.WaitForConnection();
                    }
                    catch (Exception)
                    {
                        interrupted++;
                    }
                }
            }
            finally
            {
                issued = g.Exit();
            }
        }) { IsBackground = true };
        blocked.Start();
        Assert.True(inCall.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(200);

        cts.Cancel();

        Assert.True(blocked.Join(TimeSpan.FromSeconds(2)), $"the call was still blocked 2 s after Cancel, interrupted {interrupted} time(s)");
        Assert.Equal(Waits, interrupted);
        Assert.True(issued);
        Assert.True(guard!.CancelsDelivered >= Waits);
    }

    /// <summary>
    /// Every folder listing passes through the guard, and most are never cancelled: Exit then starts no
    /// attempt and waits for none.
    /// </summary>
    [Fact]
    public void ExitWithNoCancelMakesNoAttemptAndWaitsForNone()
    {
        using var cts = new CancellationTokenSource();
        SynchronousIoCancel guard;
        using (guard = new SynchronousIoCancel(cts.Token))
        {
            Assert.True(guard.HasThreadHandle);
            Assert.True(guard.Enter());
            Assert.False(guard.Exit());
            Assert.False(guard.LastCallAborted);
        }

        Assert.Equal(0, guard.CancelAttempts);
        Assert.Equal(0, guard.PeakAttemptsInKernel);
        Assert.Equal(0, guard.AttemptsLiveAtLastDrain);
        AssertNoCancelLandsOnThisThread();
    }

    /// <summary>
    /// Attempts that may cancel but have not called CancelSynchronousIo yet when the call leaves, as on a
    /// busy machine: the first waits until four are live and 200 ms more before its call, so the next ones
    /// are started meanwhile, and they wait until Exit has begun and 100 ms more. Exit returns only once they
    /// have ended, so none lands on the thread's next wait. An Exit that did not wait for them lets that wait
    /// be aborted (measured, SL-055). Those 200 ms are eight of the canceller's ticks with the cap reached, so
    /// a canceller with no cap, or a higher one, starts a fifth (SL-055 review m2).
    /// </summary>
    [Fact]
    public void ExitWaitsForAttemptsAboutToCancelSoNoneLandsAfterIt()
    {
        using var tree = new TempTree();
        tree.File("a.bin", 10);
        tree.File("b.bin", 10);
        using var cts = new CancellationTokenSource();
        using NamedPipeServerStream server = NewPipe(out _);
        using var inCall = new ManualResetEventSlim();
        SynchronousIoCancel? guard = null;
        Exception? waitError = null, afterExit = null;
        bool issued = false;
        int attemptsAtExit = 0, liveAtExit = 0;
        string[]? listedAfterExit = null;
        var blocked = new Thread(() =>
        {
            using var g = new SynchronousIoCancel(cts.Token);
            guard = g;
            g.BeforeCancelCall = attempt =>
            {
                if (attempt == 1)
                {
                    // Waited for, not timed: on a busy machine three ticks can take longer than any fixed hold.
                    SpinWait.SpinUntil(() => g.PeakAttemptsInKernel >= 4, TimeSpan.FromSeconds(5));
                    Thread.Sleep(200);
                    return;
                }
                SpinWait.SpinUntil(() => !g.CancelIsIssued, TimeSpan.FromSeconds(5));
                Thread.Sleep(100);
            };
            g.Enter();
            inCall.Set();
            try
            {
                server.WaitForConnection();
            }
            catch (Exception e)
            {
                waitError = e;
            }
            finally
            {
                issued = g.Exit();
                attemptsAtExit = g.CancelAttempts;
                liveAtExit = g.AttemptsLiveAtLastDrain;
            }
            // Exit has returned and the guard is not disposed yet: a listing, then a wait that pends until a
            // client connects 300 ms later, while the held attempts wake.
            afterExit = Record.Exception(() =>
            {
                listedAfterExit = Directory.GetFileSystemEntries(tree.Root);
                AssertNoCancelLandsOnThisThread(clientDelayMilliseconds: 300);
            });
        }) { IsBackground = true };
        blocked.Start();
        Assert.True(inCall.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(200);

        cts.Cancel();

        Assert.True(blocked.Join(TimeSpan.FromSeconds(10)), "the thread never got through Exit and the wait after it");
        Assert.NotNull(waitError);
        Assert.True(issued);
        Assert.True(liveAtExit >= 2, $"{liveAtExit} attempt(s) live when Exit was called: none was about to cancel");
        Assert.Equal(4, guard!.PeakAttemptsInKernel); // the cap on live attempts, reached and never passed
        Assert.Null(afterExit); // an attempt that outlived Exit aborted the next wait
        Assert.Equal(2, listedAfterExit!.Length);
        Assert.Equal(attemptsAtExit, guard.CancelAttempts);
    }

    /// <summary>
    /// An exception unwinds through the guard's using while a cancel is issued, so Dispose is reached with
    /// no Exit and the canceller still sending. Dispose stops it and waits for its attempts before it closes
    /// the thread handle: none is made after, and none lands on the thread's next wait. The attempts made
    /// after the interruption are held until the exception is thrown and 250 ms more, so they are still live
    /// when Dispose drains; a Dispose that did not wait for them returns while they are held (SL-055 review m1).
    /// </summary>
    [Fact]
    public void DisposeWithoutExitWhileCancellingEndsEveryAttemptFirst()
    {
        using var cts = new CancellationTokenSource();
        using var inCall = new ManualResetEventSlim();
        SynchronousIoCancel? guard = null;
        Exception? waitError = null, unwound = null, afterDispose = null;
        bool resent = false, unwinding = false;
        int first = int.MaxValue, held = 0, released = 0;
        int attemptsAtDispose = 0, heldAtDispose = 0, releasedAtDispose = 0;
        var blocked = new Thread(() =>
        {
            try
            {
                using var g = new SynchronousIoCancel(cts.Token);
                guard = g;
                g.BeforeCancelCall = attempt =>
                {
                    if (attempt <= Volatile.Read(ref first))
                        return;
                    Interlocked.Increment(ref held);
                    SpinWait.SpinUntil(() => Volatile.Read(ref unwinding), TimeSpan.FromSeconds(5));
                    Thread.Sleep(250);
                    Interlocked.Increment(ref released);
                };
                g.Enter();
                inCall.Set();
                using NamedPipeServerStream server = NewPipe(out _);
                try
                {
                    server.WaitForConnection();
                }
                catch (Exception e)
                {
                    waitError = e;
                }
                // Still cancelling: attempts go on, finding nothing, until the guard is disposed.
                Volatile.Write(ref first, g.CancelAttempts);
                resent = SpinWait.SpinUntil(() => g.CancelAttempts >= first + 2, TimeSpan.FromSeconds(5));
                Volatile.Write(ref unwinding, true);
                throw new InvalidOperationException("unwinds through the using with no Exit");
            }
            catch (InvalidOperationException e)
            {
                unwound = e;
            }
            attemptsAtDispose = guard!.CancelAttempts;
            heldAtDispose = Volatile.Read(ref held);
            releasedAtDispose = Volatile.Read(ref released);
            afterDispose = Record.Exception(() => AssertNoCancelLandsOnThisThread(clientDelayMilliseconds: 300));
        }) { IsBackground = true };
        blocked.Start();
        Assert.True(inCall.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(200);

        cts.Cancel();

        Assert.True(blocked.Join(TimeSpan.FromSeconds(10)), "the thread never got through Dispose and the wait after it");
        Assert.NotNull(waitError);
        Assert.True(resent, "the canceller stopped sending before Dispose, so Dispose had nothing to stop");
        Assert.NotNull(unwound);
        Assert.True(guard!.CancelIsIssued, "Dispose was not reached while the call was still marked cancelled");
        Assert.True(heldAtDispose >= 1, "no attempt made after the interruption was held, so Dispose had none to wait for");
        Assert.True(guard.AttemptsLiveAtLastDrain >= 1, $"{guard.AttemptsLiveAtLastDrain} attempt(s) live when Dispose drained: none was in flight");
        Assert.Equal(heldAtDispose, releasedAtDispose); // Dispose returned while an attempt it had to wait for was still held
        Assert.Null(afterDispose);
        Thread.Sleep(100); // longer than the canceller's interval
        Assert.Equal(attemptsAtDispose, guard.CancelAttempts);
    }

    /// <summary>
    /// A call aborted with a handle open, or an open that Windows completed after a cancel was issued: the
    /// handle is never handed out or reused, and it is closed on a pool thread, never on the scan thread.
    /// </summary>
    [Fact]
    public void AnAbortedCallsHandleIsClosedOffTheScanThread()
    {
        using var tree = new TempTree();
        tree.File("file.bin", 10);

        using (var cts = new CancellationTokenSource())
        using (var guard = new SynchronousIoCancel(cts.Token))
        {
            guard.AfterCall = _ => CancelDuringTheCall(cts, guard);
            Assert.Equal(SynchronousIoCancel.Aborted, guard.OpenDirectory(tree.ExtendedRoot, out var discarded));
            Assert.Null(discarded); // Windows returned a handle; the aborted open does not hand it out
            Assert.Equal(1, guard.ClosesHandedOff);
        }

        using (var cts = new CancellationTokenSource())
        using (var guard = new SynchronousIoCancel(cts.Token))
        using (var reader = new DirectoryReader(guard))
        {
            Assert.Equal(0, guard.OpenDirectory(tree.ExtendedRoot, out var handle));
            guard.AfterCall = _ => CancelDuringTheCall(cts, guard);
            Assert.Equal(SynchronousIoCancel.Aborted, reader.List(handle!, fullClass: false));
            Assert.Equal(0, reader.Count); // Windows listed file.bin; nothing of the aborted listing is kept
            guard.Close(handle!);
            Assert.Equal(1, guard.ClosesHandedOff);
        }

        Scans.WaitForAbortedCloses();
        AssertNoCancelLandsOnThisThread();
    }

    private static void CancelDuringTheCall(CancellationTokenSource cts, SynchronousIoCancel guard)
    {
        cts.Cancel();
        Assert.True(SpinWait.SpinUntil(() => guard.CancelIsIssued, TimeSpan.FromSeconds(5)), "the canceller never marked the call");
    }

    [Fact]
    public void NoCallStartsOnceCancelIsRequested()
    {
        using var tree = new TempTree();
        tree.File("file.bin", 10);

        // Cancelled after the guard was made.
        using (var cts = new CancellationTokenSource())
        using (var guard = new SynchronousIoCancel(cts.Token))
        {
            Assert.Equal(0, guard.OpenDirectory(tree.ExtendedRoot, out var opened));
            guard.Close(opened!);
            cts.Cancel();

            Assert.Equal(SynchronousIoCancel.Aborted, guard.OpenDirectory(tree.ExtendedRoot, out var refused));
            Assert.Null(refused);
            Assert.Equal(SynchronousIoCancel.Aborted, guard.OpenForAttributes(tree.ExtendedRoot + @"\file.bin", out refused));
            Assert.Null(refused);
            Assert.False(guard.Enter());
            Assert.Equal(0, guard.CancelsDelivered); // no call was in progress, so nothing was interrupted
        }

        // Cancelled before the guard was made, as for a scan started with a cancelled token.
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            using var guard = new SynchronousIoCancel(cts.Token);
            Assert.Equal(SynchronousIoCancel.Aborted, guard.OpenDirectory(tree.ExtendedRoot, out var refused));
            Assert.Null(refused);
        }

        AssertNoCancelLandsOnThisThread();
    }

    [Theory]
    [InlineData(0, true, 995)] // SUCCESS under a cancel, as a cancelled cloud listing returns: discarded
    [InlineData(18, true, 995)] // ERROR_NO_MORE_FILES under a cancel: the listing may not have ended
    [InlineData(5, true, 995)]
    [InlineData(995, true, 995)]
    [InlineData(0, false, 0)]
    [InlineData(5, false, 5)]
    [InlineData(995, false, 995)]
    public void SettleDiscardsTheResultOfACallDuringWhichACancelWasIssued(int error, bool cancelIssued, int settled)
    {
        Assert.Equal(settled, SynchronousIoCancel.Settle(error, cancelIssued));
    }

    [Theory]
    [InlineData(995, true, "Cancelled")]
    [InlineData(995, false, "Unreadable")] // no cancel requested: never dropped silently
    [InlineData(5, true, "Unreadable")]
    [InlineData(5, false, "Unreadable")]
    [InlineData(0, true, "Read")]
    [InlineData(0, false, "Read")]
    // What a descended non-surrogate reparse folder answers (SL-043; seen 28 and 2 times on C: by the
    // spike): recorded with its code, never dropped, cancel or not.
    [InlineData(363, true, "Unreadable")]  // ERROR_CLOUD_FILE_METADATA_CORRUPT: a cloud folder
    [InlineData(363, false, "Unreadable")]
    [InlineData(1920, true, "Unreadable")] // ERROR_CANT_ACCESS_FILE: a reparse tag no filter handles
    [InlineData(1920, false, "Unreadable")]
    public void OnlyARequestedCancelMakesAnAbortedCallACancellation(int error, bool cancelRequested, string outcome)
    {
        Assert.Equal(Enum.Parse<Scanner.ReadOutcome>(outcome), Scanner.ClassifyReadError(error, cancelRequested));
    }

    [Fact]
    public void ATokenThatCannotCancelOpensNoThreadHandle()
    {
        using (var none = new SynchronousIoCancel(CancellationToken.None))
            Assert.False(none.HasThreadHandle);

        using var cts = new CancellationTokenSource();
        using var live = new SynchronousIoCancel(cts.Token);
        Assert.True(live.HasThreadHandle);
    }

    [Fact]
    public void AGuardIsUsedOnlyOnTheThreadThatCreatedIt()
    {
        using var guard = new SynchronousIoCancel(CancellationToken.None);
        Exception? error = null;
        var other = new Thread(() => error = Record.Exception(() => guard.Enter()));
        other.Start();
        other.Join();

        Assert.IsType<InvalidOperationException>(error);
    }

    /// <summary>
    /// Many small scans, each cancelled at a random moment from before its first call to after its end.
    /// After each, a synchronous wait on the scan's own thread must not be interrupted: a cancel that
    /// outlived its scan would land there.
    /// </summary>
    [Fact]
    public void CancelAtRandomMomentsEndsEveryScanCleanlyAndLeavesNoStrayCancel()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 4; i++)
        {
            tree.Folder($"f{i}");
            for (int j = 0; j < 3; j++)
                tree.File($@"f{i}\file{j}.bin", 100);
        }
        long scanTicks = Enumerable.Range(0, 5).Select(_ =>
        {
            long start = Stopwatch.GetTimestamp();
            Scans.Run(tree.Root);
            return Stopwatch.GetTimestamp() - start;
        }).Order().ElementAt(2);
        var random = new Random(24);
        int completed = 0, cancelled = 0;

        for (int run = 0; run < 100; run++)
        {
            using var cts = new CancellationTokenSource();
            long delay = random.NextInt64(0, 2 * scanTicks);
            var canceller = new Thread(() =>
            {
                long start = Stopwatch.GetTimestamp();
                while (Stopwatch.GetTimestamp() - start < delay)
                    Thread.SpinWait(20);
                cts.Cancel();
            });
            canceller.Start();

            ScanResult result = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token);
            canceller.Join();

            if (result.Status == ScanStatus.Completed)
                completed++;
            else
                cancelled++;
            Assert.Contains(result.Status, new[] { ScanStatus.Completed, ScanStatus.Cancelled });
            Assert.Empty(result.Unreadable);
            Scans.AssertChargedBytesAreTheTotals(result);
            if (result.Store.Count > 0)
                TraversalTests.AssertChildrenAreContiguous(result.Store);
            AssertNoCancelLandsOnThisThread();
        }

        Assert.True(completed > 0 && cancelled > 0, $"{completed} completed, {cancelled} cancelled: the cancels should land both during and after the scans");
        Scans.WaitForAbortedCloses();
    }

    internal static NamedPipeServerStream NewPipe(out string name)
    {
        name = "fsa-tests-" + Guid.NewGuid().ToString("N");
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
    }

    /// <summary>
    /// A synchronous wait on this thread whose client connects 50 ms later, or as asked. A
    /// CancelSynchronousIo that reaches this thread aborts it and fails the test.
    /// </summary>
    internal static void AssertNoCancelLandsOnThisThread(int clientDelayMilliseconds = 50)
    {
        using NamedPipeServerStream server = NewPipe(out string name);
        Task client = Task.Run(() =>
        {
            Thread.Sleep(clientDelayMilliseconds);
            using var connection = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            connection.Connect(5000);
        });
        server.WaitForConnection();
        client.Wait();
    }
}
