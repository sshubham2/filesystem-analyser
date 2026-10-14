using FsAnalyser.App;
using FsAnalyser.Core.Scanning;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// How the folder tree learns the kind a failed folder was stamped with (SL-106): FolderTreeSession is given the scan's
/// record and answers from it, for FolderTreeNode.Note to word through UnreadableRows.KindText. The session has no WPF in it,
/// so Core.Tests compiles it as source and its one branch runs here, where the published exe's tree case (T-007) reaches
/// it only for the two kinds it can draw.
/// </summary>
public class FolderTreeSessionTests
{
    [Fact]
    public void AnEntryInTheRecordGivesItsKindAndAnEntryNotInItGivesNone()
    {
        var session = new FolderTreeSession(store: null, [
            new UnreadableFolder(3, 5, UnreadableKind.NothingListed),
            new UnreadableFolder(5, 59, UnreadableKind.OpenedNothingListed),
            new UnreadableFolder(8, 995, UnreadableKind.ListedPartway),
        ]);

        Assert.Equal(UnreadableKind.NothingListed, session.FailedKind(3));
        Assert.Equal(UnreadableKind.OpenedNothingListed, session.FailedKind(5));
        Assert.Equal(UnreadableKind.ListedPartway, session.FailedKind(8));
        Assert.Null(session.FailedKind(4)); // a folder that read in full, or a file: no note
        Assert.Null(session.FailedKind(0));
        Assert.Null(session.FailedKind(-1));
    }

    /// <summary>The list keeps the first row for an entry the record holds twice (UnreadableRows.Build); the tree keeps the same.</summary>
    [Fact]
    public void AnEntryTheRecordHoldsTwiceKeepsItsFirstKind()
    {
        var session = new FolderTreeSession(store: null, [
            new UnreadableFolder(4, 5, UnreadableKind.NothingListed),
            new UnreadableFolder(4, 59, UnreadableKind.ListedPartway),
            new UnreadableFolder(9, 59, UnreadableKind.OpenedNothingListed),
            new UnreadableFolder(9, 5, UnreadableKind.NothingListed),
        ]);

        Assert.Equal(UnreadableKind.NothingListed, session.FailedKind(4));
        Assert.Equal(UnreadableKind.OpenedNothingListed, session.FailedKind(9));
    }

    /// <summary>
    /// A closed session drops the record with the store, so a row WPF still holds after the tree is cleared reads empty
    /// (SL-044 review M2), and the shared not-opened-yet row, which holds no store, never has a kind.
    /// </summary>
    [Fact]
    public void AClosedSessionAndTheStandInSessionGiveNoKind()
    {
        var session = new FolderTreeSession(store: null, [new UnreadableFolder(3, 5, UnreadableKind.NothingListed)]);
        Assert.Equal(UnreadableKind.NothingListed, session.FailedKind(3));

        session.Close();

        Assert.Null(session.FailedKind(3));
        Assert.Null(session.Store);
        Assert.Null(FolderTreeSession.None.FailedKind(3));
    }

    /// <summary>The session over a scan's own record: each failed folder's kind is the one the scan stamped, by entry.</summary>
    [Fact]
    public void ASessionOverARealScanGivesEachFailedFolderItsStampedKind()
    {
        using var tree = new TempTree();
        string refused = tree.Folder("a-refused");
        tree.File(@"a-refused\not-seen.txt", 5);
        string opened = tree.Folder("b-opened");
        tree.File(@"b-opened\one.txt", 5);
        string plain = tree.Folder("c-plain");
        tree.File(@"c-plain\one.txt", 5);
        tree.Deny(refused, 0x1); // FILE_LIST_DIRECTORY
        (int call, _) = FirstPageFailure.CallOf(tree, opened);
        (ScanResult result, _) = FirstPageFailure.Scan(tree, call, 59);

        var session = new FolderTreeSession(result.Store, result.Unreadable);

        Assert.Equal(UnreadableKind.NothingListed, session.FailedKind(Scans.Find(result, refused)));
        Assert.Equal(UnreadableKind.OpenedNothingListed, session.FailedKind(Scans.Find(result, opened)));
        Assert.Null(session.FailedKind(Scans.Find(result, plain)));
        Assert.Null(session.FailedKind(Scans.Find(result, tree.PathOf(@"c-plain\one.txt"))));
    }

    /// <summary>
    /// Whether the rows may ask the store for a lower bound is the session's, set from the one guard of the stopped list's marks
    /// (ScanResult.MarksAtLeast, SL-113): a session made without it, the stand-in and every completed scan's reads none.
    /// </summary>
    [Fact]
    public void ASessionReadsBoundsOnlyWhenItWasGivenTheGuard()
    {
        Assert.False(new FolderTreeSession(store: null, []).ReadsBounds);
        Assert.False(FolderTreeSession.None.ReadsBounds);
        Assert.True(new FolderTreeSession(store: null, [], readsBounds: true).ReadsBounds);
        var closed = new FolderTreeSession(store: null, [], true);
        closed.Close();
        Assert.Null(closed.Store); // a closed session has no store to ask whatever it was given
    }
}
