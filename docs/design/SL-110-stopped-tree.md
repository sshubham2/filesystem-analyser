# SL-110: after Stop, folders the scan never read say "not read yet" wherever they are shown

SL-109 gave the store a mark for a folder the scan never listed (`ScanStore.NotListed`). Before this slice nothing in the
window read it: a stopped ending showed only the results list, whose top-level rows say "(not read yet)", and no tree at all.
A tree over a stopped store would have drawn an unlisted folder and a listed empty one identically (no note, no expander,
"0 bytes"), so the slice puts the tree back after a stop and makes the unlisted folder say so.

## What the person sees

- A stopped ending still opens on the results list, as it did (Q-273). The tree is a third choice beside **Folders** and
  **File kinds**, **Folder tree**, shown only at a stopped ending that offers it and never checked when the ending opens.
- The tree is built once, on the first choice of it, from the stopped result, and kept for the ending. Choosing **Folders**
  brings the list back with its rows and its header as they were (the tree and the list are swapped, neither is rebuilt from
  the other). **File kinds** is unchanged.
- A folder the store marks not listed reads **not read yet** in the tree's note column, draws no on-disk size, size, share or
  bar, and has no expander. A folder that was listed and found empty keeps what it had: no note, `0 bytes`, no expander. A
  listed folder that holds unread folders keeps its own reading (short figures, an expander): the note follows the folder's
  own mark, not "something below is unread" (a subtree mark is filed, below).
- The stopped list's title for an unread top-level folder and the tree's note are one word, `TopLevelRowText.NotReadYet`.
  Both stopped headers are built from it: the list's ("What was read before the stop. Rows marked not read yet or stopped
  partway are short.") and the tree's, which leads with the clause that explains the word ("Not read yet: folders the stop
  came before. Largest first, by space on disk, of what was read in {root}"), because the line is cut at its end and the
  third choice beside it takes room from it.
- **Open in Explorer** is offered on a "not read yet" row exactly as on any other: `PathOf` has no exclusion for it, and
  `ShellReveal` resolves the path from disk, not from the store.
- A scan that ran to the end offers no choice, builds its tree as before, and shows "not read yet" nowhere. Going to
  **File kinds** and back to **Folders** leaves the finished tree as it was: the list/tree swap runs only while the stopped
  choice is shown (review M3).
- A new scan hides the choice, puts **Folders** back if the tree was the choice, and closes the stopped tree's session, so
  two stores are never held (#170).

## Where the code is

| What | Where |
|---|---|
| The row reads the mark once, as it is built (`_notListed`), and one gate `HasFigures` decides whether any figure is drawn | `FolderTreeNode` |
| The note arm sits after the link and container-layer arms and before the unreadable record | `FolderTreeNode.Note` |
| The unread word | `TopLevelRowText.NotReadYet` |
| Whether a stopped ending offers the tree | `FolderTreeSession.OffersStoppedTree` |
| The choice, the swap, the headers, the reset on the next scan | `MainWindow` (`ShowTreeButton`, `OnResultsViewChanged`, `BuildFolderTree`, `ShowStoppedTree`, `ShowStoppedList`, `ListHeader`, `ResetTreeChoice`) |

`OnDisk` and `Share` keep the store's values for an unread folder (0), so Core's order is untouched and `FolderTree*.cs` still
never re-sorts (SL-044). `BarWidth` stays 0 because the share is 0; `ShareShown` is hidden through `HasFigures`.

### When the tree is offered

`OffersStoppedTree(result)` is `Cancelled` and `result.TopLevel` is not null. `TopLevel` is null exactly when the root's
listing never committed, which keeps out the two shapes U-207 found: a Stop before the root was appended (an empty store, where
building the tree throws) and a Stop in the root's own listing (a root that reads not listed, which the tree would head
"is empty": a folder nobody read, drawn as empty, the defect itself). `StoppedTreeOfferTests` sweeps the stopping call over a
real tree, before and during each guarded call, and asserts the predicate against the store at every one, and that no entry of a
stopped store is both not listed and in the unreadable record (the note arms' order relies on that; Core makes them disjoint
because `FolderDone` returns at Cancelled before `SetChildren` and before the record).

### The one word

`TopLevelRowText.NotReadYet` is written nowhere else in `src/FsAnalyser.App`. A settled fitness rule fences the literal and the
parenthesised title; it is a tripwire for those two spellings, not a closed class. A second rule fences the guard that
`HasFigures` replaced (a figure property whose only guard is the session's store). Both are in `.taad/fitness.yaml`.

## Why "not read yet" is also the word for a folder Stop cut inside its own listing

Criterion 4 asks that such a folder read as not finished because the scan was stopped, never as unreadable or as "could not be
read in full". `Scanner.FolderDone` returns at the Cancelled classification before any append, before `SetChildren` and before
the unreadable record, so the cut folder keeps its mark, has no children and is not in `Unreadable`; the tree draws it as it
draws a queued one. U-189 measured that the engine cannot tell the two apart without a Core change (the cut-off folder exists
only in `Execute`'s locals), so a distinct word is not owed here. The stopped tree's header, "Not read yet: folders the stop
came before", is the line that names the stop beside the rows. A distinct word is filed.

## Superseding SL-044

`docs/design/SL-044-folder-tree.md` (lines 11 to 16) settled that a stopped scan shows no tree because a share there would be of
an incomplete whole. The person asked for the stopped tree; it shares what was read, and its header and the "Stopped:" title
over it say so. SL-044's other rules stand: stock virtualizing `TreeView`, rows built once on the first expand, rows hold a
session and never the store.

## The window cases

Row evidence comes from tree mode's WALK lines, never from the schema-1 report, which carries no row (Q-016, lesson #310). The
hook reads the store's mark for each row through `LastResult.Store` and the row's entry number, never through the row, so a row
that misreads its mark cannot vouch for itself. Every read of the stopped store is made by a `NoInlining` method of
`StoppedTree.cs` that resolves the store on each call and returns values only: no hook field, cached reflection target or
closure may hold the store, or `Release`'s check that the stopped store was collected would fail for the hook's sake (review m2).

`FSA_HOOK_TREE_STOP=<folder>[\]` (tree and reveal modes) stops the scan from a hook thread that spins on a volatile read of
`ScanProgress`'s raw current-folder reference and presses Cancel at `DispatcherPriority.Send`. It never sleeps in the poll
(`Thread.Sleep(1)` waits about 15.6 ms; review m1) and logs the measured poll interval and worst gap. **`CurrentFolder` names
the folder most recently committed** (`Scanner.FolderDone` publishes it after `SetChildren`), so the folder whose listing is in
flight when the click lands is the *next* one. `pressedAt`, the value at callback entry, is how a cut is spelled: T-011's hit is
`pressedAt == j-before` with `m-big` marked and childless (spike 120: 20 of 20 on this PC, detection to click 574 µs median).

A target with a trailing backslash matches the folder or anything under it; one without matches that folder only. A folder is
current only while the next is listed (50 to 230 µs when the next is small), so every target but one is written as a subtree
(review M1). `tests/FsAnalyser.Core.Tests/StopTargetCorpus.json` enumerates the targets and the values the scan gives them,
`StopTargetTests` feeds each row to `StopTarget.Matches` and counts the targets in `WindowCases.ps1`: a case that names one exact
folder that is not listed as allowed there fails in the default suite.

| Case | What | Serves |
|---|---|---|
| T-010 | Run 1: stopped at `m-mid\`, the tree chosen; every walked row against the store's mark; empty folders at both depths; the denied folder and the panel; the list against the tree; Open in Explorer enabled on `p04`; the views kinds, folders, tree. Run 2: the same tree scanned to the end; no choice, kinds and back leaves the tree. Run 3: stop, choose, select a row, rescan: the choice goes, the stopped store is collected, the second tree shows no unread row | criteria 1, 2, 3, 6 (window), 7 |
| T-011 | Stop at `j-before` exactly; `m-big`, 100,000 entries, is cut inside its listing: `not read yet`, no figure, no expander, outside the panel | criterion 4 |
| T-012 | Stop at `m-big\`; `m-big` listed with at least 5,000 of its subfolders unread; five timed opens, each under 500 ms. Then the same folder scanned to the end: no row marked, five opens under 500 ms | criterion 5, and 7's guard on the completed open |
| R-004 | Stop at `n-slow\` (3,000 subfolders), so `z-unread` is still queued; reveal it through the row's menu into a real Explorer window | criterion 6 (landing) |

Each case states what must have happened for a run to say anything (a void run is run again, at most three launches, and never
passes). Fixtures are rooted under `%LOCALAPPDATA%\Temp` (#360) and every launch goes through `Format-AppArgument` (#355).
T-011 and T-012 are separate cases so each stays under the engine's 120 s case limit (Q-222): measured on this PC, T-010 14 s,
T-011 60 s (28 s of it the fixture), T-012 63 s, R-004 10 s, with the app published and the hook built.

R-004 opens a real Explorer window. Q-275 extends Q-176's unattended allowance to it, for this case only: it closes only the
windows it opened, and an Explorer selection it cannot read is recorded as not verified (`Inconclusive`, exit 3), never as a pass.

## Named residuals

- A listed folder that holds unread folders shows short figures with no note: there is no subtree-unfinished mark. U-201 measured
  one (a Kind bit with every raw Kind test masked, or a 4 B column); it is a store change this view slice does not own, and
  flag bit 0x80 is already the last free flag (#483).
- A folder Stop cut cannot be told from a queued one (U-189); a distinct wording is a Core change.
- The on-demand tree build has no per-pass containment (Q-071, Q-158): the Completed path's `ShowFolderTree` has none either, and
  the known throw shapes are excluded by `OffersStoppedTree`. Deferred (review m3).
- The network pass is still owed by a person (Q-274, #136): Stop on a stalled share is not claimed.
- T-003 (Q-128's watch on the 100k cold open) fails on this PC at HEAD and with this slice alike, on its "VOID unless the taller window gave the tree
  more room" check (241.9 px against 241.9 px): the display's work area is 816 DIP, which the default window already fills. Its five opens
  pass in both (cold 96 ms at HEAD, 100 ms here). T-012's completed run carries the same open for this slice.
- Memory of a stopped 5M-entry store with the stopped tree open on a 100k-child folder is unmeasured (Q-270 is open).
