using FsAnalyser.App;
using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Workarounds;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The words for a folder left out on purpose (SC-021, SC-046): written once, in UnreadableRows, keyed on the reason the record
/// carries. Each function has one arm per named reason and a neutral fallback; a reason with no arm reaches the fallback, which is
/// true of any folder left out and says nothing of the container layer store, and these tests turn red when one does.
/// </summary>
public class UnreadableStoreWordsTests
{
    private const NotScannedReason Unnamed = (NotScannedReason)9999;
    private const string Path = @"C:\ProgramData\Microsoft\Windows\Containers\Layers";

    private static readonly KnownWorkaround Why = KnownWorkarounds.WcifsLayerStore;

    // A second precaution, as the next one found would be written: its own words, none of the first's.
    private static readonly KnownWorkaround Other = new("other-precaution", "Skip some other folders in scans", "Some outside defect.",
        "some other component", "It leaves the folder out.", "It is read like any other folder.", DefaultOn: true);

    // ---- Today's strings, byte for byte (the window cases read the note, WindowCases.ps1) ----

    [Fact]
    public void TheListRowAndTheTreeNoteKeepTheirWords()
    {
        const string today = "size not counted: Windows container layers, skipped to avoid a leak in Windows' wcifs.sys driver; see Settings";

        Assert.Equal(today, UnreadableRows.NotScannedText(NotScannedReason.ContainerLayerStore));
        Assert.Equal(today, UnreadableRows.NotScannedText(UnreadableRows.FlaggedReason));
        Assert.Equal($"{Path}  —  {today}", new NotScannedRow(Path, NotScannedReason.ContainerLayerStore, Why.Id).Text);
    }

    [Fact]
    public void TheResultsListTitleKeepsItsWords()
    {
        Assert.Equal("size not counted: Windows container layers", UnreadableRows.NotScannedShortText(UnreadableRows.FlaggedReason));
    }

    [Fact]
    public void TheEndingNoteKeepsItsWords()
    {
        string today = $"Not scanned, size not counted: {Path} (Windows container layers)." +
            $" Skipped to avoid a leak in {Why.WhereItLives} {Why.Defect} The Settings checkbox '{Why.Title}' left it out; " +
            "it can be turned off in Settings.";

        Assert.Equal(today, UnreadableRows.NotScannedNote(NotScannedReason.ContainerLayerStore, Path, Why));
        Assert.Equal($"Not scanned, size not counted: {Path} (Windows container layers).",
            UnreadableRows.NotScannedNote(NotScannedReason.ContainerLayerStore, Path, null));
    }

    [Fact]
    public void TheRefusalKeepsItsWords()
    {
        string head = "Windows container layers can't be scanned safely: the wcifs filter leaks kernel memory and a System handle " +
            "for each entry read, until a full restart. This folder is in the Windows container layer store, so nothing " +
            "in it was read.";
        string today = head + $"\n{Why.Defect}\nWhere it lives: {Why.WhereItLives}\nWhile the Settings checkbox '{Why.Title}' is ticked, as " +
            "it is at every start, a folder at or inside the store is not scanned. You can turn this protection off in Settings.";

        Assert.Equal(today, UnreadableRows.RefusedText(ScanRefusal.ContainerLayerStore, Why));
        Assert.Equal(head, UnreadableRows.RefusedText(ScanRefusal.ContainerLayerStore, null));
    }

    // ---- Keyed on the reason ----

    /// <summary>Every named reason has an arm of its own: none reaches the fallback.</summary>
    [Fact]
    public void EveryNotScannedReasonHasANamedArmInEveryFunction()
    {
        foreach (NotScannedReason reason in Enum.GetValues<NotScannedReason>())
        {
            Assert.NotEqual(UnreadableRows.NotScannedText(Unnamed), UnreadableRows.NotScannedText(reason));
            Assert.NotEqual(UnreadableRows.NotScannedShortText(Unnamed), UnreadableRows.NotScannedShortText(reason));
            Assert.NotEqual(UnreadableRows.NotScannedNote(Unnamed, Path, Why), UnreadableRows.NotScannedNote(reason, Path, Why));
            Assert.NotEqual(UnreadableRows.NotScannedInRemainder(Unnamed), UnreadableRows.NotScannedInRemainder(reason));
        }
    }

    [Fact]
    public void EveryRefusalButNoneHasANamedArm()
    {
        foreach (ScanRefusal refusal in Enum.GetValues<ScanRefusal>().Where(r => r != ScanRefusal.None))
            Assert.NotEqual(UnreadableRows.RefusedText((ScanRefusal)9999, Why), UnreadableRows.RefusedText(refusal, Why));
    }

    /// <summary>
    /// A reason with no arm gets the fallback, and the fallback says nothing the first reason's words say: no container layers, no
    /// wcifs.sys, no leak. That is the guarantee SC-046 asks for: the next precaution that leaves a folder out is never worded as the
    /// first.
    /// </summary>
    [Fact]
    public void AReasonWithNoArmIsNeverWordedAsTheContainerLayerStore()
    {
        string[] fallbacks =
        [
            UnreadableRows.NotScannedText(Unnamed),
            UnreadableRows.NotScannedShortText(Unnamed),
            UnreadableRows.NotScannedNote(Unnamed, Path, Other),
            UnreadableRows.NotScannedInRemainder(Unnamed),
            UnreadableRows.RefusedText((ScanRefusal)9999, Other),
        ];

        Assert.All(fallbacks, text =>
        {
            Assert.DoesNotContain("container layer", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wcifs", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("leak", text, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains("on purpose", UnreadableRows.NotScannedText(Unnamed));
    }

    /// <summary>
    /// The folder tree's note reads EntryFlags.NotScanned and the results list's title TopLevelState.NotScanned: neither carries a
    /// NotScannedReason, so both are worded for UnreadableRows.FlaggedReason. That is right while Core has one reason. The second
    /// makes this fail first: carry the reason to FolderTreeNode.Note and TopLevelRowText.TitleOf (a Core change to
    /// TopLevelTracker, Core's TopLevelRow, and the tree session) before writing its arm.
    /// </summary>
    [Fact]
    public void NotScannedReasonHasOneMemberSoTheFlagOnlySurfacesAreWordedForIt()
    {
        Assert.True(Enum.GetValues<NotScannedReason>().Length == 1 && UnreadableRows.FlaggedReason == NotScannedReason.ContainerLayerStore,
            "NotScannedReason has more than one member, so a flag alone no longer says which. FolderTreeNode.Note (the tree's note) and " +
            "TopLevelRowText.TitleOf (the results list's title) word a NotScanned flag for UnreadableRows.FlaggedReason: carry the " +
            "reason to both before adding the new reason's arm in UnreadableRows.");
    }

    /// <summary>The panel's remainder sentence for a reason is the one place the panel says the store was left out.</summary>
    [Fact]
    public void TheRemainderSentenceNamesTheStoreAsLeftOutUnopened()
    {
        string text = UnreadableRows.NotScannedInRemainder(NotScannedReason.ContainerLayerStore);

        Assert.Equal("It includes the Windows container layer store, which this scan left out unopened.", text);
    }
}
