using System.Text.Json;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-007 IN-M1, closed as its class (UnpairedQuoteCorpus.json): a backslash-quote outside a quoted part, which the repair
/// pairs with a later quote or leaves open, joining arguments Windows split. Every row is run against Windows' own split of
/// the line (shell32) and CommandLine.Arguments; then a sweep over every line made of a few tokens holds the properties
/// that make the class safe, whatever its spelling.
/// </summary>
public class UnpairedQuoteCorpusTests
{
    private static JsonDocument Corpus() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FailCriticalErrorsTests.FindRepo(), "tests", "FsAnalyser.Core.Tests", "UnpairedQuoteCorpus.json")));

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public void EveryRowOfTheCorpusIsRepairedKeptOrRefusedAsItsRowSays()
    {
        using JsonDocument corpus = Corpus();
        var wrong = new List<string>();
        int rows = 0;
        foreach (JsonElement row in corpus.RootElement.GetProperty("rows").EnumerateArray())
        {
            rows++;
            string id = row.GetProperty("id").GetString()!;
            string tail = row.GetProperty("tail").GetString()!;
            string expect = row.GetProperty("expect").GetString()!;
            string[] argv = TestNative.SplitArguments(tail);
            string[] windows = Strings(row.GetProperty("windows"));
            if (!argv.SequenceEqual(windows))
            {
                wrong.Add($"{id}: Windows splits [{tail}] into [{string.Join(" | ", argv)}], not the row's [{string.Join(" | ", windows)}]");
                continue;
            }

            string[]? got = CommandLine.Arguments(argv, "app.exe " + tail);
            switch (expect)
            {
                case "repaired":
                    string[] result = Strings(row.GetProperty("result"));
                    if (got is null || !got.SequenceEqual(result))
                        wrong.Add($"{id}: [{tail}] should be repaired to [{string.Join(" | ", result)}] and gave [{(got is null ? "null" : string.Join(" | ", got))}]");
                    break;
                case "kept":
                    if (!ReferenceEquals(got, argv))
                        wrong.Add($"{id}: [{tail}] should keep Windows' split [{string.Join(" | ", argv)}] and gave [{(got is null ? "null" : string.Join(" | ", got))}]");
                    break;
                case "refused":
                    if (got is not null)
                        wrong.Add($"{id}: [{tail}] should be refused (null) and gave [{string.Join(" | ", got)}]");
                    break;
                default:
                    wrong.Add($"{id}: no expectation named '{expect}'");
                    break;
            }
        }
        Assert.True(rows > 0);
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    // Every line of up to maxTokens of the tokens, joined by one space, is split by Windows and handed to the repair. The four
    // properties are the class's safety, stated apart from how the repair works.
    [Fact]
    public void NoLineMadeOfTheseTokensHidesAnOptionLeavesAQuoteOpenJoinsARootOrIsWronglyRefused()
    {
        using JsonDocument corpus = Corpus();
        JsonElement sweep = corpus.RootElement.GetProperty("sweep");
        string[] tokens = Strings(sweep.GetProperty("tokens"));
        int maxTokens = sweep.GetProperty("maxTokens").GetInt32();

        var wrong = new List<string>();
        int lines = 0;
        var level = new List<string[]> { Array.Empty<string>() };
        for (int length = 1; length <= maxTokens; length++)
        {
            level = [.. level.SelectMany(prefix => tokens.Select(t => (string[])[.. prefix, t]))];
            foreach (string[] sequence in level)
            {
                string tail = string.Join(' ', sequence);
                string[] argv = TestNative.SplitArguments(tail);
                string[]? got = CommandLine.Arguments(argv, "app.exe " + tail);
                lines++;
                string what = $"[{tail}] -> windows [{string.Join(" | ", argv)}], got [{(got is null ? "null" : string.Join(" | ", got))}]";

                if (!argv.Any(a => a.Contains('"')))
                {
                    if (!ReferenceEquals(got, argv))
                        wrong.Add($"a line Windows split cleanly was changed: {what}");
                    continue;
                }

                // 1. an option Windows exposed is never hidden
                if (got is not null && argv.Contains("--report") && !got.Contains("--report"))
                    wrong.Add($"hides --report: {what}");

                // 2. a repair never leaves a quote open, and removes every quote
                if (got is not null && !ReferenceEquals(got, argv) && (tail.Count(c => c == '"') % 2 != 0 || got.Any(a => a.Contains('"'))))
                    wrong.Add($"repairs a line whose quotes do not pair: {what}");

                // 3. a root whose only damage is the trailing quote is never joined into another argument
                if (got is not null && !ReferenceEquals(got, argv))
                {
                    foreach (string root in argv.Where(a => a.Length > 0 && a.IndexOf('"') == a.Length - 1))
                    {
                        string stem = root[..^1].TrimEnd('\\');
                        if (!got.Any(g => g.TrimEnd('\\') == stem))
                            wrong.Add($"joins the root {root}: {what}");
                    }
                }

                // 4. null is only for arguments Windows merged: a quote inside an argument
                if (got is null && argv.All(a => a.IndexOf('"') is var q && (q < 0 || q == a.Length - 1)))
                    wrong.Add($"refuses a line whose only quotes are trailing: {what}");
            }
        }
        Assert.True(lines > 1000, $"the sweep ran {lines} lines");
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {lines} lines:\n" + string.Join("\n", wrong.Take(25)));
    }
}
