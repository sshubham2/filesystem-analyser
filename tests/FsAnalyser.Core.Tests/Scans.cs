using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;
using Xunit.Sdk;

namespace FsAnalyser.Core.Tests;

/// <summary>Running a scan and finding entries in it.</summary>
internal static class Scans
{
    public static ScanResult Run(string root, ScanOptions? options = null) =>
        Scanner.Scan(root, new ScanProgress(), CancellationToken.None, options ?? ScanOptions.Default);

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
