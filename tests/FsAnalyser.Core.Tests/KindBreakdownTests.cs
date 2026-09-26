using System.Globalization;
using FsAnalyser.App;
using FsAnalyser.Core.Analysis;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The breakdown by kind of file (SL-046): the kind a name gets, and every kind's files and bytes against the root's
/// totals, over real trees under %TEMP% where a counting rule is in play and over stores built in memory where a real
/// tree cannot take the shape (files that live only online, tens of thousands of extensions). The window's words for
/// the rows (KindRowText, linked from the App) are checked on the same results.
/// </summary>
public class KindBreakdownTests(ITestOutputHelper output)
{
    private const long MiB = TempTree.MiB;
    private const long KiB = 1024;
    private const uint FileListDirectory = 0x1;

    // The window's size formatter stands in as bytes, so the words can be compared exactly.
    private static readonly Func<long, string> Bytes = b => $"{b} B";

    // ---- The kind a name gets ----

    [Theory]
    [InlineData("MOVIE.MP4", FileKind.Videos, ".mp4")]
    [InlineData("Holiday.Mkv", FileKind.Videos, ".mkv")]
    [InlineData("a.tar.gz", FileKind.Archives, ".gz")]
    [InlineData("Backup.TAR.GZ", FileKind.Archives, ".gz")]
    [InlineData("IMG_0001.JPEG", FileKind.Images, ".jpeg")]
    [InlineData("Book.AAX", FileKind.Audio, ".aax")]
    [InlineData("Level3.PAK", FileKind.Other, ".pak")]
    public void MixedCaseAndMultiPartNamesClassifyByTheirLastExtension(string name, FileKind kind, string listed)
    {
        Assert.Equal(kind, FileKinds.Of(Path.GetExtension(name.AsSpan())));

        // Through a pass, beside the same name in lower case: one kind, one extension, written in lower case.
        ScanStore store = Synthetic((name, 4 * KiB, 4 * KiB), (name.ToLowerInvariant(), 8 * KiB, 8 * KiB));
        KindBreakdownResult kinds = KindBreakdown.Compute(store);

        AssertAddsUp(store, kinds);
        KindTotal row = Assert.Single(kinds.Kinds);
        Assert.Equal(kind, row.Kind);
        Assert.Equal(2, row.Files);
        ExtensionTotal extension = Assert.Single(row.TopExtensions);
        Assert.Equal((listed, 2L, 12 * KiB), (extension.Ext, extension.Files, extension.OnDisk));
    }

    [Theory]
    [InlineData("noext")]
    [InlineData("trailingdot.")]
    [InlineData("Makefile")]
    [InlineData("archive.tar.")]
    public void ANameWithNoExtensionOrATrailingDotIsTheNoExtensionRow(string name)
    {
        Assert.Equal(FileKind.NoExtension, FileKinds.Of(Path.GetExtension(name.AsSpan())));

        ScanStore store = Synthetic((name, 4 * KiB, 4 * KiB));
        KindBreakdownResult kinds = KindBreakdown.Compute(store);

        AssertAddsUp(store, kinds);
        KindTotal row = Assert.Single(kinds.Kinds);
        Assert.Equal((FileKind.NoExtension, "no-extension", "No extension", 1L), (row.Kind, row.Id, row.Label, row.Files));
        Assert.Empty(row.TopExtensions); // the empty extension is never listed as one
        Assert.Equal(default, row.Remainder);
        Assert.Equal("—", KindRowText.Extensions(row, Bytes));
    }

    [Fact]
    public void ADotfileIsOtherUnderItsWholeNameNotNoExtension()
    {
        Assert.Equal(FileKind.Other, FileKinds.Of(Path.GetExtension(".gitignore".AsSpan())));

        ScanStore store = Synthetic((".gitignore", 100, 0), (".GITIGNORE", 60, 0), (".editorconfig", 50, 0));
        KindBreakdownResult kinds = KindBreakdown.Compute(store);

        AssertAddsUp(store, kinds);
        KindTotal row = Assert.Single(kinds.Kinds);
        Assert.Equal(FileKind.Other, row.Kind);
        Assert.Equal([(".gitignore", 2L), (".editorconfig", 1L)], row.TopExtensions.Select(e => (e.Ext, e.Files)));
        Assert.Equal(".gitignore 0 B · .editorconfig 0 B", KindRowText.Extensions(row, Bytes));
    }

    /// <summary>
    /// Extensions whose letters lower onto an extension ignoring case does not match (review M1): KELVIN SIGN U+212A
    /// lowers to 'k', so "clip.mKv" onto the table's ".mkv", and ANGSTROM SIGN, OHM SIGN and CAPITAL SHARP S
    /// onto the letters written before them here. (The title-case digraph U+01C5, which the review listed too, is one
    /// ignoring case already matches with U+01C6.) The pass does not throw: each file is counted under the extension
    /// it lowers to, whichever of a pair comes first, in a store and in a real scan.
    /// </summary>
    [Fact]
    public void AnExtensionThatLowersOntoOneAlreadyCountedIsCountedWithIt()
    {
        string[] names =
        [
            "clip.mKv",        // onto ".mkv", which the table names
            "a.k", "b.K",       // onto ".k", counted the file before
            "c.å", "d.Å",  // å, then ANGSTROM SIGN
            "e.ω", "f.Ω",  // ω, then OHM SIGN
            "g.ß", "h.ẞ",  // ß, then CAPITAL SHARP S
            "x.ǆ", "y.ǅ",  // dž, then its title case
            "r.Kr", "s.kr",     // the other way round: the sign first, then the letter it lowers to
        ];
        ScanStore store = Synthetic([.. names.Select(n => (n, 4 * KiB, 4 * KiB))]);
        KindBreakdownResult kinds = KindBreakdown.Compute(store);

        AssertAddsUp(store, kinds);
        Assert.Equal(names.Length, kinds.Files);
        Assert.Equal([FileKind.Other, FileKind.Videos], kinds.Kinds.Select(k => k.Kind));
        KindTotal videos = Row(kinds, FileKind.Videos);
        Assert.Equal((".mkv", 1L), (Assert.Single(videos.TopExtensions).Ext, videos.Files));
        Assert.Equal(FileKind.Videos, FileKinds.Of(Path.GetExtension(names[0].AsSpan()))); // one answer for the name

        // Six extensions of two files each, the same size, so listed in ordinal order.
        KindTotal other = Row(kinds, FileKind.Other);
        Assert.Equal([(".k", 2L), (".kr", 2L), (".ß", 2L)], other.TopExtensions.Select(e => (e.Ext, e.Files)));
        Assert.Equal((3, false, 6L), (other.Remainder.Extensions, other.Remainder.MoreUncounted, other.Remainder.Files));
        Assert.All(names[1..], n => Assert.Equal(FileKind.Other, FileKinds.Of(Path.GetExtension(n.AsSpan()))));

        // The same names on disk, through the scanner.
        using var tree = new TempTree();
        foreach (string name in names)
            tree.File(name, 64 * KiB);
        ScanResult result = Scans.Run(tree.Root);
        KindBreakdownResult scanned = KindBreakdown.Compute(result.Store);

        Assert.Equal(ScanStatus.Completed, result.Status);
        AssertAddsUp(result.Store, scanned);
        Assert.Equal((long)names.Length, scanned.Files);
        Assert.Equal((1L, 12L), (Row(scanned, FileKind.Videos).Files, Row(scanned, FileKind.Other).Files));
    }

    [Fact]
    public void ASetupProgramIsAProgramAndOnlyPackagesAreInstallers()
    {
        // The names the installers spike found in a real Downloads folder: a name rule caught 2 of 8.
        ScanStore store = Synthetic(
            ("Hermes-Setup.exe", 8 * MiB, 8 * MiB),
            ("setup.exe", 2 * MiB, 2 * MiB),
            ("audacity-win-3.7.7-64bit.exe", 19 * MiB, 19 * MiB),
            ("audacity-win-4.0.0-x86_64.msi", 50 * MiB, 50 * MiB),
            ("App.Msix", 1 * MiB, 1 * MiB),
            ("windows11.0-kb5034441-x64.msu", 3 * MiB, 3 * MiB));
        KindBreakdownResult kinds = KindBreakdown.Compute(store);

        AssertAddsUp(store, kinds);
        Assert.Equal([FileKind.Installers, FileKind.Programs], kinds.Kinds.Select(k => k.Kind));
        KindTotal installers = kinds.Kinds[0], programs = kinds.Kinds[1];
        Assert.Equal((3L, 54 * MiB), (installers.Files, installers.OnDisk));
        Assert.Equal([".msi", ".msu", ".msix"], installers.TopExtensions.Select(e => e.Ext));
        Assert.Equal((3L, 29 * MiB), (programs.Files, programs.OnDisk));
        Assert.Equal((".exe", 3L), (Assert.Single(programs.TopExtensions).Ext, programs.TopExtensions[0].Files));
        Assert.Equal("Programs & system files", programs.Label);

        // Both rows say where a setup program went when pointed at; no other row says anything more.
        KindRow[] rows = KindRowText.Rows(kinds, Bytes);
        Assert.All(rows, r => Assert.Equal(KindRowText.InstallersTip, r.Tip));
        Assert.Null(KindRowText.Tip(FileKind.Videos));
        Assert.Null(KindRowText.Tip(FileKind.Other));
    }

    // ---- Every kind against the root, on real trees ----

    /// <summary>
    /// The reconcile spike's tree, ported: each counting rule in play at once, and the kinds adding up to the root's
    /// totals to the byte. A file with two names adds its bytes once; links to a folder and to a file outside the tree
    /// add nothing and are not followed; a compressed and a sparse file keep both their sizes; a file at a 359-character
    /// path is counted; the files of a folder the scan could not read are in no kind, as they are in no total.
    /// </summary>
    [Fact]
    public void AddsUpToTheRootOnARealTreeUnderEveryCountingRule()
    {
        using var tree = new TempTree();
        tree.Folder("1-a");
        tree.Folder("2-b");
        string movie = tree.File(@"1-a\movie.mp4", 3 * MiB);
        string again = TempTree.Hardlink(tree.PathOf(@"2-b\movie-again.mp4"), movie);

        string outside = tree.Outside("outside");
        string zip = TempTree.WriteFile(Path.Combine(outside, "outside.zip"), 5 * MiB);
        string[] links = [tree.Junction("jn", outside), tree.DirectorySymlink("sym", outside), tree.FileSymlink("link.zip", zip)];

        string notes = tree.TextFile("notes.txt", 4 * MiB);
        TempTree.Compress(notes);
        tree.SparseFile("disk.iso", 1024 * MiB, writeOffset: 512 * MiB, writeBytes: 64 * (int)KiB);

        string photo = LongPathFile(tree, "photo.jpg", 359, 64 * KiB);
        string denied = tree.Folder("denied");
        tree.File(@"denied\hidden.mkv", 2 * MiB);
        tree.Deny(denied, FileListDirectory);

        tree.File(".gitignore", 100);
        tree.File("noext", 200);
        tree.File("UPPER.MP4", 1000);
        tree.File("a.tar.gz", 300);

        ScanResult result = Scans.Run(tree.Root);
        KindBreakdownResult kinds = KindBreakdown.Compute(result.Store);

        // The shapes were fed in.
        ScanStore store = result.Store;
        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(EntryFlags.HardlinkDuplicate, store.Flags(Scans.Find(result, again)) & EntryFlags.HardlinkDuplicate);
        Assert.All(links, link => Assert.Equal(EntryKind.Link, store.Kind(Scans.Find(result, link))));
        Assert.Equal(359, store.GetPath(Scans.Find(result, photo)).Length);
        Assert.Equal(Scans.Find(result, denied), Assert.Single(result.Unreadable).Entry);

        AssertAddsUp(store, kinds);
        Assert.Equal((result.Totals.Files, result.Totals.Bytes, result.Totals.BytesOnDisk), (kinds.Files, kinds.Size, kinds.OnDisk));
        output.WriteLine($"{kinds.Files} files, {kinds.Size:N0} B, {kinds.OnDisk:N0} B on disk: " +
            string.Join("; ", kinds.Kinds.Select(k => $"{k.Id} {k.Files} files {k.Size:N0} B {k.OnDisk:N0} B on disk")));

        KindTotal videos = Row(kinds, FileKind.Videos);
        Assert.Equal(3, videos.Files); // both names of the hardlinked file, and UPPER.MP4; not hidden.mkv
        Assert.Equal(3 * MiB + 1000, videos.Size); // the hardlinked file's bytes once
        Assert.Equal((".mp4", 3L), (Assert.Single(videos.TopExtensions).Ext, videos.TopExtensions[0].Files));

        KindTotal archives = Row(kinds, FileKind.Archives);
        Assert.Equal((1L, 300L), (archives.Files, archives.Size)); // a.tar.gz; outside.zip is only behind links
        Assert.Equal(".gz", Assert.Single(archives.TopExtensions).Ext);

        KindTotal documents = Row(kinds, FileKind.Documents);
        Assert.Equal(4 * MiB, documents.Size);
        Assert.InRange(documents.OnDisk, 1, documents.Size / 2); // compressed

        KindTotal diskImages = Row(kinds, FileKind.DiskImages);
        Assert.Equal(1024 * MiB, diskImages.Size);
        Assert.InRange(diskImages.OnDisk, 64 * KiB, 1 * MiB); // sparse: 64 KiB written
        Assert.True(IndexOf(kinds, FileKind.DiskImages) > IndexOf(kinds, FileKind.Videos),
            "the sparse image is the largest by size and ranks below the videos by space on disk");

        Assert.Equal((1L, 64 * KiB), (Row(kinds, FileKind.Images).Files, Row(kinds, FileKind.Images).Size)); // the long path
        Assert.Equal((1L, 200L), (Row(kinds, FileKind.NoExtension).Files, Row(kinds, FileKind.NoExtension).Size));
        Assert.Equal(".gitignore", Assert.Single(Row(kinds, FileKind.Other).TopExtensions).Ext);
        Assert.Equal(7, kinds.Kinds.Count);
    }

    /// <summary>
    /// A stopped scan leaves a partial store that nothing writes any more: its kinds are computed like any other's and
    /// add up to what the scan read, which is less than the tree holds (review m2).
    /// </summary>
    [Fact]
    public void AStoppedScanAddsUpToWhatItRead()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 10; i++)
        {
            tree.Folder($"f{i}");
            tree.File($@"f{i}\clip.mp4", (i + 1) * 64 * KiB);
            tree.File($@"f{i}\notes.PDF", 4 * KiB);
            tree.File($@"f{i}\README", 100);
        }
        using var cts = new CancellationTokenSource();
        var options = new ScanOptions
        {
            AfterFolder = n =>
            {
                if (n == 3)
                    cts.Cancel();
            },
        };

        ScanResult result = Scanner.Scan(tree.Root, new ScanProgress(), cts.Token, options);
        Scans.AssertChargedBytesAreTheTotals(result);
        KindBreakdownResult kinds = KindBreakdown.Compute(result.Store);

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        AssertAddsUp(result.Store, kinds);
        Assert.Equal((result.Totals.Files, result.Totals.Bytes, result.Totals.BytesOnDisk), (kinds.Files, kinds.Size, kinds.OnDisk));
        Assert.Equal(6, kinds.Files); // the root, f0 and f1 were read: three files each in two folders, of 30
        Assert.Equal((2L, 3 * 64 * KiB), (Row(kinds, FileKind.Videos).Files, Row(kinds, FileKind.Videos).Size));
        Assert.Equal(2, Row(kinds, FileKind.Documents).Files);
        Assert.Equal(2, Row(kinds, FileKind.NoExtension).Files);
    }

    // ---- Shapes a real tree cannot take ----

    /// <summary>
    /// Files that live only online: sizes, and nothing on disk anywhere. The rows are still listed, largest by size,
    /// and no share is divided by the zero total.
    /// </summary>
    [Fact]
    public void FilesWithNothingOnDiskAreListedAndTheirShareIsADash()
    {
        ScanStore store = Synthetic(("film.mkv", 4096 * MiB, 0), ("photo.HEIC", 3 * MiB, 0), ("report.pdf", 200 * KiB, 0), ("notes.pdf", 1 * KiB, 0));
        KindBreakdownResult kinds = KindBreakdown.Compute(store);

        AssertAddsUp(store, kinds);
        Assert.Equal((4L, 0L), (kinds.Files, kinds.OnDisk));
        Assert.Equal(4096 * MiB + 3 * MiB + 201 * KiB, kinds.Size);
        Assert.Equal([FileKind.Videos, FileKind.Images, FileKind.Documents], kinds.Kinds.Select(k => k.Kind));

        KindRow[] rows = KindRowText.Rows(kinds, Bytes);
        Assert.Equal(3, rows.Length);
        Assert.All(rows, r => Assert.Equal("—", r.Share));
        Assert.Equal([$"{4096 * MiB} B", $"{3 * MiB} B", $"{201 * KiB} B"], rows.Select(r => r.Size));
        Assert.Equal(".pdf 0 B", rows[2].Extensions); // on disk, as the rows are ranked

        // With something on disk, a share is a percentage of it, written as the folder tree writes its shares (review
        // m2): one formatter, whose exact midpoints (1/16 is 6.25) round away from zero.
        Assert.Equal(25.0.ToString("F1", CultureInfo.CurrentCulture) + " %", KindRowText.Share(1, 4));
        Assert.Equal(6.3.ToString("F1", CultureInfo.CurrentCulture) + " %", KindRowText.Share(4 * KiB, 64 * KiB));
        Assert.Equal(ShareText.Percent(1 / 16.0), KindRowText.Share(1, 16));
        Assert.Equal("—", KindRowText.Share(0, 0));
    }

    [Fact]
    public void AStoreWithNoFilesHasNoKindsAndZeroTotals()
    {
        var foldersOnly = new ScanStore();
        foldersOnly.AppendRoot(@"C:\folders", 0x10);
        foldersOnly.Append(0, "a", EntryKind.Folder, EntryFlags.None, 0x10, 4096, 4096, UInt128.Zero);
        foldersOnly.Append(0, "b.zip", EntryKind.Folder, EntryFlags.None, 0x10, 4096, 4096, UInt128.Zero);
        foldersOnly.Append(0, "link.mp4", EntryKind.Link, EntryFlags.None, 0x420, 1 * MiB, 1 * MiB, UInt128.Zero);
        foldersOnly.SetChildren(0, 1, 3);
        foldersOnly.Append(1, "c", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
        foldersOnly.SetChildren(1, 4, 1);

        using var tree = new TempTree();
        tree.Folder(@"empty\deeper");
        ScanResult scanned = Scans.Run(tree.PathOf("empty"));

        foreach (ScanStore store in new[] { Synthetic(), foldersOnly, scanned.Store })
        {
            KindBreakdownResult kinds = KindBreakdown.Compute(store);
            Assert.Empty(kinds.Kinds);
            Assert.Equal((0L, 0L, 0L), (kinds.Files, kinds.Size, kinds.OnDisk));
            AssertAddsUp(store, kinds);
            Assert.Empty(KindRowText.Rows(kinds, Bytes));
        }
        Assert.Throws<ArgumentNullException>(() => KindBreakdown.Compute(null!));
    }

    /// <summary>
    /// More distinct extensions than the pass tells apart (review M3). Those past the cap share one entry in Other that
    /// no row lists, and every total still includes them: one of them larger than every extension listed shows in the
    /// row's remainder, which says more were left uncounted. An extension counted before the cap and seen again after it
    /// keeps its own entry, and a mapped one (.mp4) after all of them is still a video.
    /// </summary>
    [Fact]
    public void ExtensionsPastTheCapAreNeverListedButAlwaysCounted()
    {
        int cap = KindBreakdown.UnmappedExtensionCap;
        Assert.True(cap > 4096, $"the cap is {cap}"); // more than the first design's, which this shape was written against
        var files = new List<(string, long, long)>();
        for (int i = 0; i < cap; i++)
            files.Add(($"f{i}.u{i:x}", 4 * KiB, 4 * KiB));
        files.Add(("again.U0", 4 * KiB, 4 * KiB));
        files.Add(("huge.pastcap", 1024 * MiB, 1024 * MiB));
        for (int i = 0; i < 50; i++)
            files.Add(($"g{i}.v{i}", 4 * KiB, 4 * KiB));
        files.Add(("clip.MP4", 8 * KiB, 8 * KiB));
        ScanStore store = Synthetic([.. files]);

        KindBreakdownResult kinds = KindBreakdown.Compute(store);

        AssertAddsUp(store, kinds);
        Assert.Equal(cap + 53, kinds.Files);
        Assert.Equal((".mp4", 1L), (Assert.Single(Row(kinds, FileKind.Videos).TopExtensions).Ext, Row(kinds, FileKind.Videos).Files));

        KindTotal other = Row(kinds, FileKind.Other);
        Assert.Equal(cap + 52, other.Files);
        Assert.Equal([(".u0", 2L), (".u1", 1L), (".u10", 1L)], other.TopExtensions.Select(e => (e.Ext, e.Files)));
        Assert.DoesNotContain(other.TopExtensions, e => e.Ext == ".pastcap");
        ExtensionRemainder rest = other.Remainder;
        Assert.True(rest.MoreUncounted);
        Assert.Equal(cap - 3, rest.Extensions);
        Assert.Equal(cap + 52 - 4, rest.Files);
        Assert.True(rest.OnDisk > 1024 * MiB && other.TopExtensions.All(e => e.OnDisk < rest.OnDisk),
            $"the remainder ({rest.OnDisk:N0} B) holds the uncounted extension larger than any listed");
        Assert.EndsWith($"+ more than {(cap - 3).ToString("N0", CultureInfo.CurrentCulture)} others, {rest.OnDisk} B",
            KindRowText.Extensions(other, Bytes));
    }

    /// <summary>
    /// The pass reads each name in place and builds no string per file (spike breakdown-pass-cost-at-5m-records: a
    /// string per file allocated 510 MiB a pass at 5M entries, a span lookup 400 bytes). 200,000 files over 50
    /// extensions in three cases each, and no extension: a string per file here would be over 6 MB.
    /// </summary>
    [Fact]
    public void APassBuildsNoStringPerFile()
    {
        string[] extensions =
        [
            "mp4", "mkv", "mov", "jpg", "png", "gif", "heic", "mp3", "flac", "aax", "pdf", "docx", "txt", "csv", "zip", "7z",
            "gz", "iso", "vhdx", "msi", "msix", "exe", "dll", "sys", "mui",
            "log", "json", "xml", "py", "js", "ts", "cs", "pak", "dat", "db", "tmp", "bak", "ini", "cfg", "lock", "map",
            "css", "html", "yml", "toml", "bin", "obj", "pdb", "idx", "",
        ];
        Assert.Equal(50, extensions.Length);
        const int files = 200_000;
        var store = new ScanStore();
        store.AppendRoot(@"C:\", 0x10);
        int folder = 0;
        for (int i = 0; i < files; i++)
        {
            if (i % 2000 == 0)
                folder = store.Append(0, $"d{i / 2000}", EntryKind.Folder, EntryFlags.None, 0x10, 0, 0, UInt128.Zero);
            string ext = extensions[i % extensions.Length];
            ext = (i / extensions.Length % 3) switch
            {
                0 => ext,
                1 => ext.ToUpperInvariant(),
                _ => ext.Length == 0 ? ext : char.ToUpperInvariant(ext[0]) + ext[1..],
            };
            string name = ext.Length == 0 ? $"file{i}" : $"file{i}.{ext}";
            store.Append(folder, name, EntryKind.File, EntryFlags.None, 0x20, i, i / 2, UInt128.Zero);
        }
        KindBreakdown.Compute(store); // compiled, and the kind table built, before it is measured

        long before = GC.GetAllocatedBytesForCurrentThread();
        KindBreakdownResult kinds = KindBreakdown.Compute(store);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"{files:N0} files over {extensions.Length} extensions: one pass allocated {allocated:N0} bytes");
        Assert.True(allocated < 256 * KiB, $"one pass over {files:N0} files allocated {allocated:N0} bytes");
        AssertAddsUp(store, kinds);
        Assert.Equal(files, kinds.Files);
        // Each extension counted once whatever its case, and listed in lower case (AssertAddsUp).
        Assert.Equal(extensions.Length - 1, kinds.Kinds.Sum(k => k.TopExtensions.Count + k.Remainder.Extensions));
    }

    // ---- Helpers ----

    /// <summary>A store of files directly under a root, as named.</summary>
    internal static ScanStore Synthetic(params (string Name, long Size, long OnDisk)[] files)
    {
        var store = new ScanStore();
        store.AppendRoot(@"C:\synthetic", 0x10);
        foreach ((string name, long size, long onDisk) in files)
            store.Append(0, name, EntryKind.File, EntryFlags.None, 0x20, size, onDisk, UInt128.Zero);
        store.SetChildren(0, 1, files.Length);
        return store;
    }

    /// <summary>
    /// The breakdown is the root's: its totals are the root's, the kinds add up to them, each row's listed extensions
    /// and remainder add up to the row, and the rows run largest on disk first, then by size, then in kind order. Every
    /// listed extension is lower case, has its dot, and belongs to the row's kind.
    /// </summary>
    internal static void AssertAddsUp(ScanStore store, KindBreakdownResult kinds)
    {
        Assert.Equal(((long)store.FilesBelow(0), store.RolledSize(0), store.RolledOnDisk(0)), (kinds.Files, kinds.Size, kinds.OnDisk));
        Assert.Equal((kinds.Files, kinds.Size, kinds.OnDisk),
            (kinds.Kinds.Sum(k => k.Files), kinds.Kinds.Sum(k => k.Size), kinds.Kinds.Sum(k => k.OnDisk)));
        Assert.Equal(kinds.Kinds.Count, kinds.Kinds.Select(k => k.Kind).Distinct().Count());
        for (int k = 0; k < kinds.Kinds.Count; k++)
        {
            KindTotal row = kinds.Kinds[k];
            Assert.True(row.Files > 0, $"{row.Id} is listed with no file");
            Assert.Equal((FileKinds.Id(row.Kind), FileKinds.Label(row.Kind)), (row.Id, row.Label));
            Assert.InRange(row.TopExtensions.Count, 0, 3);
            if (row.Kind == FileKind.NoExtension)
            {
                Assert.Empty(row.TopExtensions);
                Assert.Equal(default, row.Remainder);
            }
            else
            {
                Assert.Equal((row.Files, row.Size, row.OnDisk), (
                    row.TopExtensions.Sum(e => e.Files) + row.Remainder.Files,
                    row.TopExtensions.Sum(e => e.Size) + row.Remainder.Size,
                    row.TopExtensions.Sum(e => e.OnDisk) + row.Remainder.OnDisk));
            }
            for (int e = 0; e < row.TopExtensions.Count; e++)
            {
                ExtensionTotal extension = row.TopExtensions[e];
                Assert.True(extension.Ext.Length > 1 && extension.Ext[0] == '.', $"'{extension.Ext}' is listed as an extension");
                Assert.Equal(extension.Ext.ToLowerInvariant(), extension.Ext);
                Assert.Equal(row.Kind, FileKinds.Of(extension.Ext));
                if (e > 0)
                {
                    ExtensionTotal before = row.TopExtensions[e - 1];
                    Assert.True((before.OnDisk, before.Size).CompareTo((extension.OnDisk, extension.Size)) > 0
                        || ((before.OnDisk, before.Size) == (extension.OnDisk, extension.Size) && string.CompareOrdinal(before.Ext, extension.Ext) < 0),
                        $"{extension.Ext} is listed out of order after {before.Ext}");
                }
            }
            if (k > 0)
            {
                KindTotal before = kinds.Kinds[k - 1];
                Assert.True((before.OnDisk, before.Size, -(int)before.Kind).CompareTo((row.OnDisk, row.Size, -(int)row.Kind)) > 0,
                    $"{row.Id} is out of order after {before.Id}");
            }
        }
    }

    private static KindTotal Row(KindBreakdownResult kinds, FileKind kind) => Assert.Single(kinds.Kinds, k => k.Kind == kind);

    private static int IndexOf(KindBreakdownResult kinds, FileKind kind) => kinds.Kinds.Select(k => k.Kind).ToList().IndexOf(kind);

    /// <summary>A file of <paramref name="length"/> bytes whose full path is exactly <paramref name="pathLength"/> characters.</summary>
    private static string LongPathFile(TempTree tree, string name, int pathLength, long length)
    {
        // Folders and their separators take what the root and the name leave; no folder name over 200 characters.
        int rest = pathLength - tree.Root.Length - 1 - name.Length;
        Assert.True(rest >= 2, $"the temp root ({tree.Root.Length} characters) leaves no room for a {pathLength}-character path");
        string folder = tree.Root;
        for (int k = 0; rest > 0; k++)
        {
            int take = Math.Min(201, rest);
            if (rest - take == 1)
                take--;
            folder += @"\" + new string((char)('p' + k), take - 1);
            rest -= take;
        }
        Directory.CreateDirectory(@"\\?\" + folder);
        string path = folder + @"\" + name;
        TempTree.WriteFile(@"\\?\" + path, length);
        Assert.Equal(pathLength, path.Length);
        return path;
    }
}
