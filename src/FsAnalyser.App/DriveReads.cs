using System.Runtime.ExceptionServices;

namespace FsAnalyser.App;

/// <summary>
/// How the drive picker reads its drives (SC-052): one background thread per drive, so a drive whose server does not
/// answer holds no other drive's button, and the answers come back to the caller's thread one at a time. Pure: no WPF
/// and no device in it, so the default suite runs it on a pump of its own with reads that block on an event (the rule
/// 'the drive-read scheduler never touches a real drive' keeps it so). The window's LoadDrives gives it the reads.
/// </summary>
internal static class DriveReads
{
    /// <summary>
    /// Runs every read on a thread of its own and calls <paramref name="arrived"/>(index, value) on the caller's
    /// synchronization context for each read that answers, in the order they answer; a read that returns null (a drive
    /// that is not ready) arrives nowhere. The returned task completes, on that context, when every read is back or
    /// after <paramref name="wait"/>, whichever is first: a read still running then arrives later, as an ordinary
    /// arrival, and nothing is cancelled (a drive call cannot be), so its thread lives until the call returns.
    /// A read that throws before then faults the task with its exception. One that throws after it has the exception
    /// raised again on the context, where an unhandled one reaches the dispatcher as it would have from the caller.
    /// Throws InvalidOperationException, at once, when there is no context to come back to.
    /// </summary>
    /// <remarks>
    /// Every read's outcome is posted to the context before that read counts itself back, and the completion is posted
    /// after the last count, so with a first-in-first-out context every answer that came in before the completion has been
    /// delivered by the time the awaiting caller resumes. The window's DriveStatus changes at that moment, and the test
    /// hook's hold on the report releases at that change. The hold is also protected by priority: the posts arrive at the
    /// priority of the context Start was called on, which for the window's LoadDrives is the Loaded handler's (Send, in an
    /// off-screen probe), above the hook's Render-priority check. A release that is short by the same number of
    /// buttons in every run could not be caught by a window case. No Task.WhenAny and no WaitAsync: the project rule
    /// 'Cancel never abandons the scan thread' forbids them in the App, and the completion is a source set on the context.
    /// </remarks>
    public static Task Start<T>(IReadOnlyList<Func<T?>> reads, Action<int, T> arrived, TimeSpan wait) where T : class
    {
        SynchronizationContext context = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Drives are read on their own threads and answer on the caller's synchronization context; there is none.");
        if (reads.Count == 0)
            return Task.CompletedTask;

        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopTimer = new CancellationTokenSource();
        int pending = reads.Count;

        void Settle() => context.Post(_ =>
        {
            settled.TrySetResult();
            stopTimer.Cancel();
        }, null);

        for (int i = 0; i < reads.Count; i++)
        {
            int index = i;
            Func<T?> read = reads[i];
            new Thread(() =>
            {
                try
                {
                    T? value = read();
                    if (value is not null)
                        context.Post(_ => arrived(index, value), null);
                }
                catch (Exception e)
                {
                    context.Post(_ =>
                    {
                        if (!settled.TrySetException(e))
                            ExceptionDispatchInfo.Capture(e).Throw();
                    }, null);
                }
                if (Interlocked.Decrement(ref pending) == 0)
                    Settle();
            })
            { IsBackground = true, Name = $"FsAnalyser drive read {index}" }.Start();
        }

        Task.Delay(wait, stopTimer.Token).ContinueWith(_ => Settle(), CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        return settled.Task;
    }

    /// <summary>
    /// Where the read at <paramref name="index"/> goes among the reads shown so far, so that the buttons stay in the order
    /// of the drive list however the answers come in: inserts it into <paramref name="shown"/>, which stays sorted, and
    /// returns its position. An index is placed once; placing one twice is a caller's mistake and is not guarded.
    /// </summary>
    public static int Place(List<int> shown, int index)
    {
        int at = shown.BinarySearch(index);
        if (at < 0)
            at = ~at;
        shown.Insert(at, index);
        return at;
    }
}
