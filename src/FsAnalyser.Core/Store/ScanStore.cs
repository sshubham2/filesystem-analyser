using System.Runtime.CompilerServices;

namespace FsAnalyser.Core.Store;

/// <summary>
/// One scan, held in memory as index-addressed columns. Entry 0 is the root (Parent -1), and each
/// folder's children are one contiguous block starting at <see cref="FirstChild"/>.
/// </summary>
/// <remarks>
/// Layout rules that keep a 5M-entry drive under 1 GiB:
/// columns grow in fixed 65,536-entry pages and names in fixed UTF-16 char pages, and neither ever
/// moves or doubles; there is no object per entry (paths are rebuilt from Parent when asked for); and
/// repeated FileIds are found by an open-addressing table of entry indices that reads the FileId
/// columns, so no key is stored twice.
/// Reads are public. Writes are internal to Core, so the scanner is the only writer. A hardlink probe
/// corrects an already-written entry's Size and OnDisk in place (aligned 8-byte writes), so a reader
/// must not assume those values never change once written.
/// </remarks>
public sealed class ScanStore
{
    internal const int PageShift = 16;
    internal const int PageSize = 1 << PageShift;
    private const int PageMask = PageSize - 1;
    private const int MaxPages = int.MaxValue / PageSize + 1;

    internal const int NamePageShift = 20;
    internal const int NamePageChars = 1 << NamePageShift;
    private const int NamePageMask = NamePageChars - 1;
    private const int MaxNamePages = int.MaxValue / NamePageChars + 1;

    // The page directories are allocated at full size up front (256 KiB and 16 KiB of references), so
    // they never resize either.
    private readonly Page?[] _pages = new Page?[MaxPages];
    private readonly char[]?[] _namePages = new char[]?[MaxNamePages];
    private int _count;
    private int _namePage = -1;
    private int _nameUsed = NamePageChars;
    private readonly FileIdIndex _fileIds;

    public ScanStore()
    {
        _fileIds = new FileIdIndex(this);
    }

    /// <summary>The number of entries, including the root.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>The containing folder's index, or -1 for the root.</summary>
    public int Parent(int index) => At(index, out int i).Parent[i];

    /// <summary>The name as listed. For the root, the path that was scanned (for example <c>C:\</c>).</summary>
    public ReadOnlySpan<char> Name(int index)
    {
        Page page = At(index, out int i);
        int offset = page.NameOffset[i];
        return new ReadOnlySpan<char>(_namePages[offset >> NamePageShift], offset & NamePageMask, page.NameLength[i]);
    }

    public EntryKind Kind(int index) => (EntryKind)At(index, out int i).Kind[i];

    public EntryFlags Flags(int index) => (EntryFlags)At(index, out int i).Flags[i];

    /// <summary>The Win32 attributes as listed, including bits <see cref="FileAttributes"/> does not name.</summary>
    public FileAttributes Attributes(int index) => (FileAttributes)At(index, out int i).Attributes[i];

    /// <summary>Logical size in bytes (EndOfFile).</summary>
    public long Size(int index) => At(index, out int i).Size[i];

    /// <summary>Space taken on disk in bytes (AllocationSize).</summary>
    public long OnDisk(int index) => At(index, out int i).OnDisk[i];

    /// <summary>The volume's 128-bit file ID, or 0 when the volume did not supply one.</summary>
    public UInt128 FileId(int index)
    {
        Page page = At(index, out int i);
        return new UInt128(page.IdHigh[i], page.IdLow[i]);
    }

    /// <summary>The first child's index, or -1 when the entry has no children.</summary>
    public int FirstChild(int index) => At(index, out int i).FirstChild[i];

    /// <summary>The number of children. Always 0 for files and links.</summary>
    public int ChildCount(int index) => At(index, out int i).ChildCount[i];

    /// <summary>The entry's full path, rebuilt from its parents. Names are joined exactly as listed.</summary>
    public string GetPath(int index)
    {
        At(index, out _);
        int length = 0;
        int depth = 0;
        for (int e = index; e >= 0; e = Parent(e))
        {
            length += Name(e).Length;
            depth++;
        }

        Span<int> chain = depth <= 256 ? stackalloc int[depth] : new int[depth];
        int k = depth;
        for (int e = index; e >= 0; e = Parent(e))
            chain[--k] = e;

        var builder = new System.Text.StringBuilder(length + depth);
        foreach (int e in chain)
        {
            if (builder.Length > 0 && builder[^1] != '\\')
                builder.Append('\\');
            builder.Append(Name(e));
        }
        return builder.ToString();
    }

    // ---- Writes: internal to Core ----

    internal int AppendRoot(string path, uint attributes)
    {
        if (_count != 0)
            throw new InvalidOperationException("The root must be the first entry.");
        return Append(-1, path, EntryKind.Folder, EntryFlags.None, attributes, 0, 0, UInt128.Zero);
    }

    internal int Append(int parent, ReadOnlySpan<char> name, EntryKind kind, EntryFlags flags,
        uint attributes, long size, long onDisk, UInt128 fileId)
    {
        int index = _count;
        if (index == int.MaxValue)
            throw new InvalidOperationException("The scan store is full.");

        int pageIndex = index >> PageShift;
        Page page = _pages[pageIndex] ??= new Page();
        int i = index & PageMask;

        page.Parent[i] = parent;
        page.NameOffset[i] = StoreName(name);
        page.NameLength[i] = (ushort)name.Length;
        page.Kind[i] = (byte)kind;
        page.Flags[i] = (byte)flags;
        page.Attributes[i] = attributes;
        page.Size[i] = size;
        page.OnDisk[i] = onDisk;
        page.IdLow[i] = (ulong)fileId;
        page.IdHigh[i] = (ulong)(fileId >> 64);
        page.FirstChild[i] = -1;
        page.ChildCount[i] = 0;

        Volatile.Write(ref _count, index + 1);
        return index;
    }

    internal void SetChildren(int folder, int firstChild, int childCount)
    {
        Page page = At(folder, out int i);
        page.FirstChild[i] = childCount == 0 ? -1 : firstChild;
        page.ChildCount[i] = childCount;
    }

    internal void AddFlags(int index, EntryFlags flags)
    {
        Page page = At(index, out int i);
        page.Flags[i] |= (byte)flags;
    }

    internal void CorrectSizes(int index, long size, long onDisk)
    {
        Page page = At(index, out int i);
        Volatile.Write(ref page.Size[i], size);
        Volatile.Write(ref page.OnDisk[i], onDisk);
    }

    /// <summary>
    /// Records <paramref name="index"/>'s FileId. Returns false, with the entry that already holds the
    /// same ID, when the file was seen under another name. A FileId of 0 is never deduplicated.
    /// </summary>
    internal bool TryAddFileId(int index, out int charged)
    {
        Page page = At(index, out int i);
        if (page.IdLow[i] == 0 && page.IdHigh[i] == 0)
        {
            charged = index;
            return true;
        }
        return _fileIds.TryAdd(index, out charged);
    }

    internal int FileIdTableSlots => _fileIds.Slots;

    // ---- Internals ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Page At(int index, out int offset)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The store holds {Count} entries.");
        offset = index & PageMask;
        return _pages[index >> PageShift]!;
    }

    // A name never straddles two pages: if it does not fit in what is left, it starts the next page.
    private int StoreName(ReadOnlySpan<char> name)
    {
        if (name.Length > ushort.MaxValue)
            throw new ArgumentException("A name longer than 65,535 characters cannot be stored.", nameof(name));
        if (name.IsEmpty)
            return 0;

        if (_nameUsed + name.Length > NamePageChars)
        {
            _namePage++;
            if (_namePage == MaxNamePages)
                throw new InvalidOperationException("The scan store's name pages are full.");
            _namePages[_namePage] = new char[NamePageChars];
            _nameUsed = 0;
        }

        int offset = (_namePage << NamePageShift) | _nameUsed;
        name.CopyTo(_namePages[_namePage].AsSpan(_nameUsed));
        _nameUsed += name.Length;
        return offset;
    }

    private bool SameFileId(int a, int b)
    {
        Page pa = _pages[a >> PageShift]!, pb = _pages[b >> PageShift]!;
        int ia = a & PageMask, ib = b & PageMask;
        return pa.IdLow[ia] == pb.IdLow[ib] && pa.IdHigh[ia] == pb.IdHigh[ib];
    }

    private int HashFileId(int index)
    {
        Page page = _pages[index >> PageShift]!;
        int i = index & PageMask;
        // splitmix64 finaliser: NTFS IDs are near-sequential record numbers, and linear probing
        // needs them spread.
        ulong x = page.IdLow[i] ^ (page.IdHigh[i] * 0x9E3779B97F4A7C15UL);
        x ^= x >> 30;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return (int)x;
    }

    private sealed class Page
    {
        public readonly int[] Parent = new int[PageSize];
        public readonly int[] NameOffset = new int[PageSize];
        public readonly ushort[] NameLength = new ushort[PageSize];
        public readonly byte[] Kind = new byte[PageSize];
        public readonly byte[] Flags = new byte[PageSize];
        public readonly uint[] Attributes = new uint[PageSize];
        public readonly long[] Size = new long[PageSize];
        public readonly long[] OnDisk = new long[PageSize];
        public readonly ulong[] IdLow = new ulong[PageSize];
        public readonly ulong[] IdHigh = new ulong[PageSize];
        public readonly int[] FirstChild = new int[PageSize];
        public readonly int[] ChildCount = new int[PageSize];
    }

    /// <summary>
    /// Open-addressing, linear-probing set of entry indices keyed by the FileId columns: 4 bytes a slot,
    /// load kept at or under 70%. It is the one structure here that rehashes as it grows.
    /// </summary>
    private sealed class FileIdIndex(ScanStore store)
    {
        private int[] _slots = new int[1024]; // entry index + 1; 0 is empty
        private int _used;

        public int Slots => _slots.Length;

        public bool TryAdd(int index, out int existing)
        {
            int mask = _slots.Length - 1;
            int s = store.HashFileId(index) & mask;
            while (_slots[s] != 0)
            {
                int candidate = _slots[s] - 1;
                if (store.SameFileId(candidate, index))
                {
                    existing = candidate;
                    return false;
                }
                s = (s + 1) & mask;
            }

            _slots[s] = index + 1;
            existing = index;
            if (++_used * 10L > _slots.Length * 7L)
                Grow();
            return true;
        }

        private void Grow()
        {
            int[] old = _slots;
            var slots = new int[old.Length * 2];
            int mask = slots.Length - 1;
            foreach (int entry in old)
            {
                if (entry == 0)
                    continue;
                int s = store.HashFileId(entry - 1) & mask;
                while (slots[s] != 0)
                    s = (s + 1) & mask;
                slots[s] = entry;
            }
            _slots = slots;
        }
    }
}
