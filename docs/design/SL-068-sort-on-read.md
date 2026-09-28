# SL-068 "Sort on read": the child-order question, closed

The Scan Store's architecture line said the store holds "the children of each folder sorted largest first". SL-042
built something else, a sort on every read, and left the difference as an open question. This file closes it on the
measured numbers, says what the closure does not cover and what reopens it, and carries the text submitted for the
engine's line. It changes no code and no existing test; its own checks are tests/bugs/SL-068-DecisionCases.ps1.

## The question

Is a folder's children's largest-first order stored in the Scan Store, or computed on each read? SL-042 answered
with a measurement and stopped short of a decision: "this is the answer the build measured, not a decision on the
person's behalf" (docs/design/SL-042-folder-totals.md:120-126).

## The rule, and who wrote it

The rule is SL-042's design, not the person's. It reads: "Sort children on every read (OnDisk descending, then Entry
ascending) and store no per-folder order". Its measure is a timing test that "asserts every read is under 250 ms",
and "If the test fails, a cached order can sit behind the same call" (.taad/slices/SL-042/design-slice-29.json:58
and :136-138). The SL-042 doc states it at :45 and at :123-124: "no order is stored unless criterion 6's timing test
fails".

When it was written: the ledger dates SL-042's design attempt 29 at 2026-09-25T17:23:45Z and the build that
measured it at 2026-09-25T23:47:34Z, so there the rule came six hours first. Git cannot tell them apart: the design
record, the doc's line 45 and the figures at its line 88 all arrived in the same commit, b5c2b62. So this closure
applies a rule written before its result by the ledger's clock, and in the same commit by git's.

The person did not write the rule and can override this closure. Overriding it changes this record, not the code.

## What was measured

The Core timing test, `ScanStoreRollupTimingTests.TheOrderedChildrenOfAHundredThousandChildFolderReadInUnderAQuarterSecond`
(tests/FsAnalyser.Core.Tests/ScanStoreRollupTimingTests.cs:21-79): a synthetic store, one folder of 100,000
children with thousands of ties, 10 calls of `ChildrenLargestFirst`, the first cold, each asserted under 250 ms.
Release, the repo-local SDK 10.0.401, .NET 10.0.12, this PC (Ryzen 9 8945H, 16 threads, shared with other work):

- Run 1 (build check, 2026-09-28): 44.12, 37.95, 35.18, 34.82, 34.19, 34.48, 34.79, 35.45, 9.46, 10.17
- Run 2 (build check, 2026-09-28): 39.01, 36.94, 36.07, 34.37, 33.59, 35.50, 33.50, 32.75, 27.93, 8.98
- Run 3 (this build, 2026-09-28): 40.42, 36.75, 34.94, 34.35, 34.10, 34.83, 23.02, 10.16, 8.84, 10.52

Every read in ms. Run 1 printed "Test Run Successful" with its exit code not captured; runs 2 and 3 exited 0. The
Release range is **8.84 to 44.12 ms** against 250, so the slowest read, a cold first one, had 5.7 times margin. The
slice's brief rounded this to 9-40 ms, but run 1's cold read was 44.12, so the range this record quotes is 9-44.

    pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 test tests/FsAnalyser.Core.Tests -c Release --filter "FullyQualifiedName~ScanStoreRollupTimingTests" --logger "console;verbosity=detailed"

Figures from other harnesses, which are not merged into that range:

- **SL-042's build, the same test:** 35.9 to 40.2 ms (docs/design/SL-042-folder-totals.md:88).
- **SL-044's window harness** (the published exe, timed by the hook from setting `IsExpanded` to the first
  ContextIdle pass): a cold open of 100,000 children took 97.9 ms at the default window, of which Core's sort
  47.5 ms, and 141.9 ms as tall as the work area, of which the sort 44.0 ms (docs/design/SL-044-folder-tree.md:127-130).
  That is the on-screen promise, under 0.5 s, and the sort inside it.
- **The same window harness, re-run by this build** (window case T-003, the published exe, 2026-09-28, exit 0): a
  cold open took 221.9 ms at the default window, of which the sort 51.0 ms, and 115.3 ms as tall as the work area,
  of which the sort 43.8 ms. All ten opens were under 500 ms.
- **The same test in Debug** (this build): 44.22, 39.30, 36.83, 36.44, 37.95, 47.00, 43.27, 38.08, 40.28, 37.42, exit
  0. It is not evidence for the rule, and is recorded because the default run is Debug (see below).
- **Memory:** a read allocates one `ChildRow[]` of 24 bytes a row (`Unsafe.SizeOf<ChildRow>` on .NET 10.0.12), so
  2,400,000 bytes for 100,000 children, which the caller drops once it has built its rows. Nothing is kept per folder.

## Decision: closed

**No child order is stored.** `ScanStore.ChildrenLargestFirst` (src/FsAnalyser.Core/Store/ScanStore.Rollup.cs:50-76)
sorts a folder's children by on-disk size, then entry order, on every call, and keeps nothing. The folder tree reads
it (src/FsAnalyser.App/FolderTreeNode.cs:147), and three settled rules keep the window from sorting a second time
(.taad/fitness.yaml:504, :541 and :551). The rule's condition, the timing test failing, did not occur in any run, so
the stored order it held in reserve is not built.

This covers reading the children of a folder in a completed store, and opening it cold in the tree. Reversing it is
expensive: a per-folder order kept in the store and maintained while the scan writes, paid out of the about 136 MiB
of headroom SL-042's spike measured, with FolderTreeNode and the three rules changed with it.

## Not covered

- **Re-sorting during a live refresh.** Every measurement above reads a finished store. SL-044's spike on a live
  refresh with 100,000 rows open was no-go: median 84 ms against a 50 ms bar, and a UI gap of 866 ms against 250
  (docs/design/SL-044-folder-tree.md:17-19). This closure says nothing about that. It belongs to the live-refresh
  work, and a stored order proposed there has to be maintained under concurrent scan writes, which nothing here
  measured.
- **The Scan Store line's memory figures** (699 MiB peak, 572 MB for the store). SL-042 measured the as-built store at
  652.1 MiB retained at 5,000,000 entries (docs/design/SL-042-folder-totals.md:81-84). This slice did not re-audit
  them, and does not claim the rest of the line is current.

## What reopens it

1. The Core timing test reads over 250 ms in Release. A single slow run, as under machine load, is re-run once
   first, and the question reopens if it holds. A Debug number does not reopen it.
2. A measured cold open of 100,000 children reaches 500 ms. Window case T-003 checks it: it opens a folder of 100,000
   files five times in the published exe, timed by the in-process hook, and fails an open at 500 ms or over
   (`pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/WindowCases.ps1 -Case T-003`, also in `-Case All`).
   It runs only when that script is run, never in `dotnet test`, so no default run watches it.
3. A feature needs an order that sizes cannot give, such as an order a person arranges. Largest first can always be
   recomputed from sizes; such an order cannot.

How the trip wire for 1 runs: the test carries no Trait, and Core.Tests' default filter (`FsaDefaultTestFilter`,
`Network!=TEST-NET-1&Network!=SMB-LOOPBACK&Cloud!=CF-SYNC-ROOT`) leaves out only tests with those Traits, so every
`dotnet test` of Core.Tests that names no filter runs it, in the MemoryBudget collection, which runs alone. No
default Configuration is set (Directory.Build.props, the csproj and eng/dotnet.ps1 name none), so that default run
is Debug. The test asserts the same 250 ms there and logs the configuration; only a Release run counts toward 1.
This build's default run (`eng/dotnet.ps1 test tests/FsAnalyser.Core.Tests`, no filter, no `-c`) passed 381 of 381
tests, the timing test among them, in Debug, its reads 22.39 to 36.98 ms.

## The engine's Scan Store line: text submitted for the edit

Status: **not applied** by SL-068's build (2026-09-28). The build stage has no write path to the engine's
architecture record: the record is not in this repository, and the build has no tool that writes it. SL-042's build
hit the same wall. This section is the text submitted for the edit, not a copy of the record, and whoever has access
to the record applies it.

The line as handed to this build still held the old clause. Find this text, exactly once:

    the rolled-up totals per folder, and the children of each folder sorted largest first.

and replace it with:

    and the rolled-up totals per folder. No child order is stored: a folder's children are ordered largest first on each read (ChildrenLargestFirst sorts a temporary index per call; Core read of 100,000 children measured 9-44 ms in Release against a 250 ms budget).

If the find text is not there exactly once, because it was already edited or reworded, apply nothing and report the
text found. Everything else in the line stays, the budget figures included.

## Cases and their checks

Each runs as `pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-068-DecisionCases.ps1 -Case <case>`, and
reads only files and git objects.

| Case | What the check holds |
|---|---|
| C-001 no write path to the engine record | the status above says not applied; no file outside this one and the check carries the new sentence; no architecture-record file was added |
| C-002 the record no longer holds the old clause | the submitted edit, applied to the line as handed, gives the expected line; applied to an edited, a reworded or a doubled line, it applies nothing and reports the text |
| C-003 the pointer in the SL-042 doc | only added lines, after the paragraph that ends at line 126; lines 1-126 and everything after them unchanged |
| C-004 quoting the measurements | the stated range is the minimum and maximum of the listed reads; 9-40 appears only as the brief's; the SL-044 figures match its doc and no range spans the two harnesses |
| C-005 rule and result in the same commit | git finds line 45 and the figures at line 88 in one commit, and this record says so; the ledger times it quotes are the ledger's |
| C-006 the trip wire in the default run | the test has no Trait the default filter excludes and no Skip, no default Configuration is set, and this record says the default run is Debug and that T-003, outside `dotnet test`, checks the 500 ms open |
| C-007 live refresh is not covered | the Not covered section cites SL-044's 84 ms against 50, and the submitted line claims nothing about refresh |
| C-008 docs only | the slice touched only these two docs and the check; no fitness rule's files glob reaches docs/design; nothing in src, tests or eng reads either doc |
