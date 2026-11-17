using System.Text.RegularExpressions;
using FsAnalyser.App;
using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The coverage panel's model and words (SL-001), fed rows a test made: what it names, what it leaves to the remainder, and what
/// it never says. The pure file is compiled here as source, with no WPF in it. The window cases read the same model back through
/// the report.
/// </summary>
public class CoveragePanelTextTests
{
    private const long Gib = 1024L * 1024 * 1024;
    private const long Mib = 1024L * 1024;

    private static readonly CoverageFacts NoFacts = new([], [], 0, 0);

    private static UnreadableRow Unreadable(string path) =>
        new(path, UnreadableKind.NothingListed, 5, UnreadableRows.AccessDenied);

    private static NotScannedRow Store(string path = @"C:\ProgramData\Microsoft\Windows\Containers\Layers") =>
        new(path, NotScannedReason.ContainerLayerStore, "wcifs-layer-store");

    private static CoverageBreakdown Built(CoverageFacts facts, long seen = 100 * Gib, long used = 130 * Gib, long? mft = null) =>
        CoveragePanelText.Build(facts, finished: true, seen, used, mft)!;

    [Fact]
    public void AScanThatDidNotFinishHasNoPanel()
    {
        Assert.Null(CoveragePanelText.Build(NoFacts, finished: false, 100 * Gib, 130 * Gib, 10 * Gib));
        Assert.NotNull(CoveragePanelText.Build(NoFacts, finished: true, 100 * Gib, 130 * Gib, 10 * Gib));
    }

    [Fact]
    public void AScanThatCountedMoreThanTheVolumeReportsHasNoPanel()
    {
        Assert.Null(CoveragePanelText.Build(NoFacts, finished: true, seenOnDisk: 131 * Gib, volumeInUse: 130 * Gib, mft: 1 * Gib));
        Assert.Null(CoveragePanelText.Build(NoFacts, finished: true, seenOnDisk: 130 * Gib + 1, volumeInUse: 130 * Gib, mft: null));
    }

    [Fact]
    public void AScanThatSawEverythingTheVolumeReportsHasAPanelOfNothing()
    {
        CoverageBreakdown panel = Built(NoFacts, seen: 130 * Gib, used: 130 * Gib, mft: 1 * Gib);

        Assert.Equal(0, panel.Unseen);
        Assert.Empty(panel.Measured);
        Assert.Equal(0, panel.Remainder.Bytes);
    }

    /// <summary>The measured part and the remainder always add up to the unseen space, whatever the $MFT figure.</summary>
    [Theory]
    [InlineData(null, 0)]
    [InlineData(0L, 0)]
    [InlineData(-5L, 0)]
    [InlineData(10L * Gib, 1)]
    [InlineData(30L * Gib, 1)] // exactly the unseen space: the remainder is 0, not negative
    [InlineData(30L * Gib + 1, 0)] // more than the unseen space: not a part of it
    public void MeasuredAndRemainderAddUpToTheUnseenSpace(long? mft, int measuredParts)
    {
        CoverageBreakdown panel = Built(NoFacts, mft: mft);

        Assert.Equal(30 * Gib, panel.Unseen);
        Assert.Equal(measuredParts, panel.Measured.Count);
        Assert.Equal(panel.Unseen, panel.Measured.Sum(part => part.Bytes) + panel.Remainder.Bytes);
        Assert.True(panel.Remainder.Bytes >= 0);
    }

    [Fact]
    public void TheMftIsTheOneMeasuredPartAndItsWordsSayWhatWasMeasured()
    {
        CoverageBreakdown panel = Built(NoFacts, mft: 10 * Gib);

        CoveragePart mft = Assert.Single(panel.Measured);
        Assert.Equal("mft", mft.Id);
        Assert.Equal(10 * Gib, mft.Bytes);
        Assert.Matches(@"(?i)\bmeasured\b", mft.Text);
        Assert.Contains("valid data length", mft.Text);
        Assert.Contains("$MFT", mft.Text);
        Assert.Equal(20 * Gib, panel.Remainder.Bytes);
        Assert.Contains("other than the $MFT", panel.Remainder.Text);
    }

    [Fact]
    public void TheRemainderIsSpaceThisAccountCannotReadAndNeverAMeasuredFigure()
    {
        foreach (long? mft in new long?[] { null, 10 * Gib })
        {
            foreach (CoverageFacts facts in new[] { NoFacts, new CoverageFacts([Store()], [Unreadable(@"C:\a")], 2, 8192) })
            {
                string text = Built(facts, mft: mft).Remainder.Text;

                Assert.Matches(@"(?i)cannot read", text);
                Assert.DoesNotMatch(@"(?i)(?<!not |never )\bmeasured\b", text);
                Assert.DoesNotContain("above", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void WithoutTheMftTheRemainderIsTheWholeUnseenSpaceAndDoesNotSayOtherThanTheMft()
    {
        CoverageBreakdown panel = Built(NoFacts, mft: null);

        Assert.Empty(panel.Measured);
        Assert.Equal(panel.Unseen, panel.Remainder.Bytes);
        Assert.DoesNotContain("$MFT", panel.Remainder.Text);
    }

    /// <summary>Both positions of the precaution: with a store left out the remainder says it includes it, with none it never mentions it.</summary>
    [Fact]
    public void TheRemainderNamesTheContainerLayerStoreIfAndOnlyIfOneWasLeftOut()
    {
        CoverageBreakdown with = Built(new CoverageFacts([Store()], [], 0, 0));
        CoverageBreakdown without = Built(NoFacts);
        CoverageBreakdown unreadableOnly = Built(new CoverageFacts([], [Unreadable(@"C:\System Volume Information")], 0, 0));

        Assert.True(with.Remainder.IncludesStore);
        Assert.Contains("container layer", with.Remainder.Text);
        Assert.Contains("left out unopened", with.Remainder.Text);
        Assert.False(without.Remainder.IncludesStore);
        Assert.DoesNotContain("container layer", without.Remainder.Text);
        Assert.False(unreadableOnly.Remainder.IncludesStore);
        Assert.DoesNotContain("container layer", unreadableOnly.Remainder.Text);
        Assert.Equal(with.Unseen, with.Remainder.Bytes); // the store has no size of its own: it is in the leftover
    }

    [Fact]
    public void FoldersAreNamedWithNoSizeTheStoreFirstThenTheUnreadableOnesAndTheReportHoldsAllOfThem()
    {
        var unreadable = new[] { Unreadable(@"C:\System Volume Information"), Unreadable(@"C:\Users\other") };
        CoverageBreakdown panel = Built(new CoverageFacts([Store()], unreadable, 0, 0));

        Assert.Equal(["not-scanned", "unreadable", "unreadable"], panel.Folders.Select(f => f.Kind));
        Assert.All(panel.Folders, folder => Assert.Null(folder.Size));
        string[] lines = [.. panel.ScreenLines];
        Assert.Contains(lines, line => line.Contains(@"C:\System Volume Information"));
        Assert.Contains(lines, line => line.Contains(@"C:\Users\other"));
        Assert.Contains(lines, line => line.Contains(@"C:\ProgramData\Microsoft\Windows\Containers\Layers"));
        Assert.True(Array.FindIndex(lines, l => l.Contains("Containers")) < Array.FindIndex(lines, l => l.Contains("System Volume")),
            "the store is named before the unreadable folders");
    }

    [Fact]
    public void NoTextOutsideTheScreenLinesHoldsAPath()
    {
        var unreadable = new[] { Unreadable(@"C:\System Volume Information"), Unreadable(@"C:\Users\other") };
        CoverageBreakdown panel = Built(new CoverageFacts([Store()], unreadable, 2, 128 * 1024), mft: 10 * Gib);

        string[] texts = [.. panel.Measured.Select(m => m.Text), panel.Remainder.Text, panel.SizeNotRead!.Text];
        Assert.All(texts, text => Assert.False(text.Contains('\\') || text.Contains('/'), $"a path separator in: {text}"));
        Assert.All(texts, text => Assert.DoesNotContain("C:", text));
        Assert.Contains(panel.ScreenLines, line => line.Contains('\\'));
    }

    [Fact]
    public void TheFiguresComeBeforeTheFolderLines()
    {
        CoverageBreakdown panel = Built(new CoverageFacts([Store()], [Unreadable(@"C:\a")], 1, 4096), mft: 10 * Gib);

        string[] lines = [.. panel.ScreenLines];
        int remainder = Array.IndexOf(lines, panel.Remainder.Text);
        int firstFolder = Array.FindIndex(lines, l => l.Contains(@"C:\"));
        Assert.True(remainder > 0 && remainder < firstFolder, $"the remainder is line {remainder}, the first folder line {firstFolder}");
        Assert.True(Array.IndexOf(lines, panel.SizeNotRead!.Text) < firstFolder);
    }

    /// <summary>
    /// A real C:\ recorded 559 unreadable folders, 446 of them under one folder: named in path order the first 20 would all be
    /// 'ProgramData\...' report folders, and System Volume Information and another user's profile would be 'and N more'. The
    /// shallowest are named, the rest counted, and the report's list holds every one.
    /// </summary>
    [Fact]
    public void ManyDeepFoldersNeverCrowdOutTheShallowOnesAHumanRecognises()
    {
        var rows = new List<UnreadableRow>();
        for (int i = 0; i < 30; i++)
            rows.Add(Unreadable($@"C:\ProgramData\Microsoft\Windows\WER\ReportArchive\Report{i:D2}"));
        rows.Add(Unreadable(@"C:\System Volume Information"));
        rows.Add(Unreadable(@"C:\Users\other"));
        rows.Sort((a, b) => UnreadableRows.ComparePaths(a.Path, b.Path)); // the order UnreadableRows.Build gives them

        CoverageBreakdown panel = Built(new CoverageFacts([], rows, 0, 0));

        string[] lines = [.. panel.ScreenLines];
        Assert.Contains(lines, line => line.Contains(@"C:\System Volume Information"));
        Assert.Contains(lines, line => line.Contains(@"C:\Users\other"));
        Assert.Equal(CoveragePanelText.UnreadableShown, lines.Count(l => l.StartsWith("  ", StringComparison.Ordinal) && l.Contains(@"C:\")));
        Assert.Contains("  and 12 more folders, in the Unreadable folders tab", lines);
        Assert.Equal(32, panel.Folders.Count); // the report's list is complete
        Assert.All(panel.Folders, f => Assert.Equal("unreadable", f.Kind));
        Assert.DoesNotContain("above", panel.Remainder.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("32 folders", panel.Remainder.Text); // the remainder covers all of them, not the 20 named
    }

    [Fact]
    public void ShownFoldersStayInPathOrderAndAFewerThanTheCapAreAllNamedWithNoMoreLine()
    {
        var rows = new[] { Unreadable(@"C:\a"), Unreadable(@"C:\b\c"), Unreadable(@"C:\d") };

        CoverageBreakdown panel = Built(new CoverageFacts([], rows, 0, 0));

        string[] named = [.. panel.ScreenLines.Where(l => l.StartsWith("  ", StringComparison.Ordinal))];
        Assert.Equal(3, named.Length);
        Assert.True(named[0].Contains(@"C:\a") && named[1].Contains(@"C:\b\c") && named[2].Contains(@"C:\d"));
        Assert.DoesNotContain(panel.ScreenLines, l => l.Contains(" more folder"));
    }

    [Fact]
    public void OneUnreadableFolderIsWrittenInTheSingular()
    {
        CoverageBreakdown panel = Built(new CoverageFacts([], [Unreadable(@"C:\a")], 0, 0));

        Assert.Contains("It includes 1 folder this scan could not read.", panel.Remainder.Text);
    }

    /// <summary>The files whose own size could not be read are reported apart: counted in what the scan saw, never part of the unseen space.</summary>
    [Fact]
    public void FilesWhoseSizeWasNotReadAreReportedApartAndNeverSubtractedFromTheUnseenSpace()
    {
        CoverageBreakdown with = Built(new CoverageFacts([], [], 2, 128 * 1024));
        CoverageBreakdown without = Built(NoFacts);

        Assert.Equal(2, with.SizeNotRead!.Files);
        Assert.Equal(128 * 1024, with.SizeNotRead.Bytes);
        Assert.Contains("2 files", with.SizeNotRead.Text);
        Assert.Contains("not part of the unseen space", with.SizeNotRead.Text);
        Assert.Contains("at the on-disk size the folder listing gave", with.SizeNotRead.Text);
        Assert.Equal(without.Unseen, with.Unseen);
        Assert.Equal(without.Remainder.Bytes, with.Remainder.Bytes);
        Assert.Null(without.SizeNotRead);
    }

    [Fact]
    public void OneFileWhoseSizeWasNotReadIsWrittenInTheSingular()
    {
        string text = Built(new CoverageFacts([], [], 1, 4096)).SizeNotRead!.Text;

        Assert.StartsWith("1 file whose own size could not be read is not part of", text);
        Assert.Contains("it is counted in what the scan saw", text);
    }

    [Fact]
    public void ThePanelSpellsNeitherOfTheWordsTopLevelRowTextWritesOnce()
    {
        var rows = new[] { Unreadable(@"C:\a") };
        CoverageBreakdown panel = Built(new CoverageFacts([Store()], rows, 2, 4096), mft: Gib);

        string everything = string.Join("\n", panel.ScreenLines.Concat(panel.Measured.Select(m => m.Text)));
        Assert.DoesNotContain(TopLevelRowText.AtLeastWord, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TopLevelRowText.NotReadYet, everything, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1, "1 file could not be opened to read its true size on disk; its listed size was used.")]
    [InlineData(2, "2 files could not be opened to read their true size on disk; their listed size was used.")]
    [InlineData(40, "40 files could not be opened to read their true size on disk; their listed size was used.")]
    public void TheProbeFailuresNoteKeepsItsBytesFromTwoAndWritesOneInTheSingular(int files, string expected) =>
        Assert.Equal(expected, CoveragePanelText.ProbeFailuresNote(files));

    [Theory]
    [InlineData(1, "1 file was deleted before its true size could be read; its listed size was used.")]
    [InlineData(2, "2 files were deleted before their true size could be read; their listed size was used.")]
    [InlineData(7, "7 files were deleted before their true size could be read; their listed size was used.")]
    public void TheVanishedNoteKeepsItsBytesFromTwoAndWritesOneInTheSingular(int files, string expected) =>
        Assert.Equal(expected, CoveragePanelText.VanishedNote(files));

    /// <summary>The wording tests above name 'measured' only through its negation or the $MFT's own line.</summary>
    [Fact]
    public void OnlyTheMftLineSaysMeasuredWithoutNegation()
    {
        CoverageBreakdown panel = Built(new CoverageFacts([Store()], [Unreadable(@"C:\a")], 2, 8192), mft: 10 * Gib);

        var loose = new Regex(@"(?i)(?<!not |never )\bmeasured\b");
        Assert.Matches(loose, panel.Measured[0].Text);
        Assert.DoesNotMatch(loose, panel.Remainder.Text);
        Assert.DoesNotMatch(loose, panel.SizeNotRead!.Text);
    }
}
