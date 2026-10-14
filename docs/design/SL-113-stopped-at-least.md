# SL-113: after Stop, a figure that is not final says "at least", up to the root

After a Stop the window marks every figure the store says could still change as "at least", and a row with no mark is final.
SL-109 marked the folders the scan never listed ("not read yet", shown by SL-110); SL-111 and SL-112 gave the store the answers for
the rest: `ScanStore.Unfinished(i)` (could a size check still pending, or something never listed, change this entry) and
`ScanStore.AtLeast(i)` (what it holds for certain). This slice is the first caller of them outside `ScanStore`, and the only one:
the stopped tree and the stopped list read them, behind one guard, after `Scan` has returned.

## What is marked

- **A row is marked exactly when `Unfinished(entry) && !NotListed(entry)`**, and the number drawn is `AtLeast(entry)`, never
  `RolledSize`, `RolledOnDisk` or the tracker's listing figures. Rolled figures are not a lower bound: a stale-large hardlink first
  name leaves a folder at 64 MiB against 1 MiB final (SL-112). A folder never listed keeps SL-110's "not read yet" and no figure.
- **Every ancestor up to the root is marked without the App walking anything.** `Unfinished` folds in `NotListedAtOrBelow` and the
  pending run, so a folder above an unread or unfinished one is itself unfinished. That holds only under depth-first registration,
  one `SetChildren` per whole block (SL-111, lesson #510); `StoppedTopLevelTests` checks it on every Stop (an unread or unfinished
  entry has an unfinished parent) and window case T-013 walks every marked row's ancestors to the root.
- **After a Stop in the listing almost everything reads "at least 0 bytes"** (no file's own size was read: 217,336 of about 488,000
  folders in the budget test). Ranking, shares and bars stay on the listed figures, as before; Q-292 (decided) leaves a listed figure
  beside the bound to a follow-up slice.
- **A Stop in the size checks** marks only the rows with a check still pending below them; the rest are final. The hook has no
  trigger inside the sizing pass, so this branch is covered by Core facts (`StoppedTopLevelTests`) and by L-004's header, not by a
  window case.

## The tree

`FolderTreeNode` draws `SizeText` and `OnDiskText` through the gate it already had (`HasFigures`). Behind it, a row whose session
`ReadsBounds` asks the store once, the first time a figure is drawn, `Unfinished(entry)`, and caches `AtLeast(entry)`. Opening a
folder reads nothing more than before: `EnsureChildren` builds its rows from `ChildrenLargestFirst` as it did, and only the rows on
screen ask. T-014 opens the 100,000-child folder in a stopped tree with marks drawn on screen: the cold open takes 77 ms with nine
marked rows realized (bar 500 ms); a completed scan's cold open is 122 ms (T-012).

The tree has no file-count column and gets none (Q-293): the count is marked where it is drawn, on the stopped list's figures
line. The Size and On disk cells are 102 DIP wide, not 80: `at least 1,023 bytes` is 101.26 DIP at this PC's 125% DPI
(spike 125: 80 clips it to `at least 1,000 b`). The margin is 0.74 DIP at 125% only; other scales are filed for a person.

## The list

`ScanResult.StoppedTopLevel()` returns the final snapshot with `TopLevelRow.AtLeast` set on each marked row, built by
`TopLevelTracker.WithAtLeast`: row i is entry `FirstChild(0) + i`. It returns the scanner's own snapshot when no row is marked and
a new object otherwise, because `ShowFinalTopLevel` redraws only a snapshot it has not shown, and a timer tick may already have shown
the unmarked final one. A marked row's line reads `at least X (at least Y on disk) in at least N files and at least M folders`; N is
the bound's, M and the unreadable count are the tracker row's (folder counts only grow). A row never read keeps today's second line
(`0 bytes (0 bytes on disk) in 0 files and 0 folders`, Q-276, whose wording is still open).

## One guard for both surfaces

`ScanResult.MarksAtLeast` is `Status == Cancelled && TopLevel is { Final: true, Completed: false } && Store.Count > 0`. The list reads
the store only under it, and the tree session is given it as `readsBounds` (`AtLeastSeamTests.TheTreeAsksTheStoreForABoundOnlyUnder-
TheListsGuard`). It is false for a completed scan, a root that could not be read, a refused scan and a store with no entries, which
`Unfinished` and `AtLeast` throw on (lesson #517). **Correction to the design's reasoning for criterion 6 (review m3):** a completed
scan shows "at least" nowhere because the guard is false, not because `PendingRun` is false once the watermark reaches `Count`:
`Unfinished` consults `NotListedAtOrBelow` first, so a completed store that still held an unread folder would mark that folder's
whole chain. No real completed scan has been observed to leave one (the whole-C: spike `completed-whole-c-nothing-unfinished` is
blocked on a person's yes); `StoppedTopLevelTests.ANoStoreToMarkIsReturnedAsItIsAndNeverRead` builds one by hand and shows the guard
keeps it unmarked. **Criterion 6 on a real drive rests on that unrun spike**, and until it runs the guard is what stands in front of it.

## The type that closes "the word on a rolled figure"

`AtLeastFigures` moved out of `ScanStore` into `src/FsAnalyser.Core/AtLeastFigures.cs`, namespace `FsAnalyser.Core` (so
`TopLevelRowText.cs` can name it without the `using` that 'the window never touches the scan store' would flag). It is a readonly
struct, not a record: an internal constructor, get-only properties, no `with`, no conversion either way. `TopLevelRow.AtLeast` is
`AtLeastFigures?`; the composers `TopLevelRowText.AtLeastSizeText` / `AtLeastOnDiskText` take it and nothing else. `default` is
constructible and reads (0, 0, 0), a true lower bound.

The class this closes is in `tests/FsAnalyser.Core.Tests/AtLeastSeamCorpus.json` and held by `AtLeastSeamTests`: eleven argument types a
caller can hold, none assignable or convertible to the composers' parameter; the exact set of constructors of the bound, setters of the
row's `AtLeast` and readers of `ScanStore.AtLeast`, read from the IL of every method of Core; and every carrier of the type in Core and
in the App's words. The corpus also keeps the 'before': attempt 1's text rule fired on six of eighteen spellings and let the other
twelve through. That rule was removed from `.taad/fitness.yaml` (it matched `AtLeastSize(`, which no longer exists). Two settled rules
remain: 'the at-least word is composed only in TopLevelRowText' and 'only the store constructs a lower bound', with plant rows
S88-S90 and P38-P40. The second is a text tripwire; the IL census is its closure. The App assembly itself is not scanned (no test
project loads it): `FolderTreeNode` is held by the first rule, by T-013 and by the mutants.

## The headers (M1, M2, Q-290)

- The stopped tree's header: `Not read yet: folders the stop came before. Sizes marked at least may grow; unmarked sizes are final.
  Order, shares and bars are by space on disk as listed, of what was read in <root>`. It promises only the sizes: the share column
  divides listed figures by listed figures, so no sentence calls shares final. The sentence about the sizes ends at character 101;
  at the default 880 px window 108 characters are drawn, and T-013 checks the characters drawn against the end of the sentence.
- The stopped list's two headers change only their size clause (Q-290): `Figures marked at least may grow; a row with no mark is final.`
  (a Stop in the listing) and `... not every file's own size was read: figures marked at least may grow, and a row with no mark is
  final.` (a Stop in the size checks). **At the default width these clauses are reachable through the header's tooltip, not drawn**:
  the lead and the explanation of what the Stop left unread already fill the line. No case claims they are drawn; L-001 and L-004
  prove the string, not the screen. `$StoppedListHeader` and `$StoppedSizingHeader` in `tests/window/WindowCases.ps1` are the only
  expected values of the existing cases that moved.

## What was measured

- Spike 126 (go): the ending's read (`NotListed`, `Unfinished`, `AtLeast` for every root child), cold in Release on a 4,880,000-entry
  store, at cutoffs 0, 50% and 97%: 3.06-10.21 ms, 0 bytes allocated, against a 250 ms bar. So it runs on the UI thread at the ending and
  `Scanner.cs` is untouched.
- T-013 (stopped at `m-mid\`, 218 rows read): 14 rows the store reports unfinished draw the bound, in full; 4 final rows draw the
  rolled figures with no word; the root and `m-mid` draw "at least"; `b-listed` (rolled 212 KB, bound 0) draws the bound; the stopped
  list's two marked rows equal the tree's cells; a completed scan of the same tree says "at least" nowhere.
- T-014: five opens of the 100,000-child folder in a stopped tree, 77.0 ms cold, 1.7-2.1 ms warm, 25 rows built, 9 drawing "at least".

## What validate runs

Each window case is its own command under the engine's 120 s limit (Q-222).

1. `pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 test FsAnalyser.slnx`
2. The rules harness, which prints the engine commit it measured (Q-027):
   `<engine python> eng/rules-harness/harness.py --engine-root <taad_v3>/backend --scenario all`
3. `pwsh -NoProfile -ExecutionPolicy Bypass -File tests/rules/RuleCases.ps1 -Case C-001` and `-Case C-003` (lesson #486: the settled
   entries are counted from the file, and two rules were added).
4. `tests/window/WindowCases.ps1 -Case` T-013, T-014, then L-001, L-004, T-010, T-011, T-012 (the existing stopped cases), then the
   cases that draw the tree or the main window's layout: T-001, T-002, T-003, T-004, T-005, T-006, T-007, T-008, T-009. T-003 reads
   its known VOID on this PC's 816 DIP work area (Q-279) and that is not a pass; its five open timings are recorded beside it.
5. `tests/window/WindowMutants.ps1 -Mutant All` (Q-114): the App diff touches `src/FsAnalyser.App/**`. It includes the four new
   mutants (`NoTreeAtLeast`, `NoListAtLeast`, `TreeAtLeastRolled`, `NarrowAtLeastCells`), each pinned to the FAIL lines it must cause.
6. The `tests/bugs` projects that compile App sources or drive the window: SL-063, SL-100, SL-105, SL-106 (lesson #466).

## Deviations and gaps

- Q-276 stays open: a list row never read keeps today's second line.
- `TopLevelSnapshot.WithRows` builds the copy through the snapshot's one constructor and not through a second, private copy
  constructor, which is what the design said: `tests/bugs/SL-100-ResultsListRefresh` finds the snapshot's constructor by reflection as
  the only non-public one (`.Single()`), and a second one failed 28 of its 29 facts (lesson #466: the consumer check has to include
  `tests/bugs/*`). The copy is the same snapshot with other rows.
- The header table in `docs/design/SL-112-unfinished-at-stop.md` (the two stopped list headers) and the tree header in
  `docs/design/SL-110-stopped-tree.md` record the strings as they were; this slice's strings replace them (Q-290).
- The size-check Stop has no window case (no trigger inside the sizing pass); Core facts cover it.
- 102 DIP is the measured minimum plus 0.74 DIP at 125% DPI; 100% and 150% DPI and larger text are for a person.
- A Stop-time failure of `StoppedTopLevel()` at the ending is not contained by Q-071's per-pass wrap (review m2, deferred): the throw
  is unreachable under the tracker's seeding, and a contained failure must clear the list and never fall back to the unmarked rows.
