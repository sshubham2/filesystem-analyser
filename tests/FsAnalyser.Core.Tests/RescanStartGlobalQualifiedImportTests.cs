using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

// SL-008 review, input pass: a static import or an alias written with the global:: qualifier names RescanAsAdministrator.Start
// without spelling it, so a bare Start(...) or Alias.Start(...) in any other src file reaches the elevation.
public class RescanStartGlobalQualifiedImportTests
{
    [Theory]
    [InlineData("using static global::FsAnalyser.App.RescanAsAdministrator;")]
    [InlineData("global using static global::FsAnalyser.App.RescanAsAdministrator;")]
    [InlineData("using RA = global::FsAnalyser.App.RescanAsAdministrator;")]
    public void TheCallSiteRuleCatchesAGlobalQualifiedImportOfTheClass(string line)
    {
        SettledRulesCorpusTests.Rule rule = SettledRulesCorpusTests.ReadRule("only the window's own handler calls the rescan start, and nothing else names the handler");
        Assert.Contains(rule.Patterns, p => Regex.IsMatch(line, p));
    }
}
