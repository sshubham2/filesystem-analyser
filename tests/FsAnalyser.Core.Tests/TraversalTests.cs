using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

public class TraversalTests
{
    [Fact]
    public void ReadsEverythingUnderAFolder()
    {
        using var tree = new TempTree();
        tree.Folder(@"a\b\c"); // a's only child is b, b's only child is c
        tree.File(@"a\b\c\deep.txt", 123);
        tree.Folder("empty");
        tree.File("zero.bin", 0);
        TempTree.AddAttributes(tree.File("hidden-system.txt", 10), FileAttributes.Hidden | FileAttributes.System);
        string hiddenFolder = tree.Folder("hidden-system-dir");
        tree.File(@"hidden-system-dir\inside.txt", 7);
        TempTree.AddAttributes(hiddenFolder, FileAttributes.Hidden | FileAttributes.System);

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Empty(result.Unreadable);
        ScanStore store = result.Store;
        string[] expected =
        [
            tree.Root,
            tree.PathOf("a"), tree.PathOf(@"a\b"), tree.PathOf(@"a\b\c"), tree.PathOf(@"a\b\c\deep.txt"),
            tree.PathOf("empty"), tree.PathOf("zero.bin"), tree.PathOf("hidden-system.txt"),
            tree.PathOf("hidden-system-dir"), tree.PathOf(@"hidden-system-dir\inside.txt"),
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), Scans.ByPath(store).Keys.Order(StringComparer.Ordinal));

        Assert.Equal(new ScanTotals(Files: 4, Folders: 5, Links: 0, Bytes: 140, BytesOnDisk: result.Totals.BytesOnDisk), result.Totals);

        int a = Scans.Find(result, tree.PathOf("a"));
        Assert.Equal(1, store.ChildCount(a));
        Assert.Equal(tree.PathOf(@"a\b"), store.GetPath(store.FirstChild(a)));

        int empty = Scans.Find(result, tree.PathOf("empty"));
        Assert.Equal(EntryKind.Folder, store.Kind(empty));
        Assert.Equal(0, store.ChildCount(empty));
        Assert.Equal(-1, store.FirstChild(empty));

        int zero = Scans.Find(result, tree.PathOf("zero.bin"));
        Assert.Equal(EntryKind.File, store.Kind(zero));
        Assert.Equal(0, store.Size(zero));

        int hiddenFile = Scans.Find(result, tree.PathOf("hidden-system.txt"));
        Assert.True(store.Attributes(hiddenFile).HasFlag(FileAttributes.Hidden | FileAttributes.System));
        int hiddenDir = Scans.Find(result, tree.PathOf("hidden-system-dir"));
        Assert.True(store.Attributes(hiddenDir).HasFlag(FileAttributes.Hidden | FileAttributes.System));
        Assert.Equal(1, store.ChildCount(hiddenDir));

        AssertChildrenAreContiguous(store);
    }

    /// <summary>
    /// Criterion 1 of SL-043, as an ordinary user: a nested tree whose folders and files cross 260
    /// characters, the deepest over 400, holding a hidden file and a hidden folder and no link. The store
    /// must hold every entry once, at the path its parent chain gives, and exactly what a listing built
    /// another way finds: the BCL's own recursion over the \\?\ root, told to skip nothing (its defaults skip
    /// Hidden and System, and would agree with a scanner that dropped them).
    /// </summary>
    [Fact]
    public void AgreesWithAnIndependentListingOfADeepTreeAsAnOrdinaryUser()
    {
        // On Windows this reads the token's elevation (TokenElevation), not group membership: a filtered
        // administrator is not elevated. An elevated run would read what an ordinary user cannot, so it fails.
        Assert.False(Environment.IsPrivilegedProcess, "the test process is elevated; criterion 1 is about an ordinary user");

        using var tree = new TempTree();
        string ext = tree.ExtendedRoot;
        string path = "";
        for (int level = 1; level <= 9; level++)
        {
            path += $@"\level{level}-{new string('d', 33)}"; // 41 characters a level, separator included
            Directory.CreateDirectory(ext + path);
            for (int k = 1; k <= 2; k++)
                File.WriteAllBytes($@"{ext}{path}\file{k}-{new string('f', 30)}.bin", new byte[100 * level + k]);
            Directory.CreateDirectory($@"{ext}{path}\side-{level}");
            File.WriteAllBytes($@"{ext}{path}\side-{level}\note.txt", new byte[level]);
            if (level == 6)
            {
                string hiddenFile = $@"{ext}{path}\hidden-file.txt";
                File.WriteAllBytes(hiddenFile, new byte[6]);
                TempTree.AddAttributes(hiddenFile, FileAttributes.Hidden);
            }
            if (level == 7)
            {
                string hiddenFolder = $@"{ext}{path}\hidden-folder";
                Directory.CreateDirectory(hiddenFolder);
                File.WriteAllBytes($@"{hiddenFolder}\inside.bin", new byte[7]);
                TempTree.AddAttributes(hiddenFolder, FileAttributes.Hidden);
            }
        }

        var listing = Directory.EnumerateFileSystemEntries(ext, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                MatchType = MatchType.Simple,
                ReturnSpecialDirectories = false,
            })
            .Select(p => p[@"\\?\".Length..])
            .ToList();

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Empty(result.Unreadable);
        Assert.Equal(0, result.Totals.Links);
        ScanStore store = result.Store;
        var scanned = Enumerable.Range(1, store.Count - 1).Select(store.GetPath).ToList();
        Assert.Equal(scanned.Count, scanned.Distinct(StringComparer.Ordinal).Count()); // each entry once
        Assert.Equal(listing.Count, scanned.Count);
        Assert.Equal(listing.Order(StringComparer.Ordinal), scanned.Order(StringComparer.Ordinal));
        AssertChildrenAreContiguous(store);

        // The shape was fed: folders and files past 260 characters, the deepest past 400, hidden entries.
        int Longest(bool folders) => listing.Where(p => Directory.Exists(@"\\?\" + p) == folders).Max(p => p.Length);
        Assert.True(Longest(folders: true) > 260, $"the longest folder path is {Longest(folders: true)} characters");
        Assert.True(Longest(folders: false) > 400, $"the longest file path is {Longest(folders: false)} characters");
        string hidden6 = listing.Single(p => p.EndsWith(@"\hidden-file.txt", StringComparison.Ordinal));
        string hidden7 = listing.Single(p => p.EndsWith(@"\hidden-folder", StringComparison.Ordinal));
        Assert.True(store.Attributes(Scans.Find(result, hidden6)).HasFlag(FileAttributes.Hidden));
        Assert.True(store.Attributes(Scans.Find(result, hidden7)).HasFlag(FileAttributes.Hidden));
        Assert.Equal(1, store.ChildCount(Scans.Find(result, hidden7)));
    }

    [Fact]
    public void ListsAFolderThatSpansSeveralBuffers()
    {
        using var tree = new TempTree();
        const int files = 2000;
        var names = new List<string>(files);
        for (int i = 0; i < files; i++)
        {
            string name = i.ToString("D4") + new string('n', 92) + ".txt"; // 100 characters
            names.Add(name);
            tree.File(name, 0);
        }
        // Each FILE_ID_EXTD_DIR_INFO record is 88 bytes plus the UTF-16 name, 8-byte aligned.
        Assert.True(files * (88 + 200) > 8 * DirectoryReader.BufferSize, "the listing should need several buffers");

        ScanResult result = Scans.Run(tree.Root);

        Assert.Equal(ScanStatus.Completed, result.Status);
        ScanStore store = result.Store;
        Assert.Equal(files, store.ChildCount(0));
        Assert.Equal(files + 1, store.Count);
        var listed = Enumerable.Range(store.FirstChild(0), store.ChildCount(0)).Select(i => store.Name(i).ToString()).ToList();
        Assert.Equal(names.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal)); // each exactly once
        Assert.Equal(files, result.Totals.Files);
    }

    internal static void AssertChildrenAreContiguous(ScanStore store)
    {
        long children = 0;
        for (int i = 0; i < store.Count; i++)
        {
            int count = store.ChildCount(i);
            children += count;
            if (store.Kind(i) != EntryKind.Folder)
                Assert.Equal(0, count);
            for (int k = 0; k < count; k++)
                Assert.Equal(i, store.Parent(store.FirstChild(i) + k));
        }
        Assert.Equal(store.Count - 1, children);
        Assert.Equal(-1, store.Parent(0));
    }
}
