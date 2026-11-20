using System.Text;
using System.Text.Json;
using FsAnalyser.App;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-007 (SC-028, SC-037): the report's file, from the probe that finds a folder that takes no new file to the move that puts
/// the report in place. Every behaviour runs on a real folder (ReportFile is compiled in as source: System.IO only), with no exe
/// started. The links someone could plant are the class in ReportFilePlantsCorpus.json, and each row of it is run here.
/// Symbolic links need Developer Mode, as every link test in this suite does; hardlinks and junctions need nothing.
/// </summary>
public class ReportFileTests
{
    private const string Sentinel = "SENTINEL: must never be overwritten";
    private static readonly byte[] Json = Encoding.UTF8.GetBytes("{ \"schema\": 1 }\n");

    private sealed record Row(string Id, string PlantAt, string Kind, string Expect, string? CoveredBy);

    private static Row[] Corpus()
    {
        string path = Path.Combine(FailCriticalErrorsTests.FindRepo(), "tests", "FsAnalyser.Core.Tests", "ReportFilePlantsCorpus.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.GetProperty("rows").EnumerateArray().Select(r => new Row(
            r.GetProperty("id").GetString()!, r.GetProperty("plantAt").GetString()!, r.GetProperty("kind").GetString()!,
            r.GetProperty("expect").GetString()!, r.TryGetProperty("coveredBy", out JsonElement c) ? c.GetString() : null))];
    }

    public static IEnumerable<object[]> RowsAt(string plantAt, string expect) =>
        Corpus().Where(r => r.PlantAt == plantAt && r.Expect == expect).Select(r => new object[] { r.Id });

    private static Row RowById(string id) => Corpus().Single(r => r.Id == id);

    // What a planted entry is, and what an attack through it would change: TargetChanged is null while nothing it points at has
    // been written to or created, and StillThere says the planted entry itself is as it was planted.
    private sealed class Planted(Func<string?> targetChanged, Func<bool> stillThere)
    {
        public string? TargetChanged() => targetChanged();

        public bool StillThere() => stillThere();
    }

    private static Planted Plant(TempTree tree, string kind, string link, string elsewhere)
    {
        string sentinel = Path.Combine(elsewhere, "sentinel.txt");
        string targetFolder = Path.Combine(elsewhere, "target");
        string absent = Path.Combine(elsewhere, "absent.txt");
        string Relative(string path) => Path.GetRelativePath(tree.Root, path);

        string? FolderChanged(string folder) =>
            Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).SequenceEqual(["keep.txt"])
            && File.ReadAllText(Path.Combine(folder, "keep.txt")) == Sentinel
                ? null
                : $"the folder {folder} was written to";

        switch (kind)
        {
            case "hardlink":
                File.WriteAllText(sentinel, Sentinel);
                TempTree.Hardlink(link, sentinel);
                return new(() => File.ReadAllText(sentinel) == Sentinel ? null : "the sentinel was overwritten",
                    () => File.Exists(link) && File.ReadAllText(link) == Sentinel);
            case "file-symlink":
                File.WriteAllText(sentinel, Sentinel);
                tree.FileSymlink(Relative(link), sentinel);
                return new(() => File.ReadAllText(sentinel) == Sentinel ? null : "the sentinel was overwritten",
                    () => new FileInfo(link).LinkTarget is not null && File.ReadAllText(link) == Sentinel);
            case "dangling-file-symlink":
                tree.FileSymlink(Relative(link), absent);
                return new(() => File.Exists(absent) ? "a file was created at the link's target" : null,
                    () => new FileInfo(link).LinkTarget is not null);
            case "junction":
                Directory.CreateDirectory(targetFolder);
                File.WriteAllText(Path.Combine(targetFolder, "keep.txt"), Sentinel);
                tree.Junction(Relative(link), targetFolder);
                return new(() => FolderChanged(targetFolder), () => Directory.Exists(link));
            case "directory-symlink":
                Directory.CreateDirectory(targetFolder);
                File.WriteAllText(Path.Combine(targetFolder, "keep.txt"), Sentinel);
                tree.DirectorySymlink(Relative(link), targetFolder);
                return new(() => FolderChanged(targetFolder), () => Directory.Exists(link));
            case "file":
                File.WriteAllText(link, Sentinel);
                return new(() => File.ReadAllText(link) == Sentinel ? null : "the file was overwritten", () => File.Exists(link));
            case "directory":
                Directory.CreateDirectory(link);
                File.WriteAllText(Path.Combine(link, "keep.txt"), Sentinel);
                return new(() => FolderChanged(link), () => Directory.Exists(link));
            default:
                throw new InvalidOperationException($"the corpus has a plant kind this test cannot make: {kind}");
        }
    }

    private static (string Folder, string Elsewhere, string Report) Layout(TempTree tree)
    {
        string folder = tree.Folder("out");
        return (folder, tree.Folder("elsewhere"), Path.Combine(folder, "r.json"));
    }

    private static string[] Entries(string folder) =>
        [.. Directory.GetFileSystemEntries(folder).Select(e => Path.GetFileName(e)!).Order(StringComparer.Ordinal)];

    [Fact]
    public void AFreshStemIsDrawnForEachOpenAtTheMomentOfOpening()
    {
        using var tree = new TempTree();
        (string folder, _, string report) = Layout(tree);
        var drawn = new List<string>();
        var onDiskWhenDrawn = new List<string[]>();
        var file = new ReportFile(report, () =>
        {
            onDiskWhenDrawn.Add(Entries(folder)); // what anybody listing the folder could see at the moment of this draw
            string stem = $".fsa-draw{drawn.Count + 1}";
            drawn.Add(stem);
            return stem;
        });

        Assert.Empty(drawn); // the constructor stores the source of names and draws nothing

        file.Probe();
        Assert.Single(drawn);

        file.Write(Json);
        Assert.Equal(2, drawn.Count); // the write draws a stem of its own, and the probe's is not reused
        Assert.Equal(drawn.Count, drawn.Distinct().Count());

        // When the write drew its stem the probe's file was gone and its own did not exist yet: its name was on disk nowhere.
        Assert.All(onDiskWhenDrawn, entries => Assert.Empty(entries));
        Assert.Equal(["r.json"], Entries(folder));
    }

    [Fact]
    public void TheDefaultStemIsNewForEveryOpenAndLeavesTheFolderAsItWas()
    {
        using var tree = new TempTree();
        (string folder, _, string report) = Layout(tree);
        var file = new ReportFile(report);

        file.Probe();
        file.Probe();
        Assert.Empty(Entries(folder));
        file.Write(Json);
        Assert.Equal(["r.json"], Entries(folder));
        Assert.Equal(Json, File.ReadAllBytes(report));
    }

    [Fact]
    public void AProbeLeavesAWritableFolderAsItWas()
    {
        using var tree = new TempTree();
        (string folder, _, string report) = Layout(tree);
        File.WriteAllText(Path.Combine(folder, "other.txt"), "kept");

        new ReportFile(report).Probe();

        Assert.Equal(["other.txt"], Entries(folder));
        Assert.False(File.Exists(report));
    }

    [Fact]
    public void AProbeIsRefusedWhereNoFileCanBeCreated()
    {
        using var tree = new TempTree();
        (string folder, _, string report) = Layout(tree);
        tree.Deny(folder, 0x2); // FILE_ADD_FILE: the right a new file in a folder needs, which the exe's scan and write would need too

        Assert.Throws<UnauthorizedAccessException>(() => new ReportFile(report).Probe());
        Assert.Empty(Entries(folder));
    }

    [Fact]
    public void AProbeOfAnAbsentFolderThrows()
    {
        using var tree = new TempTree();
        string report = Path.Combine(tree.Root, "absent folder", "r.json");

        Assert.Throws<DirectoryNotFoundException>(() => new ReportFile(report).Probe());
    }

    [Theory]
    [MemberData(nameof(RowsAt), "temporary", "refused")]
    public void AWriteRefusesALinkAlreadyAtItsTemporaryName(string id)
    {
        Row row = RowById(id);
        using var tree = new TempTree();
        (string folder, string elsewhere, string report) = Layout(tree);
        Planted planted = Plant(tree, row.Kind, Path.Combine(folder, ".fsa-planted.tmp"), elsewhere);
        var file = new ReportFile(report, () => ".fsa-planted");

        Exception thrown = Assert.ThrowsAny<Exception>(() => file.Write(Json));

        Assert.True(thrown is IOException or UnauthorizedAccessException, $"{id}: {thrown.GetType().Name} is neither of the two the app maps to exit 3");
        Assert.True(planted.TargetChanged() is null, $"{id}: {planted.TargetChanged()}");
        Assert.True(planted.StillThere(), $"{id}: the planted entry was removed or changed");
        Assert.False(File.Exists(report), $"{id}: a report was left");
        Assert.Equal([".fsa-planted.tmp"], Entries(folder));
    }

    [Theory]
    [MemberData(nameof(RowsAt), "earlier-stem", "ignored")]
    public void AWriteIsNotCaughtByAnEntryAtTheNameAnEarlierStemWouldHaveGiven(string id)
    {
        Row row = RowById(id);
        using var tree = new TempTree();
        (string folder, string elsewhere, string report) = Layout(tree);
        var stems = new Queue<string>([".fsa-first", ".fsa-second"]);
        var file = new ReportFile(report, stems.Dequeue);
        Planted planted = Plant(tree, row.Kind, Path.Combine(folder, ".fsa-first.tmp"), elsewhere);

        file.Probe();
        file.Write(Json);

        Assert.True(planted.TargetChanged() is null, $"{id}: {planted.TargetChanged()}");
        Assert.True(planted.StillThere(), $"{id}: the planted entry was removed or changed");
        Assert.Equal(Json, File.ReadAllBytes(report));
        Assert.Equal([".fsa-first.tmp", "r.json"], Entries(folder));
    }

    [Theory]
    [MemberData(nameof(RowsAt), "report", "replaced")]
    public void AWriteReplacesALinkAtTheReportItself(string id)
    {
        Row row = RowById(id);
        using var tree = new TempTree();
        (string folder, string elsewhere, string report) = Layout(tree);
        Planted planted = Plant(tree, row.Kind, report, elsewhere);

        new ReportFile(report).Write(Json);

        Assert.True(planted.TargetChanged() is null, $"{id}: {planted.TargetChanged()}");
        Assert.Equal(Json, File.ReadAllBytes(report));
        Assert.Null(new FileInfo(report).LinkTarget); // a file of its own: not a link, and not a second name of the sentinel
        Assert.Equal(["r.json"], Entries(folder));
    }

    [Theory]
    [MemberData(nameof(RowsAt), "legacy-tmp", "ignored")]
    public void AWriteNeverUsesTheOldFixedName(string id)
    {
        Row row = RowById(id);
        using var tree = new TempTree();
        (string folder, string elsewhere, string report) = Layout(tree);
        Planted planted = Plant(tree, row.Kind, report + ".tmp", elsewhere);

        new ReportFile(report).Write(Json);

        Assert.True(planted.TargetChanged() is null, $"{id}: {planted.TargetChanged()}");
        Assert.True(planted.StillThere(), $"{id}: the planted entry was removed or changed");
        Assert.Equal(Json, File.ReadAllBytes(report));
        Assert.Equal(["r.json", "r.json.tmp"], Entries(folder));
    }

    [Fact]
    public void EveryRowOfThePlantsCorpusIsRunByATheoryAndAResidualRowNamesTheRowThatHolds()
    {
        Row[] rows = Corpus();
        Assert.Equal(rows.Length, rows.Select(r => r.Id).Distinct().Count());

        (string PlantAt, string Expect)[] run =
            [("temporary", "refused"), ("earlier-stem", "ignored"), ("report", "replaced"), ("legacy-tmp", "ignored")];
        var wrong = new List<string>();
        foreach (Row row in rows)
        {
            if (row.Expect == "residual")
            {
                Row? covering = rows.SingleOrDefault(r => r.Id == row.CoveredBy);
                if (covering is null || covering.Expect == "residual" || covering.Kind != row.Kind)
                    wrong.Add($"{row.Id}: a residual row must name a coveredBy row of its own kind that is not residual, not '{row.CoveredBy}'");
            }
            else if (!run.Contains((row.PlantAt, row.Expect)))
            {
                wrong.Add($"{row.Id}: plantAt {row.PlantAt} with expect {row.Expect} is run by no test");
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void AFailedMoveLeavesNoTemporary()
    {
        using var tree = new TempTree();
        (string folder, _, string report) = Layout(tree);
        File.WriteAllText(report, "OLD");
        // Held open without FileShare.Delete: the move over it cannot replace it, after the temporary was written.
        using var held = new FileStream(report, FileMode.Open, FileAccess.Read, FileShare.Read);
        var file = new ReportFile(report);

        Exception thrown = Assert.ThrowsAny<Exception>(() => file.Write(Json));

        Assert.True(thrown is IOException or UnauthorizedAccessException, $"{thrown.GetType().Name} is neither of the two the app maps to exit 3");
        Assert.Equal(["r.json"], Entries(folder)); // no .fsa-*.tmp: only this class knows the temporary's name, so only it can delete it
        using var reader = new StreamReader(held);
        Assert.Equal("OLD", reader.ReadToEnd());
    }

    [Fact]
    public void DiscardRemovesTheReportAndTheOldFixedTemporary()
    {
        using var tree = new TempTree();
        (string folder, _, string report) = Layout(tree);
        var file = new ReportFile(report);
        file.Write(Json);
        File.WriteAllText(report + ".tmp", "stale");
        File.WriteAllText(Path.Combine(folder, "other.txt"), "kept");

        file.Discard();

        Assert.Equal(["other.txt"], Entries(folder));
        file.Discard(); // nothing left to delete: never throws
    }
}
