using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// How the scan sizes files (SL-056): after the listing walk, on ProbePass's worker threads, a batch at a time, with
/// every reading applied by the scan thread once the batch's workers have finished. The probe each worker runs is
/// replaced here (ScanOptions.Probe) to hold it, fail it or alter what it reads.
/// </summary>
public class ProbePassTests
{
    private const long MiB = TempTree.MiB;

    /// <summary>
    /// The scan is Completed only once every probe is applied: no probe starts before the walk has listed every
    /// folder, and when Scan returns, every file carries its probe's figure, which here no listing could have.
    /// Four files a batch, so the last batches are applied after the first ones were.
    /// </summary>
    [Fact]
    public void TheScanEndsOnlyOnceEveryProbeIsApplied()
    {
        using var tree = new TempTree();
        var listed = new Dictionary<string, long>();
        for (int f = 0; f < 3; f++)
        {
            tree.Folder($"f{f}");
            for (int i = 0; i < 5; i++)
                listed[tree.File($@"f{f}\{i}.bin", 100 * (f + 1) + i)] = 100 * (f + 1) + i;
        }
        int started = 0, finished = 0;
        bool probedDuringTheWalk = false;
        var progress = new ScanProgress();
        var options = new ScanOptions
        {
            ProbeWorkers = 3,
            ProbeBatch = 4,
            AfterFolder = _ => probedDuringTheWalk |= Volatile.Read(ref started) > 0,
            Probe = (SynchronousIoCancel io, string path, out long endOfFile, out long allocationSize) =>
            {
                Interlocked.Increment(ref started);
                Thread.Sleep(10);
                int error = SizeProbe.Probe(io, path, out endOfFile, out allocationSize);
                endOfFile += 7; // a figure no listing has, so each one charged is visibly the probe's
                allocationSize += 4096;
                Interlocked.Increment(ref finished);
                return error;
            },
        };

        ScanResult result = Scanner.Scan(tree.Root, progress, CancellationToken.None, options);

        Scans.AssertChargedBytesAreTheTotals(result);
        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.False(probedDuringTheWalk, "a probe started before every folder was listed");
        Assert.Equal(15, Volatile.Read(ref finished));
        ScanStore store = result.Store;
        foreach ((string path, long size) in listed)
        {
            int entry = Scans.Find(result, path);
            Assert.Equal(EntryFlags.SizeProbed | EntryFlags.ProbeCorrected, store.Flags(entry));
            Assert.Equal(size + 7, store.Size(entry));
        }
        Assert.Equal((15, 15, 0), (result.Probes, result.ProbeCorrections, result.ProbeFailures));
        Assert.Equal(listed.Values.Sum() + 15 * 7, result.Totals.Bytes);
        Assert.Equal((15L, 15L), (progress.FilesSized, progress.FilesToSize));
        Assert.Equal(result.Totals.Bytes, progress.Bytes);
    }

    /// <summary>
    /// The shape the inline repeat probe once raced (C-013, removed by review M4): the first name of a hardlinked
    /// file is queued for its probe, and the listing meets the later name before that probe has even started. The
    /// later name is never probed and charges nothing, and the first name's probe, when it comes, charges the
    /// file once at its current size, to the first name's folder.
    /// </summary>
    [Fact]
    public void ALaterNameMetBeforeTheFirstNamesProbeIsNeverProbedOrCharged()
    {
        using var tree = new TempTree();
        tree.Folder("1-first");
        tree.Folder("2-second");
        string current = tree.File(@"2-second\current.bin", 1 * MiB);
        string stale = TempTree.Hardlink(tree.PathOf(@"1-first\stale.bin"), current);
        TempTree.Append(current, 63 * MiB);
        var probed = new ConcurrentQueue<string>();
        int foldersListed = 0, listedAtFirstProbe = -1;
        var options = new ScanOptions
        {
            AfterFolder = n => foldersListed = n,
            Probe = (SynchronousIoCancel io, string path, out long endOfFile, out long allocationSize) =>
            {
                Interlocked.CompareExchange(ref listedAtFirstProbe, Volatile.Read(ref foldersListed), -1);
                probed.Enqueue(path);
                Thread.Sleep(50);
                return SizeProbe.Probe(io, path, out endOfFile, out allocationSize);
            },
        };

        ScanResult result = Scans.Run(tree.Root, options);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(3, listedAtFirstProbe); // the root, 1-first and 2-second: the later name was met first
        Assert.Equal(new[] { LongPath.ToExtended(stale) }, probed);
        ScanStore store = result.Store;
        int charged = Scans.Find(result, stale), repeat = Scans.Find(result, current);
        Assert.Equal(EntryFlags.SizeProbed | EntryFlags.ProbeCorrected, store.Flags(charged));
        Assert.Equal(64 * MiB, store.ChargedSize(charged));
        Assert.Equal(EntryFlags.HardlinkDuplicate, store.Flags(repeat));
        Assert.Equal((0L, 0L), (store.ChargedSize(repeat), store.ChargedOnDisk(repeat)));
        Assert.Equal(new Rolled(64 * MiB, store.OnDisk(charged), 1), Rolled.Of(store, store.Parent(charged)));
        Assert.Equal(new Rolled(0, 0, 1), Rolled.Of(store, store.Parent(repeat)));
        Assert.Equal(new ScanTotals(Files: 2, Folders: 2, Links: 0, Bytes: 64 * MiB, BytesOnDisk: store.OnDisk(charged)), result.Totals);
        Assert.Equal(1, result.Probes);
    }

    /// <summary>
    /// A worker that throws fails the scan, loudly and promptly (review M3): the other workers stop taking files,
    /// and Scan throws once they have stopped, rather than hang or return files at their listing sizes.
    /// </summary>
    [Fact]
    public void AProbeWorkerThatThrowsFailsTheScanInsteadOfHanging()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 20; i++)
            tree.File($"{i:D2}.bin", i);
        int calls = 0;
        var options = new ScanOptions
        {
            ProbeWorkers = 4,
            Probe = (SynchronousIoCancel io, string path, out long endOfFile, out long allocationSize) =>
            {
                if (Interlocked.Increment(ref calls) == 5)
                    throw new InvalidDataException("the stand-in's fault");
                return SizeProbe.Probe(io, path, out endOfFile, out allocationSize);
            },
        };
        Exception? thrown = null;
        var scan = new Thread(() =>
        {
            try
            {
                Scanner.Scan(tree.Root, new ScanProgress(), CancellationToken.None, options);
            }
            catch (Exception e)
            {
                thrown = e;
            }
        }) { IsBackground = true };

        scan.Start();

        Assert.True(scan.Join(TimeSpan.FromSeconds(30)), "the scan was still running 30 s after a probe worker threw");
        var failure = Assert.IsType<InvalidOperationException>(thrown);
        Assert.IsType<InvalidDataException>(failure.InnerException);
    }

    /// <summary>
    /// Cancel while every worker is stuck in a call nothing can interrupt, as an open on a share that stopped
    /// answering can be (review M3): the scan ends Cancelled within the wait for its workers (1 s) and leaves them,
    /// and it applies nothing of the stuck batch. Every folder was committed, so every file is counted at its
    /// listing sizes.
    /// </summary>
    [Fact]
    public void CancelWhileWorkersAreStuckEndsTheScanCancelledWithinTheBound()
    {
        using var tree = new TempTree();
        for (int f = 0; f < 2; f++)
        {
            tree.Folder($"f{f}");
            for (int i = 0; i < 3; i++)
                tree.File($@"f{f}\{i}.bin", 10 + i);
        }
        using var release = new ManualResetEventSlim();
        using var bothStuck = new CountdownEvent(2);
        int entered = 0;
        var options = new ScanOptions
        {
            ProbeWorkers = 2,
            Probe = (SynchronousIoCancel io, string path, out long endOfFile, out long allocationSize) =>
            {
                if (Interlocked.Increment(ref entered) <= 2)
                    bothStuck.Signal();
                release.Wait(); // no cancel reaches this
                endOfFile = allocationSize = 1;
                return 0;
            },
        };
        using var cts = new CancellationTokenSource();
        ScanResult? result = null;
        var scan = new Thread(() => result = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, options)) { IsBackground = true };
        try
        {
            scan.Start();
            Assert.True(bothStuck.Wait(TimeSpan.FromSeconds(30)), "the workers never reached their probes");
            var clock = Stopwatch.StartNew();
            cts.Cancel();
            Assert.True(scan.Join(TimeSpan.FromSeconds(5)), "the scan was still waiting for its stuck workers 5 s after Cancel");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"the scan returned {clock.Elapsed.TotalSeconds:F1} s after Cancel");
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(ScanStatus.Cancelled, result!.Status);
        Scans.AssertChargedBytesAreTheTotals(result);
        Assert.Equal(0, result.Probes); // the stuck batch commits nothing
        Assert.Equal(new ScanTotals(Files: 6, Folders: 2, Links: 0, Bytes: 10 + 11 + 12 + 10 + 11 + 12, BytesOnDisk: result.Totals.BytesOnDisk), result.Totals);
        foreach (string f in new[] { "f0", "f1" })
        {
            int folder = Scans.Find(result, tree.PathOf(f));
            for (int c = 0; c < 3; c++)
                Assert.Equal(EntryFlags.None, result.Store.Flags(result.Store.FirstChild(folder) + c));
        }
    }

    /// <summary>
    /// Cancel interrupts the call a worker is blocked in, through the worker's own guard, made on its own thread
    /// (review m4): a synchronous pipe wait inside the guard's bracket, the stand-in CancellationTests uses for an
    /// unreachable share. The scan returns long before the 30 s it would wait to leave a stuck worker.
    /// </summary>
    [Fact]
    public void CancelInterruptsTheCallAWorkerIsBlockedIn()
    {
        using var tree = new TempTree();
        tree.File("only.bin", 10);
        using NamedPipeServerStream server = SynchronousIoCancelTests.NewPipe(out _);
        using var waiting = new ManualResetEventSlim();
        Exception? interrupted = null;
        var options = new ScanOptions
        {
            ProbeWorkers = 1,
            ProbeAbandonAfter = TimeSpan.FromSeconds(30),
            Probe = (SynchronousIoCancel io, string path, out long endOfFile, out long allocationSize) =>
            {
                endOfFile = allocationSize = 0;
                if (!io.Enter())
                    return SynchronousIoCancel.Aborted;
                bool issued;
                try
                {
                    waiting.Set();
                    server.WaitForConnection();
                }
                catch (Exception e)
                {
                    interrupted = e;
                }
                finally
                {
                    issued = io.Exit();
                }
                return SynchronousIoCancel.Settle(0, issued);
            },
        };
        using var cts = new CancellationTokenSource();
        ScanResult? result = null;
        var scan = new Thread(() => result = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, options)) { IsBackground = true };

        scan.Start();
        Assert.True(waiting.Wait(TimeSpan.FromSeconds(30)), "the worker never reached its probe");
        Thread.Sleep(200);
        Assert.True(scan.IsAlive, "the stand-in returned by itself: no blocked call was exercised");
        cts.Cancel();

        Assert.True(scan.Join(TimeSpan.FromSeconds(2)), "the scan was still running 2 s after Cancel: the worker's call was not interrupted");
        Assert.NotNull(interrupted);
        Assert.Equal(ScanStatus.Cancelled, result!.Status);
        Assert.Equal(0, result.Probes);
        Scans.WaitForAbortedCloses();
    }

    /// <summary>
    /// Probes run on dedicated worker threads, never the scan's, at BelowNormal priority (review M5), and each
    /// through a guard of its own: the real probe calls the guard's owner check, which throws on any other thread.
    /// </summary>
    [Fact]
    public void ProbesRunOnBackgroundWorkersBelowNormalEachWithItsOwnGuard()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 8; i++)
            tree.File($"{i}.bin", 1000 + i);
        var seen = new ConcurrentBag<(int Thread, ThreadPriority Priority, bool Background, SynchronousIoCancel Guard)>();
        var options = new ScanOptions
        {
            ProbeWorkers = 3,
            Probe = (SynchronousIoCancel io, string path, out long endOfFile, out long allocationSize) =>
            {
                seen.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.Priority, Thread.CurrentThread.IsBackground, io));
                return SizeProbe.Probe(io, path, out endOfFile, out allocationSize);
            },
        };
        int scanThread = Environment.CurrentManagedThreadId;

        ScanResult result = Scans.Run(tree.Root, options);

        Assert.Equal((8, 0), (result.Probes, result.ProbeFailures));
        Assert.Equal(8, seen.Count);
        Assert.All(seen, s =>
        {
            Assert.NotEqual(scanThread, s.Thread);
            Assert.Equal(ThreadPriority.BelowNormal, s.Priority);
            Assert.True(s.Background);
        });
        // One guard per worker thread, never shared between two.
        Assert.All(seen.GroupBy(s => s.Guard), g => Assert.Single(g.Select(s => s.Thread).Distinct()));
    }

    /// <summary>
    /// The single writer is structural (review M2): the store's writer and the probe pass refuse any thread but the
    /// one that made them, whatever a later change tries.
    /// </summary>
    [Fact]
    public void TheStoresWriterAndTheProbePassRefuseEveryOtherThread()
    {
        var owner = new ThreadOwner("The test's owner");
        owner.Check();
        var pass = new ProbePass(CancellationToken.None, SizeProbe.Probe, 1, TimeSpan.FromSeconds(1));
        Exception? fromOwner = null, fromPass = null;
        var other = new Thread(() =>
        {
            fromOwner = Record.Exception(owner.Check);
            fromPass = Record.Exception(() => pass.Run([], [], 0));
        });
        other.Start();
        other.Join();

        Assert.IsType<InvalidOperationException>(fromOwner);
        Assert.IsType<InvalidOperationException>(fromPass);
        Assert.Equal(BatchOutcome.Probed, pass.Run([], [], 0)); // and on its own thread it runs
    }

    /// <summary>
    /// A file deleted between being listed and being probed keeps its listing sizes and is reported apart from a
    /// refusal (review m5): not ProbeFailed, not counted in ProbeFailures.
    /// </summary>
    [Fact]
    public void AFileDeletedBeforeItsProbeKeepsItsListingSizesAndIsNoFailure()
    {
        using var tree = new TempTree();
        string kept = tree.File("kept.bin", 10);
        string gone = tree.File("gone.bin", 20);
        var options = new ScanOptions
        {
            Probe = (SynchronousIoCancel io, string path, out long endOfFile, out long allocationSize) =>
            {
                if (path.EndsWith(@"\gone.bin", StringComparison.Ordinal))
                    File.Delete(path);
                return SizeProbe.Probe(io, path, out endOfFile, out allocationSize);
            },
        };

        ScanResult result = Scans.Run(tree.Root, options);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal((2, 0, 1), (result.Probes, result.ProbeFailures, result.VanishedBeforeProbe));
        int entry = Scans.Find(result, gone);
        Assert.Equal(EntryFlags.None, result.Store.Flags(entry));
        Assert.Equal(20, result.Store.Size(entry));
        Assert.Equal(EntryFlags.SizeProbed, result.Store.Flags(Scans.Find(result, kept)));
        Assert.Equal(30, result.Totals.Bytes);
    }

    /// <summary>
    /// An entry with no usable file ID, as on a volume without the extended listing class, is always a first name
    /// (review m5): each name of a hardlinked file is probed and charged, the limit FileIdsAvailable reports.
    /// </summary>
    [Fact]
    public void AnEntryWithNoFileIdIsProbedAsAFirstName()
    {
        using var tree = new TempTree();
        tree.Folder("a");
        tree.Folder("b");
        string first = tree.File(@"a\x.bin", 1000);
        string second = TempTree.Hardlink(tree.PathOf(@"b\y.bin"), first);

        ScanResult result = Scans.Run(tree.Root, new ScanOptions { ForceFullDirectoryClass = true });

        Assert.False(result.FileIdsAvailable);
        Assert.Equal(2, result.Probes);
        foreach (string path in new[] { first, second })
            Assert.Equal(EntryFlags.SizeProbed, result.Store.Flags(Scans.Find(result, path)));
        Assert.Equal(2000, result.Totals.Bytes);
    }
}
