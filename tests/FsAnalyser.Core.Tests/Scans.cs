using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;
using Xunit.Sdk;

namespace FsAnalyser.Core.Tests;

/// <summary>Running a scan and finding entries in it.</summary>
internal static class Scans
{
    /// <summary>Scans <paramref name="root"/> and checks the store's charged bytes add up to the totals.</summary>
    public static ScanResult Run(string root, ScanOptions? options = null)
    {
        ScanResult result = Scanner.Scan(root, new ScanProgress(), CancellationToken.None, options ?? ScanOptions.Default);
        AssertChargedBytesAreTheTotals(result);
        return result;
    }

    /// <summary>
    /// Each byte counted once holds in the store's own API, not only in the scanner's running totals:
    /// ChargedSize and ChargedOnDisk summed over every entry under the root are Totals.Bytes and
    /// Totals.BytesOnDisk. And the folder totals (SL-042), whatever the scan's ending: every folder's are the
    /// sums below it (<see cref="RollupOracle.CheckStructure"/>), the root's are Bytes, BytesOnDisk and
    /// Files, and every top-level row, the scanner's second accumulator of the same numbers, is its entry's.
    /// </summary>
    public static void AssertChargedBytesAreTheTotals(ScanResult result)
    {
        long size = 0, onDisk = 0;
        for (int i = 1; i < result.Store.Count; i++)
        {
            size += result.Store.ChargedSize(i);
            onDisk += result.Store.ChargedOnDisk(i);
        }
        Assert.Equal(result.Totals.Bytes, size);
        Assert.Equal(result.Totals.BytesOnDisk, onDisk);

        ScanStore store = result.Store;
        if (store.Count == 0)
            return; // the root was never listed
        RollupOracle.CheckStructure(store);
        Assert.Equal(new Rolled(result.Totals.Bytes, result.Totals.BytesOnDisk, result.Totals.Files), Rolled.Of(store, 0));
        if (result.TopLevel is not { } top)
            return;
        Assert.Equal(store.ChildCount(0), top.Rows.Count);
        for (int i = 0; i < top.Rows.Count; i++)
        {
            TopLevelRow row = top.Rows[i];
            int entry = store.FirstChild(0) + i;
            Assert.True(new Rolled(row.Bytes, row.OnDisk, row.Files) == Rolled.Of(store, entry),
                $"top-level row {row.Name} holds {new Rolled(row.Bytes, row.OnDisk, row.Files)}, the store {Rolled.Of(store, entry)} under entry {entry}");
        }
    }

    public static Dictionary<string, int> ByPath(ScanStore store)
    {
        var paths = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < store.Count; i++)
            paths.Add(store.GetPath(i), i);
        return paths;
    }

    public static int Find(ScanResult result, string path)
    {
        Dictionary<string, int> paths = ByPath(result.Store);
        if (paths.TryGetValue(path, out int index))
            return index;
        throw new XunitException($"'{path}' is not in the scan. It holds:\n  " + string.Join("\n  ", paths.Keys.Order()));
    }

    /// <summary>
    /// The raw listing of one folder, as DirectoryReader gives it, keyed by name. Listed as the scan lists, keeping
    /// the folder's last-access time where the folder allows it (SL-056), and opening none of its files.
    /// </summary>
    public static Dictionary<string, RawEntry> Listing(string folder, bool fullClass = false)
    {
        using var io = new SynchronousIoCancel(CancellationToken.None);
        using var reader = new DirectoryReader(io);
        int error = reader.Read(LongPath.ToExtended(folder), fullClass, keepLastAccess: true);
        if (error != 0)
            throw new XunitException($"Listing {folder} failed with Win32 error {error}.");
        var entries = new Dictionary<string, RawEntry>(StringComparer.Ordinal);
        for (int i = 0; i < reader.Count; i++)
            entries.Add(reader.NameOf(reader[i]).ToString(), reader[i]);
        return entries;
    }

    /// <summary>Everything the store holds about one entry except its index.</summary>
    public readonly record struct EntryState(
        EntryKind Kind, EntryFlags Flags, FileAttributes Attributes, long Size, long OnDisk, UInt128 FileId, int ChildCount);

    /// <summary>Every entry of the scan by path, so two scans of one tree can be compared entry by entry.</summary>
    public static Dictionary<string, EntryState> Snapshot(ScanStore store)
    {
        var entries = new Dictionary<string, EntryState>(StringComparer.Ordinal);
        for (int i = 0; i < store.Count; i++)
        {
            entries.Add(store.GetPath(i), new EntryState(store.Kind(i), store.Flags(i), store.Attributes(i),
                store.Size(i), store.OnDisk(i), store.FileId(i), store.ChildCount(i)));
        }
        return entries;
    }

    /// <summary>The unreadable folders by path, with their errors.</summary>
    public static List<(string Path, int Error)> UnreadableByPath(ScanResult result) =>
        result.Unreadable.Select(u => (result.Store.GetPath(u.Entry), u.Win32Error)).Order().ToList();

    /// <summary>
    /// Waits for the closes of aborted calls' handles, which run on the pool, so a temp tree can be removed.
    /// </summary>
    public static void WaitForAbortedCloses()
    {
        Assert.True(SpinWait.SpinUntil(() => SynchronousIoCancel.PendingCloses == 0, TimeSpan.FromSeconds(10)),
            $"{SynchronousIoCancel.PendingCloses} handle(s) of aborted calls were still not closed after 10 s.");
    }

    /// <summary>Entries whose parent is a link. A scan that never enters a link has none.</summary>
    public static IEnumerable<int> UnderLinks(ScanStore store) =>
        Enumerable.Range(1, store.Count - 1).Where(i => store.Kind(store.Parent(i)) == EntryKind.Link);
}
