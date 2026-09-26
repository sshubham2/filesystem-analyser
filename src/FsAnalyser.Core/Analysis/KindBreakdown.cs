using System.Runtime.InteropServices;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Analysis;

/// <summary>
/// The files of one scan by kind (SL-046): one row for each kind that holds any file, largest on disk first,
/// and the totals of all of them, which are the root's.
/// </summary>
/// <param name="Kinds">Only kinds with at least one file; empty when the scan saw none.</param>
/// <param name="Files">Every file, each name of a hardlinked file counted: the root's FilesBelow.</param>
/// <param name="Size">Logical bytes, each counted once: the root's RolledSize.</param>
/// <param name="OnDisk">Bytes on disk, each counted once: the root's RolledOnDisk.</param>
public sealed record KindBreakdownResult(IReadOnlyList<KindTotal> Kinds, long Files, long Size, long OnDisk);

/// <summary>One kind's files, their bytes, and its biggest extensions.</summary>
/// <param name="Id">A stable id for the kind, such as "disk-images".</param>
/// <param name="Label">The kind as the window names it, such as "Disk images".</param>
/// <param name="TopExtensions">Up to three, largest on disk first. Always empty for
/// <see cref="FileKind.NoExtension"/>, whose files have none.</param>
/// <param name="Remainder">What the listed extensions leave out of the kind's totals. Always empty for
/// <see cref="FileKind.NoExtension"/>: there is no extension to list.</param>
public sealed record KindTotal(FileKind Kind, string Id, string Label, long Files, long Size, long OnDisk,
    IReadOnlyList<ExtensionTotal> TopExtensions, ExtensionRemainder Remainder);

/// <summary>The files with one extension.</summary>
/// <param name="Ext">Lower case, with its dot (".mp4"). For a dotfile, its whole name (".gitignore").</param>
public readonly record struct ExtensionTotal(string Ext, long Files, long Size, long OnDisk);

/// <summary>
/// A kind's files whose extensions are not among its <see cref="KindTotal.TopExtensions"/>. Nothing is left out
/// when <see cref="Files"/> is 0.
/// </summary>
/// <param name="Extensions">How many distinct extensions it holds that were counted one by one.</param>
/// <param name="MoreUncounted">Whether it also holds extensions past <see cref="KindBreakdown.UnmappedExtensionCap"/>,
/// which were added up together without being told apart, so it holds more than <paramref name="Extensions"/>.</param>
public readonly record struct ExtensionRemainder(int Extensions, bool MoreUncounted, long Files, long Size, long OnDisk);

/// <summary>
/// Sorts a scan's files into kinds by extension (<see cref="FileKind"/>) and adds up each kind's files and bytes.
/// </summary>
/// <remarks>
/// One pass over the store. Each file entry is looked up by its extension, read from its name in place, in a table
/// of extensions seeded with every one the kind table names; an extension the table does not name gets an entry the
/// first time it is seen, lowered once, up to <see cref="UnmappedExtensionCap"/> of them, and those past the cap
/// share one entry. So the pass allocates nothing per file (spike breakdown-pass-cost-at-5m-records: a string per
/// file allocated 510 MiB a pass at 5M entries, a span lookup 400 bytes). Each extension is then given its kind once.
/// <para>A file counts once toward its kind, each name of a hardlinked file included, and adds the bytes the store
/// charges it: nothing for a later name of a hardlinked file. Folders and links are not files and add nothing. Those
/// are the store's own rules, applied by <see cref="ScanStore.ChargedSize"/> and <see cref="ScanStore.ChargedOnDisk"/>
/// and not worked out again here, so the kinds add up to the root's totals exactly (spike
/// breakdown-sums-reconcile-with-tree-under-counting-rules, go). Nothing here asks the disk again.</para>
/// <para>Call it on the store of a returned <c>ScanResult</c>, Completed or Cancelled: the scan writes the store on
/// its own thread and has finished with it by then, and a store is read nowhere else while it is written.</para>
/// </remarks>
public static class KindBreakdown
{
    /// <summary>
    /// How many distinct extensions the table does not name are told apart; files with any extension past that
    /// share one entry in Other, which no row lists but every total includes (<see cref="ExtensionRemainder"/>).
    /// </summary>
    /// <remarks>
    /// Sized from a real drive (SL-046 build, review M3): the C:\ of this project's PC, 3,642,885 files, carries
    /// 3,021 distinct extensions, 2,958 of them not in the table: under the first design's cap of 4,096, with not
    /// half as many again to spare. This one is 22 times what was seen; at about 100 bytes an entry, a pass that
    /// reaches it holds about 7 MiB more while it runs. The same pass over that store took 118 ms and allocated 1.3 MB.
    /// </remarks>
    public const int UnmappedExtensionCap = 65_536;

    private const int Listed = 3;

    /// <summary>The breakdown of every file in <paramref name="store"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public static KindBreakdownResult Compute(ScanStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var table = new ExtensionTable();
        int entries = store.Count;
        for (int i = 0; i < entries; i++)
        {
            if (store.Kind(i) != EntryKind.File)
                continue;
            ref Slot slot = ref table.Find(Path.GetExtension(store.Name(i)));
            slot.Files++;
            slot.Size += store.ChargedSize(i);
            slot.OnDisk += store.ChargedOnDisk(i);
        }
        return table.Fold();
    }

    // What one extension, or the entry past the cap, has added up to.
    private struct Slot
    {
        public string? Ext; // null for the entry past the cap
        public FileKind Kind;
        public long Files;
        public long Size;
        public long OnDisk;
    }

    private sealed class ExtensionTable
    {
        private readonly Dictionary<string, int> _index;
        private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _byExtension;
        private readonly List<Slot> _slots;
        private int _unmapped;
        private int _pastCap = -1;

        public ExtensionTable()
        {
            _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _slots = [];
            foreach ((string ext, FileKind kind) in FileKinds.Mapped)
                Add(ext.ToLowerInvariant(), kind);
            Add("", FileKind.NoExtension);
            _byExtension = _index.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        public ref Slot Find(ReadOnlySpan<char> ext)
        {
            if (!_byExtension.TryGetValue(ext, out int at))
            {
                // Not named by the table, so Other, whatever case it is written in.
                if (_unmapped < UnmappedExtensionCap)
                {
                    _unmapped++;
                    at = Add(Lowered(ext), FileKind.Other);
                }
                else
                {
                    if (_pastCap < 0)
                    {
                        _pastCap = _slots.Count;
                        _slots.Add(new Slot { Ext = null, Kind = FileKind.Other });
                    }
                    at = _pastCap;
                }
            }
            return ref CollectionsMarshal.AsSpan(_slots)[at];
        }

        public KindBreakdownResult Fold()
        {
            var perKind = new List<Slot>[Enum.GetValues<FileKind>().Length];
            long files = 0, size = 0, onDisk = 0;
            foreach (Slot slot in _slots)
            {
                if (slot.Files == 0)
                    continue;
                (perKind[(int)slot.Kind] ??= []).Add(slot);
                files += slot.Files;
                size += slot.Size;
                onDisk += slot.OnDisk;
            }

            var kinds = new List<KindTotal>();
            for (int k = 0; k < perKind.Length; k++)
            {
                if (perKind[k] is { } slots)
                    kinds.Add(Total((FileKind)k, slots));
            }
            kinds.Sort(static (a, b) =>
                a.OnDisk != b.OnDisk ? b.OnDisk.CompareTo(a.OnDisk)
                : a.Size != b.Size ? b.Size.CompareTo(a.Size)
                : a.Kind.CompareTo(b.Kind));
            return new KindBreakdownResult(kinds.AsReadOnly(), files, size, onDisk);
        }

        // A kind's totals, its three biggest extensions, and what they leave out. The entry past the cap and the
        // empty extension are never listed: one is many extensions added together, the other is no extension.
        private static KindTotal Total(FileKind kind, List<Slot> slots)
        {
            long files = 0, size = 0, onDisk = 0;
            var named = new List<Slot>();
            foreach (Slot slot in slots)
            {
                files += slot.Files;
                size += slot.Size;
                onDisk += slot.OnDisk;
                if (!string.IsNullOrEmpty(slot.Ext))
                    named.Add(slot);
            }
            named.Sort(static (a, b) =>
                a.OnDisk != b.OnDisk ? b.OnDisk.CompareTo(a.OnDisk)
                : a.Size != b.Size ? b.Size.CompareTo(a.Size)
                : string.CompareOrdinal(a.Ext, b.Ext));

            var top = new ExtensionTotal[Math.Min(Listed, named.Count)];
            long listedFiles = 0, listedSize = 0, listedOnDisk = 0;
            for (int k = 0; k < top.Length; k++)
            {
                Slot slot = named[k];
                top[k] = new ExtensionTotal(slot.Ext!, slot.Files, slot.Size, slot.OnDisk);
                listedFiles += slot.Files;
                listedSize += slot.Size;
                listedOnDisk += slot.OnDisk;
            }

            bool pastCap = slots.Exists(static s => s.Ext is null);
            var remainder = kind == FileKind.NoExtension
                ? default
                : new ExtensionRemainder(named.Count - top.Length, pastCap, files - listedFiles, size - listedSize, onDisk - listedOnDisk);
            return new KindTotal(kind, FileKinds.Id(kind), FileKinds.Label(kind), files, size, onDisk,
                Array.AsReadOnly(top), remainder);
        }

        private int Add(string ext, FileKind kind)
        {
            int at = _slots.Count;
            _index.Add(ext, at);
            _slots.Add(new Slot { Ext = ext, Kind = kind });
            return at;
        }

        private static string Lowered(ReadOnlySpan<char> ext) =>
            string.Create(ext.Length, ext, static (span, source) => source.ToLowerInvariant(span));
    }
}
