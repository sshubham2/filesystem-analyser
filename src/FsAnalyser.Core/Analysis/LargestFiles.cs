using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Analysis;

/// <summary>One row of the largest-files list: the entry, its full path, and the bytes it is charged.</summary>
public readonly record struct LargestFile(int Index, string Path, long ChargedSize, long ChargedOnDisk);

/// <summary>
/// The largest single files anywhere under a scan's root (SL-040), ranked by the space each takes on disk.
/// </summary>
/// <remarks>
/// One pass over the store into a heap bounded at the count: ORDER BY on-disk DESC, logical DESC, index ASC
/// LIMIT count. The index makes the order total, and it follows the scan's single-threaded depth-first
/// walk, so two scans of an unchanged tree give the same list.
/// <para>A file qualifies when it is charged any bytes. A link, a later name of a hardlinked file and an
/// empty file are charged nothing, so they never appear, and a hardlinked file appears once, under the name
/// the scan charged. A file with a logical size and nothing on disk (sparse, online-only) still qualifies,
/// and ranks below every file that takes space.</para>
/// <para>Sizes are read only through <see cref="ScanStore.ChargedSize"/> and
/// <see cref="ScanStore.ChargedOnDisk"/>, and nothing here asks the disk again (architecture rule in
/// .taad/fitness.yaml). Call it on the store of a returned <c>ScanResult</c>: the scan writes the store on
/// its own thread and has finished with it by then, Completed or Cancelled.</para>
/// </remarks>
public static class LargestFiles
{
    /// <summary>
    /// Up to <paramref name="count"/> files, largest first. Fewer when fewer files qualify; none for 0.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static IReadOnlyList<LargestFile> Top(ScanStore store, int count)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            return [];

        // A min-heap: its root is the smallest file kept, and a larger one replaces it. The index is
        // negated so that among equal sizes the later entry is the smaller, and leaves first.
        var kept = new PriorityQueue<int, (long OnDisk, long Size, int NegatedIndex)>(count);
        int entries = store.Count;
        for (int i = 0; i < entries; i++)
        {
            if (store.Kind(i) != EntryKind.File)
                continue;
            long onDisk = store.ChargedOnDisk(i);
            long size = store.ChargedSize(i);
            if (onDisk == 0 && size == 0)
                continue;

            if (kept.Count < count)
                kept.Enqueue(i, (onDisk, size, -i));
            else
                kept.EnqueueDequeue(i, (onDisk, size, -i)); // gives the new one straight back when it is not larger
        }

        var top = new LargestFile[kept.Count];
        for (int k = top.Length - 1; k >= 0; k--)
        {
            int index = kept.Dequeue();
            top[k] = new LargestFile(index, store.GetPath(index), store.ChargedSize(index), store.ChargedOnDisk(index));
        }
        return top;
    }
}
