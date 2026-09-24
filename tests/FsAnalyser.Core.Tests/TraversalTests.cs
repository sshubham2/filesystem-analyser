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
