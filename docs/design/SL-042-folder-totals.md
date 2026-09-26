# SL-042 "Scan store: folder totals, hardlinks counted once, children largest first", as built

The store turns the stream of scanned entries into a folder tree in which every folder carries the
deduplicated total of everything below it, and whose children can be read largest first with their share of
the folder. This file records what the build settled, what it measured, and what it leaves for the slices
after it. Where it and the slice's design disagree, this file is right.

## What was settled

- **The store owns the counting rule.** `ScanStore.Append` finds a repeated file ID and flags the later
  name `HardlinkDuplicate` itself; a flag passed in is cleared, and `AddFlags` refuses it. `TryAddFileId` is
  private. The Scanner's own copies of the rules are deleted: no bytes for links, a folder's own
  EndOfFile/AllocationSize added to the totals, and the hardlink branch that registered the ID.
- **Only a file charges** (`Charges`): a File that is not a duplicate. A folder, readable or not, and a
  link charge 0 whatever size they state. `ChargedSize`/`ChargedOnDisk` of a folder row are now 0.
- **Totals are posted at append time.** Each File adds its charge, and one file, to `RolledSize`,
  `RolledOnDisk` and `FilesBelow` of every folder from its parent up to the root, in a loop. So the totals
  are exact after every append, which is what a cancelled scan leaves behind. The three columns sit on every
  row of the 64Ki pages (20 B/row), meaningful on folder rows.
- **One write after an append.** `CorrectSizes(entry, size, onDisk)` is always there (review M1): the
  first repeat of a hardlinked file probes it and corrects the *first* name's row, appended earlier and
  possibly stale. The store posts the old-to-new change in the charge up that row's chain. `Append` reports
  the first holder of the ID (`out firstHolder`), which is the Scanner's probe trigger now that
  `TryAddFileId` is gone.
- **One way into the running totals** (review m2). `Append` and `CorrectSizes` return what they posted
  (`Posted(Size, OnDisk, Files)`), and the Scanner's `Credit(entry, posted, inPlace)` adds it to the totals.
  For a correction it also credits the top-level row of the corrected entry (`TopLevelTracker.Moved`). So the
  root's `RolledSize`/`RolledOnDisk`/`FilesBelow` are `Totals.Bytes`/`BytesOnDisk`/`Files`. Every real-tree
  scan in the suite asserts that, and that each `TopLevelRow` equals its entry's rollup
  (`Scans.AssertChargedBytesAreTheTotals`).
- **No ID means no dedup** (review M2). `ScanStore.HasFileId` is false for 0, all ones (MS-FSCC: no unique
  ID could be established), and the 64-bit all-ones widened to 128 bits. The Scanner's `_settled` set uses
  the same predicate.
- **A parent is an entry already in the store, and a folder that can hold entries** (review m4). -1 is
  accepted only for the first entry (the root). Otherwise an index not yet appended throws
  `ArgumentOutOfRangeException`, and a File, a Link or an Unreadable folder as parent throws
  `InvalidOperationException`. So no Parent chain can loop.
- **A folder whose listing failed partway keeps what was listed** (review m3). The Scanner appends a
  listing, then flags the folder Unreadable, and `DirectoryReader.List` keeps the entries it read before the
  error. Those children stay in every total (so the root still equals the totals) and `ChildrenLargestFirst`
  returns them. "No children and adds nothing" is the Unreadable folder that listed nothing.
- **`FilesBelow` is "files at or below"** (review m1): 1 for a file, a duplicate included; 0 for a link.
  So all three values of a folder are the sums of its children's, which is what `RollupOracle.CheckStructure`
  asserts and what a `TopLevelRow`'s Files already meant for a file row.
- **Children are sorted on read, never stored.** `ChildrenLargestFirst(folder)` returns
  `ChildRow(Entry, OnDisk, Share)[]` by OnDisk descending, then Entry ascending (listing order), a total
  order, so the unstable sort repeats. Share is child OnDisk over the folder's RolledOnDisk, and exactly 0.0
  when that is 0. A file, a link or a childless folder gives an empty array.
- **One writer, a fixed order.** First-seen charging is repeatable only with one appending thread and a
  fixed append order (spike first-seen-charging-repeatable: no-go with 16 workers). It is written on
  `Append`. Nothing enforces it at run time (review M3 was overridden).

## How it is tested

- `ScanStoreRollupTests` (synthetic, no disk): the 10,000-deep chain; a 100,000-child folder with 1,000
  subfolders over three page boundaries; empty root, nested empty folders, a 400-entry mixed tree checked
  by both oracles after every append, and a seeded 50,000-entry mixed tree. It also covers three names in
  three folders, names sharing an ancestor or a parent, IDs that must not match (0, all ones, the widened
  64-bit sentinel, same low half), the store alone setting the duplicate flag, link and Unreadable leaves
  with stated sizes, bad parents, a partly listed Unreadable folder, a folder stating its own size,
  ordering with 67 children (more than 16, where the sort stops being stable), exact and 1/3 shares, and
  zero-byte folders.
- `RollupOracle`: `Expected(stream)` recomputes every entry's totals from the test's input with its own
  first-seen set and charge rule. `CheckStructure(store)` walks the child blocks from the root and checks
  every folder against the sums below it and against its children. Neither recurses.
- `ScanStoreRollupTimingTests` (in the `MemoryBudget` collection, which runs alone): 10 reads of a
  100,000-child folder with many ties, every one asserted under 250 ms, the build configuration logged.
- `ScanStoreBudgetTests`: see below.
- Real trees: `HardlinkTests` asserts per-folder totals, including the case where the charged first name
  is the stale one and the correction lands on it. `LinkTests` and `UnreadableFolderTests` assert the leaf
  adds nothing to its parent. Every scan through `Scans.Run`, `TopLevelTests`' watch and the cancellation
  tests runs `CheckStructure`, root == Totals, and each top-level row == its rollup.
- Mutants, each failing at least one test: no Entry tiebreak; the old charge rule (folders charge);
  duplicates posting no file; all ones treated as an ID; 0/0 share; a caller's duplicate flag trusted; a
  correction not posted up the chain; a correction not credited to the totals.

## Measured at build

On this PC (Ryzen 9 8945H, 16 threads), with the repo-local SDK 10.0.401, Release.

- **Memory at 5,000,000 entries** (`FiveMillionEntriesFitTheMemoryBudget`: GC-heap delta against the
  existing 800 MiB store budget). Before this slice, **retained 555.8 MiB, peak 569.8 MiB**. With the three
  columns on every row and one 100,000-child folder read largest first, **retained 652.1 MiB, peak
  658.6 MiB**. The folder-slot fallback was not needed. The test's shape changed (review M4): folders sit at
  most 16 deep instead of each under the one before (about 500,000 deep, which posting would turn into about
  10^12 additions), and 100,000 entries in a row go into one folder. Entry count, names, IDs, duplicate
  count (200,000) and budget are as they were.
- **Ordered read of 100,000 children**: 10 reads, the first cold, **35.9 to 40.2 ms** (budget 250).
- **A real scan of C:\Windows through the published exe** (`eng/read-window.ps1`): exit 0, Completed,
  188,532 files, 157,747 folders, 42,362,647,879 bytes, 40,979,543,576 on disk, top-level rows adding up
  to the totals. A trunk build of the same exe (`git archive` of d78635f) gives the same files and folders,
  and bytes within the few KB a live log under C:\Windows grows by between runs. Warm scan time, ABBA
  three each: trunk 10,260 / 9,952 / 10,041 ms, this slice 9,900 / 9,954 / 9,728 ms. The per-file posting
  costs nothing measurable.
- **The same C:\Windows scan through the store's checks** (a temporary test, removed): 346,280 entries,
  54,992 hardlink duplicates, 39,557 probes. `CheckStructure` held over the whole tree, the root's rollups
  equalled the totals and every top-level row its rollup. The widest folder, WinSxS\Manifests at 38,958
  children, read largest first in 12 to 16 ms.

## The open Totals question: what a folder's listing says on local NTFS (review m7)

Totals no longer count a folder's own EndOfFile/AllocationSize. Before building a real-tree test for that, the
listing was probed with the project's `DirectoryReader` (a temporary test, removed):

```
temp tree: big EndOfFile 0 AllocationSize 0 attrs 0x10      (1,500 files with 190-character names)
temp tree: small EndOfFile 0 AllocationSize 0 attrs 0x10
C:\Windows: 89 folders listed, 0 with a non-zero size
C:\Windows\System32: 190 folders listed, 0 with a non-zero size
C:\Program Files: 52 folders listed, 0 with a non-zero size
C:\: 25 folders listed, 0 with a non-zero size
```

Local NTFS lists every folder at 0, so dropping folders' own sizes changes nothing on a local drive (the
C:\Windows comparison above agrees), and a real-tree test of it could not fail before the change. It was not
built; the synthetic 4096/4096 case (`AFolderThatStatesItsOwnSizeAddsNothingOfIt`) stands for it. Where a
listing does give a folder a size (the review cites SMB listing 4096 intermittently), the totals no longer
move with it. The person's question stays open: if that space should be shown, it needs its own column.

## The architecture line this slice revises (review m9)

The Scan Store line said the store holds "the children of each folder sorted largest first". As built:
**children are ordered largest first on read (`ChildrenLargestFirst`); no order is stored unless
criterion 6's timing test fails.** The engine's architecture record is not in this repository, so the line
there still needs the same edit. The design left this as an open question (sort on read against a stored
order), and this is the answer the build measured, not a decision on the person's behalf.

## Merging with slice/SL-040 (review m8)

slice/SL-040 (ef74cd1, unmerged) also edits `ScanStore.cs`: it makes `Size`/`OnDisk` internal and adds
`LargestFiles`, which reads `ChargedOnDisk` for files. That reading is unaffected (files charge as before).
The touchpoints for whichever slice merges second:

- Text: `ScanStore.cs` is now `partial`, `Page` has three more columns, `Charges` changed, and the write
  section was rewritten (`Append`, `AddFlags`, `CorrectSizes`, `TryAddFileId`). The new reads are in
  `ScanStore.Rollup.cs`.
- Meaning: `Append` clears a caller's `HardlinkDuplicate`, `AddFlags` throws on it, and `TryAddFileId` is
  private. A test that built a duplicate by hand will not compile, or will now charge that entry. Grep the
  merged tests for `TryAddFileId` and `HardlinkDuplicate` and rebuild each duplicate by appending a second
  file with the same ID. A synthetic store must also append a parent before its children and never under a
  file, a link or an Unreadable folder.
- Re-run the whole Core.Tests suite after the merge; the store tests and the counting-rule tests cover both.

**As merged** (SL-040 reached uat first, at 2e089a1, and SL-042 took it in). The only textual conflict was
the class remarks of `ScanStore.cs`: SL-040's paragraph on the internal raw sizes is kept, followed by
SL-042's. The grep found one duplicate built by hand, in `LargestFilesScaleTests`
(`Append(..., EntryFlags.HardlinkDuplicate, ..., Terabyte, Terabyte, UInt128.Zero)`). Against the merged
store that name is charged 1 TB and takes the top of the list; the test failed that way when run. It is now a
later name of the file appended just before it, with the same ID and in the same folder, still listed at
1 TB. The store flags it and charges it nothing. `LargestFiles` itself reads only `ChargedSize`/
`ChargedOnDisk` of Files and needed no change. The whole suite, SL-040's tests included, passes on the
merged tree.

## Not done, and why

- The real NTFS "folder owning a directory allocation" test: see the m7 section. It cannot fail on local
  NTFS.
- Retiring `TopLevelTracker`, the second accumulator of the same numbers: it belongs to the slice that
  draws the tree. Until then it is pinned to the rollups by the real-tree assertions.
- Reading the rollups from another thread while the scan writes: not part of this contract (posting is
  child-then-ancestor, so a concurrent reader could see a child larger than its parent). The SL-039 rule
  that the window never touches the store still stands.
