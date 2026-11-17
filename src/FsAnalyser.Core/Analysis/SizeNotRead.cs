using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Analysis;

/// <summary>
/// The space the scan charged to files whose own size it could not read (SL-001): the files that refused the open or the query
/// that reads a file's true size, so the scan kept the size the folder listing gave. Those bytes are inside what the scan
/// counted (<c>ScanTotals.BytesOnDisk</c>), so they are never part of the space it did not see, and the coverage panel reports
/// them apart from it.
/// </summary>
/// <remarks>
/// One pass over the store of a returned scan, the cost of the kinds breakdown. It adds up what the store charges
/// (<see cref="ScanStore.ChargedOnDisk"/>), so a later name of a hardlinked file adds nothing, as everywhere else. A file is one
/// of these exactly where <c>Scanner.Correct</c> counted a probe failure, which is also where it stamps
/// <see cref="EntryFlags.ProbeFailed"/>: a file that vanished before its size was read is not (it is no longer on the volume), so
/// the count the window shows is <c>ScanResult.ProbeFailures</c>, and the panel and the note beside it cannot give two counts.
/// </remarks>
public static class SizeNotRead
{
    /// <summary>The on-disk bytes charged to the files in <paramref name="store"/> flagged <see cref="EntryFlags.ProbeFailed"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public static long OnDisk(ScanStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        long bytes = 0;
        int entries = store.Count;
        for (int i = 0; i < entries; i++)
        {
            if (store.Kind(i) == EntryKind.File && (store.Flags(i) & EntryFlags.ProbeFailed) != 0)
                bytes += store.ChargedOnDisk(i);
        }
        return bytes;
    }
}
