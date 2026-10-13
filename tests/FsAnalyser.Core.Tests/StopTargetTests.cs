using System.Text.Json;
using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-110 review M1 (O-015), closed as a class. The hook that stops a scan at a folder (FSA_HOOK_TREE_STOP) compares the
/// folder the scan most recently committed with the folder a window case names. That value is overwritten at every commit, so
/// a target written as one exact folder is current only while the NEXT folder is listed: tens of microseconds when it is small.
/// <c>StopTargetCorpus.json</c> enumerates the targets and the values that class produces, each row fed to
/// <see cref="StopTarget.Matches"/> (compiled here as source, with no WPF in it); and the window cases' own use of the switch is
/// counted from <c>tests/window/WindowCases.ps1</c>, so a case that names one exact folder whose successor is a short listing
/// fails here, not as a refusal after a minute of building a tree.
/// </summary>
public class StopTargetTests
{
    private const string Root = @"C:\fsa-corpus\r";

    private static JsonElement Corpus()
    {
        string path = Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\FsAnalyser.Core.Tests\StopTargetCorpus.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    public static TheoryData<string> Rows()
    {
        var rows = new TheoryData<string>();
        foreach (JsonElement row in Corpus().GetProperty("rows").EnumerateArray())
            rows.Add(row.GetProperty("id").GetString()!);
        return rows;
    }

    private static string SeenOf(JsonElement row)
    {
        if (row.TryGetProperty("seenFull", out JsonElement full))
            return full.GetString()!;
        string relative = row.GetProperty("seen").GetString()!;
        return relative.Length == 0 ? Root : Root + "\\" + relative;
    }

    // The matcher the design handed to build, kept to say what it got wrong: the value is the target and nothing else.
    private static bool ExactOnly(string target, string seen) =>
        string.Equals(seen.TrimEnd('\\'), StopTarget.Full(Root, target), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [MemberData(nameof(Rows))]
    public void EachValueTheScanGivesIsMatchedOrNotAsTheRowSays(string id)
    {
        JsonElement row = Corpus().GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
        string target = row.GetProperty("target").GetString()!;
        string seen = SeenOf(row);

        Assert.Equal(row.GetProperty("expect").GetBoolean(), StopTarget.Matches(Root, target, seen));
        Assert.Equal(row.GetProperty("exactOnly").GetBoolean(), ExactOnly(target, seen)); // the row says what the old matcher did
        Assert.Equal(row.GetProperty("kind").GetString() == "under", StopTarget.IsSubtree(target));
    }

    [Fact]
    public void TheCorpusHasTheMembersTheOldMatcherMissed()
    {
        JsonElement[] rows = [.. Corpus().GetProperty("rows").EnumerateArray()];
        JsonElement[] closed = [.. rows.Where(r => r.GetProperty("expect").GetBoolean() && !r.GetProperty("exactOnly").GetBoolean())];
        Assert.True(closed.Length >= 8, $"only {closed.Length} rows are ones the exact-only matcher missed");
        foreach (JsonElement r in closed)
            Assert.Equal("under", r.GetProperty("kind").GetString()); // the misses are all subtree targets
        Assert.Contains(rows, r => r.GetProperty("kind").GetString() == "exact" && r.GetProperty("expect").GetBoolean());
        Assert.Contains(rows, r => !r.GetProperty("expect").GetBoolean() && r.GetProperty("target").GetString()!.EndsWith('\\'));
    }

    /// <summary>
    /// The census: every FSA_HOOK_TREE_STOP a window case sets is a subtree (a trailing backslash), or is a folder the corpus
    /// lists as exact-allowed with the case that names it and why its successor is a long listing.
    /// </summary>
    [Fact]
    public void EveryStopTargetAWindowCaseNamesIsASubtreeOrAnAllowedExactFolder()
    {
        string script = File.ReadAllText(Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\window\WindowCases.ps1"));
        string[] targets = [.. Regex.Matches(script, @"FSA_HOOK_TREE_STOP\s*=\s*'([^']+)'").Select(m => m.Groups[1].Value)];
        Assert.True(targets.Length >= 5, $"the cases name {targets.Length} stop targets");

        string[] allowed = [.. Corpus().GetProperty("exactAllowed").EnumerateArray().Select(a => a.GetProperty("target").GetString()!)];
        string[] exact = [.. targets.Where(t => !StopTarget.IsSubtree(t)).Distinct()];
        foreach (string target in exact)
            Assert.True(allowed.Contains(target, StringComparer.Ordinal), $"'{target}' names one exact folder, current only while the next one is listed; write it with a trailing backslash or list it in exactAllowed");
        foreach (string target in allowed)
            Assert.True(exact.Contains(target, StringComparer.Ordinal), $"exactAllowed lists '{target}' but no case names it");
        Assert.All(Corpus().GetProperty("exactAllowed").EnumerateArray(), a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.GetProperty("why").GetString()));
            Assert.Matches(@"^T-\d+$", a.GetProperty("case").GetString());
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("\\")]
    [InlineData("\\\\")]
    public void ANameThatIsNoFolderIsNotATarget(string target)
    {
        Assert.False(StopTarget.IsUsable(target));
    }
}
