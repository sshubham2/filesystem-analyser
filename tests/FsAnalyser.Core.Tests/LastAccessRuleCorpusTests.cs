using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-096's two guards, closed as classes (review M1 and m2), each row of a corpus fed alone to the guard where it lives,
/// as ExplorerSettingsWriteRuleTests does: SystemRootTempRouteCorpus.json to the settled rule against fixtures in the
/// Windows folder's Temp, and WindowsFolderFactSkipCorpus.json to the settled rule that the Windows-folder Facts are never
/// skipped together with the twinned CountingRuleTestsAreNeverSkipped. Both settled rules are read from .taad/fitness.yaml,
/// their only copy, so a change there reaches the corpus. Each row also says what the patterns the design declared did with
/// it, kept here as they were handed to build: the rows they let through and the guards now catch are the ones the fixes
/// closed. What no line pattern can see, <see cref="TheWindowsFolderFactsRunInTheDefaultSuite"/> checks by reflection,
/// the class's whole set of tests included (SL-097). <see cref="TheMeasuredLastAccessLimitIsWrittenWhereTheTreeSaidUnmeasured"/>
/// holds the two records that name the limits to SL-097's measurement.
/// </summary>
public class LastAccessRuleCorpusTests
{
    // In two pieces, so that the name does not spell the folder the rule it names bans.
    private const string RouteRule = "no Core test builds a fixture in %SystemRoot%" + @"\Temp except the deliberate one";
    private const string SkipRule = "the Windows-folder last-access tests are never skipped or filtered out of the default suite";

    // The design's rule 2 and rule 1, as declared, before review M1 and m2.
    private static readonly Regex[] RouteBefore =
    [
        new(@"SpecialFolder\.Windows\s*\)\s*,\s*""Temp"""),
        new(@"(?i)""C:\\{1,2}Windows\\{1,2}Temp"),
        new(@"(?i)%(SystemRoot|windir)%\\{1,2}Temp"),
    ];

    private static readonly Regex[] SkipBefore =
    [
        new(@"Skip\s*="),
        new(@"Assert\.Skip(When|Unless)?\s*\("),
        new(@"Skippable(Fact|Theory)"),
        new(@"Trait\(\s*""(Network|Cloud)"""),
    ];

    private static readonly List<Regex> NeverSkipped = FailCriticalErrorsTests.FactPatterns(
        Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\FsAnalyser.Core.Tests\ArchitectureRulesTests.cs"),
        nameof(ArchitectureRulesTests.CountingRuleTestsAreNeverSkipped));

    public static TheoryData<string> RouteRows() => Ids("SystemRootTempRouteCorpus.json");

    public static TheoryData<string> SkipRows() => Ids("WindowsFolderFactSkipCorpus.json");

    [Theory]
    [MemberData(nameof(RouteRows))]
    public void EachRouteIntoTheWindowsTempFolderFiresOrStaysQuietAsTheCorpusSays(string id)
    {
        Row row = Corpus("SystemRootTempRouteCorpus.json").Single(r => r.Id == id);
        AssertRow(row, ExplorerSettingsWriteRuleTests.SettledPatterns(RouteRule), RouteBefore);
    }

    [Theory]
    [MemberData(nameof(SkipRows))]
    public void EachWayToSkipTheWindowsFolderFactsFiresOrStaysQuietAsTheCorpusSays(string id)
    {
        Row row = Corpus("WindowsFolderFactSkipCorpus.json").Single(r => r.Id == id);
        AssertRow(row, [.. ExplorerSettingsWriteRuleTests.SettledPatterns(SkipRule), .. NeverSkipped], [.. SkipBefore, .. NeverSkipped]);
    }

    /// <summary>Each corpus is what its note says: rows the fix closed are there, beside rows that must stay quiet.</summary>
    [Theory]
    [InlineData("SystemRootTempRouteCorpus.json", 7)]
    [InlineData("WindowsFolderFactSkipCorpus.json", 6)]
    public void EachCorpusHoldsRowsTheDeclaredPatternsLetThrough(string file, int patterns)
    {
        List<Row> rows = Corpus(file);
        Assert.All(rows, r => Assert.Contains(r.Now, new[] { "fire", "quiet", "miss" }));
        Assert.All(rows, r => Assert.Contains(r.Before, new[] { "fire", "quiet" }));
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
        Assert.Contains(rows, r => r.Now == "fire" && r.Before == "quiet");
        Assert.Contains(rows, r => r.Now == "quiet");
        Assert.Equal(patterns, ExplorerSettingsWriteRuleTests.SettledPatterns(file.StartsWith("System", StringComparison.Ordinal) ? RouteRule : SkipRule).Count);
    }

    /// <summary>
    /// The skip corpus's misses, closed as a grammar: the class is public, each of its tests carries exactly one stock
    /// Fact or Theory and no attribute but those and InlineData, nothing it carries sets a skip, and the default filter
    /// baked into the assembly has no clause that picks tests by name.
    /// </summary>
    [Fact]
    public void TheWindowsFolderFactsRunInTheDefaultSuite()
    {
        Type type = typeof(LastAccessUnderSystemRootTests);
        Assert.True(type.IsPublic && !type.IsAbstract, $"{type.Name} is not a public, concrete class, so xUnit does not run it");
        Assert.Empty(Foreign(type.GetCustomAttributes()));

        Type[] stock = [typeof(FactAttribute), typeof(TheoryAttribute), typeof(InlineDataAttribute)];
        string[] tests =
        [
            nameof(LastAccessUnderSystemRootTests.AScanUnderTheWindowsTempFolderCountsAsKeptOnlyTheFoldersThatStayedOld),
            nameof(LastAccessUnderSystemRootTests.AChildThatRefusesWriteAttributesLeavesItsParentCountedAsKept),
            nameof(LastAccessUnderSystemRootTests.OutsideTheWindowsFolderEveryKeptFolderStaysKept),
            nameof(LastAccessUnderSystemRootTests.InProgramDataNoKeptFolderMovesItsParent),
        ];

        // The list is the class's whole set: a Fact deleted from it is caught below by name, and one added to it is caught
        // here until it is pinned (SL-097 review M2), instead of relying on somebody remembering to.
        string[] carryingAFact = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any())
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(carryingAFact.SequenceEqual(tests.Order(StringComparer.Ordinal)),
            $"the methods of {type.Name} that carry a Fact or Theory are [{string.Join(", ", carryingAFact)}], and this list pins " +
            $"[{string.Join(", ", tests.Order(StringComparer.Ordinal))}]: a test was added or removed without being pinned here");

        foreach (string name in tests)
        {
            MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"{type.Name}.{name} is not a public instance method");
            Attribute[] attributes = method.GetCustomAttributes().ToArray();
            Attribute[] facts = attributes.Where(a => a is FactAttribute).ToArray();
            Assert.True(facts.Length == 1 && facts[0].GetType() is var kind && (kind == typeof(FactAttribute) || kind == typeof(TheoryAttribute)),
                $"{name} must carry exactly one stock [Fact] or [Theory]; it carries {string.Join(", ", facts.Select(f => f.GetType().Name))}");
            Assert.All(Foreign(attributes), a => Assert.Contains(a.GetType(), stock));
            foreach (Attribute attribute in attributes)
            {
                object? skip = attribute.GetType().GetProperty("Skip")?.GetValue(attribute);
                Assert.True(skip is null, $"{name}'s {attribute.GetType().Name} skips it: {skip}");
            }
        }

        string filter = typeof(LastAccessRuleCorpusTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "DefaultTestFilter").Value!;
        foreach (string clause in filter.Split('&', '|', '(', ')').Where(c => c.Trim().Length > 0))
        {
            string property = Regex.Match(clause.Trim(), @"^[^!=~]+").Value;
            Assert.False(property is "FullyQualifiedName" or "Name" or "ClassName" or "DisplayName",
                $"the default test filter picks tests by name ({clause.Trim()}): it could leave {type.Name} out of every routine run");
        }
    }

    /// <summary>
    /// SL-097 measured the parent move outside the Windows folder, so the two places that called it unmeasured say what was
    /// measured, and the SL-097 record exists. The known gap SL-096 wrote about a re-scan, a parent counted out although
    /// nothing moved, stays written where it was: the record may not call it falsified (review M1).
    /// </summary>
    [Fact]
    public void TheMeasuredLastAccessLimitIsWrittenWhereTheTreeSaidUnmeasured()
    {
        string repo = FailCriticalErrorsTests.FindRepo();
        string remark = File.ReadAllText(Path.Combine(repo, @"src\FsAnalyser.Core\Scanning\ScanResult.cs"));
        string triage = File.ReadAllText(Path.Combine(repo, @"docs\design\SL-096-last-access-under-windows-temp.md"));
        string recordPath = Path.Combine(repo, @"docs\design\SL-097-last-access-outside-the-windows-folder.md");
        Assert.True(File.Exists(recordPath), $"{recordPath} is not there");
        string record = File.ReadAllText(recordPath);

        Assert.DoesNotContain("are unmeasured and not corrected", remark);
        Assert.Contains("SL-097", remark);
        Assert.Contains("Windows 10", remark);
        Assert.Contains("counted out even when nothing moved", remark);

        Assert.DoesNotContain("not measured and not corrected", triage);
        Assert.Contains("SL-097", triage);
        Assert.Contains("It is wrong when nothing moved", triage);

        Assert.Contains("Windows 10", record);
        Assert.DoesNotContain("falsified", record, StringComparison.OrdinalIgnoreCase);
    }

    // Every attribute that is not the compiler's or the runtime's own.
    private static IEnumerable<Attribute> Foreign(IEnumerable<Attribute> attributes) =>
        attributes.Where(a => a.GetType().Namespace is not { } ns || !(ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)));

    private static void AssertRow(Row row, IReadOnlyList<Regex> now, IReadOnlyList<Regex> before)
    {
        bool fires = now.Any(p => p.IsMatch(row.Line));
        Assert.True(fires == (row.Now == "fire"),
            $"{row.Id} ({row.Note}): the guard {(fires ? "fires on" : "lets through")} a line the corpus says it must {(row.Now == "fire" ? "catch" : "leave")}: {row.Line}");
        bool fired = before.Any(p => p.IsMatch(row.Line));
        Assert.True(fired == (row.Before == "fire"),
            $"{row.Id}: the design's declared patterns {(fired ? "fire" : "stay quiet")} on this line, and the corpus says {row.Before}: {row.Line}");
    }

    private sealed record Row(string Id, string Now, string Before, string Line, string Note);

    private static TheoryData<string> Ids(string file)
    {
        var data = new TheoryData<string>();
        foreach (Row row in Corpus(file))
            data.Add(row.Id);
        return data;
    }

    private static List<Row> Corpus(string file)
    {
        string path = Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\FsAnalyser.Core.Tests", file);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => new Row(
                r.GetProperty("id").GetString()!,
                r.GetProperty("now").GetString()!,
                r.GetProperty("before").GetString()!,
                r.GetProperty("line").GetString()!,
                r.GetProperty("note").GetString()!))
            .ToList();
    }
}
