# SL-044 "See where the space went": the folder tree, as built

PS-003 is done when the person sees a folder tree where each folder's size includes everything inside it,
largest first, with each folder's share of its parent, and can work down from the drive to the folder that is
using the space. Core already had the numbers (SL-042: rolled-up totals, `ChildrenLargestFirst` with each
child's share). This slice draws them. This file records what it settled, what a later slice inherits, and what
the build measured. Where it and the slice's design disagree, this file is right.

## What was settled

- **The tree is built only from a completed scan.** `MainWindow.ShowOutcome`, on a Completed ending, builds the
  root row over `ScanResult.Store`, which exists only once `Scan()` has returned, so no thread writes what the
  rows read. While the scan runs the window keeps SL-039's live flat list (sorted by bytes, no shares); a
  stopped scan, a scan that failed and a root that could not be read keep today's endings and show no tree,
  because a share there would be of an incomplete whole. At the hand-off the rows can reorder: the tree ranks by
  space on disk.
- **What a live tree needs first**, and why this slice did not build one. Both risk spikes on the live path were
  no-go (a live refresh with 100,000 rows open: median 84 ms against 50, a UI gap of 866 ms against 250; shares
  read mid-scan: a child above its parent 10 to 21 times a scan). So the store is still not published before
  the scan ends, `TopLevelTracker` is deliberately not retired, and SL-039's and SL-042's "needs first" items
  (SL-039-live-results.md, SL-042-folder-totals.md) are still not built. A live tree first needs a consistent
  read path in Core: immutable per-folder snapshots to depth 4 taken at one instant, or the store published
  with a seqlock or epoch bumped around each folder's commit and each in-place correction. The architecture rule
  "Core does not publish a live ScanStore before the scan ends" points here; whoever lifts it builds that path
  first.
- **One folder at a time, and nothing re-sorts.** A folder with children starts with one shared stand-in row, so
  its expander shows. Opening it (`TreeViewItem.Expanded`, handled in `MainWindow.OnFolderExpanded`) reads
  `ChildrenLargestFirst` once, builds one row per child in that order and puts them in whole, one reset; they
  are kept until the next scan. A file, a link and a folder with nothing listed in it (empty, or not readable,
  which the store records with no children) are rows with no expander. Files are rows beside folders: a large
  file directly in a folder is part of where the space went. The numpad `*` key, which on a stock TreeView opens
  every folder below the selected one, is handled and does nothing (review M1).
- **Rows hold a session, not the store** (review M2). `FolderTreeSession` holds the store; every row holds the
  session; `StartScan` closes it before the next scan builds another store. A row that WPF keeps after the tree
  is cleared (the selected item, a recycled container, an automation peer) keeps a few bytes, not a store of
  about 650 MiB at 5M entries against a 1 GiB budget.
- **Faults on opening a folder reach the app** (review m4). `FolderTreeNode.IsExpanded` only records the state.
  The rows are built in the `Expanded` handler, which catches and raises the exception again on the dispatcher:
  the expander's own click reaches the handler through a binding, which would otherwise turn a fault into a
  trace line and leave an open folder showing nothing. The stand-in row reads no store, so its texts are
  constants.
- **The share** is Core's `ChildRow.Share` of the parent's space on disk, shown as a percentage to one decimal
  place, a midpoint rounded away from zero (`Math.Round(share * 100, 1, MidpointRounding.AwayFromZero)`, in the
  current culture like the sizes; review m2), and as a bar of share x 60 px. A share of 0, which is every
  child's under a parent that takes nothing on disk, reads "0.0 %" with no bar. A nonzero share too small for
  one decimal place also reads "0.0 %" (kept, as the design did), and its bar is drawn at least 1 px so the row
  never shows an empty bar. The root has no share and no bar. The headings say the share and the ranking are by
  space on disk; Size is shown, never ranked by.

## The window

- `FolderTree`, a stock WPF `TreeView` in the same cell as `TopLevelList`, in the configuration the memory spike
  and U-001 measured: `VirtualizingPanel.IsVirtualizing`, `VirtualizationMode=Recycling`, `ScrollUnit=Pixel`,
  every row 20 px, inside the star row that bounds its height (SL-040's split, 3 to 1 beside the outcome). One
  of the two is shown: the list while scanning and after a stop, the tree after a completed scan. The header
  reads "Largest at the top of <root>, by space on disk", or "<root> is empty."
- **The row template.** A DockPanel: Size, On disk and the share (bar and percentage) docked right at fixed
  widths, a note ("link, not followed", "could not be opened"), and the name filling the rest, trimmed with the
  whole name as its tooltip. Every text is a TextBlock bound to the row, so '_' and '&' show as written. The
  item container keeps the stock TreeViewItem's parts and triggers (`PART_Header`, `ItemsHost`, the expander)
  with one change: its header runs to the right edge of the tree instead of being sized to its content, which
  is what lets the columns line up under their headings at every depth. The headings sit above the tree with the
  same widths, offset by the tree's border and its vertical scroll bar, which is always shown.
- `IsExpanded` is bound TwoWay to the row (`FolderTreeNode` implements INotifyPropertyChanged), so expand state
  stays with the row when recycling hands its container to another.
- `--report` (schema 1, additive) gains `window.FolderTree`: `visible`, `actualHeight`, `inView`,
  `realizedRows` and `fullRows`, its layout only. No row's name or figures go in (Q-016); the rows are read by
  the hook, into its own log. After a completed scan `topLevel` is absent, because the list holds nothing.

## How it is tested

The hook's tree mode (`tests/window/FsaWindowHook/TreeMode.cs`) runs the app without `--report`, waits for the
ending, drives the tree as a person would (through each row's container: `IsExpanded`, `IsSelected`, keys,
scrolling, the window's own `StartScan`) and logs one JSON line per thing it read. A row's evidence is what its
container draws: the text of each named part of the row template (`NameText`, `NoteText`, `OnDiskText`,
`SizeText`, `ShareText`) and the laid-out width of `ShareBar`, never only what the row holds. Rows off screen are
brought in through their parent's items panel (`BringIndexIntoViewPublic`) with a Loaded-priority pass before
each is read (review M3). In `tests/window/WindowCases.ps1`:

- T-001 (rows 1 to 4): a planted tree of 36 rows walked to depth 4, every row against the tree the case built:
  order by space on disk under every folder, including a sparse file larger in bytes and smaller on disk than
  its sibling; totals through a folder three levels deep and a folder holding only a chain of folders; the root
  as the sum of its rows; a parent taking 0 bytes; thirds, two thirds, an exact midpoint (1/16), 15/16 and a
  share too small to show; bars within 1 px; links (a junction and both kinds of symbolic link) and an empty
  folder with no expander; '_', '&' and a 149-character name drawn as written. The sparse file's space on disk
  comes from `fsutil sparse queryrange` (review m3; see below).
- T-002 (row 2): a folder of 60 folders, one opened near the end, the tree scrolled to its end and back twice, the
  folder closed and reopened: only that row open, by the rows' state and as drawn, order and figures unchanged.
- T-003 (row 5, M1): 100,000 files in `big`, a child of the root (review m7), opened five times at the default
  window size and five times in a window 1,000 px tall; then every folder closed and `*` sent to the selected
  root row.
- T-004 (rows 1 and 6, M2): a second scan started with `big` open and a row three levels deep selected; a weak
  reference to the first store, taken in a method that is not inlined, is dead after a forced collection while
  the second scan runs and again after it ends, and the tree then shows the new root alone.
- T-005 (row 6, m7): an Input-priority probe every 50 ms through a scan of 100,000 files and 20,000 folders while
  the window is moved and resized every 50 ms,
  the hand-off to the tree, one open of `big` and a second scan over the open tree. This is a hook-driven gap
  measurement in a case of its own: StallRig is an SMB stall server and drives neither step.
- T-006 (row 1): Stop mid-scan, a fault on the UI thread mid-scan (the hook logs the tree's state as the app
  shuts down) and a missing root: no tree.
- C-002, L-003, B-003, B-007 and M-001 read the tree, or the report's `FolderTree`, where they read the results
  list after a completed scan.

## Measured at build

On this PC (Ryzen 9 8945H, 16 threads, 1920x1080 at 100%, shared with other work), repo-local SDK 10.0.401,
the published single-file exe, the hook's timing (from setting `IsExpanded` on the row's container to the first
ContextIdle pass with its first child's row built).

- Row 5, 100,000 files, default window (a 39 px viewport, 4 rows built), two runs: open 1 (cold) **64.6 and
  72.4 ms**, of which Core's sort 45.2 and 46.6 ms, the row build 12.0 and 8.6 ms, and the rest (the reset,
  container generation, layout) 6.6 and 16.3 ms; opens 2 to 5 took 0.9 to 2.0 ms. In a 1,000 px window (a
  134 px viewport, 14 rows built), two runs: open 1 **106.9 and 106.4 ms** (sort 44.7 and 45.0, rows 16.6 and
  17.3, the rest 44.0 and 43.3), opens 2 to 5 1.2 to 3.7 ms.
- Row 6: through a scan of about a second with the window moved and resized 23 times, the hand-off, one open of
  `big` and a second scan over it, no probe waited more than 47 ms (p95 per phase 0.5 to 30 ms).
- The first store was collectable while the second scan ran, with a row three levels deep selected and a
  3,000-row folder open, and again after it ended.
- Without the `*` handler (a scratch copy of the app with `PreviewKeyDown` unwired), the key opened all 9 rows
  below the root of a small tree; with it, none. T-003's check fails on the first and passes on the second.
- On real input, `C:\Program Files` (53 folders, 31.8 GB) walked two levels, 648 rows: every row drawn, the root
  equal to the sum of its rows in both columns, no row out of order, and `WindowsApps`, which this account
  cannot open, noted "could not be opened".
- **If row 5 ever misses 0.5 s** (review M5), the interval it measures holds Core's sort and the row build, so a
  worker thread would not shorten it: moving them off the UI thread only keeps the window responsive while the
  person waits, and is worth doing only for that. What shortens the interval is building rows only for the
  indexes WPF asks for, a read-only list over the `ChildRow[]` that makes a row on first access, so the build
  scales with the screen and not with the number of children. At 100,000 children the build was 9 to 17 ms and
  the sort 45 to 47 ms, so neither is needed now.

## Found at build, and left to others

- **A sparse file's directory entry can say nothing is allocated when 64 KiB is.** Written length first (the
  file extended, then data written into the hole), four sparse files each had 64 KiB allocated by `fsutil
  sparse queryrange`, and the scan charged three of them 0 bytes on disk: its directory entry kept the
  allocation from before the write, also after a rename and after a reopen that changed its length. Written
  data first, the entry was current and the scan charged 64 KiB. `SizeProbe` trusts the listing for sparse
  files ("sparse files really can have nothing allocated"), so such a file is charged at a stale 0. That is a
  counting-rule question for the scanner, outside this slice; T-001 writes its sparse file data first.
- **At the default 880 by 620 window the tree shows about two rows** (a 39 px viewport, T-002; 43 px at the
  window's minimum size, B-007), and in a 1,000 px window about seven, because it keeps SL-040's quarter of the
  height beside the outcome. A person
  has to make the window large to work down a big tree. The split and the default size were SL-040's decisions
  and are left alone here.
- The person's network pass (N-001 to N-004, StalledShareTests) is stale: this slice changes MainWindow.xaml(.cs),
  the hook and WindowCases.ps1, all on the staleness list (SL-020-scanner.md).

## Not done, and why

- A live tree while the scan runs: see above; it needs a Core read path first.
- Retiring `TopLevelTracker`: the live list still needs it while the scan runs.
- A hardlinked file's second name shows 0, as Core charges it. The tree shows each `ChildRow` as it is and does
  no accounting of its own; counting once is Core's, tested on real trees in Core.Tests.
