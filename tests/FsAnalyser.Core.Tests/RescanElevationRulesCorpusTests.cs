using System.Text.Json;
using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-008's settled architecture rules about elevation, which have no xUnit twin (Fitness is their only enforcement), run here
/// against the corpus that closes their classes (RescanElevationRulesCorpus.json; review M2, m3 and m4) and against the tree they
/// guard. The patterns are read from .taad/fitness.yaml, the rules' only copy, by the reader SettledRulesCorpusTests uses, so a
/// change there reaches every row. The census runs the patterns over raw text, comments included, which is stricter than the
/// settled entries (Fitness blanks comments for them). The 'before' column is recomputed from the patterns the design wrote, so
/// the rows the fix closed stay shown as rows that were quiet and now fire.
/// </summary>
public class RescanElevationRulesCorpusTests
{
    private static readonly string[] RuleNames =
    [
        "only the rescan button's file asks Windows for elevation",
        "the app never asks for elevation at start",
        "no manifest or resource is attached to the app from outside its project",
        "no manifest or resource is attached to the app from the root build files",
        "only the window's own handler calls the rescan start, and nothing else names the handler",
        "the window hook never names the rescan button",
    ];

    private static JsonDocument Corpus() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(FailCriticalErrorsTests.FindRepo(), "tests", "FsAnalyser.Core.Tests", "RescanElevationRulesCorpus.json")));

    // The files a rule's glob names, in the forms these entries use: <folder>/**/<pattern>, <folder>/** (every file below it),
    // and a pattern at the repository's root (Directory.Build.*). Never a build output.
    private static string[] FilesOf(SettledRulesCorpusTests.Rule rule)
    {
        string repo = FailCriticalErrorsTests.FindRepo();
        string glob = rule.Files;
        IEnumerable<string> files;
        if (glob.Contains("/**/", StringComparison.Ordinal))
        {
            int star = glob.IndexOf("/**/", StringComparison.Ordinal);
            files = Directory.EnumerateFiles(Path.Combine(repo, glob[..star].Replace('/', '\\')), glob[(star + 4)..], SearchOption.AllDirectories);
        }
        else if (glob.EndsWith("/**", StringComparison.Ordinal))
        {
            files = Directory.EnumerateFiles(Path.Combine(repo, glob[..^3].Replace('/', '\\')), "*", SearchOption.AllDirectories);
        }
        else
        {
            Assert.DoesNotContain('/', glob);
            files = Directory.EnumerateFiles(repo, glob, SearchOption.TopDirectoryOnly);
        }
        string[] excepted = [.. rule.ExceptFiles.Select(f => Path.GetFullPath(Path.Combine(repo, f)))];
        return [.. files
            .Where(f => !f.Split('\\').Any(part => part is "bin" or "obj"))
            .Where(f => !excepted.Contains(f, StringComparer.OrdinalIgnoreCase))];
    }

    [Fact]
    public void TheEntriesAreOneEachWrittenByHandAndTheirGlobsNameFiles()
    {
        string repo = FailCriticalErrorsTests.FindRepo();
        foreach (string name in RuleNames)
        {
            SettledRulesCorpusTests.Rule rule = SettledRulesCorpusTests.ReadRule(name);
            Assert.NotEmpty(rule.Patterns);
            Assert.All(rule.Patterns, p => Assert.StartsWith("(?m)", p, StringComparison.Ordinal));
            Assert.True(rule.PatternsAreSingleQuoted, $"'{name}': every pattern is a single-quoted scalar, as hand-written ones are");
            Assert.False(rule.IncludesComments, $"'{name}': a settled entry is matched against code only");
            Assert.True(FilesOf(rule).Length > 0, $"'{name}': its glob {rule.Files} names no file, so it checks nothing");
            Assert.All(rule.ExceptFiles, f => Assert.True(File.Exists(Path.Combine(repo, f)), $"'{name}': the excepted file {f} is not there"));
        }
        Assert.Equal(["src/FsAnalyser.App/RescanAsAdministrator.cs"], SettledRulesCorpusTests.ReadRule(RuleNames[0]).ExceptFiles);
        Assert.Equal(["src/FsAnalyser.App/MainWindow.xaml.cs", "src/FsAnalyser.App/RescanAsAdministrator.cs"], SettledRulesCorpusTests.ReadRule(RuleNames[4]).ExceptFiles);
        Assert.Empty(SettledRulesCorpusTests.ReadRule(RuleNames[1]).ExceptFiles);
    }

    [Fact]
    public void EveryRowOfTheCorpusIsCaughtOrLeftAloneAsItsRowSays()
    {
        using JsonDocument corpus = Corpus();
        var wrong = new List<string>();
        int rows = 0;
        foreach (string name in RuleNames)
        {
            SettledRulesCorpusTests.Rule rule = SettledRulesCorpusTests.ReadRule(name);
            JsonElement entries = corpus.RootElement.GetProperty("rules").GetProperty(name);
            // The root build files' entry holds the previous entry's patterns, so a few rows show that it does.
            Assert.True(entries.GetArrayLength() >= 3, $"'{name}' has too few rows to be a class");
            Assert.Contains(entries.EnumerateArray(), r => r.GetProperty("expect").GetString() == "fire");
            Assert.Contains(entries.EnumerateArray(), r => r.GetProperty("expect").GetString() == "quiet");
            foreach (JsonElement row in entries.EnumerateArray())
            {
                rows++;
                string text = string.Join("\n", row.GetProperty("lines").EnumerateArray().Select(l => l.GetString()));
                bool fires = rule.Patterns.Any(p => Regex.IsMatch(text, p));
                string expect = row.GetProperty("expect").GetString()!;
                if (fires != (expect == "fire"))
                    wrong.Add($"{row.GetProperty("id").GetString()} should {expect} for '{name}' and does not: {text.Trim()}");
            }
        }
        Assert.True(rows >= 50, $"the corpus has {rows} rows");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // The patterns the design wrote, run over the same rows: what the corpus says they did is what they do. The rows that were
    // quiet and now fire are the ones this slice closed (review m3, m4, M2); the ones that fired and are quiet were over-broad.
    [Fact]
    public void TheDesignsOwnPatternsDidWhatTheBeforeColumnSays()
    {
        using JsonDocument corpus = Corpus();
        var wrong = new List<string>();
        int closed = 0, lifted = 0;
        foreach (string name in RuleNames)
        {
            string[] before = [.. corpus.RootElement.GetProperty("beforePatterns").GetProperty(name).EnumerateArray().Select(p => p.GetString()!)];
            foreach (JsonElement row in corpus.RootElement.GetProperty("rules").GetProperty(name).EnumerateArray())
            {
                string text = string.Join("\n", row.GetProperty("lines").EnumerateArray().Select(l => l.GetString()));
                string was = before.Any(p => Regex.IsMatch(text, p)) ? "fire" : "quiet";
                string expect = row.GetProperty("expect").GetString()!;
                if (was != row.GetProperty("before").GetString())
                    wrong.Add($"{row.GetProperty("id").GetString()}: the design's patterns {was}, the corpus says {row.GetProperty("before").GetString()}: {text.Trim()}");
                if (was == "quiet" && expect == "fire")
                    closed++;
                if (was == "fire" && expect == "quiet")
                    lifted++;
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.True(closed >= 20, $"only {closed} rows were closed by this slice's patterns");
        Assert.True(lifted >= 3, $"only {lifted} over-broad rows were lifted");
    }

    [Fact]
    public void TheTreeDoesNotTripTheRulesTheCorpusRuns()
    {
        string repo = FailCriticalErrorsTests.FindRepo();
        var wrong = new List<string>();
        foreach (string name in RuleNames)
        {
            SettledRulesCorpusTests.Rule rule = SettledRulesCorpusTests.ReadRule(name);
            foreach (string file in FilesOf(rule))
            {
                string text = File.ReadAllText(file);
                foreach (string pattern in rule.Patterns)
                {
                    foreach (Match m in Regex.Matches(text, pattern))
                        wrong.Add($"'{name}': {Path.GetRelativePath(repo, file)} has `{m.Value.Trim()}`");
                }
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
