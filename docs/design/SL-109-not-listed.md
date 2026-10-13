# SL-109: the scan store marks every folder it has not listed

A stopped scan's store can tell a folder it never listed from one it listed and found empty. Before this slice both read the
same in every store read: no flags, no children, rolled totals of 0. Now `ScanStore.NotListed(int)` says which.

## The contract

- `ScanStore.NotListed(index)` is true for a folder that was appended and whose children were never registered:
  - a folder still queued when the scan stopped (a listing that committed appended it; nothing has listed it);
  - the folder Stop cut part-way through its own listing. An aborted listing commits nothing (`Scanner.FolderDone` returns
    at the Cancelled classification before any append and before `SetChildren`), so the folder keeps its mark and none of
    the buffers the reader had read;
  - the root, when Stop lands in its handling after the directory check (see the root boundary below), or its listing
    fails with nothing kept;
  - by construction, a folder a reader listed ahead of the walk and did not yet write. No such reader exists (see below).
- It is false for a folder whose children are registered, **empty or not**, including a folder flagged `Unreadable` and the
  container layer store flagged `NotScanned`: `FolderDone` and `StoreSkipped` both call `SetChildren` (with the children
  they have, which may be none) before they add the flag. So the three states, not listed, unreadable and not scanned, are
  disjoint, and a later view maps each to one phrase. For a file or a link it is always false. For every entry of a scan
  that ran to the end it is false.
- Only `Append` (and so `AppendRoot`) sets the mark, and only on a folder, before the count is published. Only `SetChildren`
  clears it, for any count including 0. `Append` and `AddFlags` throw `ArgumentException` for a caller who passes bit 0x80,
  before any write. Nothing else touches the bit: not `CorrectSizes`, not the HardlinkDuplicate set in `Append`.
- `ScanStore.Flags(index)` masks the bit off. It is not an `EntryFlags` member, so `Enum.GetValues<EntryFlags>()`, every
  record comparison of flags (`Scans.EntryState`, `CancelledFolderTests`) and every flag test in `src` see what they always
  saw. Everything that branches on the Kind byte (rolled size and on-disk, files below, children largest first,
  `CheckParent`) is unchanged: a marked folder is a folder, and `CheckParent` still accepts children under it, which
  `FolderDone` appends before it calls `SetChildren`.
- `SetChildren` is a folder's one, whole registration. A writer that registers a listing in more than one call, a
  block-wise one for instance, would clear the mark on a folder still being listed, and a Stop then reads it as listed. The
  test helper `RollupOracle.Feed` registers per child because it always finishes the stream; a product writer must not copy it.
- Exact between appends on the writing thread and after `Scan` has returned, the same contract as the rolled totals. A read
  from another thread while appends go on is outside it (U-170, U-172).

## Why bit 0x80 of the Flags byte

Criterion 7 forbids per-entry storage. `Page.Flags` is a byte column and `EntryFlags` names bits 0x01 to 0x40, so 0x80 is
free and the mark costs nothing. A Kind bit would break every read that branches on Kind. It is the **last free bit** in
the byte: the next flag needs a column of its own (about 4.8 MiB at 5M entries) or Kind's free bits with every raw Kind test
masked (U-201). Two settled architecture rules fence it: no `src` file but `ScanStore.cs` spells the bit as an entry flag,
and `EntryFlags` names no member at 0x80. The first is held to
`tests/bugs/SL-109-NotListedBitRuleCorpus.json` (97 rows: 55 spellings that must fire, 32 that must stay quiet, 10 known gaps),
run by `tests/bugs/SL-109-NotListedBitRule.Tests.ps1`.

## The root boundary

`Scanner.ListRoot` appends the root right after the attribute query shows a directory, before the last-access decision, the
keep reopen and the first listing call. So a Stop in guarded calls 3 and up of the root's handling (the last-access queries,
the keep reopen, each listing buffer) leaves one entry, the root, reading not listed. A Stop in call 1 (the open) or call 2
(the attribute query), a token cancelled before the scan, a root that is a file and a refused root still leave an empty
store: until the attribute query answers, nothing is known to be a folder. The risk spike measured that the root was absent
in 20 of 20 Stops in its listing when it was appended after `List`.

## Listed ahead, and what a future reader must keep

The scanner is single-threaded and depth-first; no reader lists ahead of the walk, so "listed ahead but not yet written"
cannot occur and has no test. It is covered by construction: the mark is cleared only by `SetChildren`, so a folder a future
read-ahead reader lists without registering stays not listed. A future read-ahead or parallel-listing slice owes that case
and must keep `SetChildren` as the only registration, called once, after the last child.

## Measured (Debug build, this PC, 2026-10-13)

- `ScanStoreBudgetTests.FiveMillionEntriesFitTheMemoryBudget`, unedited: 5,000,000 entries, retained **654.6 MiB**, sampled
  peak **670.6 MiB**, against 800 MiB. The same figures as before the slice (654.6 and 670.6): the mark added no storage.
- `NotListedBudgetTests.AStoreStoppedAtItsUnlistedPeakFitsTheBudget`: U-001's shape driven in scanner order and stopped right
  after the late folder W's 60,000 subfolders are registered. 4,879,990 entries, 4,337,982 files, 482,008 folders listed,
  **60,000 folders marked**, equal to the generator's own stack. Store retained **637.1 MiB**, sampled peak **653.6 MiB**.
  Test-host private commit **690.0 MiB** at the stop (30.7 MiB before the build, after two blocking, compacting, aggressive
  collections).
- The published app, idle with no scan, holds **64.9 MiB** private commit (6 s after start, this PC). A stopped ending builds no
  folder tree (`MainWindow.ShowOutcome`), so the app at this shape is its window, plus the store, plus the top-level list.

**The app half of criterion 7 is not measured as such.** The Core test host has no WPF window, and the real app cannot be
brought to a 5M-entry stopped store unattended: Core keeps the store private to `Scanner.Run`, so the window hook would need a
mode that feeds a synthetic store to the real window, and that is window-test infrastructure (`tests/window/**`, gated by
Q-114) that this slice does not build. What is recorded is a composition: 690.0 MiB (test host, store included) plus the
published window's 64.9 MiB is about 755 MiB, against 1 GiB; U-201 measured 808 MiB with a copy of the tree code open. That is
a derived bound, not the app's commit. It is filed for a later slice.

## Not re-measured here

The scanner's own file changed (`ListRoot`), so the standing network debt (lesson #136) goes stale. Cancel on a stalled share
is **not** re-measured by this slice, and no claim of stop-safety on shares is made: the TEST-NET-1 cancel guard is red at
baseline (Q-259), and `Network=SMB-LOOPBACK` is a person's run. The risk spike measured that a folder cut in a stalled SMB
listing returns 995 with the guard's abort, so `SetChildren` is never called and the folder keeps its mark (20 of 20, Windows
Sandbox build 26100, SMBLibrary rig; Windows 10 and other builds not measured).

## Checks a validate run makes

- `pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 test FsAnalyser.slnx`: the 964 baseline tests plus the new
  ones, 0 failed.
- Criterion 6's "no edits": no existing test file is modified. Diff against the branch point, not a pinned SHA, so an `uat`
  merged into the slice does not read as an edit: `git diff --diff-filter=M --name-only $(git merge-base HEAD uat) -- tests`
  prints nothing. If `uat` was merged in, every path it lists must have come from that merge.
- `.taad/fitness.yaml` changed, so (Q-026, Q-027): `eng/rules-harness/harness.py --engine-root <taad_v3\backend> --scenario all`
  under the engine's interpreter (167 rows, all as recorded, 0 stray hits at taad_v3 4ae0664), and `tests/rules/RuleCases.ps1`
  `-Case C-001` and `-Case C-003` (the settled count rose by two, derived from `Get-RuleCounts`).
- `pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-109-NotListedBitRule.Tests.ps1 -Python <engine python>`.
  `-Before` applies the first draft's pattern instead and fails the 46 fire rows it let through.
