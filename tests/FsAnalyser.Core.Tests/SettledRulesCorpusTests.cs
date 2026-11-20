using System.Text.Json;
using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-007's three settled architecture rules, which have no xUnit twin (Fitness is their only enforcement), run here against
/// the corpus that closes their classes (SettledRulesCorpus.json; review m1, m2 and m6) and against the App's own source. The
/// patterns are read from .taad/fitness.yaml, the rules' only copy, so a change there reaches every row. The census runs the
/// patterns over raw text, comments included, which is stricter than the settled entries (Fitness blanks comments for them).
/// </summary>
public class SettledRulesCorpusTests
{
    private const string ArgsRule = "the app decides its mode only on repaired arguments";
    private const string RebuiltRule = "nothing reads the rebuilt command line";
    private const string WriteRule = "the app writes files only by exclusive create";

    private sealed record Rule(string Name, string Files, string[] ExceptFiles, string[] Patterns, bool IncludesComments, bool PatternsAreSingleQuoted);

    private static string[] FitnessLines() =>
        File.ReadAllLines(Path.Combine(FailCriticalErrorsTests.FindRepo(), ".taad", "fitness.yaml"));

    private static int[] EntriesNamed(string[] lines, string name) =>
        [.. Enumerable.Range(0, lines.Length).Where(i => lines[i].Trim() == "- name: " + name || lines[i].Trim() == "- name: '" + name + "'")];

    private static Rule ReadRule(string name)
    {
        string[] lines = FitnessLines();
        int[] at = EntriesNamed(lines, name);
        Assert.True(at.Length == 1, $"'{name}' must be one entry of .taad/fitness.yaml, and there are {at.Length}");

        string? files = null;
        bool includesComments = false;
        var except = new List<string>();
        var patterns = new List<string>();
        bool allQuoted = true;
        string section = "";
        for (int i = at[0] + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("  - name:", StringComparison.Ordinal) || line.StartsWith("  #", StringComparison.Ordinal))
                break;
            string trimmed = line.Trim();
            int indent = line.Length - line.TrimStart().Length;
            if (trimmed.Length == 0)
                continue;
            if (indent == 4 && !trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                section = trimmed[..trimmed.IndexOf(':')];
                if (section == "files")
                    files = Unquote(trimmed["files:".Length..].Trim(), out _);
                else if (section == "including_comments")
                    includesComments = trimmed.EndsWith("true", StringComparison.Ordinal);
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) && indent is 4 or 6 && section is "except_files" or "must_not_match")
            {
                string item = Unquote(trimmed[2..].Trim(), out bool quoted);
                if (section == "except_files")
                    except.Add(item);
                else
                {
                    patterns.Add(item);
                    allQuoted &= quoted;
                }
            }
        }
        Assert.NotNull(files);
        return new Rule(name, files, [.. except], [.. patterns], includesComments, allQuoted);
    }

    private static string Unquote(string scalar, out bool wasSingleQuoted)
    {
        wasSingleQuoted = scalar.Length >= 2 && scalar[0] == '\'' && scalar[^1] == '\'';
        return wasSingleQuoted ? scalar[1..^1].Replace("''", "'") : scalar;
    }

    // The files a rule's glob names, in the one form this file's rules use: <folder>/**/<pattern>, as src/**/*.cs.
    private static IEnumerable<string> FilesOf(Rule rule)
    {
        string repo = FailCriticalErrorsTests.FindRepo();
        int star = rule.Files.IndexOf("/**/", StringComparison.Ordinal);
        Assert.True(star > 0, $"the glob {rule.Files} is not one this test reads");
        string folder = Path.Combine(repo, rule.Files[..star].Replace('/', '\\'));
        string pattern = rule.Files[(star + "/**/".Length)..];
        string[] excepted = [.. rule.ExceptFiles.Select(f => Path.GetFullPath(Path.Combine(repo, f)))];
        return Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Split('\\').Any(part => part is "bin" or "obj"))
            .Where(f => !excepted.Contains(f, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheThreeRulesAreOneEntryEachWrittenByHandAsTheAuthoringRulesSay()
    {
        foreach (string name in new[] { ArgsRule, RebuiltRule, WriteRule })
        {
            Rule rule = ReadRule(name);
            Assert.NotEmpty(rule.Patterns);
            Assert.All(rule.Patterns, p => Assert.StartsWith("(?m)", p, StringComparison.Ordinal));
            Assert.True(rule.PatternsAreSingleQuoted, $"'{name}': every pattern is a single-quoted scalar, as hand-written ones are");
            Assert.False(rule.IncludesComments, $"'{name}': a settled entry is matched against code only; including_comments would trip it on a comment");
            Assert.NotEmpty(FilesOf(rule)); // a glob that matches no file checks nothing
        }

        Assert.Equal(["src/FsAnalyser.App/ReportFile.cs"], ReadRule(WriteRule).ExceptFiles); // the one place that opens a file for writing
        Assert.Empty(ReadRule(ArgsRule).ExceptFiles);
        Assert.Empty(ReadRule(RebuiltRule).ExceptFiles);
    }

    [Fact]
    public void EveryRowOfTheCorpusIsCaughtOrLeftAloneAsItsRowSays()
    {
        using JsonDocument corpus = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FailCriticalErrorsTests.FindRepo(), "tests", "FsAnalyser.Core.Tests", "SettledRulesCorpus.json")));
        var wrong = new List<string>();
        int rows = 0;
        foreach (JsonProperty entry in corpus.RootElement.GetProperty("rules").EnumerateObject())
        {
            Rule rule = ReadRule(entry.Name);
            foreach (JsonElement row in entry.Value.EnumerateArray())
            {
                rows++;
                string line = row.GetProperty("line").GetString()!;
                bool fires = rule.Patterns.Any(p => Regex.IsMatch(line, p));
                string expect = row.GetProperty("expect").GetString()!;
                if (fires != (expect == "fire"))
                    wrong.Add($"{row.GetProperty("id").GetString()} should {expect} for '{entry.Name}' and does not: {line.Trim()}");
            }
        }
        Assert.True(rows > 0);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void TheAppsSourceDoesNotTripTheRulesTheCorpusRuns()
    {
        var wrong = new List<string>();
        foreach (string name in new[] { ArgsRule, RebuiltRule, WriteRule })
        {
            Rule rule = ReadRule(name);
            foreach (string file in FilesOf(rule))
            {
                string text = File.ReadAllText(file);
                foreach (string pattern in rule.Patterns)
                {
                    foreach (Match m in Regex.Matches(text, pattern))
                        wrong.Add($"'{name}': {Path.GetRelativePath(FailCriticalErrorsTests.FindRepo(), file)} has `{m.Value.Trim()}`");
                }
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
