using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-005 review: the walk's text rule must catch the pattern-combinator spellings of the gone codes too, as it does
/// 'case 2:' and 'error is 2 or 3'. The patterns are read from .taad/fitness.yaml, the rule the Fitness gate runs.
/// </summary>
public class SL005ReviewGoneCodeCaseSpellingTests
{
    [Theory]
    [InlineData("case 2 or 3:")]
    [InlineData("    case (2 or 3): return true;")]
    [InlineData("bool gone = !opened && error switch { 2 or 3 => true, _ => false };")]
    public void TheWalkRuleCatchesThePatternCombinatorSpellingsOfTheGoneCodes(string line)
    {
        string[] lines = File.ReadAllLines(Path.Combine(FailCriticalErrorsTests.FindRepo(), @".taad\fitness.yaml"));
        int name = Array.FindIndex(lines, l => l.StartsWith("  - name:", StringComparison.Ordinal)
            && l.Contains("calls a folder gone only through SizeProbe.IsGone", StringComparison.Ordinal));
        Assert.True(name >= 0, "no walk rule in .taad/fitness.yaml");
        int must = Array.FindIndex(lines, name, l => l.Trim() == "must_not_match:");
        Assert.True(must > name, "the walk rule has no must_not_match");
        var patterns = new List<string>();
        for (int i = must + 1; i < lines.Length && lines[i].StartsWith("    - ", StringComparison.Ordinal); i++)
        {
            string pattern = lines[i]["    - ".Length..].Trim();
            if (pattern.Length >= 2 && pattern[0] == '\'' && pattern[^1] == '\'')
                pattern = pattern[1..^1].Replace("''", "'");
            patterns.Add(pattern);
        }
        Assert.NotEmpty(patterns);
        Assert.True(patterns.Any(pattern => Regex.IsMatch(line, pattern)),
            $"the walk rule lets `{line}` through: a pattern-combinator spelling of the gone codes, a second definition beside SizeProbe.IsGone");
    }
}
