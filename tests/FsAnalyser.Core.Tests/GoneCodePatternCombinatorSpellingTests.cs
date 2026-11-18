using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-005 m1's class (spellings of the gone codes the new text rules miss): the C# 9 pattern-combinator spellings the walk's
/// rule in .taad/fitness.yaml must also catch, a switch case and a switch-expression arm.
/// </summary>
public class GoneCodePatternCombinatorSpellingTests
{
    [Theory]
    [InlineData("case 2 or 3:")]
    [InlineData("    case 3 or 2: return true;")]
    [InlineData("case (2 or 3):")]
    [InlineData("bool gone = error switch { 2 or 3 => true, _ => false };")]
    [InlineData("return error switch { 2 => true, 3 => true, _ => false };")]
    public void TheWalkRuleCatchesTheCombinatorSpellingsOfTheGoneCodes(string line)
    {
        List<string> patterns = WalkRulePatterns();
        Assert.True(patterns.Any(p => Regex.IsMatch(line, p)),
            $"the walk's gone-code rule ({string.Join(" | ", patterns)}) does not fire on `{line}`");
    }

    private static List<string> WalkRulePatterns()
    {
        string[] lines = File.ReadAllLines(Path.Combine(FailCriticalErrorsTests.FindRepo(), @".taad\fitness.yaml"));
        int name = Array.FindIndex(lines, l => l.TrimStart().StartsWith("- name:", StringComparison.Ordinal)
            && l.Contains("the walk calls a folder gone only through SizeProbe.IsGone", StringComparison.Ordinal));
        Assert.True(name >= 0, "no walk gone-code rule in .taad/fitness.yaml");
        int must = Array.FindIndex(lines, name, l => l.Trim() == "must_not_match:");
        Assert.True(must > name);
        var patterns = new List<string>();
        for (int i = must + 1; i < lines.Length && lines[i].TrimStart().StartsWith("- ", StringComparison.Ordinal)
            && !lines[i].TrimStart().StartsWith("- name:", StringComparison.Ordinal); i++)
        {
            string p = lines[i].TrimStart()[2..].Trim();
            if (p.Length >= 2 && p[0] == '\'' && p[^1] == '\'')
                p = p[1..^1].Replace("''", "'");
            patterns.Add(p);
        }
        Assert.NotEmpty(patterns);
        return patterns;
    }
}
