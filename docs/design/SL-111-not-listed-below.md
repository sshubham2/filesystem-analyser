# SL-111: the scan store says whether anything at or below a folder is unlisted

A stopped scan's store can say, for any folder, whether some folder at or below it was never listed:
`ScanStore.NotListedAtOrBelow(int)`. SL-109 gave every folder its own mark (`NotListed`); this slice answers the question
for a whole subtree without storing anything new.

## The contract

- `NotListedAtOrBelow(index)` is true when the folder, or a folder below it at any depth, reads `NotListed`. "At or below"
  includes the folder itself. It is false for every file and link.
- It is false for every entry once a scan has **Completed**, and for every entry of a scan that was **stopped in the size
  probes**, because every listing had committed by then. It does not follow the scan's status: a `Cancelled` store with
  every folder listed reads false everywhere.
- A root that was picked, opened as a directory and listed nothing (the scan ends unreadable) is still not listed, so it
  reads true, as `NotListed` does. A scan stopped before the root is known to be a folder has an empty store, and nothing
  can be asked of it (`At` throws on index 0).
- Exact on the terms `NotListed` and the rolled totals are exact on: between appends on the writing thread, and after
  `Scan` has returned. A read from another thread while appends go on is outside it.
- Exact only for a store registered in the order the scanner registers it (below). A store built another way gets a wrong
  answer and no error: `RollupOracle.Feed` and the `ScanStoreTests` generators are not such stores.
- Throws `ArgumentOutOfRangeException` for an index at or past `Count`, like every read. It allocates nothing and writes
  nothing, and no caller in `src` uses it yet.

## The derivation, and why it is exact

The read follows one chain. From the folder: if it reads `NotListed`, true. Otherwise take its registered children block
`[FirstChild, FirstChild + ChildCount)`, scan it **backward** to its last entry whose raw Kind byte is `Folder` (files and
links are skipped), and continue from that entry. No folder child, or no children, is false.

It is sound because of how the shipped scanner registers folders:

- `Scanner._pending` is a `Stack`, popped one at a time on the caller's thread and pushed **last-first**, so folders are
  listed in the preorder of the folder tree, subfolders in listing order.
- `FolderDone` appends a folder's children as one block and registers them with **one** `SetChildren`, after the whole
  block. `StoreSkipped` registers `(Count, 0)`. A listing Stop cut commits nothing, so the cut folder keeps its mark and adds
  no entries.
- So the folders listed at any moment are a prefix of the preorder. A listed last subfolder therefore means every earlier
  sibling's whole subtree is finished, and the last-folder chain from any folder reaches the first unlisted folder below it
  if there is one. One chain step moves to a strictly higher index.
- `AppendRoot` runs before the root's listing (`Scanner.ListRoot`), so a Stop after the root's directory check leaves the
  root marked, and the chain's first test answers true.

Spike 121 (experiment 1) compared this with a walk of everything below each folder: 0 differences in 501,265 folder checks
over 2,550 real Scanner stores. The same computation fed a queue (BFS) order was wrong in 975 of 137,486 comparisons (283 of
401 states) on a ragged tree, and a first-first stack in 1,512 of 82,540. On a **uniform-depth** tree a queue gave 0
mismatches, so a uniform tree cannot tell the orders apart: the guard tree here is ragged on purpose.

## It holds only while the traversal stays depth-first

The answer relies on **one writing thread, folders listed depth-first with subfolders in listing order, and each folder's
children one contiguous block registered by one `SetChildren` after the whole block.** It is a property of the scanner, not
of the store. Nothing in the store enforces it (`SetChildren` checks nothing about the block), and the walk oracle in the
tests reads the same marks, so a wrong registration is seen only by the order pins below.

Any later slice that changes how folders are registered owes one of two things:

1. keep depth-first preorder with a single `SetChildren` per whole block (this is also SL-109's rule, lesson #485: a
   block-wise writer clears the mark on a folder still being listed), and add a sweep folder larger than one flush block to
   `NotListedBelowTests`; or
2. replace `NotListedAtOrBelow` with a stored per-folder pending-subfolder count (below), and give it a 5M memory test.

That covers pause/resume (U-197, U-180), read-ahead, a block-wise flush and any reordering parallel listing.

## What a stored answer would cost (not chosen)

- **A Kind-byte bit** (U-201's `SubtreeUnfinished` at 0x80), set on a folder at append and cleared up the parent chain. It
  costs no storage, but every raw read of the Kind byte must be masked first, or a marked folder reads as a non-folder. The
  sites: `ScanStore.Kind()`, `Charges`, `CheckParent` (and the cast in its exception message), in `ScanStore.Rollup.cs`
  `RolledSize`, `FilesBelow`, `ChildrenLargestFirst` and `OnDiskAtOrBelow`, and now `NotListedAtOrBelow` (the entry check
  and the backward scan), plus the one write in `Append`. U-201 measured it at 0 added bytes (store 675 of 800 MiB) with
  all of them masked, and it still needs depth-first order.
- **A 4 B per-entry column**, about +19.2 MiB at 5M entries (U-201: store peak 694 MiB). Also under 800 MiB, and it still
  needs an order.
- **A per-folder pending-subfolder count** propagated up the parent chain: order-independent, which is what a queue or a
  parallel lister needs, paid on every write.

Flags bit 0x80 is already `NotListed` and is the last free bit of that byte (SL-109), so a second flag does not fit there.

SL-111 computes the answer because the scanner is depth-first, the store has about 146 MiB of headroom at the stopped peak
(lesson #483), and criterion 5 asks for no per-entry storage.

### Writing a marker into the Kind byte: the spellings, and what holds them

The first design added a fitness rule against writing a marker into the Kind byte. Review m1 found it catches a few spellings
only, and it was **dropped**: nothing in `.taad/fitness.yaml` changes, so no rules-harness run (Q-027) is needed. What holds the
class instead is `KindByteWriteCorpusTests` over `tests/FsAnalyser.Core.Tests/KindByteWriteCorpus.json`: a **closed
grammar** over `src/FsAnalyser.Core/Store/**/*.cs` that accepts the few forms a line may name the Kind column in and refuses
everything else, so a spelling nobody listed is refused with the ones that were. It accepts, as the column's uses:

- its one declaration, `byte[] Kind = new byte[PageSize];`;
- an element compared with `(byte)EntryKind.X` by `==` or `!=`, an element switched on, an element cast to `EntryKind`;
- the one whole write, `Kind[i] = (byte)kind;`, which the census requires to occur exactly once.

The class is bounded by who writes such a line: a slice that adds a Kind-bit marker to `ScanStore`, by accident or on purpose.
The members the corpus enumerates, all refused (K01 to K38), are: `|=`, `&=`, `^=`, `+=`, `-=`, `<<=`, `>>=`; `++` and `--`;
an assignment whose right side is not the whole kind (a built or computed value, a conditional, a helper that returns the
marked value); an alias of the column (an array, `var`, a `Span<byte>`, `AsSpan()`, `Array.Fill`, `Buffer.BlockCopy`,
`CopyTo`, enumeration, the column passed as an argument); a `ref`, `Unsafe`, `Volatile` or `Interlocked` access to an
element; the receiver spelled another way (spacing, `@Kind`, `this.`, a parenthesised receiver, none, a page looked up in
place); a helper that writes a value it was handed; a second statement on the same line; the column built or assigned
outside its declaration. Before the grammar, the two patterns the design had declared fire on 9 of those 38 and are quiet on
the other 29 (the corpus's `before` column).

What it cannot see (K39 to K41, pinned as `miss`): a marked `EntryKind` passed to `Append` or OR-ed into the `kind`
parameter on a line that does not name the column, and an identifier spelled with a unicode escape. A write from outside
`Store/**` is impossible while `Page` stays a private nested class. A slice that stores the marker masks every read above and
edits the grammar on purpose. A read spelling the grammar does not know (`is (byte)...`, a read into a local) is also
refused until someone adds it; that is what a closed grammar costs.

## Cost

Over all the rows of one folder, the read visits each entry below that folder **at most once**, because the chains of two
children lie in disjoint subtrees and each chain only moves to higher indices. So the worst shape at or under 5M entries is
S with 100,000 children, every tenth heading a chain 48 deep whose blocks list the subfolder first and nine files after it
(the whole block is scanned backward), the last chain half listed. Measured here, Debug, 5,000,000 entries, on this PC:
`ChildrenLargestFirst` plus all 100,000 answers, 10 passes, the first cold: 38.3 to 42.4 ms, of which the answers alone are
16.1 to 18.5 ms, against Core's 250 ms share of the window's 0.5 s open (spike 121: Release 29 ms, Debug 27 ms).
`ScanStoreRollupTimingTests.TheOrderedChildrenOfAHundredThousandChildFolderReadInUnderAQuarterSecond` measures the sort alone
at 20.5 to 40.2 ms.

The App is not wired in this slice: `FolderTreeNode` and the row text are untouched, so `ChildrenLargestFirst` is unchanged
and T-003 and the window cases are not in play. Showing the answer on stopped-tree rows is the next slice.

## The tests

All in `tests/FsAnalyser.Core.Tests`, on real temp trees (the 5M ones on synthetic stores driven the way the scanner drives
one).

- **Criterion 1.** `NotListedBelowTests.CancelAtAnyCallOfTheSweepTreeAnswersAsAWalk` and `...OfTheRaggedTreeAnswersAsAWalk`,
  each before and during every guarded call. For every store a Stop left, every entry's answer equals a full walk
  (`Walked`: every registered child of every entry, children first), every file and link reads false, and the depth-first
  pin holds. The sweep tree plants `m\Layers` through `NotListedTests.StopAt`/`Options` and requires a stop that sees the
  container store committed as `NotScanned` reading false; its non-vacuity floors count the stops inside `big`'s buffers and
  the stops that leave the root unlisted. The ragged tree (`RaggedTree`, 15 folders of ragged depth, a junction after a last
  subfolder, an empty folder, a last folder of files) requires a stop under a chain of listed last subfolders c > c2 > c21 >
  c211 over the unlisted c2111, stops before and after `a2` is listed, and a listed folder reading false under a root that is
  still not.
- **Criterion 2.** `TheScannerRegistersFoldersDepthFirstInListingOrder` pins the listing order (`FolderEnds` against
  `RaggedOrder` and `SweepOrder`, which covers folders listed empty) and the block order (`DepthFirstViolation`: the
  `FirstChild` of folders that have children strictly increases along the registered tree's preorder).
  `AQueueOrderedStoreBreaksTheAnswerAndTheGuardSeesIt` replays the ragged tree's shape in a fresh store, one block and one
  `SetChildren` per folder: last-first stack is the control (0 differing states, the store equals the scanner's entry for
  entry, the pin passes), a queue differs in at least one state and the pin throws.
  `AParallelOrderedStoreBreaksTheAnswerAndTheGuardSeesIt` does the same for two folders taken before either commits and for a
  first-first stack. The feeder is written fresh: `RollupOracle.Feed` registers per child and clears a mark at the first
  child (lesson #485).
- **Criterion 3.** `ScanStoreRollupTimingTests.AnsweringEveryRowOfAHundredThousandChildFolderInAStoppedFiveMillionStoreFitsTheBudget`,
  the shape above, exactly 5,000,000 entries, one entry unlisted; each pass under 250 ms with exactly one true row.
- **Criterion 4.** `NotListedTests.ACompletedScanLeavesNothingNotListed` (sweep tree with denied folder, skipped store,
  junction and hardlinks; the long-path tree) and `TheScannerRegistersFoldersDepthFirstInListingOrder` (both trees) read
  false everywhere; `AStopDuringSizingLeavesEveryFolderListed` does for a Stop in the probes.
- **Criterion 5.** `NotListedBudgetTests.AStoreStoppedAtItsUnlistedPeakFitsTheBudget` asks the read of all 4,879,990 entries
  before `retained` is measured: 0 bytes allocated (bound 64 KiB, against at least 4.6 MiB for one byte per entry), exactly
  60,002 true (the 60,000 queued folders, W and the root), each of the eight listed top folders false. Retained 637.1 MiB,
  peak 653.6 MiB against 800, unchanged from SL-109's figures.

What these do not catch: a per-block `SetChildren` inside a folder larger than the sweeps' 2,000-entry folder (lesson #485);
a block straddling a 65,536-entry page boundary (the read looks up each entry's page on its own, and SC-395 carries the test);
a loop that does not terminate on a malformed registration, because nothing checks that a chain step moves forward (SC-396);
the order changing at `Scanner._pending` and `FolderDone` with no comment there pointing here (SC-397).

## Decisions taken

- Name `NotListedAtOrBelow`; self-inclusive, unlike U-204's strict-descendant reading, because no consumer yet needs it
  strict and the goal reads "some folder at or below it".
- Placed in `ScanStore.cs` beside `NotListed`, not in `ScanStore.Rollup.cs`, whose header says a read there never walks below
  an entry. The doc comment describes the required order in words: a settled rule forbids the word `Scanner` in `Store/**`.
- The read goes over the page arrays, one page lookup per entry in the backward scan, not through the public reads; spike 121
  timed that faster (Release 29 against 39 ms, Debug 27 against 72 ms).
- The new tests use `NotListedTests`'s sweep helpers (`SweepTree`, `SweepOrder`, `Store`, `Baseline`, `RunBaseline`, `Options`,
  `StopAt`), made `internal` the way `TraversalTests.AssertChildrenAreContiguous` is.
- No runtime check in `SetChildren` and no rule on `Scanner`'s `Stack`: a runtime check would put block scans on the append
  path, and a text rule on `Stack<` misses a first-first push.
