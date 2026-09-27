using System.Diagnostics;

namespace FsAnalyser.Core.Scanning;

/// <summary>What one probe read: 0 with the file's own sizes, or the Win32 error that stopped it.</summary>
internal readonly record struct ProbeReading(int Error, long EndOfFile, long AllocationSize);

/// <summary>How a batch of probes ended.</summary>
internal enum BatchOutcome
{
    /// <summary>Every file of the batch was probed, and its reading is in place.</summary>
    Probed,

    /// <summary>Cancel reached the batch. Its readings are incomplete and none of them may be applied.</summary>
    Cancelled,
}

/// <summary>
/// Probes files a batch at a time on dedicated worker threads, once the scan's listing walk is over (SL-056).
/// </summary>
/// <remarks>
/// The scan lists everything first on its own thread, then probes, then applies: the shape the risk spike
/// measured on C: (probe-all-as-built-route-drive-time: 100.7 s listing, then 87.6 s probing every file with 16
/// workers), chosen over overlapping the two (review M-add-1).
/// <para>This class never sees the store. The scan thread hands it a batch of paths and gets a reading for each
/// back, and it applies them itself, once every worker of the batch has finished. A worker has nothing to apply
/// with, so the store keeps its one writer by construction (reviews M2, M-add-1).</para>
/// <para>Each worker is a thread of its own, at BelowNormal priority so the window's thread stays ahead (review M5),
/// with its own <see cref="SynchronousIoCancel"/> made on that thread (review m4): Cancel interrupts the probe each
/// worker is blocked in.</para>
/// <para>Cancel: a batch Cancel reaches ends Cancelled, and the scan applies none of it, as a folder Cancel aborts
/// commits nothing. Workers still inside a call <c>abandonAfter</c> after Cancel, which an uncancellable open on
/// a share that stopped answering can be, are left to end on their own (review M3): they hold this batch's
/// arrays, never the store, and write nothing else. A pass that left workers behind runs no further batch.</para>
/// <para>A worker that throws fails the batch: the others stop taking files, and <see cref="Run"/> throws once
/// they have stopped, so the scan fails loudly instead of leaving files at their listing figures.</para>
/// </remarks>
internal sealed class ProbePass(CancellationToken ct, ProbeFile probe, int workers, TimeSpan abandonAfter)
{
    /// <summary>
    /// Files per batch. Bounds what is held at once: the paths and readings of one batch (a few MiB), where one
    /// batch for the whole drive would hold about 100 MiB at 4M files, most of the 1 GiB budget's headroom (SL-042).
    /// </summary>
    public const int BatchSize = 32 * 1024;

    /// <summary>One thread fewer than the machine has, for the window's, and never more than 15.</summary>
    public static int DefaultWorkers => Math.Clamp(Environment.ProcessorCount - 1, 1, 15);

    /// <summary>How long a cancelled batch waits for workers stuck in a call before it leaves them.</summary>
    public static readonly TimeSpan DefaultAbandonAfter = TimeSpan.FromSeconds(1);

    private readonly ThreadOwner _owner = new("A probe pass");
    private readonly int _workers = workers >= 1 ? workers : throw new ArgumentOutOfRangeException(nameof(workers), workers, "At least one worker.");
    private bool _abandoned;

    /// <summary>The longest single probe of every batch so far.</summary>
    public TimeSpan LongestProbe { get; private set; }

    /// <summary>
    /// Probes <paramref name="paths"/>[0..<paramref name="count"/>) and puts each one's reading at the same index
    /// of <paramref name="readings"/>. Returns once every worker has finished, or after Cancel, once they have or
    /// once the wait for them has run out.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called off the thread that made the pass, or after a batch
    /// whose workers were left behind; or a worker threw, which is the inner exception.</exception>
    public BatchOutcome Run(string[] paths, ProbeReading[] readings, int count)
    {
        _owner.Check();
        if (_abandoned)
            throw new InvalidOperationException("A probe pass that left workers behind after Cancel runs no further batch.");
        if (ct.IsCancellationRequested)
            return BatchOutcome.Cancelled;
        if (count == 0)
            return BatchOutcome.Probed;

        int started = Math.Min(_workers, count);
        var batch = new Batch(paths, readings, count, ct, probe, new CountdownEvent(started));
        for (int i = 0; i < started; i++)
        {
            try
            {
                new Thread(batch.Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "scan-probe" }.Start();
            }
            catch (Exception e)
            {
                batch.Fail(e);
                batch.Done.Signal(started - i); // for the workers that will never start
                break;
            }
        }

        if (!WaitForWorkers(batch.Done))
        {
            _abandoned = true;
            return BatchOutcome.Cancelled;
        }
        batch.Done.Dispose();
        if (batch.Fault is { } fault)
            throw new InvalidOperationException($"A probe worker failed: {fault.Message} The scan stops rather than leave files at their listing sizes.", fault);

        LongestProbe = TimeSpan.FromTicks(Math.Max(LongestProbe.Ticks, Stopwatch.GetElapsedTime(0, batch.LongestTicks).Ticks));
        return batch.Probed == count && !ct.IsCancellationRequested ? BatchOutcome.Probed : BatchOutcome.Cancelled;
    }

    // Unbounded while nobody cancels. After Cancel, bounded: a worker stuck in a call Cancel cannot interrupt is left.
    private bool WaitForWorkers(CountdownEvent done)
    {
        try
        {
            done.Wait(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return done.Wait(abandonAfter);
        }
    }

    // One batch's shared state. Workers take the next index until none is left, Cancel is requested or one of them
    // failed; each writes only its own indices' readings.
    private sealed class Batch(string[] paths, ProbeReading[] readings, int count, CancellationToken ct, ProbeFile probe, CountdownEvent done)
    {
        private int _next;
        private int _probed;
        private long _longest;
        private Exception? _fault;

        public CountdownEvent Done => done;

        public int Probed => Volatile.Read(ref _probed);

        public long LongestTicks => Interlocked.Read(ref _longest);

        public Exception? Fault => Volatile.Read(ref _fault);

        public void Fail(Exception e) => Interlocked.CompareExchange(ref _fault, e, null);

        public void Work()
        {
            try
            {
                // Made here, on this worker's thread: a guard interrupts the calls of the thread that made it.
                using var io = new SynchronousIoCancel(ct);
                long longest = 0;
                while (Fault is null && !ct.IsCancellationRequested)
                {
                    int i = Interlocked.Increment(ref _next) - 1;
                    if (i >= count)
                        break;
                    long start = Stopwatch.GetTimestamp();
                    int error = probe(io, paths[i], out long endOfFile, out long allocationSize);
                    longest = Math.Max(longest, Stopwatch.GetTimestamp() - start);
                    readings[i] = new ProbeReading(error, endOfFile, allocationSize);
                    Interlocked.Increment(ref _probed);
                }
                for (long seen = Interlocked.Read(ref _longest); longest > seen; seen = Interlocked.Read(ref _longest))
                {
                    if (Interlocked.CompareExchange(ref _longest, longest, seen) == seen)
                        break;
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
            finally
            {
                done.Signal();
            }
        }
    }
}
