using System.Text.Json;
using FsAnalyser.Core.Native;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The class "root spellings the proof cases take as typed rather than as the app normalises them" (SL-003 review IN-m1 and
/// IN-m2), as a corpus: tests/window/W001RootSpellingsCorpus.json. W-001 classes its root, and places its record, and W-002
/// lists its root, by what the app reads the root as, which is LongPath.NormalizeRoot's display form. Every row's expected
/// shape is checked here against that method itself, so the corpus holds no guess about the app; then the script's functions
/// are run over every row.
/// </summary>
public class W001RootSpellingCorpusTests
{
    private static JsonDocument Corpus() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FailCriticalErrorsTests.FindRepo(), "tests", "window", "W001RootSpellingsCorpus.json")));

    /// <summary>
    /// What the app scans a root as, by LongPath.NormalizeRoot: a drive root is a letter, a share root is 'unc', anything else
    /// a folder, and a root the app refuses is 'refused'.
    /// </summary>
    private static string ShapeTheAppReads(string root, string driveType)
    {
        string display;
        try
        {
            display = LongPath.NormalizeRoot(root).Display;
        }
        catch (ArgumentException)
        {
            return "refused";
        }

        if (display.Length == 3 && char.IsAsciiLetter(display[0]) && display[1] == ':' && display[2] == '\\')
            return driveType == "Network" ? "network-letter" : "local-letter";
        string pathRoot = (Path.GetPathRoot(display) ?? string.Empty).TrimEnd('\\');
        return display.StartsWith(@"\\", StringComparison.Ordinal) && pathRoot.Equals(display.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? "unc" : "folder";
    }

    [Fact]
    public void EveryRowsShapeIsWhatTheAppReadsItsRootAs()
    {
        using JsonDocument corpus = Corpus();
        int rows = 0;
        foreach (JsonElement row in corpus.RootElement.GetProperty("shapes").EnumerateArray())
        {
            string root = row.GetProperty("root").GetString()!;
            string driveType = row.TryGetProperty("driveType", out JsonElement t) ? t.GetString()! : "Fixed";
            Assert.True(ShapeTheAppReads(root, driveType) == row.GetProperty("shape").GetString(),
                $"{row.GetProperty("id").GetString()}: the app reads [{root}] as {ShapeTheAppReads(root, driveType)}, the corpus says {row.GetProperty("shape").GetString()}");
            rows++;
        }

        Assert.True(rows >= 30, $"the corpus holds {rows} shape rows");
    }

    [Fact]
    public async Task EveryRootIsClassedAndItsRecordPlacedAsTheAppReadsIt()
    {
        string corpusFile = Path.Combine(FailCriticalErrorsTests.FindRepo(), "tests", "window", "W001RootSpellingsCorpus.json");
        string output = await WindowCasesHelperTests.Pwsh(WindowCasesHelperTests.ParseScript() + WindowCasesHelperTests.Load("Get-W001RootShape") + WindowCasesHelperTests.Load("Get-ProofRecordPath") + $$"""
            $corpus = Get-Content -LiteralPath '{{corpusFile.Replace("'", "''")}}' -Raw | ConvertFrom-Json
            foreach ($row in $corpus.shapes) {
                $type = if ($row.driveType) { [string]$row.driveType } else { 'Fixed' }
                $driveTypeOf = [scriptblock]::Create("param(`$letter) '$type'")
                try {
                    $shape = Get-W001RootShape ([string]$row.root) $driveTypeOf
                    $file = (Get-ProofRecordPath 'R:\repo' 'W-001' $shape ([string]$row.root)).Replace('R:\repo\', '').Replace('\', '/')
                    "row $($row.id) $shape $file"
                }
                catch { "row $($row.id) refused -" }
            }
            """);

        using JsonDocument corpus = Corpus();
        Dictionary<string, string> got = output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("row ", StringComparison.Ordinal))
            .ToDictionary(l => l.Split(' ')[1], l => string.Join(' ', l.Split(' ').Skip(2)));
        List<string> wrong = [];
        foreach (JsonElement row in corpus.RootElement.GetProperty("shapes").EnumerateArray())
        {
            string id = row.GetProperty("id").GetString()!;
            string expected = row.GetProperty("shape").GetString() + " " + (row.GetProperty("record").GetString() ?? "-");
            if (!got.TryGetValue(id, out string? actual) || actual != expected)
                wrong.Add($"{id} [{row.GetProperty("root").GetString()}]: expected '{expected}', got '{actual ?? "nothing"}'");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public async Task EveryRootTheAppScansAsOneFolderListsTheSameFiles()
    {
        // The fixture is a TempTree, which puts its trees where the Core tests' rule allows (never %SystemRoot%\Temp).
        using var fixture = new TempTree();
        string tree = fixture.Folder("spelled");
        fixture.Folder(@"spelled\a\b");
        fixture.File(@"spelled\a\b\f.txt", 1);
        fixture.File(@"spelled\g.txt", 1);
        {
            using JsonDocument corpus = Corpus();
            List<(string Id, string Root)> rows = [];
            foreach (JsonElement row in corpus.RootElement.GetProperty("listing").EnumerateArray())
            {
                string root = row.GetProperty("root").GetString()!.Replace("{tree}", tree).Replace("{tree/}", tree.Replace('\\', '/'));
                rows.Add((row.GetProperty("id").GetString()!, root));
                // The corpus's own premise: the app scans this spelling as the one folder.
                Assert.True(LongPath.NormalizeRoot(root).Display.Equals(tree, StringComparison.OrdinalIgnoreCase), $"{rows[^1].Id}: the app reads [{root}] as {LongPath.NormalizeRoot(root).Display}, not the tree");
            }

            string list = string.Join(", ", rows.Select(r => $"@('{r.Id}', '{r.Root.Replace("'", "''")}')"));
            string output = await WindowCasesHelperTests.Pwsh(WindowCasesHelperTests.ParseScript() + WindowCasesHelperTests.Load("Initialize-SeamWin32") + $$"""
                Initialize-SeamWin32
                foreach ($row in {{list}}) { "listed $($row[0]) $(([FsaWindowCases.Seam]::ListFiles($row[1])).Count)" }
                """);

            List<string> wrong = [.. rows.Where(r => !output.Contains($"listed {r.Id} 2", StringComparison.Ordinal)).Select(r => $"{r.Id} [{r.Root}] did not list 2 files")];
            Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong) + Environment.NewLine + output);
        }
    }
}
