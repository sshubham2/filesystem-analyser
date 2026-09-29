using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The shapes ArchitectureRulesTests.NothingInTheAppHandsExplorerItsSelectSwitch must catch and the look-alikes it must
/// leave (SL-088, criterion 3), fed as source lines to the rule's own pattern, read from that Fact's source (the copy
/// eng/rules-parity.ps1 compares with the yaml), so a change to the rule reaches these with no third copy to fall
/// behind. The Fitness copy gets the same shapes as plants F31-F35 and P25-P27 in eng/rules-harness/plants.json.
/// </summary>
public class SelectSwitchRuleTests
{
    private static readonly List<Regex> Rule = FailCriticalErrorsTests.FactPatterns(
        Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\FsAnalyser.Core.Tests\ArchitectureRulesTests.cs"),
        nameof(ArchitectureRulesTests.NothingInTheAppHandsExplorerItsSelectSwitch));

    // SL-047's settled pattern, which the build check found three of these five slipping past.
    private static readonly Regex Sl047 = new(@"/select\s*,");

    [Theory]
    [InlineData(@"Process.Start(""explorer.exe"", ""/select,"" + p);", true)]
    [InlineData(@"Process.Start(""explorer.exe"", $""/select, \""{p}\"""");", true)]
    [InlineData(@"psi.ArgumentList.Add(""/select"");", false)]
    [InlineData(@"Process.Start(""explorer.exe"", ""/SELECT,"" + p);", false)]
    [InlineData(@"Process.Start(""explorer.exe"", ""/select \"""" + p + ""\"""");", false)]
    public void TheRuleCatchesEveryWayOfHandingExplorerTheSwitch(string line, bool sl047CaughtIt)
    {
        Assert.Single(Rule);
        Assert.True(Rule[0].IsMatch(line), $"/{Rule[0]}/ does not catch: {line}");
        Assert.Equal(sl047CaughtIt, Sl047.IsMatch(line));
    }

    [Theory]
    [InlineData(@"string mode = ""/selection"";")]
    [InlineData(@"bool selected = row.IsSelected;")]
    [InlineData(@"item.Header = ""Open in Explorer"";")]
    public void TheRuleLeavesLookAlikesAlone(string line)
    {
        Assert.Single(Rule);
        Assert.False(Rule[0].IsMatch(line), $"/{Rule[0]}/ fires on correct code: {line}");
    }
}