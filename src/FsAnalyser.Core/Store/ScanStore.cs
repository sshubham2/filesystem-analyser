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
/// Reads are public, except the raw sizes. Writes are internal to Core, so the scanner is the only writer.
/// A hardlink probe corrects an already-written entry's Size and OnDisk in place (aligned 8-byte writes),
/// so a reader must not assume those values never change once written. Anything that adds bytes up or
/// ranks by them reads <see cref="ChargedSize"/> and <see cref="ChargedOnDisk"/>, which hold the
/// count-each-byte-once rule. <see cref="Size"/> and <see cref="OnDisk"/> skip that rule, so they are
/// internal to Core (SL-040, M2): the compiler keeps the App and every later project to the charged values.
/// <para>The store owns that rule: each append finds a repeated file ID and flags the later name, and
/// posts every file's charge up its Parent chain
/// into each folder's <see cref="RolledSize"/>, <see cref="RolledOnDisk"/> and <see cref="FilesBelow"/>.
/// Those are exact between appends: on the writing thread, and once the scan has ended or was
/// cancelled. Reading them from another thread while appends go on is not part of this contract.</para>
/// <para>A folder is appended not listed and stays so until <see cref="SetChildren"/> registers its children, which is
/// how a store a scan stopped in tells a folder it never listed from one it listed and found empty
/// (<see cref="NotListed"/>). The mark lives in a bit the flags byte had free, so it costs no storage, and it is exact on
/// the same terms as the rolled totals: between appends on the writing thread and once the scan has returned.</para>
/// </remarks>
public sealed partial class ScanStore
{
    internal const int PageShift = 16;
    internal const int PageSize = 1 << PageShift;
    private const int PageMask = PageSize - 1;
    private const int MaxPages = int.MaxValue / PageSize + 1;

    internal const int NamePageShift = 20;
    internal const int NamePageChars = 1 << NamePageShift;
    private const int NamePageMask = NamePageChars - 1;
    private const int MaxNamePages = int.MaxValue / NamePageChars + 1;

    // Bit 7 of the per-entry flags byte, the one bit EntryFlags leaves free (EntryKind.cs): set on a folder by its append,
    // cleared by SetChildren, hidden from Flags(). It costs no column.
    private const byte NotListedBit = 0x80;
    private const byte ListedMask = 0x7F;

    // The page directories are allocated at full size up front (256 KiB and 16 KiB of references), so
    // they never resize either.
    private readonly Page?[] _pages = new Page?[MaxPages];
    private readonly char[]?[] _namePages = new char[]?[MaxNamePages];
    private int _count;
    private int _namePage = -1;
    private int _nameUsed = NamePageChars;
    private readonly FileIdIndex _fileIds;
    private readonly TaskCompletionSource<long?> _volumeUsed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScanStore()
    {
        _fileIds = new FileIdIndex(this);
    }

    /// <summary>The number of entries, including the root.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// For a scan of a whole drive (<c>C:\</c>), the bytes the volume reports in use, read once as the scan
    /// starts: what the tree is set against to show how much of the drive the scan could not see. Null for a
    /// scan of a folder or a share, and when the volume did not answer within the read's bound (SL-043).
    /// </summary>
    /// <remarks>
    /// Never null itself, completed at most once and never faulted. Readable while the scan runs and after it
    /// ends, cancelled or not. Pending means the volume has not answered yet: the read runs on a thread of
    /// its own, and nothing in Core waits for it (O-031). A reader must not block a UI thread on it either; it
    /// completes, with null at the latest, once the read's bound has passed.
    /// </remarks>
    public Task<long?> VolumeUsed => _volumeUsed.Task;

    /// <summary>
    /// The completion source behind <see cref="VolumeUsed"/>, which the scan hands to the volume read. Named
    /// after it, so that the rule that nothing in Core waits on the figure matches a wait on this too (M2).
    /// </summary>
    internal TaskCompletionSource<long?> VolumeUsedSource => _volumeUsed;

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

    /// <summary>
    /// The entry's flags, without the store's own not-listed mark (bit 0x80): that is never an
    /// <see cref="EntryFlags"/> member and never shows here, so every comparison and test of these flags sees the
    /// values it always has. Read the mark through <see cref="NotListed"/>.
    /// </summary>
    public EntryFlags Flags(int index) => (EntryFlags)(At(index, out int i).Flags[i] & ~NotListedBit);

    /// <summary>
    /// Whether this folder was appended and its children were never registered: still queued when the scan
    /// stopped, cut by Stop part-way through its listing (which commits nothing), the root when the scan stopped or
    /// failed in the root's own handling after its directory check, or, by construction, a folder a reader listed
    /// ahead without writing. False for a folder whose listing committed, empty or not, an Unreadable folder and the
    /// NotScanned container store included (their children are registered, with none), for every file and link, and
    /// for every entry once a scan has run to the end.
    /// </summary>
    /// <remarks>
    /// Exact between appends on the writing thread and after the scan has returned, the same contract as the rolled
    /// totals; a read from another thread while appends go on is not part of it. Only <see cref="Append(int, ReadOnlySpan{char}, EntryKind, EntryFlags, uint, long, long, UInt128, out int, out Posted)"/>
    /// sets the mark, on a folder, and only <see cref="SetChildren"/> clears it.
    /// </remarks>
    public bool NotListed(int index) => (At(index, out int i).Flags[i] & NotListedBit) != 0;

    /// <summary>The Win32 attributes as listed, including bits <see cref="FileAttributes"/> does not name.</summary>
    public FileAttributes Attributes(int index) => (FileAttributes)At(index, out int i).Attributes[i];

    /// <summary>
    /// Logical size in bytes (EndOfFile), as this name was listed or probed. Do not sum it: a later name
    /// of a hardlinked file carries the file's size again. Sum <see cref="ChargedSize"/>.
    /// </summary>
    internal long Size(int index) => At(index, out int i).Size[i];

    /// <summary>Space taken on disk in bytes (AllocationSize). Do not sum it; sum <see cref="ChargedOnDisk"/>.</summary>
    internal long OnDisk(int index) => At(index, out int i).OnDisk[i];

    /// <summary>
    /// The logical bytes this entry adds to any total, so each byte is counted once: a file's Size, or 0
    /// for a later name of a file already charged. A folder (readable or not) and a link charge 0,
    /// whatever size they state. Summed over entries 1..Count-1 this is the scan's <c>Totals.Bytes</c>.
    /// </summary>
    public long ChargedSize(int index)
    {
        Page page = At(index, out int i);
        return Charges(page, i) ? Volatile.Read(ref page.Size[i]) : 0;
    }

    /// <summary>The on-disk bytes this entry adds to any total; see <see cref="ChargedSize"/>.</summary>
    public long ChargedOnDisk(int index)
    {
        Page page = At(index, out int i);
        return Charges(page, i) ? Volatile.Read(ref page.OnDisk[i]) : 0;
    }

    /// <summary>
    /// The volume's 128-bit file ID as listed: 0 when the volume did not supply one, and possibly one of
    /// the "no ID" values <see cref="HasFileId"/> rules out.
    /// </summary>
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

    /// <summary>
    /// Whether <paramref name="fileId"/> identifies a file, so that two entries carrying it are two names
    /// of one file. False for 0 (the volume gave no ID), for all ones (MS-FSCC: no unique ID could be
    /// established, and the value must be ignored), and for the 64-bit all-ones value widened to 128 bits.
    /// </summary>
    internal static bool HasFileId(UInt128 fileId) =>
        fileId != UInt128.Zero && fileId != UInt128.MaxValue && fileId != NoFileId64;

    private static readonly UInt128 NoFileId64 = new(0, ulong.MaxValue);

    // ---- Writes: internal to Core ----

    /// <summary>What an append or a correction added to every folder above the entry, and so to the totals.</summary>
    internal readonly record struct Posted(long Size, long OnDisk, int Files);

    internal int AppendRoot(string path, uint attributes)
    {
        if (_count != 0)
            throw new InvalidOperationException("The root must be the first entry.");
        return Append(-1, path, EntryKind.Folder, EntryFlags.None, attributes, 0, 0, UInt128.Zero);
    }

    /// <summary>Appends one entry; see the overload that reports what the append posted.</summary>
    internal int Append(int parent, ReadOnlySpan<char> name, EntryKind kind, EntryFlags flags,
        uint attributes, long size, long onDisk, UInt128 fileId) =>
        Append(parent, name, kind, flags, attributes, size, onDisk, fileId, out _, out _);

    /// <summary>
    /// Appends one entry under <paramref name="parent"/> and applies the counting rule to it. A file whose
    /// ID (<see cref="HasFileId"/>) an earlier entry already holds is flagged HardlinkDuplicate: it counts
    /// as a file and charges nothing. Every file then posts its charge, and one file, to each folder from
    /// its parent up to the root. A folder or a link posts nothing.
    /// </summary>
    /// <remarks>
    /// Two obligations on the caller, because the first name seen is the one charged: one writing
    /// thread, and a fixed append order, a parent before its children and each folder's children as one
    /// contiguous block in listing order. Appended from several threads in a varying order, the root's
    /// total still comes out right, but which folder holds a hardlinked file's bytes moves from run to run
    /// (measured with 16 threads on C:\Windows: 68 folders moved by more than 10 MiB between runs).
    /// </remarks>
    /// <param name="flags">Anything but HardlinkDuplicate, which only the store sets: it is cleared here.</param>
    /// <param name="firstHolder">The entry already holding this file's ID when the append flagged it a
    /// duplicate; otherwise the new entry itself.</param>
    /// <param name="posted">What was added to every folder above it: (0, 0, 0) for a folder or a link.</param>
    /// <exception cref="ArgumentOutOfRangeException">The parent is not already in the store. Only the first
    /// entry, the root, has no parent (-1).</exception>
    /// <exception cref="InvalidOperationException">The parent is a file, a link, or a folder flagged
    /// Unreadable.</exception>
    internal int Append(int parent, ReadOnlySpan<char> name, EntryKind kind, EntryFlags flags,
        uint attributes, long size, long onDisk, UInt128 fileId, out int firstHolder, out Posted posted)
    {
        int index = _count;
        if (index == int.MaxValue)
            throw new InvalidOperationException("The scan store is full.");
        // Before anything is written: the mark is the store's, set below for a folder and by nobody else.
        if (((byte)flags & NotListedBit) != 0)
            throw new ArgumentException("Bit 0x80 of the flags is the store's not-listed mark: an append sets it on a folder itself.", nameof(flags));
        CheckParent(parent, index);

        int pageIndex = index >> PageShift;
        Page page = _pages[pageIndex] ??= new Page();
        int i = index & PageMask;

        page.Parent[i] = parent;
        page.NameOffset[i] = StoreName(name);
        page.NameLength[i] = (ushort)name.Length;
        page.Kind[i] = (byte)kind;
        // A folder is born not listed. The byte is written before the count is published, so no reader sees a folder without it.
        page.Flags[i] = (byte)((byte)(flags & ~EntryFlags.HardlinkDuplicate) | (kind == EntryKind.Folder ? NotListedBit : 0));
        page.Attributes[i] = attributes;
        page.Size[i] = size;
        page.OnDisk[i] = onDisk;
        page.IdLow[i] = (ulong)fileId;
        page.IdHigh[i] = (ulong)(fileId >> 64);
        page.FirstChild[i] = -1;
        page.ChildCount[i] = 0;
        page.RolledSize[i] = 0;
        page.RolledOnDisk[i] = 0;
        page.FilesBelow[i] = 0;

        Volatile.Write(ref _count, index + 1);

        firstHolder = index;
        posted = default;
        if (kind != EntryKind.File)
            return index;

        if (!TryAddFileId(index, fileId, out firstHolder))
            page.Flags[i] |= (byte)EntryFlags.HardlinkDuplicate;
        posted = Charges(page, i) ? new Posted(size, onDisk, 1) : new Posted(0, 0, 1);
        Post(parent, posted);
        return index;
    }

    /// <summary>
    /// Registers a folder's children: the whole of them, in one call, once they are all appended. Clears the
    /// folder's not-listed mark, for any count including 0, and nothing else does (<see cref="NotListed"/>).
    /// </summary>
    internal void SetChildren(int folder, int firstChild, int childCount)
    {
        Page page = At(folder, out int i);
        page.FirstChild[i] = childCount == 0 ? -1 : firstChild;
        page.ChildCount[i] = childCount;
        page.Flags[i] &= ListedMask;
    }

    /// <summary>Adds flags other than HardlinkDuplicate, which only an append sets, and the not-listed mark, which only an append sets and only SetChildren clears.</summary>
    internal void AddFlags(int index, EntryFlags flags)
    {
        if ((flags & EntryFlags.HardlinkDuplicate) != 0)
        {
            throw new ArgumentException("Only an append flags a hardlink duplicate: the flag changes what the entry charges, " +
                "and its charge is already in every folder above it.", nameof(flags));
        }
        if (((byte)flags & NotListedBit) != 0)
            throw new ArgumentException("Bit 0x80 of the flags is the store's not-listed mark: only an append sets it and only SetChildren clears it.", nameof(flags));
        Page page = At(index, out int i);
        page.Flags[i] |= (byte)flags;
    }

    /// <summary>
    /// Replaces an appended entry's Size and OnDisk with what the file itself says, and posts the change in
    /// its charge up the same chain its append posted to. The only write to those columns after an append.
    /// A later name of a hardlinked file charges nothing before or after, so it posts nothing.
    /// </summary>
    /// <returns>What was added to every folder above it. Files is always 0.</returns>
    internal Posted CorrectSizes(int index, long size, long onDisk)
    {
        Page page = At(index, out int i);
        Posted posted = Charges(page, i) ? new Posted(size - page.Size[i], onDisk - page.OnDisk[i], 0) : default;
        Volatile.Write(ref page.Size[i], size);
        Volatile.Write(ref page.OnDisk[i], onDisk);
        Post(page.Parent[i], posted);
        return posted;
    }

    internal int FileIdTableSlots => _fileIds.Slots;

    // ---- Internals ----

    // Only a file charges, and only under the first name seen. What a folder or a link states as its own
    // size is nobody's bytes.
    private static bool Charges(Page page, int i) =>
        page.Kind[i] == (byte)EntryKind.File && (page.Flags[i] & (byte)EntryFlags.HardlinkDuplicate) == 0;

    // A parent is an entry already appended, so every Parent chain climbs to lower indices and ends at the
    // root; Post relies on it.
    private void CheckParent(int parent, int index)
    {
        if (index == 0)
        {
            if (parent != -1)
                throw new ArgumentOutOfRangeException(nameof(parent), parent, "The first entry is the root, whose parent is -1.");
            return;
        }
        if ((uint)parent >= (uint)index)
            throw new ArgumentOutOfRangeException(nameof(parent), parent, $"A parent must already be in the store, which holds {index} entries. Only the root has none.");

        Page page = _pages[parent >> PageShift]!;
        int p = parent & PageMask;
        if (page.Kind[p] != (byte)EntryKind.Folder)
            throw new InvalidOperationException($"Entry {parent} is a {(EntryKind)page.Kind[p]}, and only a folder holds entries.");
        if ((page.Flags[p] & (byte)EntryFlags.Unreadable) != 0)
            throw new InvalidOperationException($"Entry {parent} is a folder that could not be read, and nothing more is appended under it.");
    }

    // First seen wins: false, with the entry that already holds the ID, when the file was appended under
    // another name before. An entry with no ID (HasFileId) is never deduplicated.
    private bool TryAddFileId(int index, UInt128 fileId, out int firstHolder)
    {
        if (!HasFileId(fileId))
        {
            firstHolder = index;
            return true;
        }
        return _fileIds.TryAdd(index, out firstHolder);
    }

    // Adds a change to every folder from `folder` up to the root. A loop, never recursion: a chain can be
    // thousands of folders deep.
    private void Post(int folder, Posted posted)
    {
        if (posted == default)
            return;
        while (folder >= 0)
        {
            Page page = _pages[folder >> PageShift]!;
            int i = folder & PageMask;
            page.RolledSize[i] += posted.Size;
            page.RolledOnDisk[i] += posted.OnDisk;
            page.FilesBelow[i] += posted.Files;
            folder = page.Parent[i];
        }
    }

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

        // Meaningful on folder rows only: the sums over every file below the folder, at any depth.
        public readonly long[] RolledSize = new long[PageSize];
        public readonly long[] RolledOnDisk = new long[PageSize];
        public readonly int[] FilesBelow = new int[PageSize];
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
