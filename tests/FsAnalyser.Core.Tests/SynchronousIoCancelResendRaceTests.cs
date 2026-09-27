/*
 * Bug (SL-055): the cancel canceller can be stuck inside CancelSynchronousIo, so a call that blocks again
 * after a cancel is never freed. This is the cause of the flake in
 * SynchronousIoCancelTests.CancelIsResentWhileACallBlocksAgainAfterACancel ("the call was still blocked
 * 2 s after Cancel"), which fails under load and never on an idle machine.
 *
 * Expected: Cancel frees the call however it is scheduled. The guard keeps cancelling until the call
 * leaves, so a call that blocks again after each interruption (a populating cloud folder re-issuing its
 * fetch) is interrupted each time and returns within 2 s of Cancel.
 *
 * Actual, before the fix: the scan-cancel thread made one CancelSynchronousIo call and that call did not
 * return once the interrupted thread had issued its next synchronous wait first. Windows held the canceller
 * in the kernel (Wait/Executive) while the new wait sat uncancelled, so the 25 ms resend never ran again: one
 * interruption, then the call was still blocked after 2 s (CancelAttempts stayed 1, CancelsDelivered 0).
 * Idle, the canceller usually got out of the API before the thread re-blocked, so the test passed; under
 * load the thread won that race, and it was the same race every time it did.
 *
 * This test makes the thread win it every time: the process runs on one CPU and the blocked thread has the
 * higher priority, so the moment its wait is cancelled it runs on to its next wait before the canceller
 * is scheduled again. The fix issues each CancelSynchronousIo from a thread of its own, so the next tick's
 * attempt reaches the new wait. Measured with 3, 6 and 8 waits: the held attempt returns once the next one has
 * interrupted the new wait, so attempts do not pile up one per wait: 2 were live at once, and 3 in 2 of 87
 * runs under load, under the guard's cap of 4. The second test waits more times than the cap.
 */

using System.Diagnostics;
using System.IO.Pipes;
using FsAnalyser.Core.Scanning;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The resend race pins the whole process to one CPU, so nothing else may run beside it: a test in another
/// class would share that CPU with a thread at the highest priority.
/// </summary>
[CollectionDefinition(nameof(ResendRace), DisableParallelization = true)]
public class ResendRace;

[Collection(nameof(ResendRace))]
public class SynchronousIoCancelResendRaceTests(ITestOutputHelper output)
{
    [Fact]
    public void CancelFreesACallThatBlocksAgainBeforeTheCancelCallHasReturned() =>
        CancelACallThatBlocksAgainOnOneCpu(waits: 3, TimeSpan.FromSeconds(2));

    /// <summary>
    /// More waits than the guard's cap of 4 live attempts: were held attempts to pile up one per wait, the
    /// cap would stop the resend and a wait would stay blocked for good. This asks whether every wait is
    /// interrupted, not how fast: with the whole process on one CPU that 32 busy threads share, 6 waits took
    /// 1.1 to 2.05 s (SL-055's build), so the bar here tells a lost cancel from a slow one.
    /// </summary>
    [Fact]
    public void CancelFreesACallThatBlocksAgainMoreTimesThanTheCapOfLiveAttempts() =>
        CancelACallThatBlocksAgainOnOneCpu(waits: 8, TimeSpan.FromSeconds(10));

    private void CancelACallThatBlocksAgainOnOneCpu(int waits, TimeSpan bar)
    {
        using var tree = new TempTree();
        tree.File("a.bin", 10);
        tree.File("b.bin", 10);
        using Process self = Process.GetCurrentProcess();
        nint original = self.ProcessorAffinity;
        self.ProcessorAffinity = original & -original; // one CPU: the interrupted thread runs before the canceller does
        try
        {
            using var cts = new CancellationTokenSource();
            using var inCall = new ManualResetEventSlim();
            using var left = new ManualResetEventSlim();
            SynchronousIoCancel? guard = null;
            int interrupted = 0;
            bool issued = false;
            int attemptsAtExit = 0, liveAtExit = 0;
            string[]? listedAfterExit = null;
            Exception? afterExit = null;
            // The priority is this thread's own and ends with it; only the affinity is the process's.
            var blocked = new Thread(() =>
            {
                using var g = new SynchronousIoCancel(cts.Token);
                guard = g;
                g.Enter();
                inCall.Set();
                try
                {
                    for (int i = 0; i < waits; i++)
                    {
                        using NamedPipeServerStream server = SynchronousIoCancelTests.NewPipe(out _);
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
                    attemptsAtExit = g.CancelAttempts;
                    liveAtExit = g.AttemptsLiveAtLastDrain;
                    left.Set();
                }

                // Exit has returned and the guard is not disposed yet. A listing, then a wait that pends
                // until a client connects 300 ms later: a cancel of this guard that lands now aborts it.
                afterExit = Record.Exception(() =>
                {
                    listedAfterExit = Directory.GetFileSystemEntries(tree.Root);
                    SynchronousIoCancelTests.AssertNoCancelLandsOnThisThread(clientDelayMilliseconds: 300);
                });
            }) { IsBackground = true, Priority = ThreadPriority.Highest };
            blocked.Start();
            Assert.True(inCall.Wait(TimeSpan.FromSeconds(5)));
            Thread.Sleep(200);

            var clock = Stopwatch.StartNew();
            cts.Cancel();

            Assert.True(left.Wait(bar),
                $"the call was still blocked {bar.TotalSeconds:0} s after Cancel, interrupted {interrupted} time(s), {guard!.CancelAttempts} cancel attempt(s) made, at most {guard.PeakAttemptsInKernel} live at once");
            output.WriteLine($"left {clock.ElapsedMilliseconds} ms after Cancel: interrupted {interrupted} time(s), {attemptsAtExit} attempt(s), {guard.CancelsDelivered} delivered, at most {guard.PeakAttemptsInKernel} live at once, {liveAtExit} live at Exit");
            Assert.True(blocked.Join(TimeSpan.FromSeconds(10)), "the wait after Exit never ended");
            Assert.Equal(waits, interrupted);
            Assert.True(issued);
            Assert.Null(afterExit); // a cancel that outlived Exit aborts the listing or the wait
            Assert.Equal(2, listedAfterExit!.Length);
            Assert.True(liveAtExit >= 1, "no attempt was live when Exit was called, so its drain waited for nothing");
            Assert.Equal(attemptsAtExit, guard.CancelAttempts); // none was made once Exit had returned
        }
        finally
        {
            self.ProcessorAffinity = original;
        }

        using Process after = Process.GetCurrentProcess();
        Assert.Equal(original, after.ProcessorAffinity);
    }
}
