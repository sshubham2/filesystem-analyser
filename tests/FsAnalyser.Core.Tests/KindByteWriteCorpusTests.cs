using System.Text.Json;
using System.Text.RegularExpressions;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// SL-111, review m1: the Kind byte of a store page is written whole and nothing ORs a marker into it. Many reads compare
/// that byte with <see cref="FsAnalyser.Core.Store.EntryKind"/> values raw (Kind, Charges, CheckParent, RolledSize,
/// FilesBelow, ChildrenLargestFirst, OnDiskAtOrBelow and NotListedAtOrBelow), so a bit stored in it, as U-201's
/// SubtreeUnfinished was, would make every marked folder read as a non-folder in all of them until each is masked. SL-111
/// derives the subtree answer instead of storing it, and this holds that: a closed grammar over the text of
/// src/FsAnalyser.Core/Store/**/*.cs that accepts the few forms a line may name the Kind column in and refuses the rest.
/// A spelling nobody listed (a compound assignment, an alias of the column, a ref to an element, a helper that returns the
/// marked value) is refused with the ones that were. KindByteWriteCorpus.json enumerates the class and what the first
/// two patterns the design declared did with each row ('before'); a slice that stores the marker masks the reads and edits
/// <see cref="Grammar"/> on purpose.
/// </summary>
public class KindByteWriteCorpusTests
{
    /// <summary>The two patterns SL-111's design declared for a fitness rule, dropped at review m1 for catching a few spellings only.</summary>
    private static readonly Regex[] Declared =
    [
        new(@"\.Kind\s*\[[^\]]*\]\s*(?:\|=|&=|\^=)"),
        new(@"\.Kind\s*\[[^\]]*\]\s*=(?!=)[^;]*\|"),
    ];

    /// <summary>
    /// What a source text does with the Kind column, and which uses are outside the grammar. A use is an occurrence of the
    /// identifier Kind not followed by a call parenthesis (the method <c>Kind(index)</c> is not the column). Accepted:
    /// the column's one declaration; an element compared with <c>(byte)EntryKind.X</c> by == or !=; an element switched on;
    /// an element cast to EntryKind; and the one whole write <c>Kind[i] = (byte)kind;</c> as a statement. Anything else is
    /// refused, with the line it is on.
    /// </summary>
    internal static class Grammar
    {
        internal sealed record Result(List<string> Refusals, int Declarations, int Writes, int Reads);

        public static Result Check(string source)
        {
            // Comments become blanks of the same shape, so a refusal still reports the line it is on.
            string code = Regex.Replace(source, @"/\*[\s\S]*?\*/", m => Regex.Replace(m.Value, @"[^\r\n]", " "));
            code = Regex.Replace(code, @"//[^\r\n]*", m => new string(' ', m.Length));

            var refusals = new List<string>();
            int declarations = 0, writes = 0, reads = 0;
            foreach (Match use in Regex.Matches(code, @"(?<!\w)Kind(?!\w)(?!\s*\()"))
            {
                string prefix = code[..use.Index], tail = code[(use.Index + use.Length)..];
                int line = code.AsSpan(0, use.Index).Count('\n') + 1;
                string? why = null;

                if (Regex.IsMatch(prefix, @"\bbyte\s*\[\s*\]\s+$") && Regex.IsMatch(tail, @"^\s*=\s*new\s+byte\s*\[\s*PageSize\s*\]\s*;"))
                {
                    declarations++;
                }
                else if (Regex.IsMatch(tail, @"^\s*\["))
                {
                    int open = tail.IndexOf('['), depth = 0, close = -1;
                    for (int n = open; n < tail.Length && close < 0; n++)
                    {
                        depth += tail[n] == '[' ? 1 : tail[n] == ']' ? -1 : 0;
                        if (depth == 0)
                            close = n;
                    }
                    string rest = close < 0 ? "" : tail[(close + 1)..];
                    if (Regex.IsMatch(rest, @"^\s*=\s*\(byte\)kind\s*;") && Regex.IsMatch(prefix, @"(?:^|[;{}])[\s\w!.\[\]&>@]*\.\s*$"))
                        writes++;
                    else if (Regex.IsMatch(rest, @"^\s*(?:==|!=)\s*\(byte\)EntryKind\.\w+(?!\w)") || Regex.IsMatch(rest, @"^\s+switch\b")
                        || (Regex.IsMatch(prefix, @"\(EntryKind\)\s*[\w!.]*(?:\([^()]*\))?\.$") && Regex.IsMatch(rest, @"^\s*[;})\],]")))
                        reads++;
                    else
                        why = "an element that is not compared with an EntryKind, switched on, cast to EntryKind, or the one whole write";
                }
                else
                {
                    why = "the column itself, other than its declaration: an alias, an argument, a span, or an assignment";
                }

                if (why is not null)
                    refusals.Add($"line {line}: {why}: {code.Split('\n')[line - 1].Trim()}");
            }
            return new Result(refusals, declarations, writes, reads);
        }
    }

    /// <summary>The store as it is: every use of the Kind column is in the grammar, and it is written once.</summary>
    [Fact]
    public void NothingInTheStoreWritesAMarkerIntoTheKindByte()
    {
        string store = Path.Combine(FailCriticalErrorsTests.FindRepo(), @"src\FsAnalyser.Core\Store");
        var refusals = new List<string>();
        int declarations = 0, writes = 0, reads = 0, files = 0;
        foreach (string file in Directory.EnumerateFiles(store, "*.cs", SearchOption.AllDirectories))
        {
            files++;
            Grammar.Result result = Grammar.Check(File.ReadAllText(file));
            refusals.AddRange(result.Refusals.Select(r => $"{Path.GetFileName(file)} {r}"));
            declarations += result.Declarations;
            writes += result.Writes;
            reads += result.Reads;
        }

        Assert.True(refusals.Count == 0, "the store names the Kind column in a way the grammar does not know (a marker stored in the byte would need every raw Kind read masked first):\n" + string.Join("\n", refusals));
        // Not vacuous: the census met the store's files, its one declaration, its one whole write and its raw reads.
        Assert.True(files >= 3, $"{files} files");
        Assert.Equal(1, declarations);
        Assert.Equal(1, writes);
        Assert.True(reads >= 8, $"{reads} raw Kind reads met");
    }

    public static TheoryData<string> RowIds()
    {
        var data = new TheoryData<string>();
        foreach (Row row in Corpus())
            data.Add(row.Id);
        return data;
    }

    /// <summary>Every row of the corpus fed, alone, to the grammar, and to the patterns the design declared first.</summary>
    [Theory]
    [MemberData(nameof(RowIds))]
    public void EachRowIsRefusedOrAcceptedAsTheCorpusSays(string id)
    {
        Row row = Corpus().Single(r => r.Id == id);
        bool refused = Grammar.Check(row.Line).Refusals.Count > 0;
        Assert.True(refused == (row.Verdict == "refused"),
            $"{row.Id} ({row.Note}): the grammar {(refused ? "refuses" : "accepts")} a line the corpus says is {row.Verdict}: {row.Line}");
        bool declaredFires = Declared.Any(p => p.IsMatch(row.Line));
        Assert.True(declaredFires == (row.Before == "fire"),
            $"{row.Id}: the patterns the design declared {(declaredFires ? "fire" : "stay quiet")} on this line, and the corpus says {row.Before}: {row.Line}");
    }

    /// <summary>The corpus is what its note says: rows the declared patterns let through are refused now, and the misses are pinned.</summary>
    [Fact]
    public void TheCorpusHoldsRowsTheDeclaredPatternsLetThrough()
    {
        List<Row> rows = Corpus();
        Assert.All(rows, r => Assert.Contains(r.Verdict, new[] { "refused", "accepted", "miss" }));
        Assert.All(rows, r => Assert.Contains(r.Before, new[] { "fire", "quiet" }));
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
        Assert.True(rows.Count(r => r.Verdict == "refused" && r.Before == "quiet") >= 20, "few rows are spellings the declared patterns miss");
        Assert.True(rows.Count(r => r.Verdict == "refused" && r.Before == "fire") >= 5, "few rows are spellings the declared patterns catch");
        Assert.True(rows.Count(r => r.Verdict == "accepted") >= 8, "few quiet rows");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(CorpusPath));
        JsonElement root = document.RootElement;
        string[] outside = root.GetProperty("bounded_by").GetProperty("outside_the_bound").GetProperty("rows").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(rows.Where(r => r.Verdict == "miss").Select(r => r.Id).Order(), outside.Order());

        // Every row an alias names exists, and every refused row is under some alias.
        var named = new HashSet<string>();
        foreach (JsonProperty alias in root.GetProperty("aliases").EnumerateObject())
        {
            foreach (JsonElement member in alias.Value.EnumerateArray())
            {
                Assert.Contains(rows, r => r.Id == member.GetString());
                named.Add(member.GetString()!);
            }
        }
        foreach (Row row in rows.Where(r => r.Verdict == "refused" && r.Id.StartsWith('K')))
            Assert.True(named.Contains(row.Id), $"{row.Id} is under no alias");
    }

    private sealed record Row(string Id, string Verdict, string Before, string Line, string Note);

    private static string CorpusPath => Path.Combine(FailCriticalErrorsTests.FindRepo(), @"tests\FsAnalyser.Core.Tests\KindByteWriteCorpus.json");

    private static List<Row> Corpus()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(CorpusPath));
        return document.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => new Row(
                r.GetProperty("id").GetString()!,
                r.GetProperty("verdict").GetString()!,
                r.GetProperty("before").GetString()!,
                r.GetProperty("line").GetString()!,
                r.GetProperty("note").GetString()!))
            .ToList();
    }
}
