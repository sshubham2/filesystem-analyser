using FsAnalyser.Core.Scanning;
using FsAnalyser.Core.Store;

namespace FsAnalyser.Core.Tests;

/// <summary>
/// The rows the window fills in while a scan runs (SL-039): one per entry directly under the root, published
/// by the scanner's thread as immutable snapshots. Every scan here publishes on every change
/// (TopLevelInterval zero) and keeps each snapshot as it is published, from AfterFolder. Each one's rows must
/// add up to the scanner's running totals at that moment, a row may only move forward and never change once
/// it is settled, and the final one's every row must be what the store holds under its entry. No timing is
/// asserted.
/// </summary>
public class TopLevelTests
{
    private const long MiB = TempTree.MiB;
    private const uint FileListDirectory = 0x1;

    // ---- Counting rules, per row ----

    [Fact]
    public void ACompletedScanHasOneDoneRowPerTopLevelEntryAddingUpToTheTotals()
    {
        using var tree = new TempTree();
        Put(tree, "at-root.bin", 5000);
        tree.Folder(@"deep\a\b\c");
        Put(tree, @"deep\a\one.bin", 100);
        Put(tree, @"deep\a\b\c\two.bin", 200);
        tree.Folder(@"deep\empty");
        tree.Folder("empty");
        tree.Folder(@"wide\x");
        tree.Folder(@"wide\y");
        Put(tree, @"wide\y\three.bin", 300);

        Watched scan = Watch(tree.Root);

        Assert.Equal(ScanStatus.Completed, scan.Result.Status);
        TopLevelSnapshot final = scan.Final;
        Assert.Equal(["at-root.bin", "deep", "empty", "wide"], final.Rows.Select(r => r.Name));
        Assert.All(final.Rows, r => Assert.Equal(TopLevelState.Done, r.State));
        Assert.Equal(Row(TopLevelKind.File, TopLevelState.Done, 5000, 1, 0), Figures(final, "at-root.bin"));
        Assert.Equal(Row(TopLevelKind.Folder, TopLevelState.Done, 300, 2, 5), Figures(final, "deep"));
        Assert.Equal(Row(TopLevelKind.Folder, TopLevelState.Done, 0, 0, 1), Figures(final, "empty"));
        Assert.Equal(Row(TopLevelKind.Folder, TopLevelState.Done, 300, 1, 3), Figures(final, "wide"));
        Assert.Contains(scan.Snapshots, s => Named(s, "deep").State == TopLevelState.Reading);
    }

    /// <summary>
    /// Each byte is counted once: the first name the scan meets charges the file, the second counts as a
    /// file and adds nothing, so the file's bytes are in exactly one row.
    /// </summary>
    [Fact]
    public void AHardlinkAcrossTwoTopFoldersIsChargedOnceToTheRowOfTheFirstName()
    {
        using var tree = new TempTree();
        tree.Folder("1-folder");
        tree.Folder("2-folder");
        string first = Put(tree, @"1-folder\a.bin", 1 * MiB);
        TempTree.Hardlink(tree.PathOf(@"2-folder\b.bin"), first);

        Watched scan = Watch(tree.Root);

        Assert.Equal(ScanStatus.Completed, scan.Result.Status);
        TopLevelRow charged = Named(scan.Final, "1-folder"), repeat = Named(scan.Final, "2-folder");
        Assert.Equal(1 * MiB, charged.Bytes);
        Assert.True(charged.OnDisk >= 1 * MiB, $"1-folder takes {charged.OnDisk} bytes on disk");
        Assert.Equal((1L, 1L), (charged.Files, charged.Folders));
        Assert.Equal((0L, 0L, 1L, 1L), (repeat.Bytes, repeat.OnDisk, repeat.Files, repeat.Folders));
        Assert.Equal(scan.Result.Totals.Bytes, charged.Bytes + repeat.Bytes);
        Assert.Equal(1, scan.Result.Probes); // the first repeat's probe, which found the charged size current
    }

    /// <summary>
    /// HardlinkTests' stale shape across two rows: 1-folder's name is met first and charged at its stale
    /// 1 MiB, and 1-folder's row is Done. Listing 2-folder probes the current name and corrects the FIRST
    /// name's record in place. The 63 MiB belong to 1-folder's row, not to the listing they arrived in.
    /// This is the one change a Done row can still take.
    /// </summary>
    [Fact]
    public void AnInPlaceCorrectionOfAnEarlierNameIsCreditedToThatNamesRow()
    {
        using var tree = new TempTree();
        tree.Folder("1-folder");
        tree.Folder("2-folder");
        string current = Put(tree, @"2-folder\current.bin", 1 * MiB);
        TempTree.Hardlink(tree.PathOf(@"1-folder\stale.bin"), current);
        TempTree.Append(current, 63 * MiB);

        Watched scan = Watch(tree.Root, correctedRows: ["1-folder"]);

        Assert.Equal(ScanStatus.Completed, scan.Result.Status);
        Assert.Equal(1, scan.Result.Probes);
        int stale = Scans.Find(scan.Result, tree.PathOf(@"1-folder\stale.bin"));
        Assert.Equal(EntryFlags.SizeProbed, scan.Result.Store.Flags(stale));

        // Done at the stale size first, then corrected in place while 2-folder was listed.
        TopLevelSnapshot before = scan.Snapshots.First(s => Named(s, "1-folder").State == TopLevelState.Done);
        Assert.Equal(1 * MiB, Named(before, "1-folder").Bytes);
        Assert.Equal(TopLevelState.NotRead, Named(before, "2-folder").State);
        TopLevelRow charged = Named(scan.Final, "1-folder"), repeat = Named(scan.Final, "2-folder");
        Assert.Equal(64 * MiB, charged.Bytes);
        Assert.Equal(scan.Result.Store.OnDisk(stale), charged.OnDisk);
        Assert.Equal((1L, 1L), (charged.Files, charged.Folders));
        // Never credited to the listing the correction arrived in, in any snapshot.
        Assert.All(scan.Snapshots, s => Assert.Equal((0L, 0L), (Named(s, "2-folder").Bytes, Named(s, "2-folder").OnDisk)));
        Assert.Equal(1L, repeat.Files);
    }

    /// <summary>Developer Mode is on (Q-001): the symbolic links are created for real and never skipped.</summary>
    [Fact]
    public void TopLevelLinksAreNotFollowedRowsWithNothingCounted()
    {
        using var tree = new TempTree();
        string outside = tree.Outside("target");
        string outsideFile = TempTree.WriteFile(Path.Combine(outside, "big.bin"), 1 * MiB);
        tree.Folder("inside");
        Put(tree, @"inside\real.txt", 100);
        tree.Junction("j-junction", outside);
        tree.DirectorySymlink("d-folder-symlink", outside);
        tree.FileSymlink("f-file-symlink", outsideFile);

        Watched scan = Watch(tree.Root);

        Assert.Equal(ScanStatus.Completed, scan.Result.Status);
        foreach (string link in new[] { "j-junction", "d-folder-symlink", "f-file-symlink" })
        {
            Assert.All(scan.Snapshots, s => Assert.Equal(Row(TopLevelKind.Link, TopLevelState.NotFollowed, 0, 0, 0), Figures(s, link)));
            Assert.Equal(0, Named(scan.Final, link).OnDisk);
        }
        Assert.Equal(Row(TopLevelKind.Folder, TopLevelState.Done, 100, 1, 1), Figures(scan.Final, "inside"));
        Assert.Equal(3, scan.Result.Totals.Links);
    }

    /// <summary>
    /// Link folders are never pushed, so they never hold a row open: the pending count is raised where a
    /// folder is pushed, not from the Folders counter.
    /// </summary>
    [Fact]
    public void AFolderRowHoldingLinkFoldersIsDoneWhenTheScanCompletes()
    {
        using var tree = new TempTree();
        string outside = tree.Outside("target");
        TempTree.WriteFile(Path.Combine(outside, "big.bin"), 1 * MiB);
        tree.Folder(@"top\a\a1");
        tree.Folder(@"top\b");
        Put(tree, @"top\a\a1\one.bin", 10);
        Put(tree, @"top\b\two.bin", 20);
        tree.Junction(@"top\j-junction", outside);
        tree.DirectorySymlink(@"top\d-symlink", outside);
        tree.Junction(@"top\a\j-back", tree.Root); // a cycle, never entered
        Put(tree, "z-last.bin", 1);

        Watched scan = Watch(tree.Root);

        Assert.Equal(ScanStatus.Completed, scan.Result.Status);
        Assert.Equal(Row(TopLevelKind.Folder, TopLevelState.Done, 30, 2, 4), Figures(scan.Final, "top"));
        Assert.Contains(scan.Snapshots, s => Named(s, "top").State == TopLevelState.Reading);
        Assert.Equal(3, scan.Result.Totals.Links);
    }

    [Fact]
    public void AnUnreadableTopFolderIsAnUnreadableRowAndTheScanCompletes()
    {
        using var tree = new TempTree();
        tree.Folder("a-readable");
        Put(tree, @"a-readable\one.txt", 11);
        string denied = tree.Folder("b-denied");
        Put(tree, @"b-denied\not-seen.txt", 1000);
        tree.Folder("c-readable");
        Put(tree, @"c-readable\two.txt", 22);
        tree.Deny(denied, FileListDirectory);

        Watched scan = Watch(tree.Root);

        Assert.Equal(ScanStatus.Completed, scan.Result.Status);
        Assert.Equal(5, Assert.Single(scan.Result.Unreadable).Win32Error);
        Assert.Equal(Row(TopLevelKind.Folder, TopLevelState.Unreadable, 0, 0, 1), Figures(scan.Final, "b-denied"));
        Assert.Equal(0, Named(scan.Final, "b-denied").UnreadableBelow);
        Assert.Equal(Row(TopLevelKind.Folder, TopLevelState.Done, 11, 1, 1), Figures(scan.Final, "a-readable"));
        Assert.Equal(Row(TopLevelKind.Folder, TopLevelState.Done, 22, 1, 1), Figures(scan.Final, "c-readable"));
    }

    [Fact]
    public void AnUnreadableFolderDeeperInsideLetsItsRowFinishWithOneUnreadableBelow()
    {
        using var tree = new TempTree();
        tree.Folder(@"top\a");
        Put(tree, @"top\a\one.txt", 11);
        string denied = tree.Folder(@"top\b-denied");
        Put(tree, @"top\b-denied\not-seen.txt", 1000);
        tree.Folder(@"top\b-denied\under");
        tree.Folder(@"top\c");
        Put(tree, @"top\c\two.txt", 22);
        tree.Folder("z-after");
        tree.Deny(denied, FileListDirectory);

        Watched scan = Watch(tree.Root);

        Assert.Equal(ScanStatus.Completed, scan.Result.Status);
        TopLevelRow top = Named(scan.Final, "top");
        Assert.Equal(Row(TopLevelKind.Folder, TopLevelState.Done, 33, 2, 4), Figures(scan.Final, "top"));
        Assert.Equal(1, top.UnreadableBelow);
        Assert.Equal(TopLevelState.Done, Named(scan.Final, "z-after").State);
    }

    // ---- Cancel ----

    /// <summary>
    /// Cancel between listings, at the AfterFolder seam: after the root, f0 and f0\s0. f0 has started and
    /// has f0\s1 still to list, so it stopped partway; f1 to f9 were never listed.
    /// </summary>
    [Fact]
    public void CancelBetweenListingsLeavesAFinalSnapshotThatAddsUpToTheTotals()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 10; i++)
        {
            Put(tree, $@"f{i}\file.bin", 10);
            Put(tree, $@"f{i}\s0\a.bin", 1);
            Put(tree, $@"f{i}\s1\b.bin", 2);
        }
        using var cts = new CancellationTokenSource();

        Watched scan = Watch(tree.Root, cts.Token, (n, _) =>
        {
            if (n == 3)
                cts.Cancel();
        });

        Assert.Equal(ScanStatus.Cancelled, scan.Result.Status);
        TopLevelSnapshot final = scan.Final;
        Assert.True(final.Final);
        Assert.False(final.Completed);
        Assert.Equal(Row(TopLevelKind.Folder, TopLevelState.StoppedPartway, 11, 2, 3), Figures(final, "f0"));
        Assert.Single(final.Rows, r => r.State == TopLevelState.StoppedPartway);
        Assert.Equal(9, final.Rows.Count(r => r.State == TopLevelState.NotRead));
        Assert.Equal(new ScanTotals(Files: 2, Folders: 12, Links: 0, Bytes: 11, BytesOnDisk: final.OnDisk), scan.Result.Totals);
    }

    /// <summary>
    /// Cancel fed in at each guarded call of a real scan in turn, as CancelledFolderTests does, before the
    /// call or during it (SynchronousIoCancel then discards what it returned). That includes every buffer of
    /// d\big, a deeper listing that takes several, and the repeat's probe. A folder Cancel aborts commits
    /// nothing, so it changes no total and no row: the final snapshot holds exactly the figures of the last
    /// one published before the stop, and adds up to the totals. Cancelled in the root's own listing, the
    /// scan never had a row to show.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelAtAnyCallLeavesAFinalSnapshotThatAddsUpToTheTotals(bool duringTheCall)
    {
        using var tree = new TempTree();
        Put(tree, @"a\one.txt", 11);
        tree.Folder(@"d\big");
        for (int i = 0; i < 2000; i++)
            Put(tree, $@"d\big\{i:D4}{new string('n', 92)}.txt", 1); // 100-character names
        Put(tree, @"d\after.txt", 5);
        string charged = Put(tree, @"h1\x.bin", 1000);
        tree.Folder("h2");
        TempTree.Hardlink(tree.PathOf(@"h2\y.bin"), charged);
        tree.Folder("probes");
        tree.SparseFile(@"probes\large.bin", 64 * MiB);
        Put(tree, @"z\last.txt", 33);

        SynchronousIoCancel? guard = null;
        SynchronousIoCancel.Created = g => guard = g;
        Watched baseline;
        try
        {
            using var live = new CancellationTokenSource();
            baseline = Watch(tree.Root, live.Token);
        }
        finally
        {
            SynchronousIoCancel.Created = null;
        }
        Assert.Equal(ScanStatus.Completed, baseline.Result.Status);
        int calls = guard!.Calls;

        int stoppedInBig = 0;
        for (int k = 1; k <= calls; k++)
        {
            using var cts = new CancellationTokenSource();
            int cancelAt = k;
            SynchronousIoCancel.Created = g =>
            {
                if (duringTheCall)
                {
                    g.AfterCall = call =>
                    {
                        if (call != cancelAt)
                            return;
                        cts.Cancel();
                        Assert.True(SpinWait.SpinUntil(() => g.CancelIsIssued, TimeSpan.FromSeconds(5)), "the canceller never marked the call");
                    };
                }
                else
                {
                    g.BeforeCall = call =>
                    {
                        if (call == cancelAt)
                            cts.Cancel();
                    };
                }
            };
            Watched scan;
            try
            {
                scan = Watch(tree.Root, cts.Token);
            }
            finally
            {
                SynchronousIoCancel.Created = null;
            }

            string at = $"Cancel {(duringTheCall ? "during" : "before")} call {k} of {calls}";
            Assert.True(scan.Result.Status == ScanStatus.Cancelled, $"{at}: {scan.Result.Status}");
            if (scan.Result.Store.Count <= 1) // the root's own listing never committed
            {
                Assert.True(scan.Result.TopLevel is null, $"{at}: the root was never listed, yet there is a snapshot");
                continue;
            }
            TopLevelSnapshot final = scan.Final;
            Assert.True(scan.Snapshots.Count >= 2, $"{at}: no snapshot before the final one");
            TopLevelSnapshot last = scan.Snapshots[^2];
            for (int i = 0; i < final.Rows.Count; i++)
            {
                TopLevelRow was = last.Rows[i], now = final.Rows[i];
                Assert.True((was.Bytes, was.OnDisk, was.Files, was.Folders, was.UnreadableBelow) ==
                    (now.Bytes, now.OnDisk, now.Files, now.Folders, now.UnreadableBelow), $"{at}: row {now.Name} changed at the stop");
            }
            Assert.True(final.Rows.Count(r => r.State == TopLevelState.StoppedPartway) <= 1, $"{at}: more than one row stopped partway");
            if (Named(final, "d").State == TopLevelState.StoppedPartway && Named(final, "d").Files == 1)
                stoppedInBig++; // d listed (after.txt), d\big aborted or never reached
        }
        Assert.True(stoppedInBig > 0, "no cancel landed while d was being read");

        Scans.WaitForAbortedCloses();
    }

    // ---- Endings ----

    [Theory]
    [InlineData("already cancelled")]
    [InlineData("missing root")]
    [InlineData("a file as the root")]
    [InlineData("empty root")]
    public void NothingToShowIsNoSnapshotOrAnEmptyOne(string shape)
    {
        using var tree = new TempTree();
        using var cts = new CancellationTokenSource();
        string root = tree.Root;
        switch (shape)
        {
            case "already cancelled":
                Put(tree, "file.bin", 10);
                cts.Cancel();
                break;
            case "missing root":
                root = tree.PathOf("does-not-exist");
                break;
            case "a file as the root":
                root = Put(tree, "file.txt", 10);
                break;
        }

        Watched scan = Watch(root, cts.Token);

        if (shape == "empty root")
        {
            Assert.Equal(ScanStatus.Completed, scan.Result.Status);
            TopLevelSnapshot final = Assert.Single(scan.Snapshots, s => s.Final);
            Assert.True(final.Completed);
            Assert.Empty(final.Rows);
            Assert.Equal((0L, 0L, 0L, 0L), (final.Bytes, final.OnDisk, final.Files, final.Folders));
        }
        else
        {
            Assert.NotEqual(ScanStatus.Completed, scan.Result.Status);
            Assert.Null(scan.Result.TopLevel);
            Assert.Null(scan.Progress.TopLevel);
            Assert.Empty(scan.Snapshots);
        }
    }

    // ---- The snapshot handed across threads ----

    /// <summary>
    /// A snapshot held from early in the scan must read the same at the end, although rows it holds were
    /// replaced in every later one: immutability is the whole of the thread-safety argument.
    /// </summary>
    [Fact]
    public void ASnapshotHeldEarlyNeverChangesWhileTheScanGoesOn()
    {
        using var tree = new TempTree();
        for (int i = 0; i < 6; i++)
        {
            Put(tree, $@"f{i}\file.bin", 100 + i);
            Put(tree, $@"f{i}\sub\more.bin", 1000);
        }
        TopLevelSnapshot? held = null;
        List<(string, TopLevelKind, TopLevelState, long, long, long, long, long)>? copied = null;

        Watched scan = Watch(tree.Root, afterFolder: (n, progress) =>
        {
            if (n != 2)
                return; // the root and f0
            held = progress.TopLevel;
            copied = Values(held!);
        });

        Assert.Equal(ScanStatus.Completed, scan.Result.Status);
        Assert.NotNull(held);
        Assert.Equal(copied, Values(held!));
        Assert.False(held!.Final);
        // Not vacuous: every row but f0 changed after it was held, under new objects.
        TopLevelSnapshot final = scan.Final;
        for (int i = 1; i < final.Rows.Count; i++)
        {
            Assert.NotSame(held.Rows[i], final.Rows[i]);
            Assert.NotEqual(held.Rows[i].State, final.Rows[i].State);
        }
        // The rows cannot be changed through the list either.
        Assert.IsNotType<TopLevelRow[]>(held.Rows);
        var asList = Assert.IsAssignableFrom<IList<TopLevelRow>>(held.Rows);
        Assert.Throws<NotSupportedException>(() => asList[0] = final.Rows[0]);
    }

    // ---- The watch ----

    private sealed record Watched(ScanResult Result, ScanProgress Progress, List<TopLevelSnapshot> Snapshots)
    {
        public TopLevelSnapshot Final => Result.TopLevel ?? throw new Xunit.Sdk.XunitException("the scan has no final snapshot");
    }

    /// <summary>
    /// Scans with a snapshot on every change, keeps each one, checks each against the store as it is
    /// published, and then checks them all (<see cref="AssertSnapshotsHold"/>).
    /// </summary>
    /// <param name="correctedRows">Rows an in-place hardlink correction may change after they are Done.</param>
    private static Watched Watch(string root, CancellationToken ct = default, Action<int, ScanProgress>? afterFolder = null,
        IReadOnlyCollection<string>? correctedRows = null)
    {
        var progress = new ScanProgress();
        var snapshots = new List<TopLevelSnapshot>();
        var options = new ScanOptions
        {
            TopLevelInterval = TimeSpan.Zero,
            AfterFolder = n =>
            {
                // Every committed listing changes a row, so with no interval each one publishes.
                TopLevelSnapshot? now = progress.TopLevel;
                Assert.True(now is not null, $"no snapshot after listing {n}");
                Assert.True(snapshots.Count == 0 || !ReferenceEquals(snapshots[^1], now), $"listing {n} published nothing");
                snapshots.Add(now!);
                // Its sums are the scanner's running totals, as published beside it.
                Assert.True((now!.Bytes, now.OnDisk, now.Files, now.Folders) == (progress.Bytes, progress.BytesOnDisk, progress.Files, progress.Folders),
                    $"snapshot {now.Sequence} is not the running totals");
                afterFolder?.Invoke(n, progress);
            },
        };
        ScanResult result = Scanner.Scan(root, progress, ct, options);
        Scans.AssertChargedBytesAreTheTotals(result);
        if (result.TopLevel is { } last)
            snapshots.Add(last);
        AssertSnapshotsHold(result, progress, snapshots, correctedRows ?? []);
        return new Watched(result, progress, snapshots);
    }

    /// <summary>
    /// What every sequence of snapshots must hold. Each adds up to its own sums; rows keep their names and
    /// kinds; a folder row only moves NotRead, Reading, then Done or Unreadable; once Done, Unreadable or
    /// NotFollowed a row never changes again (M2), except the bytes of a row an in-place correction reaches;
    /// the final one is the result's and progress's, adds up to the totals, and matches the store row by row.
    /// A Done row's figures are therefore the store's at the end too.
    /// </summary>
    private static void AssertSnapshotsHold(ScanResult result, ScanProgress progress, List<TopLevelSnapshot> snapshots,
        IReadOnlyCollection<string> correctedRows)
    {
        if (result.TopLevel is null)
        {
            Assert.Empty(snapshots);
            Assert.Null(progress.TopLevel);
            return;
        }
        TopLevelSnapshot final = result.TopLevel;
        Assert.Same(final, progress.TopLevel);
        Assert.True(final.Final);
        Assert.Equal(result.Status == ScanStatus.Completed, final.Completed);
        Assert.Equal(result.Root, final.Root);
        Assert.Equal((result.Totals.Bytes, result.Totals.BytesOnDisk, result.Totals.Files, result.Totals.Folders),
            (final.Bytes, final.OnDisk, final.Files, final.Folders));
        AssertRowsAreTheStore(result.Store, final, $"final snapshot {final.Sequence}");

        var names = final.Rows.Select(r => (r.Name, r.Kind)).ToList();
        var settledAt = new int[final.Rows.Count];
        Array.Fill(settledAt, -1);
        for (int k = 0; k < snapshots.Count; k++)
        {
            TopLevelSnapshot s = snapshots[k];
            string at = $"snapshot {s.Sequence}";
            Assert.True(k == snapshots.Count - 1 ? s.Final : !s.Final && !s.Completed, $"{at}: Final is {s.Final}");
            Assert.True(k == 0 || s.Sequence > snapshots[k - 1].Sequence, $"{at}: the sequence did not rise");
            Assert.Equal(result.Root, s.Root);
            Assert.Equal(names, s.Rows.Select(r => (r.Name, r.Kind)).ToList());
            Assert.True((s.Rows.Sum(r => r.Bytes), s.Rows.Sum(r => r.OnDisk), s.Rows.Sum(r => r.Files), s.Rows.Sum(r => r.Folders)) ==
                (s.Bytes, s.OnDisk, s.Files, s.Folders), $"{at}: the rows do not add up to its sums");

            for (int i = 0; i < s.Rows.Count; i++)
            {
                TopLevelRow row = s.Rows[i];
                string which = $"{at}, row {row.Name}";
                switch (row.Kind)
                {
                    case TopLevelKind.File:
                        Assert.True(row.State == TopLevelState.Done, $"{which}: a file row is {row.State}");
                        break;
                    case TopLevelKind.Link:
                        Assert.True(row.State == TopLevelState.NotFollowed, $"{which}: a link row is {row.State}");
                        Assert.True((row.Bytes, row.OnDisk, row.Files, row.Folders) == (0, 0, 0, 0), $"{which}: a link row counted something");
                        break;
                    default:
                        if (k > 0)
                            AssertMovedOn(snapshots[k - 1].Rows[i].State, row.State, s.Final, which);
                        break;
                }

                if (settledAt[i] >= 0)
                {
                    TopLevelRow then = snapshots[settledAt[i]].Rows[i];
                    bool corrected = correctedRows.Contains(row.Name);
                    Assert.True(then.State == row.State && then.Files == row.Files && then.Folders == row.Folders &&
                        then.UnreadableBelow == row.UnreadableBelow && (corrected || (then.Bytes, then.OnDisk) == (row.Bytes, row.OnDisk)),
                        $"{which} changed after it was {then.State} in snapshot {snapshots[settledAt[i]].Sequence}");
                }
                else if (row.State is TopLevelState.Done or TopLevelState.Unreadable or TopLevelState.NotFollowed)
                {
                    settledAt[i] = k;
                }
            }
        }

        foreach (TopLevelRow row in final.Rows.Where(r => r.Kind == TopLevelKind.Folder))
        {
            if (final.Completed)
                Assert.True(row.State is TopLevelState.Done or TopLevelState.Unreadable, $"row {row.Name} is {row.State} at completion");
            else
                Assert.True(row.State != TopLevelState.Reading, $"row {row.Name} is still reading after the stop");
        }
    }

    // Folder rows only: NotRead, Reading, then Done or Unreadable; the final snapshot turns Reading into
    // StoppedPartway after Cancel.
    private static void AssertMovedOn(TopLevelState before, TopLevelState now, bool final, string which)
    {
        bool ok = (before, now) switch
        {
            (TopLevelState.Reading, TopLevelState.StoppedPartway) => final,
            (_, TopLevelState.StoppedPartway) => false,
            _ => Rank(now) >= Rank(before),
        };
        Assert.True(ok, $"{which} went from {before} to {now}");
    }

    private static int Rank(TopLevelState state) => state switch
    {
        TopLevelState.NotRead => 0,
        TopLevelState.Reading => 1,
        _ => 2,
    };

    /// <summary>Every row is what the store holds under its entry: charged bytes, files, folders and unreadable folders below.</summary>
    private static void AssertRowsAreTheStore(ScanStore store, TopLevelSnapshot snapshot, string at)
    {
        int first = store.FirstChild(0), count = store.ChildCount(0);
        Assert.True(count == snapshot.Rows.Count, $"{at}: {snapshot.Rows.Count} rows for {count} entries under the root");
        var stack = new Stack<int>();
        for (int i = 0; i < count; i++)
        {
            int top = first + i;
            long bytes = 0, onDisk = 0, files = 0, folders = 0, unreadable = 0;
            stack.Push(top);
            while (stack.Count > 0)
            {
                int e = stack.Pop();
                bytes += store.ChargedSize(e);
                onDisk += store.ChargedOnDisk(e);
                files += store.Kind(e) == EntryKind.File ? 1 : 0;
                folders += store.Kind(e) == EntryKind.Folder ? 1 : 0;
                unreadable += e != top && (store.Flags(e) & EntryFlags.Unreadable) != 0 ? 1 : 0;
                for (int c = 0; c < store.ChildCount(e); c++)
                    stack.Push(store.FirstChild(e) + c);
            }
            TopLevelRow row = snapshot.Rows[i];
            Assert.Equal(store.Name(top).ToString(), row.Name);
            Assert.True((bytes, onDisk, files, folders, unreadable) == (row.Bytes, row.OnDisk, row.Files, row.Folders, row.UnreadableBelow),
                $"{at}: row {row.Name} holds {(row.Bytes, row.OnDisk, row.Files, row.Folders, row.UnreadableBelow)}, the store {(bytes, onDisk, files, folders, unreadable)}");
        }
    }

    /// <summary>A file of <paramref name="length"/> bytes, its folders made first.</summary>
    private static string Put(TempTree tree, string relative, long length = 0)
    {
        tree.Folder(Path.GetDirectoryName(relative) ?? "");
        return tree.File(relative, length);
    }

    private static TopLevelRow Named(TopLevelSnapshot snapshot, string name) => snapshot.Rows.Single(r => r.Name == name);

    private static (TopLevelKind, TopLevelState, long Bytes, long Files, long Folders) Figures(TopLevelSnapshot snapshot, string name)
    {
        TopLevelRow row = Named(snapshot, name);
        return (row.Kind, row.State, row.Bytes, row.Files, row.Folders);
    }

    private static (TopLevelKind, TopLevelState, long Bytes, long Files, long Folders) Row(TopLevelKind kind, TopLevelState state,
        long bytes, long files, long folders) => (kind, state, bytes, files, folders);

    private static List<(string, TopLevelKind, TopLevelState, long, long, long, long, long)> Values(TopLevelSnapshot snapshot) =>
        [.. snapshot.Rows.Select(r => (r.Name, r.Kind, r.State, r.Bytes, r.OnDisk, r.Files, r.Folders, r.UnreadableBelow)),
            (snapshot.Root, default, default, snapshot.Sequence, snapshot.Bytes, snapshot.OnDisk, snapshot.Files, snapshot.Folders)];
}
