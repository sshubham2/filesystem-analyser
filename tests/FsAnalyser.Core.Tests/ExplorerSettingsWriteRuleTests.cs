using System.Text.Json;
using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The class of lines that change Explorer's Hidden or ShowSuperHidden setting, closed (SL-091, review n2): every row of
/// ExplorerSettingsWriteCorpus.json fed, alone, as the twinned rule's xUnit copy matches a file, to the guard of its
/// scope. 'src' rows go to ArchitectureRulesTests.TheAppNeverWritesExplorersSettings's patterns, read from that Fact's
/// source (the copy eng/rules-parity.ps1 compares with the yaml); 'window' rows to the settled entry over tests/window,
/// read from .taad/fitness.yaml, whose only copy it is. So a change to either guard reaches the corpus with no third copy
/// to fall behind. Each row also says what the patterns the design declared did with it, kept here as they were handed
/// to build: the rows they let through and the guards now catch are the ones the n2 fix closed.
/// </summary>
public class ExplorerSettingsWriteRuleTests
{
    private const string WindowRule = "no window case or hook changes Explorer's view settings";

    private static readonly List<Regex> SrcRule = FailCriticalErrorsTests.FactPatterns(
        Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\FsAnalyser.Core.Tests\ArchitectureRulesTests.cs"),
        nameof(ArchitectureRulesTests.TheAppNeverWritesExplorersSettings));

    private static readonly List<Regex> WindowRulePatterns = SettledPatterns(WindowRule);

    // The design's rule B and rule C as declared, before review n2.
    private static readonly Regex[] SrcBefore =
    [
        new(@"\bSHGetSetSettings\b[^;\r\n]*\btrue\b"),
        new(@"\.SetValue[ \t]*\([ \t]*""(Hidden|ShowSuperHidden)"""),
        new(@"OpenSubKey[^;\r\n]*Explorer\\{1,2}Advanced[^;\r\n]*\btrue\b"),
        new(@"\bRegSetValue\w*")
    ];

    private static readonly Regex[] WindowBefore =
    [
        new(@"(?i)(Set|New)-ItemProperty[^\r\n]*Explorer\\{1,2}Advanced"),
        new(@"(?i)\breg(\.exe)?[ \t]+add[^\r\n]*Explorer\\{1,2}Advanced"),
        new(@"\bSHGetSetSettings\b[^;\r\n]*\btrue\b"),
        new(@"\.SetValue[ \t]*\([ \t]*""(Hidden|ShowSuperHidden)""")
    ];

    public static TheoryData<string> RowIds()
    {
        var data = new TheoryData<string>();
        foreach (Row row in Corpus())
            data.Add(row.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(RowIds))]
    public void EachRowFiresOrStaysQuietAsTheCorpusSays(string id)
    {
        Row row = Corpus().Single(r => r.Id == id);
        (List<Regex> now, Regex[] before) = row.Scope switch
        {
            "src" => (SrcRule, SrcBefore),
            "window" => (WindowRulePatterns, WindowBefore),
            _ => throw new InvalidDataException($"{row.Id}: scope '{row.Scope}' is neither src nor window")
        };

        bool fires = now.Any(p => p.IsMatch(row.Line));
        Assert.True(fires == (row.Now == "fire"),
            $"{row.Id} ({row.Note}): the {row.Scope} guard {(fires ? "fires on" : "lets through")} a line the corpus says it must {(row.Now == "fire" ? "catch" : "leave")}: {row.Line}");
        bool fired = before.Any(p => p.IsMatch(row.Line));
        Assert.True(fired == (row.Before == "fire"),
            $"{row.Id}: the design's declared patterns {(fired ? "fire" : "stay quiet")} on this line, and the corpus says {row.Before}: {row.Line}");
    }

    /// <summary>The corpus is what its note says: rows the n2 fix closed are there, in both scopes.</summary>
    [Fact]
    public void TheCorpusHoldsRowsTheDeclaredPatternsLetThroughInBothScopes()
    {
        List<Row> rows = Corpus();
        Assert.All(rows, r => Assert.Contains(r.Now, new[] { "fire", "quiet", "miss" }));
        Assert.All(rows, r => Assert.Contains(r.Before, new[] { "fire", "quiet" }));
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
        foreach (string scope in new[] { "src", "window" })
        {
            Assert.Contains(rows, r => r.Scope == scope && r.Now == "fire" && r.Before == "quiet");
            Assert.Contains(rows, r => r.Scope == scope && r.Now == "quiet");
        }
        Assert.Equal(9, WindowRulePatterns.Count);
        Assert.Equal(6, SrcRule.Count);
    }

    private sealed record Row(string Id, string Scope, string Now, string Before, string Line, string Note);

    private static List<Row> Corpus()
    {
        string path = Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\FsAnalyser.Core.Tests\ExplorerSettingsWriteCorpus.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => new Row(
                r.GetProperty("id").GetString()!,
                r.GetProperty("scope").GetString()!,
                r.GetProperty("now").GetString()!,
                r.GetProperty("before").GetString()!,
                r.GetProperty("line").GetString()!,
                r.GetProperty("note").GetString()!))
            .ToList();
    }

    /// <summary>
    /// The must_not_match patterns of one settled entry of .taad/fitness.yaml, by its name: each a single-quoted scalar on
    /// a line of its own under the entry's must_not_match key, '' read as one quote, compiled as the xUnit copies of the
    /// twinned rules are, with RegexOptions.Multiline.
    /// </summary>
    private static List<Regex> SettledPatterns(string name)
    {
        string[] lines = File.ReadAllLines(Path.Combine(FailCriticalErrorsTests.FindRepo(), @".taad\fitness.yaml"));
        int entry = Array.FindIndex(lines, l => l.TrimEnd() == "  - name: " + name);
        if (entry < 0)
            throw new InvalidDataException($".taad/fitness.yaml has no entry '{name}'");
        int key = Array.FindIndex(lines, entry + 1, l => l.TrimEnd() == "    must_not_match:");
        if (key < 0 || lines.Skip(entry + 1).Take(key - entry - 1).Any(l => l.StartsWith("  - ", StringComparison.Ordinal)))
            throw new InvalidDataException($"the entry '{name}' has no must_not_match of its own");
        var patterns = new List<Regex>();
        for (int n = key + 1; n < lines.Length; n++)
        {
            Match item = Regex.Match(lines[n], @"^\s+- '(.*)'$");
            if (!item.Success)
                break;
            patterns.Add(new Regex(item.Groups[1].Value.Replace("''", "'"), RegexOptions.Multiline));
        }
        if (patterns.Count == 0)
            throw new InvalidDataException($"the entry '{name}' has no single-quoted patterns under must_not_match");
        return patterns;
    }
}
