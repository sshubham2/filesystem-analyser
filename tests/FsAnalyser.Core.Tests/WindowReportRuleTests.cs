using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The shapes ArchitectureRulesTests.TheWindowReportReadsOnlyTheFailedFolderCount must catch and the look-alikes it must
/// leave (SL-089, review M4), fed as source lines to the rule's own pattern, read from that Fact's source (the copy
/// eng/rules-parity.ps1 compares with the yaml), so a change to the rule reaches these with no third copy to fall behind.
/// Each line is matched alone, as the xUnit twin matches a file: the record at the end of a line, the first line of the
/// report's two-line fluent layout, must fire on its own. The Fitness copy gets the same shapes as plants F36-F40 and
/// P28-P31 in eng/rules-harness/plants.json.
/// </summary>
public class WindowReportRuleTests
{
    private static readonly List<Regex> Rule = FailCriticalErrorsTests.FactPatterns(
        Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\FsAnalyser.Core.Tests\ArchitectureRulesTests.cs"),
        nameof(ArchitectureRulesTests.TheWindowReportReadsOnlyTheFailedFolderCount));

    // The first pattern the design declared, which fires only when a member access follows on the record's own line.
    private static readonly Regex Declared = new(@"\bUnreadable\s*\??\.\s*(?!Count\b)\w");

    [Theory]
    [InlineData(@"        [""unreadableRows""] = result.Unreadable", false)]
    [InlineData(@"        [""paths""] = result.Unreadable.Select(f => result.Store.GetPath(f.Entry)).ToArray(),", true)]
    [InlineData(@"        string first = result.Store.GetPath(result.Unreadable[0].Entry);", false)]
    [InlineData(@"        foreach (UnreadableFolder f in result.Unreadable)", false)]
    [InlineData(@"        int? entry = result.Unreadable?.FirstOrDefault()?.Entry;", true)]
    public void TheRuleCatchesEveryWayOfReadingTheRecordPastItsCount(string line, bool declaredCaughtIt)
    {
        Assert.Single(Rule);
        Assert.True(Rule[0].IsMatch(line), $"/{Rule[0]}/ does not catch: {line}");
        Assert.Equal(declaredCaughtIt, Declared.IsMatch(line));
    }

    [Theory]
    [InlineData(@"                unreadable = result.Unreadable.Count,")]
    [InlineData(@"                unreadable = result.Unreadable?.Count ?? 0,")]
    [InlineData(@"                unreadable = rows.Count(r => r.State == TopLevelState.Unreadable),")]
    [InlineData(@"                UnreadableList = new { actualHeight = window.UnreadableList.ActualHeight },")]
    public void TheRuleLeavesLookAlikesAlone(string line)
    {
        Assert.Single(Rule);
        Assert.False(Rule[0].IsMatch(line), $"/{Rule[0]}/ fires on correct code: {line}");
    }
}
