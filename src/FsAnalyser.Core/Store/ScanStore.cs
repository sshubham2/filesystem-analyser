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
/// the same terms as the rolled totals: between appends on the writing thread and once the scan has returned.
/// <see cref="NotListedAtOrBelow"/> derives the answer for a whole subtree from the marks and from the depth-first,
/// one-block-per-folder order a scan registers folders in; it stores nothing and is exact on the same terms.</para>
/// <para>What a stopped scan had not yet sized from the file itself (SL-112) is told by one number, not a mark per
/// entry: the first entry whose size check was not applied (<see cref="EndSizeChecks"/>). Checks are applied in ascending
/// index order and a batch is applied whole or not at all, so the checks still pending are exactly the entries of a
/// sized-from-the-file kind (<c>EntryRules.ChecksOwnSize</c>) at or after that index. <see cref="SizeCheckPending"/>,
/// <see cref="Unfinished"/> and <see cref="AtLeast"/> derive their answers from it and from the columns that exist.
/// They are exact once the scan has returned. Before that, on the writing thread, the number is still 0 and they
/// over-mark: a folder is never called finished that was not. A folder's descendants are one contiguous run of indices
/// from its first child, found by the same depth-first, one-block-per-folder order <see cref="NotListedAtOrBelow"/> needs;
/// a store registered in any other order gets a wrong answer and no error.</para>
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
    // The first entry whose size check was not applied (EndSizeChecks); 0 until a scan has said, so that a store nobody
    // has declared over-marks and never calls a folder finished that was not.
    private int _firstUnchecked;
    private int _namePage = -1;
    private int _nameUsed = NamePageChars;
    private readonly FileIdIndex _fileIds;
    private readonly TaskCompletionSource<long?> _volumeUsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<long?> _volumeUsedMft = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ScanStore()
    {
        _fileIds = new FileIdIndex(this);
    }

    /// <summary>The number of entries, including the root.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// For a scan of a whole drive (<c>C:\</c>), the bytes the volume reports in use, read once as the scan
    /// starts: what the tree is set against to show how much of the drive the scan could not see. Null for a
    /// scan of a folder, a share or a drive on a network file system (SL-001), when the root could not be opened to be asked,
    /// and when the volume did not answer within the read's bound (SL-043).
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

    /// <summary>
    /// The size of the volume's $MFT, its valid data length (U-001), read by the same thread as <see cref="VolumeUsed"/> in
    /// the same breath, so the window can set the one measured part of the space the scan did not see apart from the rest
    /// (SL-001). Null when the volume is not NTFS, its $MFT could not be read, the root is not a whole local drive, or the
    /// read's bound passed: so null whenever <see cref="VolumeUsed"/> is null, except when the read lands just as its bound
    /// passes, when the size can stand beside a null figure. Complete whenever <see cref="VolumeUsed"/> has completed with a number.
    /// </summary>
    /// <remarks>
    /// Never null itself, completed at most once and never faulted, and nothing in Core waits for it. It is answered strictly
    /// before <see cref="VolumeUsed"/>, so once that has completed with a number this one is complete too, and a reader
    /// that looks only after it never meets it pending. Named after the figure so that the rule that nothing waits on the
    /// figure covers this one too.
    /// </remarks>
    public Task<long?> VolumeUsedMft => _volumeUsedMft.Task;

    /// <summary>The completion source behind <see cref="VolumeUsedMft"/>, which the scan hands to the volume read.</summary>
    internal TaskCompletionSource<long?> VolumeUsedMftSource => _volumeUsedMft;

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

    /// <summary>
    /// Whether this folder, or any folder below it at any depth, is not listed (<see cref="NotListed"/>): something a
    /// stopped scan never read lies at or under it. False for every file and link, and for every entry once a scan has
    /// Completed or was stopped in the size probes, after every listing had committed. A root that was picked, opened and
    /// listed nothing (the scan ends unreadable) is still not listed, so it reads true, as <see cref="NotListed"/> does.
    /// </summary>
    /// <remarks>
    /// Exact on the same terms as <see cref="NotListed"/>: between appends on the writing thread, and after the scan has
    /// returned; a read from another thread while appends go on is not part of the contract.
    /// <para>It stores nothing and is derived from the marks, and it is exact only for a store registered in the order a
    /// scan registers it: one writing thread, folders listed depth-first with each folder's subfolders taken in listing
    /// order, and each folder's children appended as one contiguous block and registered by one
    /// <see cref="SetChildren"/> after the whole block. Then the folders listed so far are a prefix of the preorder of the
    /// folder tree, a folder cut part-way commits nothing, and so a listed last subfolder means every earlier sibling's
    /// subtree is finished: the last subfolder is the only one to follow, down to the first folder not listed. A store
    /// registered in any other order (a queue, folders listed two at a time, a block registered child by child, as the
    /// test oracle's feeder does) gets a wrong answer here and no error. Whoever changes that order must replace this read.</para>
    /// <para>It allocates nothing. Over all the rows of one folder it reads each entry below that folder at most once,
    /// because the chains of two children lie in disjoint subtrees.</para>
    /// </remarks>
    public bool NotListedAtOrBelow(int index)
    {
        Page page = At(index, out int i);
        while (true)
        {
            if ((page.Flags[i] & NotListedBit) != 0)
                return true;
            int count = page.ChildCount[i];
            if (count == 0 || page.Kind[i] != (byte)EntryKind.Folder)
                return false;

            // The block's last folder, each entry's page looked up on its own: a block can straddle two pages.
            int first = page.FirstChild[i];
            int last = first + count - 1;
            while (last >= first && _pages[last >> PageShift]!.Kind[last & PageMask] != (byte)EntryKind.Folder)
                last--;
            if (last < first)
                return false;
            page = _pages[last >> PageShift]!;
            i = last & PageMask;
        }
    }

    /// <summary>
    /// Whether this file's own size check had not been applied when the scan ended: a file of a sized-from-the-file kind
    /// (<c>EntryRules.ChecksOwnSize</c>) at or after the first entry whose check was not applied. False for a folder, a link,
    /// a cloud-only file, a later name of a hardlinked file, and for every entry once a scan has Completed.
    /// </summary>
    /// <remarks>Exact once the scan has returned; see the class remarks for the store before that.</remarks>
    public bool SizeCheckPending(int index)
    {
        Page page = At(index, out int i);
        return IsSizeCheckPending(page, i, index);
    }

    /// <summary>
    /// Whether this entry's figures could still change: for a folder, anything at or below it is not listed
    /// (<see cref="NotListedAtOrBelow"/>) or a file below it has a size check that was not applied; for a file,
    /// <see cref="SizeCheckPending"/>. A pending check can raise or lower its file's figures, and a hardlink's charge is
    /// its first name's own check, so this is also the folders a pending charge could move. False for a link, and for every
    /// entry once a scan has Completed, a file that vanished before its check included.
    /// </summary>
    /// <remarks>
    /// Exact once the scan has returned; on the writing thread before it, it over-marks (see the class remarks). It
    /// stores nothing and allocates nothing. It reads the run of entries from the folder's first child to the end of its
    /// descendants, from the first entry whose check was not applied, and stops at the first pending file.
    /// </remarks>
    public bool Unfinished(int index)
    {
        Page page = At(index, out int i);
        if (page.Kind[i] == (byte)EntryKind.File)
            return IsSizeCheckPending(page, i, index);
        if (page.Kind[i] != (byte)EntryKind.Folder)
            return false;
        if (NotListedAtOrBelow(index))
            return true;
        if (!PendingRun(page, i, out int start, out int end))
            return false;
        for (int e = start; e < end;)
        {
            Page run = _pages[e >> PageShift]!;
            int stop = Math.Min(end, (e | PageMask) + 1);
            for (int k = e & PageMask, last = k + (stop - e); k < last; k++)
            {
                if (EntryRules.ChecksOwnSize((EntryKind)run.Kind[k], (EntryFlags)run.Flags[k]))
                    return true;
            }
            e = stop;
        }
        return false;
    }

    /// <summary>
    /// What the entry holds for certain: a lower bound on the size, on-disk size and file count an uninterrupted scan of
    /// the unchanged tree gives it. For a folder it is the rolled figures less the listing's size and on-disk size of every
    /// file below it whose size check was not applied, which may be above or below the file's own; the file count is
    /// <see cref="FilesBelow"/>. For a file, (0, 0, 1) when its check is pending, else its charged figures; for a link,
    /// nothing. It equals (<see cref="RolledSize"/>, <see cref="RolledOnDisk"/>, <see cref="FilesBelow"/>) whenever
    /// <see cref="Unfinished"/> is false, and for every entry once a scan has Completed.
    /// </summary>
    /// <remarks>
    /// Exact on the same terms as <see cref="Unfinished"/>. The rolled totals are not rewritten: they stay the listing's
    /// figures plus every correction applied, which a pending check can still lower. A file the stopped scan did list but
    /// whose check is pending counts as a file and as 0 bytes. It allocates nothing.
    /// </remarks>
    public AtLeastFigures AtLeast(int index)
    {
        Page page = At(index, out int i);
        if (page.Kind[i] == (byte)EntryKind.File)
        {
            if (IsSizeCheckPending(page, i, index))
                return new AtLeastFigures(0, 0, 1);
            return new AtLeastFigures(Charges(page, i) ? Volatile.Read(ref page.Size[i]) : 0, Charges(page, i) ? Volatile.Read(ref page.OnDisk[i]) : 0, 1);
        }
        if (page.Kind[i] != (byte)EntryKind.Folder)
            return default;

        long size = page.RolledSize[i], onDisk = page.RolledOnDisk[i];
        if (PendingRun(page, i, out int start, out int end))
        {
            for (int e = start; e < end;)
            {
                Page run = _pages[e >> PageShift]!;
                int stop = Math.Min(end, (e | PageMask) + 1);
                for (int k = e & PageMask, last = k + (stop - e); k < last; k++)
                {
                    if (!EntryRules.ChecksOwnSize((EntryKind)run.Kind[k], (EntryFlags)run.Flags[k]))
                        continue;
                    size -= run.Size[k];
                    onDisk -= run.OnDisk[k];
                }
                e = stop;
            }
        }
        return new AtLeastFigures(size, onDisk, page.FilesBelow[i]);
    }

    private bool IsSizeCheckPending(Page page, int i, int index) =>
        index >= Volatile.Read(ref _firstUnchecked) && EntryRules.ChecksOwnSize((EntryKind)page.Kind[i], (EntryFlags)page.Flags[i]);

    // The run of entries below this folder whose size check may be pending: from the later of its first child and the first
    // entry whose check was not applied, to the end of its descendants. False when the run is empty. A folder's descendants
    // are its block, then the descendants of each of its folders in order (depth-first, one block each), so they end where
    // the last block of the chain of last folders-with-children ends.
    private bool PendingRun(Page page, int i, out int start, out int end)
    {
        start = end = 0;
        int count = page.ChildCount[i];
        int watermark = Volatile.Read(ref _firstUnchecked);
        if (count == 0 || watermark >= Count)
            return false;

        end = page.FirstChild[i] + count;
        start = Math.Max(watermark, page.FirstChild[i]);
        while (true)
        {
            int first = page.FirstChild[i];
            int last = first + page.ChildCount[i] - 1;
            while (last >= first)
            {
                Page child = _pages[last >> PageShift]!;
                int c = last & PageMask;
                if (child.Kind[c] == (byte)EntryKind.Folder && child.ChildCount[c] > 0)
                {
                    page = child;
                    i = c;
                    end = child.FirstChild[c] + child.ChildCount[c];
                    break;
                }
                last--;
            }
            if (last < first)
                return start < end;
        }
    }

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

    /// <summary>
    /// Says where the scan's size checks stopped: <paramref name="firstUnchecked"/> is the first entry whose check was not
    /// applied, so every entry of a sized-from-the-file kind (<c>EntryRules.ChecksOwnSize</c>) from there on is pending
    /// (<see cref="SizeCheckPending"/>). The scan's last write to the store, once, as it returns: the store's entry count
    /// after a scan that ran to the end, the first entry of the batch that was cancelled after a stop in the size checks,
    /// and 0 after a stop in the listing, which never reached them.
    /// </summary>
    /// <remarks>
    /// Relies on the checks being applied in ascending index order and on a cancelled batch applying none of its readings;
    /// whoever changes either must replace this number with something per entry or per run. 0 is accepted on an empty store.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 0 or above the entry count.</exception>
    internal void EndSizeChecks(int firstUnchecked)
    {
        if ((uint)firstUnchecked > (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(firstUnchecked), firstUnchecked, $"The store holds {Count} entries.");
        Volatile.Write(ref _firstUnchecked, firstUnchecked);
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
