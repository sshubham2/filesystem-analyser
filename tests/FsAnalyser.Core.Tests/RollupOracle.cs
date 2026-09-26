using FsAnalyser.Core.Store;
using Xunit.Sdk;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// One entry of a synthetic append stream, in the order a test feeds it to the store. Entry 0 is the root
/// (Parent -1); every parent comes before its children, and each folder's children are one contiguous
/// block, as the scanner appends them.
/// </summary>
internal readonly record struct Fed(int Parent, EntryKind Kind, long Size = 0, long OnDisk = 0, UInt128 Id = default,
    EntryFlags Flags = EntryFlags.None, string Name = "e");

/// <summary>Logical bytes, on-disk bytes and files at or below one entry.</summary>
internal readonly record struct Rolled(long Size, long OnDisk, long Files)
{
    public static Rolled operator +(Rolled a, Rolled b) => new(a.Size + b.Size, a.OnDisk + b.OnDisk, a.Files + b.Files);

    public static Rolled Of(ScanStore store, int entry) => new(store.RolledSize(entry), store.RolledOnDisk(entry), store.FilesBelow(entry));
}

/// <summary>
/// The brute-force checks the store's folder totals are held to. Two, each independent of the store's own
/// ancestor walk, and neither recursive, so a chain 10,000 folders deep cannot overflow the test thread's
/// stack (which kills the host with no message):
/// <list type="bullet">
/// <item><see cref="Expected"/> recomputes every entry's totals from the test's own input stream, with the
/// test's own first-seen set of IDs and its own rule for what charges. It reads nothing from the store, so a
/// defect in the store's charge rule cannot hide behind it.</item>
/// <item><see cref="CheckStructure"/> walks the tree from the root over FirstChild and ChildCount, never the
/// Parent chain the store posts along, and checks every folder's totals are the sums over everything below
/// it and over its children. It is what real-tree tests use, where no input stream exists.</item>
/// </list>
/// </summary>
internal static class RollupOracle
{
    private static readonly UInt128 NoId64 = new(0, ulong.MaxValue);

    /// <summary>
    /// Feeds <paramref name="stream"/> to a new store through the append path only, extending each folder's
    /// child block after every append so the store's structure is whole at every cut.
    /// </summary>
    /// <param name="afterEach">Called after each append with the store and the entry's index.</param>
    public static ScanStore Feed(IReadOnlyList<Fed> stream, Action<ScanStore, int>? afterEach = null)
    {
        var store = new ScanStore();
        var blocks = new Dictionary<int, (int First, int Count)>();
        for (int k = 0; k < stream.Count; k++)
        {
            Fed e = stream[k];
            if (k == 0)
            {
                if (e.Parent != -1 || e.Kind != EntryKind.Folder)
                    throw new InvalidOperationException("Test setup: the stream must start with the root, a folder with parent -1.");
                store.AppendRoot(e.Name, 0x10);
            }
            else
            {
                uint attributes = e.Kind == EntryKind.Folder ? 0x10u : 0x20u;
                Assert.Equal(k, store.Append(e.Parent, e.Name, e.Kind, e.Flags, attributes, e.Size, e.OnDisk, e.Id));
                (int first, int count) = blocks.TryGetValue(e.Parent, out var block) ? block : (k, 0);
                if (first + count != k)
                    throw new InvalidOperationException($"Test setup: entry {k} does not follow the other children of folder {e.Parent}.");
                blocks[e.Parent] = (first, count + 1);
                store.SetChildren(e.Parent, first, count + 1);
            }
            afterEach?.Invoke(store, k);
        }
        return store;
    }

    /// <summary>
    /// What the criteria say every entry of the first <paramref name="count"/> entries of the stream holds at
    /// or below it. Only a file charges, and only when no earlier file carried the same ID; 0, all ones and
    /// the 64-bit all-ones are no ID. Every file counts, a repeated one included. A reverse sweep adds each
    /// entry into its parent, which is sound because every parent precedes its children.
    /// </summary>
    public static Rolled[] Expected(IReadOnlyList<Fed> stream, int count = -1)
    {
        count = count < 0 ? stream.Count : count;
        var totals = new Rolled[count];
        var seen = new HashSet<UInt128>();
        for (int k = 0; k < count; k++)
        {
            Fed e = stream[k];
            if (e.Kind != EntryKind.File)
                continue;
            bool isId = e.Id != UInt128.Zero && e.Id != UInt128.MaxValue && e.Id != NoId64;
            totals[k] = !isId || seen.Add(e.Id) ? new Rolled(e.Size, e.OnDisk, 1) : new Rolled(0, 0, 1);
        }
        for (int k = count - 1; k > 0; k--)
            totals[stream[k].Parent] += totals[k];
        return totals;
    }

    /// <summary>Every entry's three totals in the store are the expected ones, or the first that is not is named with its depth.</summary>
    public static void AssertMatches(ScanStore store, Rolled[] expected)
    {
        Assert.Equal(expected.Length, store.Count);
        for (int k = 0; k < expected.Length; k++)
        {
            Rolled actual = Rolled.Of(store, k);
            if (actual != expected[k])
                throw new XunitException($"Entry {k} ({store.Kind(k)} at depth {Depth(store, k)}): the store holds {actual}, the stream says {expected[k]}.");
        }
    }

    /// <summary>
    /// Every entry is reached exactly once from the root through the child blocks, under the parent it
    /// names, and only folders have children. Then, children before parents: each entry's three totals are
    /// the sums of ChargedSize, ChargedOnDisk and one per file over itself and everything below it, and a
    /// folder's are also the sums of its children's own.
    /// </summary>
    public static void CheckStructure(ScanStore store)
    {
        int n = store.Count;
        if (n == 0)
            return;

        var preOrder = new List<int>(n);
        var depth = new int[n];
        var reached = new bool[n];
        var stack = new Stack<int>();
        stack.Push(0);
        reached[0] = true;
        while (stack.Count > 0)
        {
            int e = stack.Pop();
            preOrder.Add(e);
            int count = store.ChildCount(e);
            if (count == 0)
            {
                if (store.FirstChild(e) != -1)
                    throw new XunitException($"Entry {e} at depth {depth[e]} has no children but FirstChild {store.FirstChild(e)}.");
                continue;
            }
            if (store.Kind(e) != EntryKind.Folder)
                throw new XunitException($"Entry {e} at depth {depth[e]} is a {store.Kind(e)} with {count} children.");
            int first = store.FirstChild(e);
            for (int c = first; c < first + count; c++)
            {
                if (store.Parent(c) != e)
                    throw new XunitException($"Entry {c} is in folder {e}'s child block but names {store.Parent(c)} as its parent.");
                if (reached[c])
                    throw new XunitException($"Entry {c} is in more than one child block.");
                reached[c] = true;
                depth[c] = depth[e] + 1;
                stack.Push(c);
            }
        }
        if (preOrder.Count != n)
            throw new XunitException($"{n - preOrder.Count} entries are in no folder's child block; the first is {Array.IndexOf(reached, false)}.");

        var below = new Rolled[n];
        var children = new Rolled[n];
        for (int k = preOrder.Count - 1; k >= 0; k--)
        {
            int e = preOrder[k];
            Rolled own = new(store.ChargedSize(e), store.ChargedOnDisk(e), store.Kind(e) == EntryKind.File ? 1 : 0);
            Rolled subtree = below[e] + own;
            Rolled stored = Rolled.Of(store, e);
            if (stored != subtree)
                throw new XunitException($"Entry {e} ({store.Kind(e)} at depth {depth[e]}): the store holds {stored}, everything at or below it sums to {subtree}.");
            if (store.Kind(e) == EntryKind.Folder && stored != children[e])
                throw new XunitException($"Folder {e} at depth {depth[e]}: the store holds {stored}, its children's own totals sum to {children[e]}.");
            if (e != 0)
            {
                below[store.Parent(e)] += subtree;
                children[store.Parent(e)] += stored;
            }
        }
    }

    private static int Depth(ScanStore store, int entry)
    {
        int depth = 0;
        for (int e = store.Parent(entry); e >= 0; e = store.Parent(e))
            depth++;
        return depth;
    }
}
