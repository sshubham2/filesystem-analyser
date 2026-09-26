using FsAnalyser.Core.Native;

namespace FsAnalyser.Core.Scanning;

/// <summary>
/// The volume's used space for a scan of a whole drive, read once as the scan starts (SL-043). The read
/// runs on a background thread of its own that nothing in the scan waits for, so a volume that does not
/// answer (a mapped drive whose server stopped) can hold neither the scan nor its cancel (O-031). The thread
/// is handed only the completion source it answers through: a read that never returns parks that one
/// thread and keeps no scan's memory alive.
/// </summary>
internal static class VolumeSpace
{
    private const string Extended = @"\\?\";

    /// <summary>
    /// How long the figure waits for the volume before it is given up as null. A local volume answers in
    /// well under a millisecond; on a share that stopped answering the call waits as long as the SMB client
    /// does (21 to 66 s measured), and the figure does not wait that long. The thread itself stays parked
    /// until the call returns.
    /// </summary>
    internal static readonly TimeSpan DefaultBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether <paramref name="root"/> is a whole drive: one letter, a colon and a backslash, in either case,
    /// after an optional <c>\\?\</c>. A folder, a share and a volume mounted on a folder are not. This is the
    /// App's rule before SL-043, moved and not widened: a SUBST letter passes it, and gets the figure of the
    /// volume under it (m3, deferred).
    /// </summary>
    internal static bool IsDriveRoot(string root)
    {
        ReadOnlySpan<char> path = root.AsSpan();
        if (path.StartsWith(Extended, StringComparison.Ordinal))
            path = path[Extended.Length..];
        return path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
    }

    /// <summary>
    /// Answers <paramref name="figure"/> for a scan of <paramref name="root"/>: with null at once when the
    /// root is not a drive root; otherwise with what <paramref name="reader"/> returns, from a background
    /// thread of its own, or with null once <paramref name="bound"/> has passed without an answer, whichever
    /// comes first. A reader that fails or throws answers null, so the figure is never faulted.
    /// </summary>
    /// <param name="reader">The read, given the root without <c>\\?\</c>; <see cref="Read"/> by default.
    /// Tests substitute one that blocks.</param>
    /// <param name="anyRoot">Tests only: read whatever the root is, so that a folder scan can be shown to
    /// complete while the read is still stalled.</param>
    internal static void Start(string root, TaskCompletionSource<long?> figure,
        Func<string, long?>? reader = null, TimeSpan? bound = null, bool anyRoot = false)
    {
        if (!anyRoot && !IsDriveRoot(root))
        {
            figure.TrySetResult(null);
            return;
        }

        // The bound first, so the figure is answered even if the thread below never starts.
        Task.Delay(bound ?? DefaultBound).ContinueWith(
            static (_, source) => ((TaskCompletionSource<long?>)source!).TrySetResult(null), figure,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        string volume = root.StartsWith(Extended, StringComparison.Ordinal) ? root[Extended.Length..] : root;
        Func<string, long?> read = reader ?? Read;
        var thread = new Thread(() => Answer(figure, volume, read)) { IsBackground = true, Name = "volume-used" };
        thread.Start();
    }

    /// <summary>The default read: the used bytes GetDiskFreeSpaceExW gives, or null when the volume did not answer.</summary>
    internal static long? Read(string root) => Kernel32.TryGetVolumeUsed(root, out long used, out _) ? used : null;

    // The thread's whole work. It holds the completion source, the root and the reader, and nothing else.
    private static void Answer(TaskCompletionSource<long?> figure, string root, Func<string, long?> reader)
    {
        long? used = null;
        try
        {
            used = reader(root);
        }
        catch (Exception)
        {
            // A read that failed is no figure. Never a faulted one, and never an exception on this thread,
            // which would end the process.
        }
        figure.TrySetResult(used);
    }
}
