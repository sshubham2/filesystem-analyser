using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Xunit.Sdk;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// Runs a call that may block on something outside the test (a scan of a cloud folder) on a thread of its own,
/// and bounds how long it may go on once its token is cancelled (SL-049, M2). One still running
/// <c>grace</c> after the cancel is one Cancel did not free: a regressed guard, or a read the guard does not
/// cover. Then <c>free</c> releases it by force (the cloud rig kills its provider, which ends the blocked call
/// in about 15 ms, SL-049 spike 4) and the run fails saying so. So no such call hangs the test process until
/// cldflt gives up on the callback 60 s later, and none can pass that way.
/// </summary>
internal static class ScanDeadline
{
    /// <summary>A token nobody has cancelled after this long is cancelled here, so the bound always applies.</summary>
    private static readonly TimeSpan Uncancelled = TimeSpan.FromMinutes(5);

    /// <summary>How long the call is given to return once it was freed by force.</summary>
    private static readonly TimeSpan AfterFree = TimeSpan.FromSeconds(10);

    /// <summary>What <c>whileRunning</c> can see of the call.</summary>
    public sealed class Watch
    {
        private readonly ManualResetEventSlim _ended = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public bool IsRunning => !_ended.IsSet;

        /// <summary>Since the call started.</summary>
        public TimeSpan Elapsed => _clock.Elapsed;

        internal WaitHandle EndedHandle => _ended.WaitHandle;

        internal void End() => _ended.Set();

        internal bool WaitEnded(TimeSpan timeout) => _ended.Wait(timeout);
    }

    /// <summary>
    /// Starts <paramref name="call"/>, runs <paramref name="whileRunning"/> on this thread, then waits for the
    /// call until <paramref name="grace"/> after <paramref name="cts"/> is cancelled. If <paramref name="whileRunning"/>
    /// throws, the token is cancelled, the same bound applies, and its exception is the one thrown.
    /// </summary>
    /// <exception cref="XunitException">The call was still running <paramref name="grace"/> after the cancel.</exception>
    public static T Run<T>(Func<T> call, CancellationTokenSource cts, TimeSpan grace, Action free, Action<Watch>? whileRunning = null)
    {
        var watch = new Watch();
        T? result = default;
        ExceptionDispatchInfo? callError = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = call();
            }
            catch (Exception e)
            {
                callError = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                watch.End();
            }
        }) { IsBackground = true, Name = "deadline-call" };
        thread.Start();

        ExceptionDispatchInfo? watchError = null;
        try
        {
            whileRunning?.Invoke(watch);
        }
        catch (Exception e)
        {
            watchError = ExceptionDispatchInfo.Capture(e);
            cts.Cancel();
        }

        if (WaitHandle.WaitAny([watch.EndedHandle, cts.Token.WaitHandle], Uncancelled) == WaitHandle.WaitTimeout)
            cts.Cancel();
        if (!watch.WaitEnded(grace))
        {
            var sinceFree = Stopwatch.StartNew();
            free();
            bool freed = watch.WaitEnded(AfterFree);
            string ending = freed
                ? $"It returned {sinceFree.Elapsed.TotalMilliseconds:F0} ms after it was freed by force."
                : $"It was still blocked {AfterFree.TotalSeconds:F0} s after it was freed by force.";
            string before = watchError is null ? "" : $"\nBefore that: {watchError.SourceException.Message}";
            throw new XunitException(
                $"The call was still running {grace.TotalSeconds:0.###} s after Cancel, {watch.Elapsed.TotalSeconds:F1} s after it started: Cancel did not free it. {ending}{before}");
        }

        watchError?.Throw();
        callError?.Throw();
        return result!;
    }
}
