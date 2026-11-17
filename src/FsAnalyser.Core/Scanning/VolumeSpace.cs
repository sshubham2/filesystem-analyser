using FsAnalyser.Core.Native;

namespace FsAnalyser.Core.Scanning;

/// <summary>
/// The volume's used space for a scan of a whole drive, read once as the scan starts (SL-043). The read
/// runs on a background thread of its own that nothing in the scan waits for, so a volume that does not
/// answer (a mapped drive whose server stopped) can hold neither the scan nor its cancel (O-031). The thread
/// is handed only the completion sources it answers through: a read that never returns parks that one
/// thread and keeps no scan's memory alive.
/// </summary>
/// <remarks>
/// SL-001 added two things to the same thread. A root on a network file system gets no figure at all (a mapped drive's
/// GetDiskFreeSpaceExW answers the server volume's figures, and a share is usually one folder of that volume, so the rest
/// of the server's disk would be put down to this account), and the NTFS volume's $MFT is read beside the used figure,
/// answered before it, so the window can set that one measured part apart from the rest of the unseen space.
/// </remarks>
internal static class VolumeSpace
{
    /// <summary>
    /// Whether <paramref name="root"/> is a whole drive: one letter, a colon and a backslash, in either case,
    /// in its display form (<see cref="LongPath.ToDisplay"/>), so <c>\\?\C:\</c> is one too. A folder, a share
    /// and a volume mounted on a folder are not. This is the App's rule before SL-043, moved and not widened: a
    /// SUBST letter passes it, and gets the figure of the volume under it (m3, deferred). Only the shape: whether
    /// the letter is a network drive is asked of its handle, on the read's own thread (<see cref="Read(string)"/>).
    /// </summary>
    internal static bool IsDriveRoot(string root)
    {
        string path = LongPath.ToDisplay(root);
        return path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
    }

    /// <summary>
    /// Answers <paramref name="figure"/> for a scan of <paramref name="root"/>: with null at once when the
    /// root is not a drive root; otherwise with what <paramref name="reader"/> returns, from a background
    /// thread of its own, or with null once <paramref name="bound"/> has passed without an answer, whichever
    /// comes first. A reader that fails or throws answers null, so the figure is never faulted.
    /// </summary>
    /// <param name="reader">The read, given the root's display form, without <c>\\?\</c>; <see cref="Read(string)"/>
    /// when null. Tests substitute one that blocks.</param>
    /// <param name="bound">How long the figure waits; <see cref="ScanOptions.VolumeReadBound"/> owns the default.</param>
    /// <param name="anyRoot">Tests only: read whatever the root is, so that a folder scan can be shown to
    /// complete while the read is still stalled.</param>
    /// <param name="mft">Answered with the $MFT's size, strictly before <paramref name="figure"/> is, or with null when
    /// <paramref name="figure"/> is answered null (so it is never pending once the figure has landed). When the read lands just
    /// as <paramref name="bound"/> passes, the size can stand beside a null figure. Null when not wanted.</param>
    /// <param name="mftReader">The $MFT read, given the root as <paramref name="reader"/> is; <see cref="ReadMft"/> when
    /// null and no <paramref name="reader"/> was substituted, and none (null) when one was, so a test that substitutes
    /// the used read never reads the real disk's $MFT. Called only after the used read returned a figure.</param>
    internal static void Start(string root, TaskCompletionSource<long?> figure,
        Func<string, long?>? reader, TimeSpan bound, bool anyRoot,
        TaskCompletionSource<long?>? mft = null, Func<string, long?>? mftReader = null)
    {
        if (!anyRoot && !IsDriveRoot(root))
        {
            mft?.TrySetResult(null);
            figure.TrySetResult(null);
            return;
        }

        // The bound first, so the figures are answered even if the thread below never starts. The $MFT first, as the
        // thread does, so that a figure that has landed is never met with its $MFT still pending.
        Task.Delay(bound).ContinueWith(
            static (_, answers) =>
            {
                var (mftSource, figureSource) = ((TaskCompletionSource<long?>?, TaskCompletionSource<long?>))answers!;
                mftSource?.TrySetResult(null);
                figureSource.TrySetResult(null);
            },
            (mft, figure), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        string volume = LongPath.ToDisplay(root);
        Func<string, long?> read = reader ?? Read;
        Func<string, long?>? readMft = mftReader;
        if (readMft is null && reader is null)
            readMft = ReadMft;
        var thread = new Thread(() => Answer(figure, mft, volume, read, readMft)) { IsBackground = true, Name = "volume-used" };
        thread.Start();
    }

    /// <summary>
    /// The default read: the used bytes GetDiskFreeSpaceExW gives, or null when the volume did not answer, or when the
    /// root is on a network file system or cannot be opened to be asked (SL-001, SC-015).
    /// </summary>
    internal static long? Read(string root) => Read(root, Kernel32.IsRemoteRoot, ReadUsed);

    /// <summary>
    /// <see cref="Read(string)"/> with its two probes given, so a test can say what the root is and what the volume reports
    /// without a network drive or a real disk. A root that is remote, or that could not be asked (null), gets no figure and
    /// the used probe is never called: the rest of a server's disk is not this scan's gap.
    /// </summary>
    internal static long? Read(string root, Func<string, bool?> isRemote, Func<string, long?> used) =>
        isRemote(root) is false ? used(root) : null;

    /// <summary>The used bytes GetDiskFreeSpaceExW gives, or null when the volume did not answer.</summary>
    internal static long? ReadUsed(string root) => Kernel32.TryGetVolumeUsed(root, out long used, out _) ? used : null;

    /// <summary>The valid data length of the NTFS volume's $MFT under the root, or null (not NTFS, or it could not be read).</summary>
    internal static long? ReadMft(string root) => Kernel32.TryGetMftValidData(root, out long bytes, out _) ? bytes : null;

    // The thread's whole work. It holds the completion sources, the root and the readers, and nothing else.
    private static void Answer(TaskCompletionSource<long?> figure, TaskCompletionSource<long?>? mft, string root,
        Func<string, long?> reader, Func<string, long?>? mftReader)
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

        long? size = null;
        if (used is not null && mft is not null && mftReader is not null)
        {
            try
            {
                size = mftReader(root);
            }
            catch (Exception)
            {
                // No $MFT figure; the used figure is still answered below.
            }
        }
        mft?.TrySetResult(size);
        figure.TrySetResult(used);
    }
}
