using System.Collections.Concurrent;
using System.Diagnostics;
using FsAnalyser.App;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The drive picker's scheduling (SC-052), run on a pump of its own with reads that answer, return nothing, throw, or block on
/// an event: no real drive is touched (a mapped drive whose server is down keeps its type and blocks a root call for minutes, so
/// a test that opened one would depend on that host; lessons #878, #879). The pump stands in for the window's dispatcher: one
/// thread, first in first out. xUnit 2.9 gives every test method a synchronization context of its own that forwards to the
/// pool, so every body here runs as a callback on the pump and asserts that it is the current context.
/// </summary>
public sealed class DriveReadsTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_read_that_does_not_answer_holds_no_other_drive()
    {
        // O-001: three reads, the middle one blocked past the bound. The barrier needs all three in at once, so a read that
        // waited behind another's thread (or the pool) would never reach it.
        using var pump = new Pump();
        using var never = new ManualResetEventSlim();
        using var together = new Barrier(3);
        try
        {
            await pump.RunAsync(async () =>
            {
                var arrived = new List<(int Index, string Value)>();
                Func<string?> Meet(string answer, bool block) => () =>
                {
                    if (!together.SignalAndWait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("the reads did not run at the same time");
                    if (block)
                        never.Wait();
                    return answer;
                };
                Func<string?>[] reads = [Meet("c:", false), Meet("z:", true), Meet("d:", false)];
                var clock = Stopwatch.StartNew();
                await DriveReads.Start(reads, (i, v) => arrived.Add((i, v)), TimeSpan.FromMilliseconds(300));
                clock.Stop();

                Assert.Equal([0, 2], arrived.Select(a => a.Index).Order().ToArray());
                Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10));
            });
        }
        finally
        {
            never.Set();
        }
    }

    [Fact]
    public async Task The_wait_ends_only_after_every_answer_has_been_delivered()
    {
        // O-002: every read answers at once. The window's status changes when the awaited call resumes and the test hook's hold
        // on the report releases at that change, so no answer may still be queued then.
        using var pump = new Pump();
        await pump.RunAsync(async () =>
        {
            var arrived = new List<int>();
            var onPump = new List<bool>();
            Func<string?>[] reads = [() => "a", () => "b", () => "c", () => "d"];
            var clock = Stopwatch.StartNew();
            await DriveReads.Start(reads, (i, _) => { arrived.Add(i); onPump.Add(pump.IsPumpThread); }, Long);
            clock.Stop();

            Assert.Equal([0, 1, 2, 3], arrived.Order().ToArray());
            Assert.All(onPump, Assert.True);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"it resumed after {clock.Elapsed}, not when the last read was back");
        });
    }

    [Fact]
    public async Task A_drive_that_answers_after_the_wait_arrives_on_the_pump()
    {
        // O-003: the wait ends with one read still blocked; releasing it gives an ordinary arrival, on the pump, not on the read's thread.
        using var pump = new Pump();
        using var release = new ManualResetEventSlim();
        int readThread = 0;
        try
        {
            await pump.RunAsync(async () =>
            {
                var late = new TaskCompletionSource<(int Index, string Value, bool OnPump)>(TaskCreationOptions.RunContinuationsAsynchronously);
                var arrived = new List<int>();
                Func<string?>[] reads = [() => "a", () => { readThread = Environment.CurrentManagedThreadId; release.Wait(); return "b"; }];
                await DriveReads.Start(reads, (i, v) =>
                {
                    arrived.Add(i);
                    if (i == 1)
                        late.TrySetResult((i, v, pump.IsPumpThread));
                }, TimeSpan.FromMilliseconds(300));
                Assert.Equal([0], arrived);

                release.Set();
                var answer = await late.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal((1, "b", true), answer);
                Assert.NotEqual(readThread, pump.ThreadId);
            });
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task When_no_drive_answers_in_time_the_wait_still_ends_with_nothing()
    {
        // O-004: no read ever counts itself back, so only the bound's timer can end the wait.
        using var pump = new Pump();
        using var never = new ManualResetEventSlim();
        try
        {
            await pump.RunAsync(async () =>
            {
                int arrived = 0;
                Func<string?>[] reads = [() => { never.Wait(); return "a"; }, () => { never.Wait(); return "b"; }];
                var clock = Stopwatch.StartNew();
                await DriveReads.Start(reads, (_, _) => arrived++, TimeSpan.FromMilliseconds(300));
                clock.Stop();

                Assert.Equal(0, arrived);
                Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10));
            });
        }
        finally
        {
            never.Set();
        }
    }

    [Fact]
    public async Task A_drive_that_is_not_ready_arrives_nowhere_and_still_counts_as_back()
    {
        // O-005: a null is a drive that was not ready (or stopped being). It must not reach the caller, and it must not make the wait run to the bound.
        using var pump = new Pump();
        await pump.RunAsync(async () =>
        {
            var arrived = new List<int>();
            Func<string?>[] reads = [() => "a", () => null, () => "c"];
            var clock = Stopwatch.StartNew();
            await DriveReads.Start(reads, (i, _) => arrived.Add(i), Long);
            clock.Stop();

            Assert.Equal([0, 2], arrived.Order().ToArray());
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"it waited {clock.Elapsed} for a read that was already back");
        });
    }

    [Fact]
    public async Task A_PC_with_no_drive_to_read_is_done_at_once()
    {
        // O-006: with no read there is no count to reach zero, so the empty list has its own way out.
        using var pump = new Pump();
        await pump.RunAsync(async () =>
        {
            int arrived = 0;
            var clock = Stopwatch.StartNew();
            await DriveReads.Start(Array.Empty<Func<string?>>(), (_, _) => arrived++, Long);
            clock.Stop();

            Assert.Equal(0, arrived);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"it took {clock.Elapsed} with nothing to read");
        });
    }

    [Fact]
    public void Buttons_keep_the_order_of_the_drive_list_whatever_order_the_answers_come_in()
    {
        // O-007: the drives answer in the order 2, 0, 3, 1.
        var shown = new List<int>();
        int[] positions = new[] { 2, 0, 3, 1 }.Select(index => DriveReads.Place(shown, index)).ToArray();

        Assert.Equal([0, 0, 2, 1], positions);
        Assert.Equal([0, 1, 2, 3], shown);
    }

    [Fact]
    public async Task An_unexpected_failure_while_reading_faults_the_wait()
    {
        // O-008: today it reaches the dispatcher from LoadDrives. Awaiting the wait must throw it, and it must not be raised a second time on the pump.
        using var pump = new Pump();
        await pump.RunAsync(async () =>
        {
            Func<string?>[] reads = [() => "a", () => throw new InvalidOperationException("the read failed")];
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => DriveReads.Start(reads, (_, _) => { }, Long));

            Assert.Equal("the read failed", e.Message);
            Assert.Empty(pump.Faults);
        });
    }

    [Fact]
    public async Task An_unexpected_failure_from_a_drive_that_answered_late_is_raised_on_the_pump()
    {
        // O-009: the wait is over and nobody awaits it any more. The exception is raised again on the pump, where the dispatcher would
        // get it, not dropped and not thrown on the read's own thread (which would end the process outside the dispatcher).
        using var pump = new Pump();
        using var release = new ManualResetEventSlim();
        try
        {
            await pump.RunAsync(async () =>
            {
                Func<string?>[] reads = [() => "a", () => { release.Wait(); throw new InvalidOperationException("the late read failed"); }];
                Task wait = DriveReads.Start(reads, (_, _) => { }, TimeSpan.FromMilliseconds(300));
                await wait;
                Assert.True(wait.IsCompletedSuccessfully);

                release.Set();
                Exception fault = await pump.NextFault.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsType<InvalidOperationException>(fault);
                Assert.Equal("the late read failed", fault.Message);
                Assert.True(wait.IsCompletedSuccessfully);
            });
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void Starting_without_a_synchronization_context_refuses_before_any_read_runs()
    {
        // m4: xUnit installs a context for the test method, so this runs on a thread of its own, which has none. Whatever it
        // throws is kept for the test thread to judge: an exception left on a raw thread would end the test host.
        Exception? caught = null;
        int started = 0;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                Func<string?>[] reads = [() => { Interlocked.Increment(ref started); return "a"; }];
                _ = DriveReads.Start(reads, (_, _) => { }, Long);
            }
            catch (Exception e)
            {
                caught = e;
            }
        })
        { IsBackground = true };
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Start did not return");
        Assert.IsType<InvalidOperationException>(caught);
        Assert.Equal(0, Volatile.Read(ref started));
    }

    /// <summary>One thread, first in first out, standing in for the window's dispatcher.</summary>
    private sealed class Pump : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;
        private readonly TaskCompletionSource<Exception> _firstFault = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Pump()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "DriveReadsTests pump" };
            _thread.Start();
        }

        /// <summary>Exceptions a posted callback let escape, as the dispatcher would have received them.</summary>
        public ConcurrentQueue<Exception> Faults { get; } = new();

        public Task<Exception> NextFault => _firstFault.Task;

        public int ThreadId => _thread.ManagedThreadId;

        public bool IsPumpThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

        // A late read may post after its test is over: that is dropped, not thrown on the read's thread.
        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                _queue.Add((d, state));
            }
            catch (InvalidOperationException)
            {
                // the queue is closed (an ObjectDisposedException is one too)
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        /// <summary>
        /// Runs the body as a callback on the pump, with the pump as the current context, and returns when it is done: what the body
        /// throws (a failed assertion included) is thrown here, and a body that does not finish in 30 s fails instead of hanging.
        /// </summary>
        public async Task RunAsync(Func<Task> body)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try
                {
                    Assert.Same(this, Current);
                    await body();
                    done.TrySetResult();
                }
                catch (Exception e)
                {
                    done.TrySetException(e);
                }
            }, null);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        public void Dispose() => _queue.CompleteAdding();

        private void Loop()
        {
            SetSynchronizationContext(this);
            foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                catch (Exception e)
                {
                    Faults.Enqueue(e);
                    _firstFault.TrySetResult(e);
                }
            }
        }
    }
}
