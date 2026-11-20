using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

// Code review of SL-008: a using directive that names RescanAsAdministrator lets a bare Start(...) reach the elevation call
// without spelling RescanAsAdministrator.Start, so the call-site rule must fire on every spelling of one.
public class CrSl008UsingDirectiveSpellingsTests
{
    private const string CallSiteRule = "only the window's own handler calls the rescan start, and nothing else names the handler";

    [Theory]
    [InlineData("using static global::FsAnalyser.App.RescanAsAdministrator;")]
    [InlineData("using RA = global::FsAnalyser.App.RescanAsAdministrator;")]
    [InlineData("using static FsAnalyser.App . RescanAsAdministrator;")]
    public void TheCallSiteRuleFiresOnAUsingDirectiveThatNamesTheClass(string line)
    {
        SettledRulesCorpusTests.Rule rule = SettledRulesCorpusTests.ReadRule(CallSiteRule);
        Assert.Contains(rule.Patterns, p => Regex.IsMatch(line, p));
    }
}
