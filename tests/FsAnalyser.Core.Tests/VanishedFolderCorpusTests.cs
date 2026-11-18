using System.Text.Json;
using System.Text.RegularExpressions;
using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The two classes SL-005's review closed as corpora, each fed to the code that has to hold it (the scans that make a row
/// happen for real are in <see cref="VanishedFolderTests"/>):
/// <c>VolumeLossCorpus.json</c> (M1), the gone codes a lost volume, drive letter or share produces, against the predicates that
/// decide a folder is deleted; and <c>GoneCodeSpellingsCorpus.json</c> (m1), the spellings of those codes, against the text rules
/// in <c>.taad/fitness.yaml</c> that keep the walk from writing a second definition of gone.
/// </summary>
public class VanishedFolderCorpusTests
{
    private static JsonElement Corpus(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\FsAnalyser.Core.Tests", file))).RootElement;

    public static TheoryData<string> VolumeLossRows()
    {
        var rows = new TheoryData<string>();
        foreach (JsonElement row in Corpus("VolumeLossCorpus.json").GetProperty("rows").EnumerateArray())
            rows.Add(row.GetProperty("id").GetString()!);
        return rows;
    }

    public static TheoryData<string> SpellingRows()
    {
        var rows = new TheoryData<string>();
        foreach (JsonElement row in Corpus("GoneCodeSpellingsCorpus.json").GetProperty("rows").EnumerateArray())
            rows.Add(row.GetProperty("id").GetString()!);
        return rows;
    }

    /// <summary>
    /// A folder is deleted when its own open found nothing AND the scan root still opens: every shape that loses the path above
    /// the folder, whatever code the root's open then gives, leaves it unreadable.
    /// </summary>
    [Theory]
    [MemberData(nameof(VolumeLossRows))]
    public void TheRootOpenTellsADeletedFolderFromAPathThatStoppedResolving(string id)
    {
        JsonElement row = Corpus("VolumeLossCorpus.json").GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        int folderError = row.GetProperty("folderError").GetInt32();
        bool opened = row.GetProperty("opened").GetBoolean();
        int rootOpenError = row.GetProperty("rootOpenError").GetInt32();

        bool gone = Scanner.FolderVanished(folderError, opened) && Scanner.GoneGivenRootOpen(rootOpenError);

        Assert.True(row.GetProperty("gone").GetBoolean() == gone,
            $"{id} ({row.GetProperty("source").GetString()}): folder open {folderError} (opened {opened}), root open {rootOpenError}: " +
            $"expected {(gone ? "unreadable" : "gone")}, the code says {(gone ? "gone" : "unreadable")}");
    }

    /// <summary>The corpus names, for every row a real scan can make, a test that exists.</summary>
    [Fact]
    public void EveryLiveRowNamesATestThatExists()
    {
        foreach (JsonElement row in Corpus("VolumeLossCorpus.json").GetProperty("rows").EnumerateArray())
        {
            if (!row.TryGetProperty("live", out JsonElement live))
                continue;
            string[] parts = live.GetString()!.Split('.');
            Type? type = typeof(VanishedFolderTests).Assembly.GetType($"FsAnalyser.Core.Tests.{parts[0]}");
            Assert.True(type?.GetMethod(parts[1]) is not null, $"{row.GetProperty("id").GetString()}: no test {live}");
        }
    }

    /// <summary>
    /// Each spelling the walk's text rule must catch is caught by the patterns .taad/fitness.yaml holds, and each line it must
    /// leave alone is left alone. Fails before the rule is widened past the constant names.
    /// </summary>
    [Theory]
    [MemberData(nameof(SpellingRows))]
    public void TheTextRulesCatchTheSpellingsOfTheGoneCodesAndLeaveTheFilesOwnLinesAlone(string id)
    {
        JsonElement corpus = Corpus("GoneCodeSpellingsCorpus.json");
        JsonElement row = corpus.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        string line = row.GetProperty("line").GetString()!;
        string key = corpus.GetProperty("rules").GetProperty(row.GetProperty("rule").GetString()!).GetString()!;

        bool fires = PatternsOf(key).Any(pattern => Regex.IsMatch(line, pattern));

        Assert.True(row.GetProperty("fires").GetBoolean() == fires,
            $"{id}: the rule '{key}' {(fires ? "fires on" : "does not fire on")} `{line}`, and must {(row.GetProperty("fires").GetBoolean() ? "" : "not ")}fire");
    }

    /// <summary>
    /// What no text rule can hold: the walk's own predicate (Scanner.FolderVanished) answers as SizeProbe.IsGone does for
    /// every code of a failed open, so a code the walk comes to accept beside IsGone's two, spelled however it is spelled,
    /// fails here; and a folder whose open returned a handle is never gone, whatever the code. The sweep runs the codes 0 to
    /// 2000, which covers every Win32 error the open can return and the NT-status-mapped codes beside them.
    /// </summary>
    [Fact]
    public void TheWalksPredicateAgreesWithSizeProbeIsGoneForEveryCode()
    {
        var disagree = new List<int>();
        for (int code = 0; code <= 2000; code++)
        {
            if (Scanner.FolderVanished(code, opened: false) != SizeProbe.IsGone(code))
                disagree.Add(code);
            Assert.False(Scanner.FolderVanished(code, opened: true), $"a folder whose open returned a handle is never gone (code {code})");
        }
        Assert.True(disagree.Count == 0, $"Scanner.FolderVanished and SizeProbe.IsGone disagree on codes {string.Join(", ", disagree)}");
    }

    /// <summary>
    /// The rules are quiet on the files they guard, comments included, which they read too: a doc line that spells a comparison
    /// of an error with 2 or 3 would fail the Fitness gate. Checked here so that it fails in the default suite instead.
    /// </summary>
    [Theory]
    [InlineData("walk", @"src\FsAnalyser.Core\Scanning\Scanner.cs")]
    [InlineData("reader", @"src\FsAnalyser.Core\Scanning\DirectoryReader.cs")]
    public void TheRulesAreQuietOnTheSourcesTheyGuard(string rule, string file)
    {
        string key = Corpus("GoneCodeSpellingsCorpus.json").GetProperty("rules").GetProperty(rule).GetString()!;
        string text = File.ReadAllText(Path.Combine(FailCriticalErrorsTests.FindRepo(), file));
        foreach (string pattern in PatternsOf(key))
        {
            Match hit = Regex.Match(text, pattern, RegexOptions.Multiline);
            Assert.False(hit.Success, $"{file} matches the rule '{key}' ({pattern}) at `{hit.Value}`");
        }
    }

    /// <summary>
    /// The must_not_match patterns of the SL-005 rule in .taad/fitness.yaml whose name holds <paramref name="key"/>: read from
    /// the file as it stands, so the corpus is run against the rule the Fitness gate runs, not a copy of it.
    /// </summary>
    private static List<string> PatternsOf(string key)
    {
        string[] lines = File.ReadAllLines(Path.Combine(FailCriticalErrorsTests.FindRepo(), @".taad\fitness.yaml"));
        int name = Array.FindIndex(lines, l => l.StartsWith("  - name:", StringComparison.Ordinal) && l.Contains(key, StringComparison.Ordinal));
        Assert.True(name >= 0, $"no rule named '...{key}...' in .taad/fitness.yaml");
        int must = Array.FindIndex(lines, name, l => l.Trim() == "must_not_match:");
        Assert.True(must > name, $"the rule '{key}' has no must_not_match");
        var patterns = new List<string>();
        for (int i = must + 1; i < lines.Length && lines[i].StartsWith("    - ", StringComparison.Ordinal); i++)
        {
            string pattern = lines[i]["    - ".Length..].Trim();
            if (pattern.Length >= 2 && pattern[0] == '\'' && pattern[^1] == '\'')
                pattern = pattern[1..^1].Replace("''", "'");
            patterns.Add(pattern);
        }
        Assert.NotEmpty(patterns);
        return patterns;
    }
}
