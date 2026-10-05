using System.Diagnostics;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;
using FsAnalyser.Core.Workarounds;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-095: the Windows container layer store is left out of a scan by its path, and a root at or inside it is refused.
/// Every case runs on a real NTFS tree in %TEMP% with a planted store (ScanOptions.ContainerLayerStorePath); none of
/// them goes near the real store, whose live layer leaks wcifs pool on first touch. The planted store holds bytes, so a
/// scan that walked into it would count them: each case says what the totals must be without them.
/// </summary>
public class ContainerLayerStoreTests
{
    // Under the tree: x\Containers\Layers is the planted store, shaped like this PC's live one: a layer whose Files tree
    // nests folders with files in them, and Hives, Bindings and a dotnet.bcf file beside Files. ContainerStorages and a
    // file sit beside the store, and z comes after x in listing order.
    private const string Store = @"x\Containers\Layers";
    private const long StoreBytes = 1000 + 50 + 300 + 20 + 30;
    private const long OutsideBytes = 10 + 5 + 7;

    private static TempTree PlantedTree()
    {
        var tree = new TempTree();
        tree.Folder(@"x\Containers\Layers\L1\Files\Windows\System32\Speech");
        tree.File(@"x\Containers\Layers\L1\Files\Windows\System32\Speech\voice.dat", 1000);
        tree.File(@"x\Containers\Layers\L1\Files\Windows\win.ini", 50);
        tree.Folder(@"x\Containers\Layers\L1\Hives");
        tree.File(@"x\Containers\Layers\L1\Hives\SYSTEM_Delta", 300);
        tree.Folder(@"x\Containers\Layers\L1\Bindings");
        tree.File(@"x\Containers\Layers\L1\Bindings\binding.dat", 20);
        tree.File(@"x\Containers\Layers\L1\dotnet.bcf", 30);
        tree.Folder(@"x\Containers\ContainerStorages");
        tree.File(@"x\Containers\ContainerStorages\sandbox.bin", 10);
        tree.File(@"x\other.txt", 5);
        tree.Folder("z");
        tree.File(@"z\last.txt", 7);
        return tree;
    }

    private static ScanOptions Planted(TempTree tree, Action<int>? afterFolder = null, ProbeFile? probe = null) => new()
    {
        ContainerLayerStorePath = tree.PathOf(Store),
        AfterFolder = afterFolder,
        Probe = probe,
    };

    private static bool UnderStore(string path, TempTree tree) =>
        Scanner.IsAtOrUnder(LongPath.ToExtended(path), LongPath.ToExtended(tree.PathOf(Store))) && path.Length > tree.PathOf(Store).Length;

    // ---- A root above the store ----

    /// <summary>
    /// C-043 / C-029 / C-015 / C-001: the walk pushes the store like any folder and stops at it when it pops it. The
    /// store, holding a nested Files tree with files and Hives, Bindings and dotnet.bcf beside it, is an entry, never
    /// opened, with nothing listed and its size not counted; it is the one SkippedStores entry, named by its
    /// precaution, not in Unreadable; nothing at or under it is listed (AfterFolder) or probed, and the backstop refused
    /// nothing.
    /// </summary>
    [Fact]
    public void AScanAboveTheStoreStopsAtItAndCountsEverythingElse()
    {
        using TempTree tree = PlantedTree();
        var progress = new ScanProgress();
        var listed = new List<string>();
        var probed = new List<string>();
        ProbeFile recorder = (SynchronousIoCancel io, string path, out long eof, out long alloc) =>
        {
            lock (probed)
                probed.Add(path);
            return SizeProbe.Probe(io, path, out eof, out alloc);
        };

        ScanResult result = Scanner.Scan(tree.Root, progress, CancellationToken.None,
            Planted(tree, _ => listed.Add(progress.CurrentFolder!), recorder));
        Scans.AssertChargedBytesAreTheTotals(result);

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(OutsideBytes, result.Totals.Bytes);
        Assert.Equal(3, result.Totals.Files);
        int store = Scans.Find(result, tree.PathOf(Store));
        NotScannedFolder skipped = Assert.Single(result.SkippedStores);
        Assert.Equal(store, skipped.Entry);
        Assert.Equal(NotScannedReason.ContainerLayerStore, skipped.Reason);
        Assert.Equal(KnownWorkarounds.WcifsLayerStore.Id, skipped.WorkaroundId);
        Assert.Equal(EntryKind.Folder, result.Store.Kind(store));
        Assert.True((result.Store.Flags(store) & EntryFlags.NotScanned) != 0);
        Assert.True((result.Store.Flags(store) & EntryFlags.Unreadable) == 0);
        Assert.Equal(0, result.Store.ChildCount(store));
        Assert.Equal(0, result.Store.ChargedOnDisk(store));
        Assert.Empty(result.Unreadable);
        Assert.Equal(0, result.OpensUnderExcludedStore);
        Assert.Equal(ScanRefusal.None, result.Refusal);
        Assert.DoesNotContain(listed, path => UnderStore(path, tree) || path == tree.PathOf(Store));
        Assert.Contains(tree.PathOf(@"x\Containers\ContainerStorages"), listed); // the store's sibling is still listed
        Assert.DoesNotContain(probed, path => path.Contains(@"\Layers\", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, probed.Count);
        Assert.DoesNotContain(Scans.ByPath(result.Store).Keys, path => UnderStore(path, tree));
    }

    /// <summary>
    /// C-015: the root is the store's parent (...\Containers, as the bug test scans on this PC). The walk pushes Layers
    /// like any folder and only the pop-time check stops it: the store is an entry and a row of its own state, never
    /// opened or listed, the one SkippedStores entry, not in Unreadable, and the backstop refused nothing.
    /// </summary>
    [Fact]
    public void TheStoreAsATopLevelRowIsNotScannedNeitherDoneNorUnreadable()
    {
        using TempTree tree = PlantedTree();
        var progress = new ScanProgress();
        var listed = new List<string>();

        ScanResult result = Scanner.Scan(tree.PathOf(@"x\Containers"), progress, CancellationToken.None,
            Planted(tree, _ => listed.Add(progress.CurrentFolder!)));

        Assert.Equal(ScanStatus.Completed, result.Status);
        TopLevelRow layers = result.TopLevel!.Rows.Single(r => r.Name == "Layers");
        Assert.Equal(TopLevelState.NotScanned, layers.State);
        Assert.Equal(0, layers.Bytes);
        Assert.Equal(TopLevelState.Done, result.TopLevel.Rows.Single(r => r.Name == "ContainerStorages").State);
        int store = Scans.Find(result, tree.PathOf(Store));
        Assert.Equal(store, Assert.Single(result.SkippedStores).Entry);
        Assert.True((result.Store.Flags(store) & EntryFlags.NotScanned) != 0);
        Assert.True((result.Store.Flags(store) & EntryFlags.Unreadable) == 0);
        Assert.Equal(0, result.Store.ChildCount(store));
        Assert.Empty(result.Unreadable);
        Assert.Equal(0, result.OpensUnderExcludedStore);
        Assert.DoesNotContain(listed, path => UnderStore(path, tree) || path == tree.PathOf(Store));
        Assert.Contains(tree.PathOf(@"x\Containers\ContainerStorages"), listed);
        Assert.Equal(10, result.Totals.Bytes);
    }

    /// <summary>
    /// C-008: the store is the last folder its parent holds, and its parent is a row of its own. The skip takes the
    /// completion path a refused folder takes, so the row settles Done, the scan completes, and AfterFolder fired only
    /// for folders that were listed.
    /// </summary>
    [Fact]
    public void TheStoreAsItsParentsLastPendingFolderLetsTheRowFinish()
    {
        using var tree = new TempTree();
        tree.Folder(@"x\Containers\Layers\L1\Files");
        tree.File(@"x\Containers\Layers\L1\Files\a.bin", 50);
        tree.File(@"x\Containers\c.txt", 3);
        int afterFolder = 0;

        ScanResult result = Scans.Run(tree.Root, new ScanOptions
        {
            ContainerLayerStorePath = tree.PathOf(Store),
            AfterFolder = _ => afterFolder++,
            TopLevelInterval = TimeSpan.Zero,
        });

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(TopLevelState.Done, result.TopLevel!.Rows.Single(r => r.Name == "x").State);
        Assert.Equal(3, afterFolder); // the root, x and Containers; never Layers
        Assert.Equal(3, result.Totals.Bytes);
        Assert.Single(result.SkippedStores);
    }

    /// <summary>
    /// C-030: a file outside the store with a second name inside it is charged once, to the name outside: the store's
    /// name is never seen, so the first name seen is the outside one, and the file is not charged zero times.
    /// </summary>
    [Fact]
    public void AFileWithASecondNameInsideTheStoreIsChargedOnceOutsideIt()
    {
        using TempTree tree = PlantedTree();
        tree.Folder("a");
        string outside = tree.File(@"a\shared.bin", 4096);
        TempTree.Hardlink(tree.PathOf(@"x\Containers\Layers\L1\Files\shared.bin"), outside);

        ScanResult result = Scans.Run(tree.Root, Planted(tree));

        Assert.Equal(ScanStatus.Completed, result.Status);
        int charged = Scans.Find(result, outside);
        Assert.Equal(4096, result.Store.ChargedSize(charged));
        Assert.True((result.Store.Flags(charged) & EntryFlags.HardlinkDuplicate) == 0);
        Assert.Equal(OutsideBytes + 4096, result.Totals.Bytes);
        Assert.Equal(4, result.Totals.Files);
    }

    /// <summary>
    /// C-047 / C-031 / C-017 / C-005: a folder named Layers elsewhere, and siblings of the store named Layers2,
    /// LayersBackup, 'Layers.' and 'Layers ' (the last two only a \\?\ path can make, and GetFullPath would trim onto
    /// the store), are walked and counted. Only the one path is left out.
    /// </summary>
    [Fact]
    public void FoldersNamedLikeTheStoreElsewhereOrBesideItAreCounted()
    {
        using TempTree tree = PlantedTree();
        tree.Folder(@"a\Layers");
        tree.File(@"a\Layers\elsewhere.bin", 100);
        tree.Folder(@"x\Containers\Layers2");
        tree.File(@"x\Containers\Layers2\beside.bin", 200);
        tree.Folder(@"x\Containers\LayersBackup");
        tree.File(@"x\Containers\LayersBackup\backup.bin", 400);
        string containers = tree.ExtendedRoot + @"\x\Containers\";
        Directory.CreateDirectory(containers + "Layers.");
        TempTree.WriteFile(containers + @"Layers.\dot.bin", 1000);
        Directory.CreateDirectory(containers + "Layers ");
        TempTree.WriteFile(containers + @"Layers \space.bin", 2000);

        ScanResult result = Scans.Run(tree.Root, Planted(tree));

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(OutsideBytes + 700 + 3000, result.Totals.Bytes);
        Assert.Equal(tree.PathOf(Store), result.Store.GetPath(Assert.Single(result.SkippedStores).Entry));
        Assert.Equal(0, result.OpensUnderExcludedStore);
    }

    /// <summary>
    /// C-018: siblings named 'Layers.' and 'Layers ', which only a \\?\ path can make. GetFullPath would trim them onto
    /// the store; the walk compares paths as listed, so both are walked and counted.
    /// </summary>
    [Fact]
    public void SiblingsNamedLayersDotAndLayersSpaceAreCounted()
    {
        using TempTree tree = PlantedTree();
        string containers = tree.ExtendedRoot + @"\x\Containers\";
        Directory.CreateDirectory(containers + "Layers.");
        TempTree.WriteFile(containers + @"Layers.\dot.bin", 20);
        Directory.CreateDirectory(containers + "Layers ");
        TempTree.WriteFile(containers + @"Layers \space.bin", 40);

        ScanResult result = Scans.Run(tree.Root, Planted(tree));

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(OutsideBytes + 60, result.Totals.Bytes);
        Assert.Single(result.SkippedStores);
    }

    /// <summary>
    /// C-032 / C-004 / C-021: a root above the store spelled \\?\, in another case, or with a trailing separator. Every
    /// popped path carries the root's spelling, and the comparison is without case, so the store is still met.
    /// </summary>
    [Theory]
    [InlineData("extended")]
    [InlineData("lower")]
    [InlineData("upper")]
    [InlineData("trailing")]
    public void ARootAboveTheStoreInAnotherSpellingStillStopsAtIt(string spelling)
    {
        using TempTree tree = PlantedTree();
        string root = spelling switch
        {
            "extended" => tree.ExtendedRoot,
            "lower" => tree.Root.ToLowerInvariant(),
            "upper" => tree.Root.ToUpperInvariant(),
            _ => tree.Root + @"\",
        };

        ScanResult result = Scans.Run(root, Planted(tree));

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(OutsideBytes, result.Totals.Bytes);
        Assert.Single(result.SkippedStores);
        Assert.Equal(0, result.OpensUnderExcludedStore);
    }

    /// <summary>C-022: the store beyond MAX_PATH, so every path to it is a long one.</summary>
    [Fact]
    public void AStoreBeyondMaxPathIsStillLeftOut()
    {
        using var tree = new TempTree();
        string deep = string.Join('\\', Enumerable.Range(0, 10).Select(i => $"deep-folder-{i:D2}-{new string('d', 14)}"));
        string store = $@"{deep}\Containers\Layers";
        tree.Folder($@"{store}\L1\Files");
        TempTree.WriteFile(LongPath.ToExtended(tree.PathOf($@"{store}\L1\Files\inside.bin")), 900);
        TempTree.WriteFile(LongPath.ToExtended(tree.PathOf($@"{deep}\beside.bin")), 9);
        Assert.True(tree.PathOf(store).Length > 260, "setup: the store's path is within MAX_PATH");

        ScanResult result = Scans.Run(tree.Root, new ScanOptions { ContainerLayerStorePath = tree.PathOf(store) });

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(9, result.Totals.Bytes);
        Assert.Equal(tree.PathOf(store), result.Store.GetPath(Assert.Single(result.SkippedStores).Entry));
    }

    /// <summary>
    /// C-019 / C-032: a root reached through a subst drive letter mapped above the store. Only the final path by handle
    /// relates X:\ to the store, and the store is rebased into X:'s spelling, which is how the walk pops it.
    /// </summary>
    [Fact]
    public void ARootThroughASubstDriveAboveTheStoreStopsAtIt()
    {
        using TempTree tree = PlantedTree();
        using var drive = new SubstDrive(tree.PathOf("x"));

        ScanResult result = Scans.Run(drive.Root, Planted(tree));

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(5 + 10, result.Totals.Bytes);
        Assert.Equal(drive.Root + @"Containers\Layers", result.Store.GetPath(Assert.Single(result.SkippedStores).Entry));
        Assert.Equal(0, result.OpensUnderExcludedStore);
    }

    /// <summary>The same through a junction above the store: the junction is the root, opened through to its target.</summary>
    [Fact]
    public void ARootThroughAJunctionAboveTheStoreStopsAtIt()
    {
        using TempTree tree = PlantedTree();
        string junction = tree.Junction("j", tree.PathOf("x"));

        ScanResult result = Scans.Run(junction, Planted(tree));

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(5 + 10, result.Totals.Bytes);
        Assert.Equal(junction + @"\Containers\Layers", result.Store.GetPath(Assert.Single(result.SkippedStores).Entry));
    }

    /// <summary>C-020: a root given as \\?\Volume{GUID}\..., which names the store's volume without its drive letter.</summary>
    [Fact]
    public void ARootSpelledWithTheVolumeGuidStopsAtTheStore()
    {
        using TempTree tree = PlantedTree();
        string volume = VolumeGuidOf(Path.GetPathRoot(tree.Root)!);
        string root = volume + tree.PathOf("x")[3..];

        ScanResult result = Scans.Run(root, Planted(tree));

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(5 + 10, result.Totals.Bytes);
        Assert.Single(result.SkippedStores);
        Assert.Equal(0, result.OpensUnderExcludedStore);
    }

    /// <summary>
    /// C-010 / C-025 (review M5, X1): a scan that meets both an access-denied folder and the store records them apart. The
    /// denied folder is the one Unreadable entry, flagged Unreadable; the store is the one SkippedStores entry, flagged
    /// NotScanned and never Unreadable; neither list holds the other's entry, so nothing that reads or retries unreadable
    /// folders reaches the store, and the unreadable count is what it would be with no store at all.
    /// </summary>
    [Fact]
    public void AnUnreadableFolderAndTheStoreAreRecordedApart()
    {
        using TempTree tree = PlantedTree();
        string denied = tree.Folder(@"x\Containers\Denied");
        tree.File(@"x\Containers\Denied\hidden.bin", 50);
        tree.Deny(denied, 0x1); // FILE_LIST_DIRECTORY

        ScanResult result = Scans.Run(tree.Root, Planted(tree));
        ScanResult plain = Scans.Run(tree.Root, new ScanOptions { ContainerLayerStorePath = tree.PathOf(Store), EnabledWorkarounds = Off() });

        Assert.Equal(ScanStatus.Completed, result.Status);
        UnreadableFolder unreadable = Assert.Single(result.Unreadable);
        NotScannedFolder skipped = Assert.Single(result.SkippedStores);
        Assert.Equal(denied, result.Store.GetPath(unreadable.Entry));
        Assert.Equal(tree.PathOf(Store), result.Store.GetPath(skipped.Entry));
        Assert.NotEqual(unreadable.Entry, skipped.Entry);
        Assert.True((result.Store.Flags(unreadable.Entry) & EntryFlags.NotScanned) == 0);
        Assert.True((result.Store.Flags(skipped.Entry) & EntryFlags.Unreadable) == 0);
        Assert.Equal(plain.Unreadable.Count, result.Unreadable.Count);
        Assert.Equal(OutsideBytes, result.Totals.Bytes);
        Assert.Equal(0, result.OpensUnderExcludedStore);
    }

    /// <summary>
    /// Review m1: a root spelled with 8.3 names, as `dir /x` shows them. The root's normalisation (GetFullPath, which
    /// expands a name with a tilde to its long form) spells it as the store is spelled, so the walk meets the store by
    /// its long path; whichever step relates them, the store is left out and nothing under it is opened.
    /// </summary>
    [Fact]
    public void ARootSpelledWithShortNamesStopsAtTheStore()
    {
        using TempTree tree = PlantedTree();
        string longRoot = tree.PathOf("x");
        string shortRoot = ShortPathOf(longRoot);
        Assert.True(!string.Equals(shortRoot, longRoot, StringComparison.OrdinalIgnoreCase),
            $"setup: the volume gave {longRoot} no 8.3 alias, so this root is not spelled differently");

        ScanResult result = Scans.Run(shortRoot, Planted(tree));

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(5 + 10, result.Totals.Bytes);
        Assert.Equal(tree.PathOf(Store), result.Store.GetPath(Assert.Single(result.SkippedStores).Entry), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, result.OpensUnderExcludedStore);
    }

    // The path's 8.3 form, from cmd's own %~s expansion, which needs no interop here.
    private static string ShortPathOf(string path)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /c for %I in (\"{path}\") do @echo %~sI")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd().Trim();
        Assert.True(process.WaitForExit(30_000), "cmd did not exit");
        return output;
    }

    // ---- Cancel ----

    /// <summary>
    /// C-033 / C-009: cancelled after the walk passed the store. The Cancelled result still carries the store's
    /// not-scanned record, apart from the unreadable ones, and its row says so.
    /// </summary>
    [Fact]
    public void ACancelAfterTheStoreKeepsItsNotScannedRecord()
    {
        using TempTree tree = PlantedTree();
        var progress = new ScanProgress();
        using var cts = new CancellationTokenSource();

        ScanResult result = Scanner.Scan(tree.Root, progress, cts.Token, Planted(tree, _ =>
        {
            if (progress.CurrentFolder == tree.PathOf("z"))
                cts.Cancel();
        }));
        Scans.AssertChargedBytesAreTheTotals(result);

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        int store = Assert.Single(result.SkippedStores).Entry;
        Assert.Equal(tree.PathOf(Store), result.Store.GetPath(store));
        Assert.True((result.Store.Flags(store) & EntryFlags.NotScanned) != 0);
        Assert.Empty(result.Unreadable);
        Assert.Equal(0, result.OpensUnderExcludedStore);
    }

    /// <summary>
    /// C-009 / C-012: cancelled while the store is still pending. Nothing under it is opened on the way out: the store
    /// is never popped, so it is neither listed nor recorded, and the backstop refused nothing.
    /// </summary>
    [Fact]
    public void ACancelWhileTheStoreIsPendingOpensNothingUnderIt()
    {
        using TempTree tree = PlantedTree();
        var progress = new ScanProgress();
        using var cts = new CancellationTokenSource();

        ScanResult result = Scanner.Scan(tree.Root, progress, cts.Token, Planted(tree, _ =>
        {
            if (progress.CurrentFolder == tree.PathOf(@"x\Containers"))
                cts.Cancel(); // Layers was pushed by this listing and not popped yet
        }));
        Scans.AssertChargedBytesAreTheTotals(result);

        Assert.Equal(ScanStatus.Cancelled, result.Status);
        Assert.Empty(result.SkippedStores);
        Assert.Equal(0, result.OpensUnderExcludedStore);
        int store = Scans.Find(result, tree.PathOf(Store));
        Assert.Equal(0, result.Store.ChildCount(store));
        Assert.True((result.Store.Flags(store) & (EntryFlags.NotScanned | EntryFlags.Unreadable)) == 0);
        Assert.Empty(result.Unreadable);
    }

    // ---- A root at or inside the store ----

    /// <summary>
    /// C-044 / C-034 / C-016 / C-002 / C-003: a root that is the store, a layer, a folder of its Files tree
    /// (Layers\&lt;id&gt;\Files\Windows) or one deep inside it is Refused, named by its precaution: nothing listed or
    /// probed (the scan's guard made no call, so no DirectoryReader listing), no unreadable record, no exception. Q-179
    /// and Q-182 decided refusal; the earlier design scanned it. Report mode's exit 0 and outcome refused on this PC's
    /// own store are WindowCases X-001's.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(@"\L1")]
    [InlineData(@"\L1\Files\Windows")]
    [InlineData(@"\L1\Files\Windows\System32\Speech")]
    public void ARootAtOrInsideTheStoreIsRefused(string below)
    {
        using TempTree tree = PlantedTree();
        SynchronousIoCancel? scanGuard = null;

        ScanResult result = Scans.WithGuard(
            guard => scanGuard = guard,
            () => Scanner.Scan(tree.PathOf(Store) + below, new ScanProgress(), CancellationToken.None, Planted(tree)));
        int calls = scanGuard!.Calls;

        Assert.Equal(ScanStatus.Refused, result.Status);
        Assert.Equal(ScanRefusal.ContainerLayerStore, result.Refusal);
        Assert.Equal(KnownWorkarounds.WcifsLayerStore.Id, result.RefusalWorkaroundId);
        Assert.Equal(0, calls); // the scan's guard made no call at all: nothing opened, listed or probed
        Assert.Equal(0, result.Store.Count);
        Assert.Null(result.TopLevel);
        Assert.Empty(result.Unreadable);
        Assert.Empty(result.SkippedStores);
        Assert.Equal(default, result.Totals);
    }

    /// <summary>
    /// C-035 / C-007: the store spelled \\?\, with a trailing separator, or in another case is refused by the string
    /// step alone. The resolution is given a cancelled token, so any open it tried would be refused and decide nothing:
    /// a Refuse here was reached with no open.
    /// </summary>
    [Theory]
    [InlineData("extended")]
    [InlineData("trailing")]
    [InlineData("lower")]
    [InlineData("upper")]
    public void ARootInsideTheStoreInAnotherSpellingIsRefusedWithNoOpen(string spelling)
    {
        using TempTree tree = PlantedTree();
        string store = tree.PathOf(Store);
        string root = spelling switch
        {
            "extended" => @"\\?\" + store + @"\L1",
            "trailing" => store + @"\",
            "lower" => store.ToLowerInvariant() + @"\l1\files",
            _ => store.ToUpperInvariant(),
        };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        StoreVerdict verdict = ContainerLayerStore.Resolve(LongPath.NormalizeRoot(root).Win32, store, cancelled.Token);
        ScanResult result = Scanner.Scan(root, new ScanProgress(), CancellationToken.None, Planted(tree));

        Assert.Equal(StoreVerdictKind.Refuse, verdict.Kind);
        Assert.Equal(ScanStatus.Refused, result.Status);
    }

    /// <summary>
    /// C-046 / C-036: a root that reaches inside the store through a junction, a subst drive letter (DefineDosDevice, as
    /// subst.exe makes it for this user), or 8.3 short names. The string step sees no relation for the first two; only the
    /// final path by the root's handle does. The 8.3 spelling is asserted when the volume gives the folder a short form
    /// distinct from its long one (whichever step relates it). Each is refused, with nothing in the store listed.
    /// </summary>
    [Fact]
    public void ARootThroughAJunctionIntoTheStoreIsRefused()
    {
        using TempTree tree = PlantedTree();
        string junction = tree.Junction("into", tree.PathOf($@"{Store}\L1"));
        ScanResult throughJunction = Scanner.Scan(junction, new ScanProgress(), CancellationToken.None, Planted(tree));

        Assert.Equal(ScanStatus.Refused, throughJunction.Status);
        Assert.Equal(ScanRefusal.ContainerLayerStore, throughJunction.Refusal);
        Assert.Equal(0, throughJunction.Store.Count);

        using (var drive = new SubstDrive(tree.PathOf($@"{Store}\L1\Files")))
        {
            ScanResult throughSubst = Scanner.Scan(drive.Root, new ScanProgress(), CancellationToken.None, Planted(tree));
            Assert.Equal(ScanStatus.Refused, throughSubst.Status);
            Assert.Equal(ScanRefusal.ContainerLayerStore, throughSubst.Refusal);
            Assert.Equal(0, throughSubst.Store.Count);
        }

        string longInside = tree.PathOf($@"{Store}\L1\Files\Windows\System32");
        string shortInside = ShortPathOf(longInside);
        if (!string.Equals(shortInside, longInside, StringComparison.OrdinalIgnoreCase))
        {
            ScanResult throughShort = Scanner.Scan(shortInside, new ScanProgress(), CancellationToken.None, Planted(tree));
            Assert.Equal(ScanStatus.Refused, throughShort.Status);
            Assert.Equal(0, throughShort.Store.Count);
        }
    }

    // ---- No store, and a root the resolution cannot open ----

    /// <summary>
    /// C-048 / C-037 / C-024 / C-006: the store does not exist (the override names a missing path, as on Windows 10 or a
    /// PC without the containers feature). Resolve returns None without throwing, even when the string step decides
    /// nothing and the final-path step cannot open the store's parent; and the scan is the one it would be with the
    /// precaution off: same totals, nothing skipped, nothing unreadable, no not-scanned row.
    /// </summary>
    [Fact]
    public void WithNoStoreTheScanIsWhatItWasBefore()
    {
        using var tree = new TempTree();
        tree.Folder(@"x\Containers\ContainerStorages");
        tree.File(@"x\Containers\ContainerStorages\a.bin", 10);
        tree.File(@"x\b.txt", 5);
        tree.Folder("z");
        string absent = tree.PathOf(@"gone\Containers\Layers"); // its parent, the final-path step's anchor, is missing too

        StoreVerdict unrelated = ContainerLayerStore.Resolve(LongPath.ToExtended(tree.PathOf("z")), absent, CancellationToken.None);
        ScanResult guarded = Scans.Run(tree.Root, new ScanOptions { ContainerLayerStorePath = tree.PathOf(Store) });
        ScanResult gone = Scans.Run(tree.PathOf("z"), new ScanOptions { ContainerLayerStorePath = absent });
        ScanResult plain = Scans.Run(tree.Root, new ScanOptions { EnabledWorkarounds = Off() });

        Assert.Equal(StoreVerdictKind.None, unrelated.Kind);
        Assert.Equal(ScanStatus.Completed, gone.Status);
        Assert.Empty(gone.SkippedStores);

        Assert.Equal(ScanStatus.Completed, guarded.Status);
        Assert.Equal(plain.Totals, guarded.Totals);
        Assert.Equal(plain.Store.Count, guarded.Store.Count);
        Assert.Empty(guarded.SkippedStores);
        Assert.Equal(plain.Unreadable.Count, guarded.Unreadable.Count);
        Assert.Equal(0, guarded.OpensUnderExcludedStore);
    }

    /// <summary>
    /// C-007 / C-010: the known folder answers "" (GetFolderPath's failure) or a relative path, or the override is
    /// empty, as for a PC with no store. An empty prefix would hold every path; here it holds none.
    /// </summary>
    [Fact]
    public void AnEmptyKnownFolderMeansNoStoreNeverEveryPath()
    {
        Assert.Null(ContainerLayerStore.Locate(""));
        Assert.Null(ContainerLayerStore.Locate(null));
        Assert.Null(ContainerLayerStore.Locate("   "));
        Assert.Null(ContainerLayerStore.Locate(@"ProgramData"));
        Assert.EndsWith(@"\Microsoft\Windows\Containers\Layers", ContainerLayerStore.Locate(@"C:\ProgramData"));

        using TempTree tree = PlantedTree();
        foreach (string empty in new[] { "", "  ", "relative" })
        {
            Assert.Equal(StoreVerdictKind.None, ContainerLayerStore.Resolve(LongPath.ToExtended(tree.Root), empty, CancellationToken.None).Kind);
            ScanResult result = Scans.Run(tree.Root, new ScanOptions { ContainerLayerStorePath = empty });
            Assert.Equal(ScanStatus.Completed, result.Status);
            Assert.Equal(OutsideBytes + StoreBytes, result.Totals.Bytes);
            Assert.Empty(result.SkippedStores);
        }
    }

    /// <summary>
    /// C-038: a root that does not exist, or that refuses to be opened, leaves the string verdict standing (None), and
    /// the scan goes on to its usual RootUnreadable: no exception, no refusal.
    /// </summary>
    [Fact]
    public void ARootTheResolutionCannotOpenIsTheUsualRootUnreadable()
    {
        using TempTree tree = PlantedTree();
        string missing = tree.PathOf("missing");
        string denied = tree.Folder("denied");
        tree.Deny(denied, 0x80 | 0x1); // FILE_READ_ATTRIBUTES | FILE_LIST_DIRECTORY

        Assert.Equal(StoreVerdictKind.None, ContainerLayerStore.Resolve(LongPath.ToExtended(missing), tree.PathOf(Store), CancellationToken.None).Kind);
        ScanResult gone = Scanner.Scan(missing, new ScanProgress(), CancellationToken.None, Planted(tree));
        ScanResult refused = Scanner.Scan(denied, new ScanProgress(), CancellationToken.None, Planted(tree));

        Assert.Equal(ScanStatus.RootUnreadable, gone.Status);
        Assert.Equal(ScanRefusal.None, gone.Refusal);
        Assert.Equal(ScanStatus.RootUnreadable, refused.Status);
        Assert.Equal(Kernel32.ERROR_ACCESS_DENIED, refused.RootError);
    }

    // ---- The backstop ----

    /// <summary>
    /// C-023 (review F4): DirectoryReader.Read called straight with a folder under the store, past the walk's check, the
    /// one call site of SynchronousIoCancel.OpenDirectory. The folder denies this user its listing, so an open that was
    /// actually issued would come back access denied: what comes back is the skip outcome, with no call made, and the
    /// backstop's counter is 1. A count that only ever read 0 could not tell a working backstop from a broken one. A
    /// folder beside the store is listed as usual and not counted.
    /// </summary>
    [Fact]
    public void TheReaderRefusesAndCountsAFolderUnderTheStoreWithNoOpen()
    {
        using TempTree tree = PlantedTree();
        string inside = tree.PathOf($@"{Store}\L1\Files");
        tree.Deny(inside, 0x1); // FILE_LIST_DIRECTORY: an open of it that went out would fail with error 5
        using var guard = new SynchronousIoCancel(CancellationToken.None);
        using var reader = new DirectoryReader(guard) { ExcludedStore = LongPath.ToExtended(tree.PathOf(Store)) };

        int error = reader.Read(LongPath.ToExtended(inside), fullClass: false, keepLastAccess: false);

        Assert.NotEqual(Kernel32.ERROR_ACCESS_DENIED, error);
        Assert.Equal(DirectoryReader.InsideExcludedStore, error);
        Assert.NotEqual(Scanner.ReadOutcome.Read, Scanner.ClassifyReadError(error, cancelRequested: false));
        Assert.Equal(0, guard.Calls);
        Assert.Equal(0, reader.Count);
        Assert.Equal(1, reader.OpensUnderExcludedStore);

        Assert.Equal(0, reader.Read(LongPath.ToExtended(tree.PathOf(@"x\Containers\ContainerStorages")), fullClass: false, keepLastAccess: false));
        Assert.Equal(1, reader.Count);
        Assert.Equal(1, reader.OpensUnderExcludedStore);
    }

    // ---- The precaution off ----

    /// <summary>
    /// C-049 (Q-186): both positions of the 'wcifs-layer-store' checkbox over a scan above the store, on a planted store
    /// only (off against the real store leaks until a Restart, so nothing ever runs it there). On is the default for every
    /// scan. Off restores the plain walk exactly: the store is listed and counted like any folder, there is no not-scanned
    /// record and the reader has no rule to refuse with.
    /// </summary>
    [Fact]
    public void WithThePrecautionOffTheStoreIsListedAndCountedLikeAnyFolder()
    {
        using TempTree tree = PlantedTree();

        ScanResult on = Scans.Run(tree.Root, Planted(tree));
        ScanResult off = Scans.Run(tree.Root, PlantedOff(tree));

        Assert.Equal(OutsideBytes, on.Totals.Bytes);
        Assert.Single(on.SkippedStores);
        Assert.Equal(ScanStatus.Completed, off.Status);
        Assert.Equal(OutsideBytes + StoreBytes, off.Totals.Bytes);
        Assert.Empty(off.SkippedStores);
        Assert.Equal(0, off.OpensUnderExcludedStore);
        Assert.True((off.Store.Flags(Scans.Find(off, tree.PathOf(Store))) & EntryFlags.NotScanned) == 0);
        Assert.Contains(Scans.ByPath(off.Store).Keys, path => path == tree.PathOf($@"{Store}\L1\Files\Windows\win.ini"));

        Assert.Contains(KnownWorkarounds.WcifsLayerStore, KnownWorkarounds.All);
        Assert.True(KnownWorkarounds.WcifsLayerStore.DefaultOn);
        Assert.Contains(KnownWorkarounds.WcifsLayerStore.Id, ScanOptions.Default.EnabledWorkarounds);
        Assert.Contains(KnownWorkarounds.WcifsLayerStore.Id, new ScanOptions().EnabledWorkarounds);
    }

    /// <summary>
    /// C-049 (Q-186): a root picked at or inside the store, both positions of the checkbox, on a planted store only. On,
    /// it is Refused with nothing listed. Off lifts the refusal with the exclusion: the root is scanned as picked, every
    /// entry under it listed and counted, with no refusal, no not-scanned record and no backstop refusal. Off decides
    /// with nothing compared or opened: the store itself as the root, on a cancelled token, is None.
    /// </summary>
    [Theory]
    [InlineData("", 5, StoreBytes)]
    [InlineData(@"\L1", 5, StoreBytes)]
    [InlineData(@"\L1\Files\Windows", 2, 1000 + 50)]
    public void WithThePrecautionOffARootAtOrInsideTheStoreIsScannedAsPicked(string below, long files, long bytes)
    {
        using TempTree tree = PlantedTree();
        string root = tree.PathOf(Store) + below;

        ScanResult on = Scanner.Scan(root, new ScanProgress(), CancellationToken.None, Planted(tree));
        ScanResult off = Scanner.Scan(root, new ScanProgress(), CancellationToken.None, PlantedOff(tree));

        Assert.Equal(ScanStatus.Refused, on.Status);
        Assert.Equal(0, on.Store.Count);
        Assert.Equal(ScanStatus.Completed, off.Status);
        Assert.Equal(ScanRefusal.None, off.Refusal);
        Assert.Null(off.RefusalWorkaroundId);
        Assert.Equal(files, off.Totals.Files);
        Assert.Equal(bytes, off.Totals.Bytes);
        Assert.Contains(Scans.ByPath(off.Store).Keys, path => path == tree.PathOf($@"{Store}\L1\Files\Windows\win.ini"));
        Assert.Empty(off.SkippedStores);
        Assert.Empty(off.Unreadable);
        Assert.Equal(0, off.OpensUnderExcludedStore);

        string storeWin32 = LongPath.ToExtended(tree.PathOf(Store));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(StoreVerdictKind.Refuse, ContainerLayerStore.Resolve(storeWin32, tree.PathOf(Store), cancelled.Token).Kind);
        Assert.Equal(StoreVerdictKind.None, ContainerLayerStore.Resolve(storeWin32, tree.PathOf(Store), cancelled.Token, Off()).Kind);
    }

    // ---- A root through this PC's own administrative share ----

    /// <summary>
    /// The planted store spelled through this PC's administrative share of its drive, \\host\C$\..., for every host name
    /// that is this PC. Comparing strings settles it with no open: a root inside the store is Refused and a root above
    /// it Excludes the store in the root's own spelling. Every spelling of another host, another share, or a drive
    /// share of another letter leaves the path as it was.
    /// </summary>
    [Fact]
    public void ARootThroughThisPcsAdminShareIsDecidedAsTheDrivePathItNames()
    {
        using TempTree tree = PlantedTree();
        string store = tree.PathOf(Store);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        string[] hosts = ["localhost", "LOCALHOST", "127.0.0.1", "127.1.2.3", "0--1.ipv6-literal.net", Environment.MachineName,
            System.Net.Dns.GetHostName()];
        foreach (string host in hosts)
        {
            string inside = LongPath.NormalizeRoot(Loopback(store + @"\L1\Files", host)).Win32;
            string above = LongPath.NormalizeRoot(Loopback(tree.Root, host)).Win32;
            Assert.Equal(@"\\?\" + store + @"\L1\Files", ContainerLayerStore.ThroughLoopbackShare(inside), StringComparer.OrdinalIgnoreCase);

            Assert.Equal(StoreVerdictKind.Refuse, ContainerLayerStore.Resolve(inside, store, cancelled.Token).Kind);
            StoreVerdict verdict = ContainerLayerStore.Resolve(above, store, cancelled.Token);
            Assert.Equal(StoreVerdictKind.Exclude, verdict.Kind);
            Assert.Equal(above + @"\x\Containers\Layers", verdict.Excluded);
            Assert.Equal(StoreVerdictKind.None, ContainerLayerStore.Resolve(inside, store, cancelled.Token, Off()).Kind);
        }

        string drive = store[..1];
        foreach (string other in new[] { $@"\\?\UNC\fsa-not-this-pc\{drive}$\x", $@"\\?\UNC\localhost\share\x", @"\\?\UNC\localhost\" + drive + @"$$\x",
            @"\\?\UNC\localhost", @"\\?\" + store, @"\\?\UNC\127.0.0.1x\" + drive + @"$\x" })
            Assert.Same(other, ContainerLayerStore.ThroughLoopbackShare(other));
    }

    /// <summary>
    /// O-084 (validate attempt 2's \\localhost\C$ finding): the scan itself through this PC's administrative share,
    /// listed over SMB. A root inside the planted store is Refused with nothing listed; a root above it skips the store
    /// as one not-scanned folder in the share's spelling, counting everything else; a directory symbolic link to the share
    /// relates to the store only by its final path, which comes back as a share and is mapped the same way. Only the
    /// share's answering is conditional, since a PC can turn administrative shares off: the comparison itself is
    /// asserted with no share by the test above. On this PC they answer, so every row runs.
    /// </summary>
    [Fact]
    public void AScanThroughThisPcsAdminShareRefusesInsideTheStoreAndSkipsItFromAbove()
    {
        using TempTree tree = PlantedTree();
        foreach (string host in new[] { "localhost", "127.0.0.1" })
        {
            string share = Loopback(tree.Root, host);
            if (!Directory.Exists(share))
                continue; // administrative shares are off on this PC: the comparison is still asserted, shareless, above

            SynchronousIoCancel? scanGuard = null;
            ScanResult inside = Scans.WithGuard(guard => scanGuard = guard,
                () => Scanner.Scan(Loopback(tree.PathOf($@"{Store}\L1\Files"), host), new ScanProgress(), CancellationToken.None, Planted(tree)));
            Assert.Equal(ScanStatus.Refused, inside.Status);
            Assert.Equal(ScanRefusal.ContainerLayerStore, inside.Refusal);
            Assert.Equal(0, scanGuard!.Calls);
            Assert.Equal(0, inside.Store.Count);

            var progress = new ScanProgress();
            var listed = new List<string>();
            ScanResult above = Scanner.Scan(share, progress, CancellationToken.None, Planted(tree, _ => listed.Add(progress.CurrentFolder!)));
            Assert.Equal(ScanStatus.Completed, above.Status);
            Assert.Equal(OutsideBytes, above.Totals.Bytes);
            Assert.Equal(share + @"\x\Containers\Layers", above.Store.GetPath(Assert.Single(above.SkippedStores).Entry), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(0, above.OpensUnderExcludedStore);
            Assert.DoesNotContain(listed, path => path.Contains(@"\Containers\Layers", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(listed, path => path.EndsWith(@"\Containers\ContainerStorages", StringComparison.OrdinalIgnoreCase));

            string link = tree.DirectorySymlink($"to-{host}", Loopback(tree.PathOf("x"), host));
            ScanResult throughLink = Scans.Run(link, Planted(tree));
            Assert.Equal(ScanStatus.Completed, throughLink.Status);
            Assert.Equal(5 + 10, throughLink.Totals.Bytes);
            Assert.Equal(link + @"\Containers\Layers", throughLink.Store.GetPath(Assert.Single(throughLink.SkippedStores).Entry));
            string linkInside = tree.DirectorySymlink($"into-{host}", Loopback(tree.PathOf($@"{Store}\L1"), host));
            Assert.Equal(ScanStatus.Refused, Scanner.Scan(linkInside, new ScanProgress(), CancellationToken.None, Planted(tree)).Status);
        }
    }

    // A local path as this PC's administrative share of its drive names it: C:\a\b through host is \\host\C$\a\b.
    private static string Loopback(string localPath, string host) => $@"\\{host}\{localPath[0]}$\{localPath[3..]}";

    private static ScanOptions PlantedOff(TempTree tree) => new() { ContainerLayerStorePath = tree.PathOf(Store), EnabledWorkarounds = Off() };

    private static IReadOnlySet<string> Off() =>
        KnownWorkarounds.DefaultEnabled.Where(id => id != KnownWorkarounds.WcifsLayerStore.Id).ToHashSet(StringComparer.Ordinal);

    // The volume's \\?\Volume{GUID}\ name, from mountvol, which needs no administrator to list.
    private static string VolumeGuidOf(string driveRoot)
    {
        string output = RunTool("mountvol.exe", driveRoot, "/L").Trim();
        Assert.StartsWith(@"\\?\Volume{", output);
        return output;
    }

    private static string RunTool(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), $"{file} did not exit");
        Assert.True(process.ExitCode == 0, $"{file} {string.Join(' ', args)} exited {process.ExitCode}: {output} {error}");
        return output;
    }

    /// <summary>
    /// A subst drive letter for this logon session, mapped to a folder of the tree and removed again: the first letter
    /// from Z: down that no drive uses.
    /// </summary>
    private sealed class SubstDrive : IDisposable
    {
        private readonly string _letter;

        public SubstDrive(string target)
        {
            HashSet<char> used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
            char free = Enumerable.Range('M', 'Z' - 'M' + 1).Select(c => (char)c).Reverse().FirstOrDefault(c => !used.Contains(c));
            Assert.True(free != default, "no drive letter from M: to Z: is free for subst");
            _letter = free + ":";
            RunTool("subst.exe", _letter, target);
            Root = _letter + @"\";
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                RunTool("subst.exe", _letter, "/d");
            }
            catch (Exception)
            {
                // Best effort: a mapping left behind is gone at the next sign-in.
            }
        }
    }
}
