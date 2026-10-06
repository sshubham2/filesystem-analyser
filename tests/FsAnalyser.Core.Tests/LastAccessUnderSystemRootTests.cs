using FsAnalyser.Core.Native;
using FsAnalyser.Core.Scanning;
using Xunit.Abstractions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The scan's kept count where its own opens move last-access times (SL-096): under the Windows folder, built there on
/// purpose. In %SystemRoot%\Temp, opening a child folder with write access moves its parent's last-access time, and the
/// scan's keep-open of each child folder is such an open. TempTree builds every other tree outside the Windows folder, so
/// these are the default-suite tests that keep that side effect, and whether the count tells the truth about it, in
/// every run. They are never skipped and carry no trait: .taad/fitness.yaml says so, and LastAccessRuleCorpusTests checks
/// it by reflection too.
/// </summary>
/// <remarks>
/// Each tree is back-dated 48 h deepest first, so no back-dating moves a folder already back-dated, and every entry is
/// checked old before the scan. LastAccessKept must equal the folders still old after it, which fails either way: over
/// (the scanner's correction is gone, or matches the Windows folder too narrowly) and under (it counts out a folder that
/// did not move, or this machine no longer moves parents there). Each tree must be gone after Dispose.
/// </remarks>
public class LastAccessUnderSystemRootTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly string WindowsTempTests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "fsa-tests");

    // Root, plain and links each hold two folders; two of the files sit in folders with no folder below them.
    private static readonly string[] NestedFolders = ["", "plain", @"plain\sub", @"plain\empty", "links", @"links\a-first", @"links\b-second"];
    private static readonly (string Path, long Length)[] NestedFiles =
        [("top.bin", 100), (@"plain\small.txt", 10_000), (@"plain\sub\deeper.bin", 65_536), (@"links\b-second\written.bin", 1_024)];

    /// <summary>
    /// The defect: (asked, kept) was (7, 7) while root, plain and links moved, 4 still old. Removing a parent once per
    /// kept child would give 1; once per parent gives 4. The root is counted on ListRoot's own path. The root's spelling
    /// varies: GetFolderPath says C:\WINDOWS, so a case-sensitive match would miss the lower-case root and read 7 against 4.
    /// The trailing separator is trimmed by Scan before anything compares (review m5), so its row is a spelling check only.
    /// </summary>
    [Theory]
    [InlineData("as-is")]
    [InlineData("lower-case")]
    [InlineData("trailing-separator")]
    public void AScanUnderTheWindowsTempFolderCountsAsKeptOnlyTheFoldersThatStayedOld(string spelling)
    {
        InTree(new TempTree(WindowsTempTests), tree =>
        {
            BuildNested(tree);
            string root = spelling switch
            {
                "as-is" => tree.Root,
                "lower-case" => tree.Root.ToLowerInvariant(),
                "trailing-separator" => tree.Root + @"\",
                _ => throw new ArgumentOutOfRangeException(nameof(spelling), spelling, "no such spelling"),
            };

            Scanned scanned = BackDateAndScan(tree, root, NestedFolders, NestedFiles.Select(f => f.Path).ToArray(), exempt: []);

            Assert.Equal(NestedFolders.Length, scanned.Result.LastAccessAsked);
            AssertKeptIsTheFoldersStillOld(scanned, tree);
        });
    }

    /// <summary>
    /// A kept folder whose only child folder refuses FILE_WRITE_ATTRIBUTES: the refused open moves nothing (SL-096 spike 1,
    /// step 6), so the parent stays kept. A rule keyed on attempted opens counted it out: kept 0 while it still read 48 h old.
    /// </summary>
    [Fact]
    public void AChildThatRefusesWriteAttributesLeavesItsParentCountedAsKept()
    {
        InTree(new TempTree(WindowsTempTests), tree =>
        {
            tree.Folder("refusing");
            tree.File(@"refusing\x.bin", 10);
            tree.Deny(tree.PathOf("refusing"), 0x100); // FILE_WRITE_ATTRIBUTES, no inheritance: only this folder refuses
            // Back-dated after the deny, whose open moves the root. refusing itself cannot be back-dated now: exempt.

            Scanned scanned = BackDateAndScan(tree, tree.Root, ["", "refusing"], [@"refusing\x.bin"], exempt: ["refusing"]);

            Assert.Equal(2, scanned.Result.LastAccessAsked);
            AssertKeptIsTheFoldersStillOld(scanned, tree);
        });
    }

    /// <summary>The same nested tree where TempTree builds it, outside the Windows folder: the correction never fires.</summary>
    [Fact]
    public void OutsideTheWindowsFolderEveryKeptFolderStaysKept()
    {
        InTree(new TempTree(), tree =>
        {
            string windows = LongPath.NormalizeRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).Win32;
            Assert.False(Scanner.IsAtOrUnder(LongPath.ToExtended(tree.Root), windows),
                $"TempTree built {tree.Root} under the Windows folder; its root choice should have moved it out");
            BuildNested(tree);

            Scanned scanned = BackDateAndScan(tree, tree.Root, NestedFolders, NestedFiles.Select(f => f.Path).ToArray(), exempt: []);

            int n = NestedFolders.Length;
            Assert.Equal((n, n, n), (scanned.Result.LastAccessAsked, scanned.Result.LastAccessKept, scanned.StillOld));
        });
    }

    private sealed record Scanned(ScanResult Result, int Folders, int StillOld, List<string> Moved);

    private static void BuildNested(TempTree tree)
    {
        foreach (string folder in NestedFolders.Skip(1))
            tree.Folder(folder);
        foreach ((string path, long length) in NestedFiles)
            tree.File(path, length);
    }

    // Disposes the tree and checks it is gone: a tree left in the Windows folder's Temp is found only by its name.
    private static void InTree(TempTree tree, Action<TempTree> body)
    {
        string root = tree.Root;
        using (tree)
            body(tree);
        Assert.False(Directory.Exists(LongPath.ToExtended(root)), $"{root} is still there after the tree was disposed");
    }

    // Back-dates every entry but the exempt ones 48 h, deepest first, checks each reads old, scans the root as spelled, and
    // reads every folder again: how many still read old, and which moved.
    private Scanned BackDateAndScan(TempTree tree, string scanRoot, string[] folders, string[] files, string[] exempt)
    {
        DateTime old = DateTime.UtcNow.AddHours(-48);
        string[] entries = [.. folders, .. files];
        foreach (string relative in entries.Except(exempt).OrderByDescending(Depth))
        {
            string path = tree.PathOf(relative);
            if (Directory.Exists(path))
                Directory.SetLastAccessTimeUtc(path, old);
            else
                File.SetLastAccessTimeUtc(path, old);
        }

        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromHours(47);
        var before = new Dictionary<string, DateTime>();
        var notOld = new List<string>();
        foreach (string relative in entries)
        {
            before[relative] = LastAccess(tree.PathOf(relative));
            if (!exempt.Contains(relative) && before[relative] >= cutoff)
                notOld.Add($"{Name(relative)} reads {before[relative]:u}");
        }
        Assert.True(notOld.Count == 0,
            $"setup: not back-dated under {tree.Root}: {string.Join("; ", notOld)}. This machine may record no access, or a back-dating moved an entry already back-dated.");

        ScanResult result = Scanner.Scan(scanRoot, new ScanProgress(), CancellationToken.None);
        Assert.Equal(ScanStatus.Completed, result.Status);
        Assert.Empty(result.Unreadable);

        int stillOld = 0;
        var moved = new List<string>();
        foreach (string relative in folders)
        {
            DateTime after = LastAccess(tree.PathOf(relative));
            if (after < cutoff)
                stillOld++;
            if (after - before[relative] > Hour)
                moved.Add(Name(relative));
        }
        output.WriteLine($"{scanRoot}: kept {result.LastAccessKept} of {result.LastAccessAsked}; {stillOld} of {folders.Length} folders still at least 47 h old; " +
            $"moved: {(moved.Count == 0 ? "none" : string.Join(", ", moved))}");
        return new Scanned(result, folders.Length, stillOld, moved);
    }

    private static void AssertKeptIsTheFoldersStillOld(Scanned scanned, TempTree tree)
    {
        int kept = scanned.Result.LastAccessKept;
        string moved = scanned.Moved.Count == 0 ? "none" : string.Join(", ", scanned.Moved);
        Assert.True(kept == scanned.StillOld,
            $"(asked, kept) = ({scanned.Result.LastAccessAsked}, {kept}), but {scanned.StillOld} of the {scanned.Folders} folders under {tree.Root} " +
            $"still read at least 47 h old after the scan (moved: {moved}). " +
            (kept > scanned.StillOld
                ? "Kept over-counts: the scan counted as kept a folder whose last access its own later keep-open of a child folder moved. " +
                  "The Scanner's correction (CountListingOpen) is missing, or matches the Windows folder too narrowly (case, spelling)."
                : "Kept under-counts: the scan counted out a folder whose last access stayed. The correction is keyed on something other " +
                  "than a child folder's successful keep-open, or this machine no longer moves a parent's last access here."));
    }

    private static int Depth(string relative) => relative.Length == 0 ? 0 : relative.Count(c => c == '\\') + 1;

    private static string Name(string relative) => relative.Length == 0 ? "<root>" : relative;

    private static DateTime LastAccess(string path) =>
        Directory.Exists(path) ? Directory.GetLastAccessTimeUtc(path) : File.GetLastAccessTimeUtc(path);
}
