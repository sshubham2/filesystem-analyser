namespace FsAnalyser.Core.Store;

/// <summary>One child of a folder, as <see cref="ScanStore.ChildrenLargestFirst"/> reads it.</summary>
/// <param name="Entry">The child's index.</param>
/// <param name="OnDisk">What it ranks by, <see cref="ScanStore.RolledOnDisk"/>: a folder's total below it, a
/// file's charged on-disk bytes (0 for a later name of a hardlinked file), 0 for a link.</param>
/// <param name="Share">OnDisk over the folder's RolledOnDisk, and exactly 0 when that is 0.</param>
public readonly record struct ChildRow(int Entry, long OnDisk, double Share);

// The rolled-up reads. Every value here was posted by an append or a correction, so a read never walks
// below the entry except to list one folder's children.
public sealed partial class ScanStore
{
    /// <summary>
    /// Logical bytes at or below the entry, each counted once. A folder: the charged Size of every file
    /// below it at any depth, and nothing of its own stated size. A file: its <see cref="ChargedSize"/>.
    /// A link: 0. Links and folders that could not be read add nothing to any folder above them.
    /// </summary>
    public long RolledSize(int entry)
    {
        Page page = At(entry, out int i);
        return page.Kind[i] == (byte)EntryKind.Folder ? page.RolledSize[i]
            : Charges(page, i) ? Volatile.Read(ref page.Size[i]) : 0;
    }

    /// <summary>On-disk bytes at or below the entry, each counted once; see <see cref="RolledSize"/>.</summary>
    public long RolledOnDisk(int entry)
    {
        Page page = At(entry, out int i);
        return OnDiskAtOrBelow(page, i);
    }

    /// <summary>
    /// Files at or below the entry. A folder: every file below it at any depth, with every name of a
    /// hardlinked file counted. A file: 1, a later name of a hardlinked file included. A link: 0. So a
    /// folder's value is the sum of its children's, as its <see cref="RolledSize"/> and
    /// <see cref="RolledOnDisk"/> are.
    /// </summary>
    public int FilesBelow(int entry)
    {
        Page page = At(entry, out int i);
        return page.Kind[i] switch
        {
            (byte)EntryKind.Folder => page.FilesBelow[i],
            (byte)EntryKind.File => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// A folder's children, largest first by <see cref="RolledOnDisk"/>, each with its share of the
    /// folder's. Equal sizes keep listing order, so every read of an unchanged folder returns the same
    /// order. Sorted on every call and never stored; each call allocates one array.
    /// </summary>
    /// <returns>Empty for a file, a link, and a folder with no children, which includes an empty folder and
    /// one that could not be opened. A folder whose listing failed partway returns what was listed.</returns>
    public ChildRow[] ChildrenLargestFirst(int folder)
    {
        Page page = At(folder, out int i);
        int count = page.ChildCount[i];
        if (page.Kind[i] != (byte)EntryKind.Folder || count == 0)
            return [];

        int first = page.FirstChild[i];
        long total = page.RolledOnDisk[i];
        var rows = new ChildRow[count];
        for (int k = 0; k < count; k++)
        {
            int child = first + k;
            long onDisk = OnDiskAtOrBelow(_pages[child >> PageShift]!, child & PageMask);
            // 0 over 0 is NaN in double and throws in long, so an empty total is every child's 0 share.
            rows[k] = new ChildRow(child, onDisk, total == 0 ? 0.0 : (double)onDisk / total);
        }
        rows.AsSpan().Sort(new LargestFirst());
        return rows;
    }

    private static long OnDiskAtOrBelow(Page page, int i) =>
        page.Kind[i] == (byte)EntryKind.Folder ? page.RolledOnDisk[i]
        : Charges(page, i) ? Volatile.Read(ref page.OnDisk[i]) : 0;

    // OnDisk descending, then Entry ascending, which is listing order: a total order, so the unstable
    // sort returns equal sizes in the same order on every read.
    private readonly struct LargestFirst : IComparer<ChildRow>
    {
        public int Compare(ChildRow x, ChildRow y) =>
            x.OnDisk != y.OnDisk ? y.OnDisk.CompareTo(x.OnDisk) : x.Entry.CompareTo(y.Entry);
    }
}
