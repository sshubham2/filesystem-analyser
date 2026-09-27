# SL-063 "What a Done row promises": design as built

A top-level row that is Done and whose figures a correction moved after it settled now reads `Name  (revised)` in
the results list. This amends SL-063's design with the review findings the person accepted (M2, M3, m2, m3, m4), the
reasons recorded on the two they overrode (M1, M4), and what the build measured. Where this file and the design
disagree, this file is right.

## The promise

**Done means every folder under the row has been listed and measured. It does not mean the figures are final.**
Until the scan ends they can still move: once every folder is listed, the SL-056 probe pass reads each file's own
size and corrects in place any file whose listing was stale. A Done row whose figures moved that way says so, and
keeps saying so for the rest of the scan. Once the scan ends, Completed or Cancelled, nothing changes again (spike
terminal-state-rows-never-change: 0 changes across 48 real C:\Windows scans).

The design said a Done row can move "only through a hardlink recharge". The code says any probe correction can move
it: `Scanner.Correct` credits a correction whenever the handle's sizes differ from the listing's. That covers a
hardlink name that was not written through (validate's C:\Windows case) and also a file another process was writing
(U-018). The mark covers both, because it is derived from the figures and not from the cause. The doc comment on
`TopLevelState.Done` says this.

## How the mark is derived (review M2)

- `TopLevelTracker.Row` keeps `SettledBytes` and `SettledOnDisk`. They are captured when the row's pending count
  reaches 0, which is in `FolderExited` for a folder row and in `Seed` for a file row, and the count never rises
  again after that.
- `Build`, the only place a `TopLevelRow` is constructed, sets the row's `Revised` latch when the state is Done and
  (Bytes, OnDisk) differ from the settled pair. It publishes the latch as `TopLevelRow.Revised`, an init-only bool
  that defaults to false.
- Nothing in `Moved` marks anything. Every writer of a row's figures is therefore covered, including the planned MFT
  reader and any later on-disk correction, and none of them has to remember to set the mark.
- What follows from this:
  - A zero change leaves the row unmarked.
  - A correction on a row that is still Reading becomes part of the figure the row settles at, so it is never
    marked.
  - A charge that moves between two Done rows marks both of them. `Moved` credits one row per call, so this takes
    two calls.
  - A change and its reversal with no publish between them leaves the row unmarked, because nobody could have seen
    the moved figure. Once a publish has happened in between, the mark stays.
  - A correction that lands in the same 250 ms interval in which the row first became Done is still marked (case 9,
    not material). That over-marking is accepted.

## Order and threads (the design's two must-verify points)

- **The order is structural, not timing (M1, overridden with this reason).** `Scanner.Execute` sizes files only after
  the listing loop has emptied `_pending` (Scanner.cs, `while (_pending.Count > 0)` and then `SizeFiles()`). Every
  folder row is therefore at pending 0 before any correction can land. No `AfterFolder` fallback is needed, and no
  `InternalsVisibleTo` for the bug assembly. "Charging is deterministic" is dropped as the reason the acceptance is
  reproducible: that spike measured final sizes, not order.
  - Test: `DoneRowFinalityTests.TheCorrectionLandsAfterTheRowSettledInEveryRun` built 20 fresh trees, and all 20
    runs came out Done and Revised.
  - Each stale tree reproduces only once. The first scan's probe opens the stale name, and NTFS then refreshes that
    name's directory entry. Measured at build: `dir` showed stale.bin at 1,048,576 bytes before a scan and 67,108,864
    after it, and a second scan of the same tree corrected nothing. So every run needs a fresh tree.
- **No lock (M4, overridden).** The tracker takes none. It is single-threaded by construction: see its remarks, and
  `Scanner.Correct` and `Credit` call `_owner.Check()`. The design's "under the lock Moved already uses" was wrong.
  Its precondition, that whoever applies the revision knows the row was already Done, holds on that one thread.

## Why nothing is marked in advance (key decision 5, restated for m4)

These are the reasons:
- The acceptance asks for after-the-fact marking.
- The earlier critique's fix, "make Done immutable", is recorded as impossible.
- Since SL-056 every correction lands after the walk has ended, so holding Done back would hold every row until the
  probe pass finishes.

The 3.0% at-risk figure from spike at-risk-rows-minority-on-full-c is folder-level (932,531 Done folders on C:\), not
top-level rows. The Microsoft hard-links page supports only that a directory entry's size is updated at the link the
change was made through. It is no evidence that prediction is impossible, and it is not relied on here.

## Where the mark shows

- The mark is worded only in `TopLevelRowText.TitleOf`: `TopLevelState.Done when row.Revised => "Name  (revised)"`,
  placed before the fall-through. The wording uses the two spaces the other states' suffixes already use.
- The window fills `TopLevelList` in two ways: on a tick (`RefreshTopLevel`, then `ShowTopLevel`) and at the ending
  (`ShowFinalTopLevel`, then `ShowTopLevel`). The list has one template, and its title binds `RowTitle`.
  `DoneRowFinalityTests.TheResultsListShowsTheMarkOnATickAndAtTheEnding` reads the realised rows of both paths in
  real `MainWindow`s.
- The mark is on screen while a scan runs, which in practice means during the probe pass, when every row is already
  Done, and after a stop. A completed scan replaces the list with the folder tree (SL-044). The tree shows final
  figures and no state, so it carries no mark. Whether it should is the design's open question, and it stays open.
- The schema-1 report is untouched (spike report-captures-mid-scan-done-row, no-go).

## Rules (M3, m2, m3)

The three declared rules are replaced by two settled entries in .taad/fitness.yaml:

- **Only TopLevelTracker.cs sets `Revised`.** Pattern: `\bRevised\s*=(?![=>])` over src/FsAnalyser.*/**/*.cs.
  - The lookahead is a deviation from the decided `\bRevised\s*=`. That pattern matches the converter's own
    `when row.Revised =>`, and `=>` and `==` only read the flag.
- **In the App, only TopLevelRowText.cs reads `.Revised`.**

Plants in a scratch tree, run through the engine's Fitness, showed that:
- each rule fires on a set or a read outside its exception;
- neither fires on the converter or the tracker;
- `== true` trips only the reader rule.

## Cases and their tests

| Case | Test |
|---|---|
| C-001 non-zero correction of a Done row | `TopLevelTests.ANonZeroCorrectionOfADoneRowMarksItRevisedForTheRestOfTheScan` (both endings, and the latch) |
| C-002 correction of a row still Reading | `TopLevelTests.ACorrectionOfARowStillReadingDoesNotMarkIt` |
| C-003 zero-byte correction of a Done row | `TopLevelTests.ACorrectionOfADoneRowMarksItOnlyWhenAFigureMoved` (0/0 unmarked; either figure alone marks) |
| C-004 charge moving between two Done rows | `TopLevelTests.AChargeMovingBetweenTwoDoneRowsMarksBoth` |
| C-005 a Done row nothing corrected | `DoneRowFinalityTests.ADoneRowNothingCorrectedStillReadsItsBareName` |
| C-006 the acceptance | `DoneRowFinalityTests.ADoneRowCorrectedInPlaceAfterItSettledReadsDifferentlyFromItsBareName` (unchanged) |
| C-007 Revised with every other state | `DoneRowFinalityTests.TheMarkShowsOnlyOnADoneRow` |
| C-008 tick and ending both show it | `DoneRowFinalityTests.TheResultsListShowsTheMarkOnATickAndAtTheEnding` |
| C-010 the acceptance is not a race | `DoneRowFinalityTests.TheCorrectionLandsAfterTheRowSettledInEveryRun` |

`TopLevelTests`' shared watcher also checks every real scan in that class against four rules:
- a row is marked only while it is Done;
- a Done figure that moved is marked;
- a mark never goes away;
- a row that no correction reaches is never marked.

## Measured at build

- **Real C:\Windows, through the public `Scanner.Scan` and the window's own converter, polled every 250 ms:**
  Completed in 111 s. Of 126 rows, 119 were Done. None was revised, and none moved after a polled Done, which agrees
  with spike post-done-change-only-from-hardlinks (0/126).
- **A fresh repro tree:** 1-folder read `1-folder  (revised)` and 2-folder read `2-folder`.
- **The published exe in report mode on a repro tree:** exit 0, Completed, and the folder tree shown in the list's
  place.
