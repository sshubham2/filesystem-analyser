using System.ComponentModel;
using FsAnalyser.App;
using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The unreadable-folders list (SL-087) from failed-folder records made here, with paths from a lookup rather than a
/// store: one row per folder in the record, in path order, each with the kind the record holds and a reason a person
/// can read. The real scans that make such records are in UnreadableFolderTests.
/// </summary>
public class UnreadableRowsTests
{
    [Fact]
    public void ANothingListedAndAPartwayEntryGiveOneRowEachWithTheirOwnKind()
    {
        UnreadableFolder[] record = [new(7, 995, UnreadableKind.ListedPartway), new(3, 5, UnreadableKind.NothingListed)];
        var paths = new Dictionary<int, string> { [3] = @"C:\data\denied", [7] = @"C:\data\partway" };

        IReadOnlyList<UnreadableRow> rows = UnreadableRows.Build(record, e => paths[e]);

        Assert.Equal(
            [
                new UnreadableRow(@"C:\data\denied", UnreadableKind.NothingListed, 5, "access denied"),
                new UnreadableRow(@"C:\data\partway", UnreadableKind.ListedPartway, 995, UnreadableRows.Reason(995)),
            ],
            rows);
        Assert.Equal("C:\\data\\denied  \u2014  could not be opened: access denied", rows[0].Text);
        Assert.Equal($"C:\\data\\partway  \u2014  only partly read: {new Win32Exception(995).Message.Trim()} (Windows error 995)", rows[1].Text);
        Assert.Equal(rows[1].Text, rows[1].ToString()); // what the list shows
    }

    [Fact]
    public void AnEntryRecordedThreeTimesIsOneRowWithItsFirstKindAndCode()
    {
        UnreadableFolder[] record =
        [
            new(4, 5, UnreadableKind.NothingListed),
            new(9, 32, UnreadableKind.NothingListed),
            new(4, 59, UnreadableKind.ListedPartway),
            new(4, 363, UnreadableKind.ListedPartway),
        ];
        var looked = new List<int>();

        IReadOnlyList<UnreadableRow> rows = UnreadableRows.Build(record, e => { looked.Add(e); return $@"C:\x\{e}"; });

        Assert.Equal(
            [
                new UnreadableRow(@"C:\x\4", UnreadableKind.NothingListed, 5, "access denied"),
                new UnreadableRow(@"C:\x\9", UnreadableKind.NothingListed, 32, "in use by another program"),
            ],
            rows);
        Assert.Equal([4, 9], looked); // each path is the lookup's, asked once per folder
    }

    [Fact]
    public void AnEmptyRecordGivesNoRows() =>
        Assert.Empty(UnreadableRows.Build([], e => throw new InvalidOperationException("no entry to look up")));

    /// <summary>U-104's shape: more than 10,000 failed folders, every one listed, none capped or summed up.</summary>
    [Fact]
    public void TenThousandAndFiftyShuffledEntriesAreAllListedInPathOrder()
    {
        const int count = 10_050;
        var paths = new Dictionary<int, string>();
        for (int i = 0; i < count; i++)
            paths[i] = i % 3 == 0 ? $@"C:\Windows\Logs\d{i}" : i % 3 == 1 ? $@"C:\Windows\Logs\d{i / 10}\sub {i}" : $@"C:\ProgramData\{(char)('a' + i % 26)}.{i}";
        var shuffled = Enumerable.Range(0, count).ToArray();
        new Random(87).Shuffle(shuffled);
        UnreadableFolder[] record = shuffled.Select(e => new UnreadableFolder(e, 5, UnreadableKind.NothingListed)).ToArray();

        IReadOnlyList<UnreadableRow> rows = UnreadableRows.Build(record, e => paths[e]);

        Assert.Equal(count, rows.Count);
        Assert.Equal(paths.Values.Order(StringComparer.Ordinal), rows.Select(r => r.Path).Order(StringComparer.Ordinal));
        for (int i = 1; i < rows.Count; i++)
            Assert.True(UnreadableRows.ComparePaths(rows[i - 1].Path, rows[i].Path) < 0, $"row {i - 1} '{rows[i - 1].Path}' is not before row {i} '{rows[i].Path}'");
        Assert.All(rows, r => Assert.Equal($"{r.Path}  \u2014  could not be opened: access denied", r.Text));
    }

    /// <summary>
    /// Names that ordinal-ignore-case or culture order would put between a folder and what is under it: a space and a
    /// dot sort below the separator there, and an upper-cased letter sorts below it ordinally. Here the separator is
    /// lowest, so a folder's rows stay together. The order is literal, not Explorer's numeric one: d10 before d2.
    /// </summary>
    [Theory]
    [InlineData(new[] { @"C:\a b", @"C:\a.b", @"C:\d2", @"C:\a\b", @"C:\d10", @"C:\a", @"C:\d1\x" },
        new[] { @"C:\a", @"C:\a\b", @"C:\a b", @"C:\a.b", @"C:\d1\x", @"C:\d10", @"C:\d2" })]
    [InlineData(new[] { @"C:\a b", @"C:\a\b", @"C:\A", @"C:\a.b", @"C:\B" },
        new[] { @"C:\A", @"C:\a\b", @"C:\a b", @"C:\a.b", @"C:\B" })]
    [InlineData(new[] { @"C:\Z", @"C:\a\z", @"C:\a/y", @"C:\_", @"C:\a" },
        new[] { @"C:\a", @"C:\a/y", @"C:\a\z", @"C:\Z", @"C:\_" })]
    public void SiblingsThatStraddleTheSeparatorSortAfterTheFolderAndEverythingInIt(string[] given, string[] expected)
    {
        UnreadableFolder[] record = given.Select((_, i) => new UnreadableFolder(i, 5, UnreadableKind.NothingListed)).ToArray();

        IReadOnlyList<UnreadableRow> rows = UnreadableRows.Build(record, e => given[e]);

        Assert.Equal(expected, rows.Select(r => r.Path));
    }

    /// <summary>
    /// Folders whose names differ only in case, as a case-sensitive directory can hold, with folders under two of them.
    /// Ignoring case they tie; the ordinal tie-break, taken where each name ends, puts them in one order whatever order
    /// the record holds them in, and keeps what is under each directly below it (review R2-m2).
    /// </summary>
    [Fact]
    public void PathsThatDifferOnlyInCaseSortTheSameWhateverTheRecordOrder()
    {
        string[] paths = [@"C:\cs\readme", @"C:\cs\README", @"C:\cs\ReadMe", @"C:\cs", @"C:\cs\README\y", @"C:\cs\readme\x"];
        string[] expected = [@"C:\cs", @"C:\cs\README", @"C:\cs\README\y", @"C:\cs\ReadMe", @"C:\cs\readme", @"C:\cs\readme\x"];

        foreach (int[] order in new[] { new[] { 0, 1, 2, 3, 4, 5 }, [5, 4, 3, 2, 1, 0], [5, 1, 4, 0, 3, 2], [4, 2, 5, 3, 0, 1], [0, 5, 1, 4, 2, 3] })
        {
            UnreadableFolder[] record = order.Select(e => new UnreadableFolder(e, 5, UnreadableKind.NothingListed)).ToArray();
            Assert.Equal(expected, UnreadableRows.Build(record, e => paths[e]).Select(r => r.Path));
        }
        Assert.NotEqual(0, UnreadableRows.ComparePaths(@"C:\cs\readme", @"C:\cs\README"));
        // Each pair both ways, so the order is antisymmetric on the shapes above.
        foreach (string p in paths)
        {
            foreach (string q in paths)
                Assert.Equal(-Math.Sign(UnreadableRows.ComparePaths(q, p)), Math.Sign(UnreadableRows.ComparePaths(p, q)));
        }
    }

    /// <summary>
    /// Each code of the three plain families gets its phrase and no number. The table is written from memory with
    /// winerror.h names, so each row also checks that Windows has a message for the code, which a mistyped number
    /// would not (review m3).
    /// </summary>
    [Theory]
    [InlineData(5, "access denied")]
    [InlineData(65, "access denied")]
    [InlineData(32, "in use by another program")]
    [InlineData(33, "in use by another program")]
    [InlineData(51, "network or share error")]
    [InlineData(53, "network or share error")]
    [InlineData(54, "network or share error")]
    [InlineData(55, "network or share error")]
    [InlineData(58, "network or share error")]
    [InlineData(59, "network or share error")]
    [InlineData(64, "network or share error")]
    [InlineData(67, "network or share error")]
    [InlineData(70, "network or share error")]
    [InlineData(71, "network or share error")]
    [InlineData(72, "network or share error")]
    [InlineData(1203, "network or share error")]
    [InlineData(1219, "network or share error")]
    [InlineData(1222, "network or share error")]
    [InlineData(1231, "network or share error")]
    [InlineData(1232, "network or share error")]
    [InlineData(1236, "network or share error")]
    [InlineData(2250, "network or share error")]
    public void ACodeOfAPlainFamilyGetsItsPhrase(int code, string phrase)
    {
        Assert.Equal(phrase, UnreadableRows.Reason(code));
        Assert.DoesNotContain(code.ToString(), UnreadableRows.Reason(code));
        Assert.False(new Win32Exception(code).Message.StartsWith("Unknown error", StringComparison.Ordinal),
            $"Windows has no message for {code}: the table names a code that does not exist");
    }

    /// <summary>
    /// Codes outside the families that scans of this PC recorded (2, 363, 1920) or the Done-when names (the cloud and
    /// reparse codes) keep Windows' own message, on one line, and then the code. The message is the runtime's lookup,
    /// so this checks the shape and the number; which language it is in is not checked here (review m2).
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(362)]
    [InlineData(363)]
    [InlineData(1920)]
    [InlineData(1921)]
    [InlineData(4390)]
    [InlineData(4391)]
    [InlineData(4392)]
    [InlineData(4393)]
    [InlineData(4394)]
    public void AnotherCodeGetsWindowsOwnMessageAndItsNumber(int code)
    {
        string message = new Win32Exception(code).Message.Trim();

        Assert.False(message.StartsWith("Unknown error", StringComparison.Ordinal), $"Windows has no message for {code}");
        Assert.Equal($"{message} (Windows error {code})", UnreadableRows.Reason(code));
        Assert.Null(UnreadableRows.PlainPhrase(code));
    }

    /// <summary>A code Windows has no message for: the runtime's text names it in hex only, and the decimal number follows.</summary>
    [Theory]
    [InlineData(9999)]
    [InlineData(15999)]
    [InlineData(65535)]
    public void ACodeWithNoMessageStillNamesItsNumber(int code)
    {
        string reason = UnreadableRows.Reason(code);

        Assert.EndsWith($" (Windows error {code})", reason);
        Assert.False(string.IsNullOrWhiteSpace(reason[..^$" (Windows error {code})".Length]), $"'{reason}' has no text before its number");
        Assert.Equal($"{new Win32Exception(code).Message.Trim()} (Windows error {code})", reason);
    }

    /// <summary>
    /// The lookup giving nothing, which the runtime never did for any code on this PC: the fallback names the code. And a
    /// message over several lines is put on one, so a row stays one line (review m5).
    /// </summary>
    [Theory]
    [InlineData(null, "no description from Windows (Windows error 4390)")]
    [InlineData("", "no description from Windows (Windows error 4390)")]
    [InlineData("   ", "no description from Windows (Windows error 4390)")]
    [InlineData("\r\n\t\r\n", "no description from Windows (Windows error 4390)")]
    [InlineData("First line.\r\nSecond line.\r\n", "First line. Second line. (Windows error 4390)")]
    [InlineData("  One.  \n\n  Two.\rThree.", "One. Two. Three. (Windows error 4390)")]
    public void AnEmptyOrBrokenSystemMessageStillGivesAOneLineReason(string? message, string expected)
    {
        Assert.Equal(expected, UnreadableRows.Reason(4390, _ => message));
        Assert.Equal("access denied", UnreadableRows.Reason(5, _ => message)); // a phrase never asks
    }

    /// <summary>
    /// Every code up to 16,000: never an empty reason, never a line break, and every code outside the plain families
    /// names itself (review M1: the phrases carry no number, by the #4 contract).
    /// </summary>
    [Fact]
    public void EveryCodeUpTo16000HasAOneLineReasonAndOutsideThePhrasesNamesItsCode()
    {
        var problems = new List<string>();
        for (int code = 1; code <= 16_000; code++)
        {
            string reason = UnreadableRows.Reason(code);
            if (string.IsNullOrWhiteSpace(reason))
                problems.Add($"{code}: empty");
            else if (reason.Contains('\r') || reason.Contains('\n'))
                problems.Add($"{code}: a line break in '{reason}'");
            else if (UnreadableRows.PlainPhrase(code) is null && !reason.EndsWith($" (Windows error {code})", StringComparison.Ordinal))
                problems.Add($"{code}: no number in '{reason}'");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
    }

    /// <summary>
    /// The one wording source (SL-106): every member of UnreadableKind has words of its own, none of them the fallback, and
    /// only a refused open says it could not be opened, so a kind added without an arm cannot pass as one and a folder that
    /// opened is never worded as refused. A value nobody named, and no kind at all, get the plain fallback and do not
    /// throw: the results list's converter calls this while a scan is on.
    /// </summary>
    [Fact]
    public void EveryKindHasWordsOfItsOwnAndOnlyARefusedOpenSaysItCouldNotBeOpened()
    {
        const string fallback = "could not be read";
        UnreadableKind[] kinds = Enum.GetValues<UnreadableKind>();
        Assert.Equal(3, kinds.Length);

        string[] words = kinds.Select(k => UnreadableRows.KindText(k)).ToArray();
        Assert.Equal(words.Length, words.Distinct().Count());
        Assert.DoesNotContain(fallback, words);
        Assert.Equal([UnreadableKind.NothingListed], kinds.Where(k => UnreadableRows.KindText(k).Contains("could not be opened")));
        Assert.Equal("could not be opened", UnreadableRows.KindText(UnreadableKind.NothingListed));
        Assert.Equal("opened, but nothing could be listed", UnreadableRows.KindText(UnreadableKind.OpenedNothingListed));
        Assert.Equal("only partly read", UnreadableRows.KindText(UnreadableKind.ListedPartway));

        Assert.Equal(fallback, UnreadableRows.KindText((UnreadableKind)99));
        Assert.Equal(fallback, UnreadableRows.KindText(null));
    }

    /// <summary>The kind the record holds reaches the list's line as that kind's words, for all three kinds.</summary>
    [Fact]
    public void AnOpenedNothingListedEntryIsOneRowWordedAsOpenedAndNotAsRefused()
    {
        UnreadableFolder[] record = [new(2, 59, UnreadableKind.OpenedNothingListed), new(1, 5, UnreadableKind.NothingListed)];

        IReadOnlyList<UnreadableRow> rows = UnreadableRows.Build(record, e => $@"C:\x\{e}");

        Assert.Equal(
            [@"C:\x\1  —  could not be opened: access denied", @"C:\x\2  —  opened, but nothing could be listed: network or share error"],
            rows.Select(r => r.Text));
    }
}
