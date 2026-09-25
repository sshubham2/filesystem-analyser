# SL-039 "Watch a scan and stop it": results that fill in, as built

PS-002 is done when, while a big drive is being read, the person can see how far it has got, cancel it,
and see results fill in as it goes. The counters and Cancel were already built (SL-020, SL-038). This slice
adds the results: a list of the scan root's direct children, largest first, filled in while the scan runs
and kept after it ends. This file records what the slice settled that the stack left open, and what the
build measured. Where it and the slice's design disagree, this file is right.

## What was settled

- **The window never reads the live store.** The scanner's own thread keeps one row per root child and
  publishes an immutable `TopLevelSnapshot` on `ScanProgress.TopLevel`; the last one is also
  `ScanResult.TopLevel`. Both no-go spikes (mid-scan-read-while-scanner-writes: +12 to 18% scan time at
  200k, 28% at 5M; mid-scan-shares-never-exceed-parent: a child larger than its parent in 20 of 20 runs)
  were about a second thread reading the store while the scanner writes. Nothing does here, so neither of
  their "needs first" items was built: the store is still not published before Finish, and there is no
  consistent rollup read path. Both belong to the folder-tree slice, which replaces this list with the
  tree's first level and deletes the "window never touches the scan store" rule on purpose.
- **One level.** The root's direct children: files, folders and links. No tree, no drill-down, no treemap,
  no shares or percentages (mid-scan and after Cancel a share would be of an incomplete whole). Whether one
  level closes "results fill in as it goes" is the person's question, still open at design.
- **Every change to the totals lands in exactly one row**, so every snapshot's rows add up to the scanner's
  running totals at that moment, and the final one to `ScanResult.Totals`:
  - The root's listing seeds the rows from each child's own charged size in the store
    (`TopLevelTracker.Seed`), after any probe the listing made.
  - Every later listing that commits is credited to its row: the change in the running totals since the
    last credit (`FolderExited`). The row is carried on the scanner's stack beside each pushed folder
    (`_pending`'s third field), set when a root child is pushed and inherited below it, so the lookup is
    O(1) and reads nothing from the store (review m5; the Parent walk the design had is not used).
  - A listing that Cancel aborts commits nothing and changes no total (Scanner.cs remarks, and line 139), so
    it is never credited. The design's "credit on every exit, including an interrupted one" was dropped
    (the design fork O-001 was settled that way): there is nothing to credit.
  - A probe that corrects an earlier name's record in place credits that name's row at once
    (`TopLevelTracker.Moved`, called from `Scanner.Correct`), whichever listing it happened in. It walks
    Parent from the corrected record up to its root child; that is the only Parent walk, and it runs once
    per first repeat of a hardlinked file.
- **Done comes from a pending count, decremented at one place.** A folder row starts at 1 (its own folder,
  pushed by the root listing); every folder that exits `FolderDone` under it adds the subfolders it pushed
  and takes away itself. There is one call per folder, `FolderExited(folder, row, unreadable, pushed)`,
  after the push loop, so a folder is counted down exactly once whether it was read or could not be opened
  (review M1), and a cancelled folder, which returns before it, is never counted as unreadable. Links are
  never pushed, so they never hold a row open. `Debug.Assert(pending >= 0)`.
  - States: File rows are Done; Link rows NotFollowed with nothing counted; a Folder row is NotRead until
    its own folder has exited its listing, then Reading while its pending count is above 0, then Done, or
    Unreadable when its own folder could not be opened or listed. The final snapshot after Cancel turns
    Reading into StoppedPartway; NotRead stays NotRead. `UnreadableBelow` counts unreadable folders under the
    row's own folder.
  - **Done is final except for one thing:** a later name of a hardlinked file elsewhere can still correct a
    Done row's bytes in place, because the first name met is charged at its listing and corrected when the
    repeat is probed. Only Bytes and OnDisk can change; `TopLevelTests` pins that every other Done,
    Unreadable or NotFollowed row never changes again (review M2) and exempts exactly this case.
- **Publishing.** When something changed and 250 ms have passed (`ScanOptions.TopLevelInterval`, zero in the
  tests), at the root listing, and always once more at the end. Unchanged rows keep their objects. The
  snapshot constructor copies the row array and exposes it through `Array.AsReadOnly`, which cannot be cast
  back to an array (review M3), so a row the tracker replaces later never changes a snapshot already
  handed out. The tracker publishes in listing order; the window sorts.
- **The final snapshot cannot be forgotten.** `ScanResult.TopLevel` is `required`, and `Run.Finish`, the only
  place a `ScanResult` is built, calls `TopLevelTracker.Finish` (review m1). It is null only when the root's
  listing never committed: a root that could not be read, or a Cancel before or during the root listing,
  which reads the root whole before appending anything, so nothing was counted either (review m2, overridden).
- **A tracker exception is a bug and fails the scan** (review m3, overridden): the window's Scan failed
  ending then clears the list.

## The window

- `TopLevelList`, a ListBox with virtualization on (`IsVirtualizing`, `VirtualizationMode=Recycling`,
  `CanContentScroll`), under a header `TopLevelHeader`, in its own panel below the scanning and outcome
  panels. The design put it under the counters inside the scanning panel; that panel collapses at the
  ending, and the list has to stay after it, so it moved. The unreadable list inside the outcome panel has
  a MaxHeight of 200. Since SL-040 (O-025) the outcome panel and this one are the two rows of the window's
  last child, a Grid: each row is star-sized only while its panel is shown, so while a scan runs this
  panel gets all the height left, and after an ending the two share it 2 to 1, with the outcome never
  below 316 px (328 px while its unreadable folders are open). The list always has a bounded height, which
  is what lets it virtualize. That floor fits only from about 580 px of window height up, so the window's
  MinHeight is 600, its default (SL-040, O-027); at 880x600 it leaves this list its header and part of a
  row after an ending. SL-040's B-007 measures it at the default size and at the window's MinHeight.
- Two TextBlocks a row, formatted by a converter (`TopLevelRowText`) so only the rows on screen are ever put
  into words: the name with its state ("reading...", "not read yet", "stopped partway", "could not be
  opened", "link, not followed"), and the size, on-disk size, files and folders inside, and "N folders
  inside could not be opened" when any.
- Header: "Largest at the top of <root>, so far. Rows marked reading are still growing." while scanning;
  "What was read before the stop. Rows marked not read yet or stopped partway are short." after Cancel;
  "Largest at the top of <root>" after Completed, or "<root> is empty." for an empty root.
- The refresh rule. On each 250 ms tick, after the counters: nothing if the latest snapshot is the one shown
  (compared by reference, review m4; each scan has its own ScanProgress, created in StartScan). Otherwise the
  rows are copied, sorted by Bytes descending then name (ordinal, ignoring case) on the UI thread, and replace
  the list's items whole; the scroll offset is put back at Loaded priority, and the time from the start of
  the handler to that point is the refresh's. A tick after a refresh that took over 100 ms skips its refresh
  once. The ending always shows the final snapshot, whatever the skip state; with no final snapshot (root
  never listed, Could not read, Scan failed) it clears the list. Starting a scan clears it at once.
- `--report` (schema 1, additive) gains `window.TopLevelHeader` and a `topLevel` object counted from the
  rows the list holds: rows, reading, notRead, stoppedPartway, unreadable, bytes, onDisk, files, and whether
  they came from the final snapshot. No names and no paths (Q-016). It is absent when the list shows nothing.
  eng/read-window.ps1 prints the JSON as written and parses nothing, so it needed no change.

## How it is tested

- `tests/FsAnalyser.Core.Tests/TopLevelTests.cs` (default filter, real NTFS trees, no timing): every scan
  runs with `TopLevelInterval` zero and keeps each snapshot from `AfterFolder`. Each must add up to its own
  sums, which must be the running totals; rows keep their names and kinds; folder rows only move forward;
  settled rows never change (M2); the final one is the result's and progress's, adds up to the totals, and
  every row equals what the store holds under its entry. Shapes: a completed tree; a hardlink across two
  top folders; the stale-size correction across two rows; a top-level junction, folder symlink and file
  symlink (Developer Mode, never skipped, Q-001); a folder row holding link folders; an unreadable top
  folder; an unreadable folder deeper inside; Cancel after the third listing; Cancel before and during every
  guarded call of a scan in turn (as CancelledFolderTests does), including every buffer of a deeper listing
  that needs several and the repeat's probe; nothing to show (already cancelled, missing root, a file as the
  root, an empty root); and a snapshot held early that must read the same at the end. Four tracker mutants
  (Moved a no-op, the snapshot aliasing the tracker's array, an unreadable folder decremented twice, the
  final keeping Reading) each failed at least one of them.
- `tests/window/WindowCases.ps1`: C-002 now checks the list at the ending against the tree. L-001 reads the
  list mid-scan through the hook's new watch mode (a folder row with bytes while another is not read and
  Cancel is still pressable), presses Cancel, and checks the final rows against core and that no top-level
  name is in the report. L-002 does the same over 3,000 top-level folders with every refresh slowed to
  150 ms by the hook (`FSA_HOOK_SLOW_REFRESH_MS`), presses Cancel only once a tick has skipped and the next
  skip is armed again, and checks that only a screenful of containers was realized and that the ending
  still showed the final snapshot; a mutant whose ending obeyed the skip failed it on final, reading, bytes
  and files. L-003 is the endings with nothing to show. The case where the scan faults after rows were
  shown has no window check: in report mode the app's fault handler ends it with exit 70 and no report
  (C-012), and nothing in the shipped exe can make the scan itself throw.
- M-001, named only: the list at 100,000 rows, measured by the hook's measure mode (below).

## Measured at build

On this PC (Ryzen 9 8945H, 16 threads, shared with other work; CPU load 36 to 50% around the runs), with the
repo-local SDK 10.0.401, Release.

- **Scan time, trunk Core against this slice's Core** (limit 15%, from spike 1). The same console program
  built against each Core (trunk's taken with `git archive` at 11f7ad9), each run a separate process, the
  scan alone timed, one warm-up pair discarded.
  - Nested tree, 8,191 folders 12 deep with 23 empty files in each (196,583 entries under the root), 5 runs
    each interleaved ABBA: trunk median **410.2 ms** (385.8, 421.1, 414.8, 401.4, 410.2), this slice
    **408.1 ms** (442.2, 416.0, 408.1, 403.6, 406.6): **-0.5%**, inside the noise.
  - Flat root of 100,000 empty folders, ABBA once: trunk 7,569.9 and 6,807.5 ms, this slice 7,083.7 and
    6,915.2 ms (warm-up pair 7,437.8 and 7,985.4, discarded). Inside the noise either way.
  - The work added per listing is a few additions, one timestamp read and one stack field; publishing is one
    array copy at most every 250 ms. If a later measurement misses the limit, the cost to look at is per
    listing (review m5), not the publish cadence.
- **The list at 100,000 rows (review m7, M-001).** Spike 2 passed a TreeView stand-in at 100k with tick p95
  17.6 ms and Input-probe p95 30 ms against limits of 100 and 200 ms. Re-measured on the ListBox as built,
  over a scan of a root with 100,000 empty folders, the window's own refresh time (handler, sort and layout
  to the Loaded-priority marker) and an Input-priority probe every 50 ms from the hook:

  | Run | Refreshes | Refresh p95 | Refresh max | Probes | Input p95 | Input max | Skipped | Containers |
  |---|---|---|---|---|---|---|---|---|
  | 1 | 31 | 25.4 ms | 54.7 ms | 154 | 13.0 ms | 72.8 ms | 0 | 6 |
  | 2 | 25 | 31.8 ms | 45.8 ms | 129 | 8.2 ms | 88.9 ms | 0 | (0, void) |
  | 3 | 32 | 33.1 ms | 47.8 ms | 162 | 8.7 ms | 88.6 ms | 0 | 5 |
  | 4 | 31 | 46.5 ms | 64.3 ms | 158 | 15.7 ms | 73.5 ms | 0 | 5 |

  Both limits held in every run, no refresh came near 1 s, no tick needed to skip, and 5 or 6 of 100,000
  rows had containers. Runs 1 and 2 counted containers at the moment the scan returned, which can fall
  between a refresh's reset of the items and its layout (run 2 read 0); from run 3 the hook counts them after
  each refresh has been laid out and logs the most it saw. Timing on a shared machine: these are
  measurements, and M-001 is not part of `-Case All`.

## Not done, and why

- The spikes' two "needs first" items (publish the store before Finish with an ordered child-range publish;
  a consistent rollup read path) are not done and are not needed here. The tree slice owns them.
- Drill-down into a row is not built: it needs both of them.
- Cancel on a share that stopped answering is still unmeasured by any stage: N-001 to N-004 and
  StalledShareTests are the person's run (SL-020-scanner.md). This slice changes MainWindow.xaml(.cs),
  `src/FsAnalyser.Core/Scanning/**`, the hook and WindowCases.ps1, all on the staleness list, so the
  person's last network pass is stale from this slice on.
