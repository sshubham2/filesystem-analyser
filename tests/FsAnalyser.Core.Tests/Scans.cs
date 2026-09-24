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
    /// Totals.BytesOnDisk.
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

    /// <summary>The raw listing of one folder, as DirectoryReader gives it, keyed by name.</summary>
    public static Dictionary<string, RawEntry> Listing(string folder, bool fullClass = false)
    {
        using var reader = new DirectoryReader();
        int error = reader.Read(LongPath.ToExtended(folder), fullClass);
        if (error != 0)
            throw new XunitException($"Listing {folder} failed with Win32 error {error}.");
        var entries = new Dictionary<string, RawEntry>(StringComparer.Ordinal);
        for (int i = 0; i < reader.Count; i++)
            entries.Add(reader.NameOf(reader[i]).ToString(), reader[i]);
        return entries;
    }

    /// <summary>Entries whose parent is a link. A scan that never enters a link has none.</summary>
    public static IEnumerable<int> UnderLinks(ScanStore store) =>
        Enumerable.Range(1, store.Count - 1).Where(i => store.Kind(store.Parent(i)) == EntryKind.Link);
}
