# SL-112: after Stop, the store says which folders a pending size check could still change

A stopped scan's store can say, for any entry, whether a size check that had not been applied when Stop took effect could
still change its figures, and what the entry holds for certain: `ScanStore.SizeCheckPending(int)`,
`ScanStore.Unfinished(int)` and `ScanStore.AtLeast(int)`. SL-109 marked the folders never listed and SL-111 answered that for a
whole subtree. This slice does the same for the file sizes the scan had not yet read from the files themselves. It stores one
`int` and no per-entry mark.

## What is traced, and by what

The scan lists every folder first and then reads each file's own size (the size checks, `Scanner.SizeFiles`): a file of a
sized-from-the-file kind is probed and `CorrectSizes` replaces its listing size with the probed one. The listing's size can be
above the file's own as well as below it (a hardlink's first name lists stale in either direction), so a Stop before a check
leaves a figure that may be wrong either way.

The checks are applied in **ascending entry order**, and a batch is applied **whole or not at all**: `SizeBatch` applies nothing
from a batch that comes back `Cancelled`, even readings that finished. So when Stop takes effect the checks still pending are
exactly the entries of a sized-from-the-file kind at or after one index, the first entry of the batch that was cancelled, or
index 0 when the Stop landed in the listing, where `SizeFiles` never ran (spike `stop-pending-checks-are-one-cutoff`: 0
violations in 2,588 real Stops). `ScanStore.EndSizeChecks(firstUnchecked)` records that index. It is written once, by
`Scanner.Run.Finish`, as the last write to the store, and read by nothing else.

- `Count` after a scan that ran to the end, **including one where files vanished before their check** (they carry neither
  `SizeProbed` nor `ProbeFailed`, so a rule keyed on flags would mark their folders: the spike measured 2 folders a run). A scan
  with no file due returns `Completed` without running a batch, and the watermark is still `Count`.
- The first entry of the cancelled batch after a Stop in the size checks.
- 0 after a Stop in the listing, a refused root and an unreadable root: every sized-from-the-file entry is pending, which is
  true because `SizeFiles` never ran. 0 is accepted on an empty store (a Stop in calls 1 and 2 of the root's handling).
- A store no scan has declared reads 0, so a wrong default over-marks and never calls a folder final that was not.

The rule "sized from the file itself" (a file, not a later hardlink name, not cloud-only) has one copy,
`EntryRules.ChecksOwnSize`, which `SizeProbe.IsEligible` calls and the store reads. See the deviations below for why it is not
on `ScanStore`.

## What a Stop means here: `Scanner.Scan` has returned

Writes between the Cancel request and the return belong to the stopped store, and the marks are computed from it: `FolderDone`
commits a listing that returned cleanly after Cancel (error 0 is `Read` whatever the cancel state), and `Finish` writes the
watermark. After the return nothing writes a per-entry column or the watermark. A probe worker abandoned after a Stop writes
only its batch's readings array, which is never applied (`ProbePass`). `ScanStore.VolumeUsed` is outside this: it is a
whole-drive figure completed on its own thread, and no entry's figure or mark.

`UnfinishedTests.NothingChangesOnceTheScanHasReturned` dumps every figure and mark when `Scan` returns with every probe worker
stuck in a call nothing interrupts, lets the workers go and checks the dump is the same. It also pins the folder whose listing
returned cleanly after Cancel: it reads listed, and its files read pending.

## The three reads

- `SizeCheckPending(i)`: a file of a sized-from-the-file kind at or after the watermark. False for folders, links, later
  names, cloud-only files, and every entry after a `Completed` scan.
- `Unfinished(i)`: for a folder, `NotListedAtOrBelow(i)` or a pending file anywhere below it; for a file, `SizeCheckPending`;
  false for a link. **A pending hardlink charge is the first name's own pending check**: names are charged at append
  (`HardlinkDuplicate` is fixed then) and `CorrectSizes` posts nothing for a later name, so no charge moves between names after
  append, and a stopped listing is a prefix of the full one (U-190), so the first name is the same entry in a stopped and a
  full run. The sweep pins this: every stopped store charges the same name as the scans that ran to the end. A folder whose
  pending check would **lower** it (a stale-large first name) is marked by the same read.
- `AtLeast(i)`: `(RolledSize, RolledOnDisk, FilesBelow)` less the listing size and on-disk size of every pending file below.
  Pending files still count as files and contribute 0 bytes. It equals the rolled figures whenever `Unfinished` is false.

Marking alone cannot meet "at least": a stale-large first name left its folder at 64 MiB against 1 MiB final (survey runs A and
B). The stored rolled totals are **not rewritten**: rewriting at Stop costs pending files times depth (1.7 s at 80 levels), cannot
be undone, and would make `RolledSize` disagree with the per-file recount that `NotListedTests` asserts. `Totals`, the
top-level rows, `RolledSize` and `ChargedSize` therefore stay listing-based, and which figure a surface shows for an unfinished
folder is the App slice's choice (SC-393 lineage).

Both folder reads walk a folder's descendants as one contiguous index range from its `FirstChild` to the end of the last block
on its chain of last folders-with-children (a block can straddle two pages, so each entry's own page is read), from the later of
that start and the watermark. They allocate nothing and write nothing.

## It holds only while the traversal stays depth-first

`Unfinished` and `AtLeast` rest on the same registration as `NotListedAtOrBelow`: one writing thread, folders listed in
preorder, each folder's children appended as one block and registered by one `SetChildren`. See
`docs/design/SL-111-not-listed-below.md`, "It holds only while the traversal stays depth-first" (lines 55-62), whose two options
apply unchanged to any slice that changes registration, and a comment at `FolderDone`'s last-first push loop in `Scanner.cs`
says so (lesson #510). They also rest on the **apply order** of the size checks above: a parallel, out-of-order or partial-batch
apply (pause/resume, U-197) needs a per-entry or per-range structure and a 5M memory test in place of the watermark. In a
queue (BFS) order the range read gave 32,068 wrong figures in 7.9M (spike `at-least-figures-5m-within-budget`).

## The header

The results list's header (`MainWindow.ListHeader`) has three arms:

| Where the Stop landed | Header |
|---|---|
| the scan is running | unchanged: `Largest at the top of <root>, so far. Rows marked reading are still growing.` |
| the size checks (`TopLevelSnapshot.StoppedInSizeChecks`) | `What was read before the stop. Every folder was listed, but not every file's own size was read, so any row, unmarked ones included, may be off, on disk especially.` |
| the listing (or anything else that stops) | `What was read before the stop. Rows marked not read yet or stopped partway leave out what the scan did not reach. No sizes have been checked yet, so any row's size may be off, on disk especially.` |

`StoppedInSizeChecks` is an `init` property of the final snapshot, true only when the scan was `Cancelled` and a size-check batch
was cancelled (Q-111: Core passes it on in the result). 'Unmarked' is how a Done row reads (a bare name, `TopLevelRowText.TitleOf`).
This settles the derived criterion (SC-082) by its second option: the header names the Done rows and the on-disk size.

Three decisions meet here. **Q-111** (human) limits the new words to a Stop in the size checks. **Q-283** (model, owner away)
extends them to a Stop in the listing, because survey run A measured a Done row above its final figure there too. **Q-160**
(model) asked that Done rows' on-disk sizes be called short, assuming file sizes on Done rows were otherwise complete. The
measurements say neither "short" nor "complete": 3-shrink stood at 64 MiB logical and on disk against 1 MiB final, and the
un-subtracted listed figure was above the reference in 20,998 unfinished folders of the at-least sweep. Review finding m1 therefore
keeps Q-283's departure from Q-111 but takes the 'may be off, on disk especially' wording, and **drops the clause that marked rows
'are short'**; the words 'at least' can return once the App shows `AtLeast` figures. If the owner wanted Q-111 word for word, going
back undoes both. The stopped tree's header is not changed here.

## Prior art

A backup with late links still to set is not complete (storeBackup, node74). The cited page also says an interrupted backup is
"simply broken and also finished in this sense", so nothing here rests on how an interrupted one is described; node75 says the
last (unfinished) backup is not a full backup.

## Deviations from the design

- **The predicate is `EntryRules.ChecksOwnSize`, not `ScanStore.ChecksOwnSize`.** The settled rule "Core does not publish a live
  ScanStore before the scan ends" allows only a few files to name `ScanStore`, and `SizeProbe.cs` is not one: with
  `SizeProbe.IsEligible` calling `ScanStore.ChecksOwnSize` the rules harness's clean baseline was not clean (a hit at
  `SizeProbe.cs:28`). A neutral internal class in `Store/EntryRules.cs` names neither side, which is the other home the design
  listed as a taste choice, and no fitness rule was widened.
- **Review m2, m3, m7 and m8 were waived** (SC-407 to SC-410): the watermark is recorded on the cancelled branch of `SizeBatch`
  and not advanced per commit; the store is not sealed after `EndSizeChecks`; `L-004` has no sweep of orphaned roots from a killed
  run; nor was a separate Core Fact for the flag built, though `Check` and `CompletedMarksNothing` assert
  `StoppedInSizeChecks` on every Stop and every completed scan anyway.

## Tests and what each costs

All in `tests/FsAnalyser.Core.Tests`, none of the existing files edited (`git diff --diff-filter=MD 4df2b1c` over the folder is
empty). The temp tree is built once for the class (`TempTree.Hardlink` spawns a cmd.exe per link); a group is made stale again
before every scan, the references included, by `SetLength` through its names. Each case command was run once with the build:

| Case | Test | Time |
|---|---|---|
| stops in the listing | `UnfinishedTests.StopsInTheListingMarkWhatAPendingCheckCouldChange` | 8 s |
| stops in the size checks, 1 and 4 workers | `...StopsInTheSizeChecksMarkWhatAPendingCheckCouldChange` | 10 s |
| the stuck workers and the commit after Cancel | `...NothingChangesOnceTheScanHasReturned` | 6 s |
| a scan that runs to the end | `...AScanThatRunsToTheEndMarksNothing` | 5 s |
| every scanner-shaped store | `...TheReadAnswersAsAParentWalkOnEveryScannerShapedStore` | 6 s |
| 5M entries | `UnfinishedBudgetTests.AStoreStoppedWithChecksPendingFitsTheBudget` | 14 s |
| the header, Stop in the size checks | `tests/window/WindowCases.ps1 -Case L-004` | 30 s |
| the header, Stop in the listing | `tests/window/WindowCases.ps1 -Case L-001` | 38 s with a publish |

`Tally.Bound` (15 s) is asked at every Stop, because a Fact that made its links again before every scan took 24 s against 1.6
to 2.6 s. The 5M rows measured, Debug: stopped at the unlisted peak (watermark 0), retained 637.3 MiB and sampled peak 653.7 MiB
against 800, both reads of every one of 4,880,000 entries 779 ms and the 100,000-child folder's rows 29 ms, 0 bytes allocated;
listed to the end with checks 97% done, retained 637.1 MiB, peak 653.5 MiB, 435 ms and 6 ms. The sweeps compare every folder of
every stopped store with scans of the unchanged tree that ran to the end: final folders equal them, unfinished folders' at-least
figures are no greater, and the teeth rows (a stale-large first name whose rolled figure is above the reference) occur.

The window cases need the published exe and the hook; `WindowMutants.ps1 -Mutant All` is a required validate check for this
slice (Q-114), since it touches `src/FsAnalyser.App/**`. The network pass (Cancel on a stalled share) stays unmeasured and is a
person's run (lesson #136); this slice adds one write to the Stop path and claims no cancel-safety.

## The fitness rule

`only the scan's ending writes the size-check watermark` (`.taad/fitness.yaml`, settled, no xUnit twin) allows the identifier
`EndSizeChecks` only in `ScanStore.cs` and `Scanner.cs`. Plant rows S87 (a second caller under `Core/Analysis`, must fire) and
P37 (the call in `Scanner.cs`, must stay quiet) are in `eng/rules-harness/plants.json`; the harness passed 169 of 169 rows, and
RuleCases C-001 and C-003 held. The rule is a tripwire for one spelling, not a claim to close a class (lesson #486).
