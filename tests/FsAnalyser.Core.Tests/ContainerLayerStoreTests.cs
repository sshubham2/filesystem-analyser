using System.Diagnostics;
using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-095: the Windows container layer store is left out of a scan by its path, and a root at or inside it is refused.
/// Every case runs on a real NTFS tree in %TEMP% with a planted store (ScanOptions.ContainerLayerStorePath); none of
/// them goes near the real store, whose live layer leaks wcifs pool on first touch. The planted store holds bytes, so a
/// scan that walked into it would count them: each case says what the totals must be without them.
/// </summary>
public class ContainerLayerStoreTests
{
    // Under the tree: x\Containers\Layers is the planted store, with a layer's Files tree in it; ContainerStorages and a
    // file sit beside it, and z comes after x in listing order.
    private const string Store = @"x\Containers\Layers";
    private const long StoreBytes = 1000 + 300;
    private const long OutsideBytes = 10 + 5 + 7;

    private static TempTree PlantedTree()
    {
        var tree = new TempTree();
        tree.Folder(@"x\Containers\Layers\L1\Files\Windows\System32\Speech");
        tree.File(@"x\Containers\Layers\L1\Files\Windows\System32\Speech\voice.dat", 1000);
        tree.File(@"x\Containers\Layers\L1\Hives.dat", 300);
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
    /// C-029 / C-015 / C-001: the walk pushes the store like any folder and stops at it when it pops it. The store is
    /// an entry, never opened, with nothing listed and its size not counted; it is the one SkippedStores entry, not
    /// in Unreadable; nothing under it is listed or probed, and the backstop refused nothing.
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

    /// <summary>The store as a row directly under the root (a scan of its parent, ...\Containers): a row of its own state.</summary>
    [Fact]
    public void TheStoreAsATopLevelRowIsNotScannedNeitherDoneNorUnreadable()
    {
        using TempTree tree = PlantedTree();

        ScanResult result = Scans.Run(tree.PathOf(@"x\Containers"), Planted(tree));

        Assert.Equal(ScanStatus.Completed, result.Status);
        TopLevelRow layers = result.TopLevel!.Rows.Single(r => r.Name == "Layers");
        Assert.Equal(TopLevelState.NotScanned, layers.State);
        Assert.Equal(0, layers.Bytes);
        Assert.Equal(TopLevelState.Done, result.TopLevel.Rows.Single(r => r.Name == "ContainerStorages").State);
        Assert.Single(result.SkippedStores);
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
    /// C-031 / C-017 / C-005: a folder named Layers elsewhere, and siblings of the store named Layers2 and LayersBackup,
    /// are walked and counted. Only the one path is left out.
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

        ScanResult result = Scans.Run(tree.Root, Planted(tree));

        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Equal(OutsideBytes + 700, result.Totals.Bytes);
        Assert.Equal(tree.PathOf(Store), result.Store.GetPath(Assert.Single(result.SkippedStores).Entry));
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
        ScanResult plain = Scans.Run(tree.Root, new ScanOptions { ContainerLayerStorePath = tree.PathOf(Store), Precautions = Off() });

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
    /// C-034 / C-016 / C-002 / C-003: a root that is the store, or a folder deep inside it, is Refused: nothing listed or
    /// probed, no unreadable record, no exception. Q-179 and Q-182 decided refusal; the earlier design scanned it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(@"\L1")]
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
    /// C-036: a root that is a junction, or a subst drive, whose target is inside the store. The string step sees no
    /// relation; only the final path by handle does, and the scan is refused.
    /// </summary>
    [Fact]
    public void ARootThroughAJunctionIntoTheStoreIsRefused()
    {
        using TempTree tree = PlantedTree();
        string junction = tree.Junction("into", tree.PathOf($@"{Store}\L1"));

        ScanResult result = Scanner.Scan(junction, new ScanProgress(), CancellationToken.None, Planted(tree));

        Assert.Equal(ScanStatus.Refused, result.Status);
        Assert.Equal(0, result.Store.Count);
    }

    [Fact]
    public void ARootThroughASubstDriveIntoTheStoreIsRefused()
    {
        using TempTree tree = PlantedTree();
        using var drive = new SubstDrive(tree.PathOf($@"{Store}\L1\Files"));

        ScanResult result = Scanner.Scan(drive.Root, new ScanProgress(), CancellationToken.None, Planted(tree));

        Assert.Equal(ScanStatus.Refused, result.Status);
        Assert.Equal(ScanRefusal.ContainerLayerStore, result.Refusal);
    }

    // ---- No store, and a root the resolution cannot open ----

    /// <summary>
    /// C-037 / C-024 / C-006: the store does not exist (the override names a missing path, as on a PC without the
    /// containers feature). The scan is the one it would be with the precaution off: same totals, nothing skipped,
    /// nothing unreadable.
    /// </summary>
    [Fact]
    public void WithNoStoreTheScanIsWhatItWasBefore()
    {
        using var tree = new TempTree();
        tree.Folder(@"x\Containers\ContainerStorages");
        tree.File(@"x\Containers\ContainerStorages\a.bin", 10);
        tree.File(@"x\b.txt", 5);

        ScanResult guarded = Scans.Run(tree.Root, new ScanOptions { ContainerLayerStorePath = tree.PathOf(Store) });
        ScanResult plain = Scans.Run(tree.Root, new ScanOptions { Precautions = Off() });

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
    /// C-023: DirectoryReader.Read called straight with a folder under the store, past the walk's check. No open is made,
    /// it is not an unreadable error but the skip outcome, and the backstop's counter says so.
    /// </summary>
    [Fact]
    public void TheReaderRefusesAndCountsAFolderUnderTheStoreWithNoOpen()
    {
        using TempTree tree = PlantedTree();
        using var guard = new SynchronousIoCancel(CancellationToken.None);
        using var reader = new DirectoryReader(guard) { ExcludedStore = LongPath.ToExtended(tree.PathOf(Store)) };

        int error = reader.Read(LongPath.ToExtended(tree.PathOf($@"{Store}\L1\Files")), fullClass: false, keepLastAccess: false);

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
    /// Both positions of the switch (Workarounds.ContainerLayerStore). Off restores the plain behaviour exactly: the
    /// store is walked and counted like any folder, and a root inside it is scanned, not refused.
    /// </summary>
    [Fact]
    public void WithThePrecautionOffTheStoreIsWalkedAndARootInsideItIsScanned()
    {
        using TempTree tree = PlantedTree();

        ScanResult on = Scans.Run(tree.Root, Planted(tree));
        ScanResult off = Scans.Run(tree.Root, new ScanOptions { ContainerLayerStorePath = tree.PathOf(Store), Precautions = Off() });
        ScanResult insideOff = Scans.Run(tree.PathOf(Store), new ScanOptions { ContainerLayerStorePath = tree.PathOf(Store), Precautions = Off() });

        Assert.Equal(OutsideBytes, on.Totals.Bytes);
        Assert.Equal(OutsideBytes + StoreBytes, off.Totals.Bytes);
        Assert.Empty(off.SkippedStores);
        Assert.Equal(ScanStatus.Completed, insideOff.Status);
        Assert.Equal(StoreBytes, insideOff.Totals.Bytes);
        Assert.True(WorkaroundSwitches.Defaults.IsOn(Workarounds.ContainerLayerStore.Id));
        Assert.True(Workarounds.ContainerLayerStore.OnByDefault);
        Assert.Contains(Workarounds.ContainerLayerStore, Workarounds.All);
    }

    private static WorkaroundSwitches Off() => WorkaroundSwitches.Defaults.With(Workarounds.ContainerLayerStore.Id, on: false);

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
