# SL-056 "Size every file by asking it for its metadata": design as built

Every file a scan charges is now sized from its own metadata query, not from the folder listing's figure, and a
scan leaves the metadata of the files and folders it reads as it found them where the volume and the folder let it.
This amends SL-056's design with the review findings the person accepted (B1, M2 to M8, m1 to m8, M-add-1,
M-add-2) and what the build measured. Where this file and the design disagree, this file is right.

## The rule

`SizeProbe.IsEligible`: a file (a link is never one; EntryClassifier's surrogate-bit rule), not cloud-only, and the
first name the scan met for its file ID. It is read from the store's own record after the append, which has already
applied the dedup. There is no listing-driven gate left: the WOF-suspect, 64 MiB and first-repeat cases of SL-020 are
gone, because no figure in the listing says when the listing is stale. A file another process is growing lists 0 B
for as long as it is held open (U-018; this build saw it on this PC: a 257 MiB file listed at 0 B for 15 s and more,
until its handle closed), and a hardlink name not written through keeps its old size (U-005). The handle's figures
are exact in both cases, share mode 0 included (U-010).

- **A later name of a hardlinked file is never probed** (review M4). It charges nothing, every name of a file has the
  same handle-reported sizes (U-010), and the first name the scan met is the one probed. The inline repeat probe of
  SL-020 and the async-versus-inline race it created are gone.
- **An entry with no usable file ID** (the fallback listing class, some SMB servers, FAT) is always a first name, as
  the store's `HasFileId` already treats it (review m5): each of its names is probed and charged, the limit
  `FileIdsAvailable` reports.
- **A file gone by the time it is probed** (errors 2 and 3) keeps its listing sizes and is counted in
  `ScanResult.VanishedBeforeProbe`, apart from `ProbeFailures`, which are refusals (review m5). The window says so in
  its notes. **Accepted, not handled:** a path deleted and recreated between listing and probe has the new file's
  sizes applied to the old entry, because nothing compares FileIdInfo; a FileIdInfo query on every probe would add a
  call per file under the drive-time pressure M5 describes.

## The shape: list, then probe, then apply (review M-add-1)

The scan thread lists every folder first, exactly as before (single-threaded, listing order, which keeps first-seen
hardlink attribution repeatable: SL-042). Then it sizes the files: it builds a batch of paths from the store, hands
them to `ProbePass`, and applies every reading itself once the batch's workers have all finished. This is the shape
the risk spike measured on C: (probe-all-as-built-route-drive-time: 100.7 s listing, then 87.6 s probing with 16
workers, 188.3 s warm), chosen over overlapping the listing and the probes, which was never measured.

- **Batches, not one join** (a deviation from M-add-1's letter, not its intent). The fix described one results array
  for the whole drive, applied after one join. At about 4M files that is about 100 MiB of paths and readings, most of
  the headroom SL-042 and SL-044 measured under the 1 GiB budget, and M-add-1's own decision records that as unmeasured.
  So the pass works on batches of 32,768 files (`ProbePass.BatchSize`): the scan thread applies a batch only once
  every worker of that batch has finished, so single-writer and apply order hold per batch exactly as they would for
  one join, and at most one batch of paths and readings is held.
- **Workers**: `ProcessorCount - 1`, clamped to 1..15, one thread each per batch, background, **BelowNormal** priority
  so the window's thread stays ahead (review M5), each with its own `SynchronousIoCancel` made on its own thread
  (review m4; the guard refuses any other thread anyway).
- **Single writer, structurally** (review M2). `ProbePass` never sees the store: the rule that Core publishes no live
  `ScanStore` keeps the type out of it, and a worker gets paths in and readings out, nothing to apply with. Scanner's
  `Correct` and `Credit`, the only writes after an append, call `ThreadOwner.Check`, which throws on any thread but
  the scan's, so every scan in the suite enforces it; `ProbePass.Run` does the same.
- **Completion**: `Completed` only once every batch is applied (C-012). Progress has a sizing phase:
  `ScanProgress.FilesToSize` and `FilesSized`, and the window shows "Reading each file's own size: n of m" in place of
  the last folder. The totals and the top-level rows take each batch's corrections as it is applied
  (`TopLevelTracker.Corrected`, at most once per interval). **Cost**: a file keeps its listing figure until its batch
  is applied, a tail of about 90 s on C: by the spike's measure.
- **Cancel** (review M3, adapted: there is no Submit to block in this shape). A batch Cancel reaches ends `Cancelled`
  and the scan applies none of it, as a folder Cancel aborts commits nothing; the scan ends through
  `ScanStatus.Cancelled`, never an exception. Cancel interrupts the call each worker is blocked in, through its own
  guard. Workers still inside a call 1 s after Cancel (`ProbePass.DefaultAbandonAfter`), which an open on a share that
  stopped answering can be, are left to end on their own: they hold that batch's arrays, never the store, and write
  nothing else, and a pass that left workers behind runs no further batch.
- **A worker that throws** fails the scan: the other workers stop taking files, and `Scanner.Scan` throws an
  `InvalidOperationException` with the worker's exception inside once they have stopped. The window shows Scan failed.

## Counters and flags (review m2)

- `EntryFlags.SizeProbed` now marks every file whose probe succeeded, not only the ones a probe corrected.
- `EntryFlags.ProbeCorrected` (new, 32) is set with it when the file's own Size or OnDisk differed from its listing:
  the probe, not the listing, supplied the figure. `ScanResult.ProbeCorrections` (internal) counts them.
- `ScanResult.Probes` (internal) counts every probe made, whatever came of it: from a handful per scan to every file.
- Also internal, for the validate re-measurement: `ListingTime`, `SizingTime`, `LongestProbe`, `LastAccessAsked` and
  `LastAccessKept`.
- **Consumers.** A search of the App, the report writer (WindowReport.cs), tests/window and eng/ found no reader of
  `SizeProbed`, `ProbeFailed` or `Probes` outside Core and its tests. The window reads `ProbeFailures` for its notes and
  now `VanishedBeforeProbe` too, and `FilesToSize`/`FilesSized` for its progress line. The schema-1 report carries
  neither flag nor count, so it is unchanged.

## Last-access times (criterion 6's folder half)

`SynchronousIoCancel.OpenDirectory`, the one function that hands out a listing handle (review M8), takes
`keepLastAccess`. When asked, it opens with FILE_LIST_DIRECTORY | FILE_WRITE_ATTRIBUTES and, before the handle is
handed out, sets FILE_BASIC_INFO with LastAccessTime -1 and 0 in every other field, which changes nothing and tells
the file system not to update the last-access time for anything done through that handle. That is the sequence the
folder-atime-suspend-on-list-handle spike measured leaving a folder's four times, attributes and USN unchanged (3 of
3). Both steps are inside one bracketed call, so the scan thread's call count per folder is unchanged.

- **The payload** is one `static readonly` FILE_BASIC_INFO, and SynchronousIoCancel checks when it loads that the
  struct is 40 bytes with LastAccessTime at offset 8 and FileAttributes at 32, and refuses to run otherwise (review m7).
- **Where it is asked** (review M6). Only on a local NTFS volume, decided once at the root from the root's handle:
  `GetVolumeInformationByHandleW` must say NTFS and `FileRemoteProtocolInfo` must be refused (a share can call itself
  NTFS). Everywhere else (SMB, ReFS, FAT, exFAT) folders are listed as before. And never in a folder a sync engine
  manages: at or under a OneDrive folder the environment names (`OneDrive`, `OneDriveConsumer`,
  `OneDriveCommercial`: this PC's sync roots, U-004 and U-019), or a folder listed with RECALL_ON_DATA_ACCESS,
  RECALL_ON_OPEN, OFFLINE, PINNED or UNPINNED. **Not covered:** a folder another sync engine (Dropbox, Google Drive,
  iCloud, or the test provider's sync root under %TEMP%) manages without those attributes is not recognised, and gets
  the set. M6's other option, running the spike's check through the opt-in cloud rig (placeholder in-sync state
  unchanged, no provider callbacks), was not run: it registers a real sync root on this PC, the open question SL-049
  left for a person, so enabling the set in sync roots stays unproven and the environment's OneDrive roots are excluded.
- **Where the folder refuses** (review M7). A folder that refuses the write-attributes open or the set, as most of
  C:\Windows and Program Files do to an ordinary user, is opened plainly in the same call and listed exactly as before,
  and its last-access time may move. Only a failed plain open makes a folder unreadable. `LastAccessAsked` and
  `LastAccessKept` count the two outcomes.
- **So the guarantee is narrower than "nothing changes"**: it holds for folders on local NTFS, outside the recognised
  sync roots, whose ACL grants the user FILE_WRITE_ATTRIBUTES. Per review M1 (overridden by the person), criterion 6 is
  read as each file's and folder's own metadata through a handle; the scan's metadata open of a file still makes
  Windows correct a stale directory entry's cached size (SL-045), which `dir` and Explorer then show.
- **Controlled Folder Access**: whether an attribute-only set on a protected folder raises a notification was not
  found. Validate records `Get-MpPreference` EnableControlledFolderAccess next to the fsutil state.

## How the tests observe (review B1)

`HeldOpenTreeTests` builds one tree and scans it once (after one scan under the replaced rule, below). Opening a file
makes Windows refresh its stale directory entry (SL-045), and a plain listing moves a folder's last-access time, so:
nothing opens a held file or the stale hardlink name before the scan returns; held files are observed only through
their holders' own handles (tests/HoldRig `hold`), the hardlinked file only through the name it was written through,
everything else through HoldRig's handle-only `snapshot`, which lists nothing; everything observed is back-dated 48 h
(NTFS records an access only when the stored time is over an hour old); a control folder outside the root must show
its last-access time move under a plain listing; `fsutil behavior query disablelastaccess` is recorded; and the last
step before the scan is the listing's own reading of the held files, written to the test output. "The listing still
says 0 B" is read as: until the scanner lists the file, because the scanner's own probe then refreshes the entry.

Criterion 7's expected folder totals come from those readings, never from a listing or an open that races the scan:
plain files from the snapshot, held files from their holders, and the hardlinked file once, charged to `links\a-first`,
the folder of the name the scan meets first by name order (review m6).

**The baseline** (B1 fix 5): the fixture first scans the same tree under the rule SL-056 replaced, through
`ScanOptions.ProbeRule` (a reproduction of SL-020's IsDue, without its first-repeat probe), and
`TheRuleSl056ReplacedFailsCriteria1_3And7` asserts that the checks of criteria 1, 3 and 7 fail under it. At build, the
old gate was also put back into Scanner's default rule by hand: the Facts for criteria 1, 3 and 7 and both C-011 tests
failed, and passed again once the file was restored (hash checked).

## Drive time (advisory) and network volumes (review M5, M-add-2)

None of the person-agreed criteria names a time budget, and a miss is not a failure of this slice (M-add-1's decision,
after Q-097); the design's `would_force_resynthesis` is false. Validate re-measures the spike's method on C: with the
built scan and reports the phases from `ScanResult`: `ListingTime` (the walk, with the kept and fallback opens in it),
`SizingTime`, `LongestProbe`, and `LastAccessAsked` against `LastAccessKept` (how many folders took the fallback open).

On a network volume every probe is its own create, query and close round trip, and nothing limits probing by volume
type; nothing measured it. The old rule probed a handful of files per share; this one probes every file. Validate
extends the re-measurement to the loopback SMB rig (opt-in, Trait Network=SMB-LOOPBACK, which changes SMB client
mappings and so needs a person's go-ahead) and reports per-probe latency and total probe time beside the listing time.
If the ratio is large, a person decides, rather than someone finding out in use.

## Architecture rules (settled, in .taad/fitness.yaml; no xUnit twin)

| Rule | Files | Must not match |
|---|---|---|
| Probe results are applied only on the listing thread | `Scanning/ProbePass.cs` | `.Correct(`, `.Credit(`, `CorrectSizes`, `AddFlags` |
| Only the directory-listing open path may write a file-system timestamp | `src/FsAnalyser.Core/**` except `Native/Kernel32.cs`, `Scanning/SynchronousIoCancel.cs` | `SetFileInformationByHandle`, `SetFileTime`, `NtSetInformationFile`, `SetFileAttributes*`, `File/Directory.Set(LastAccess/LastWrite/Creation)Time`, `.…Time(Utc) =` |
| The size probe's open stays on CreateFileW | `Scanning/*.cs` | `NtOpenFile` |
| Only DirectoryReader lists a directory | `src/FsAnalyser.Core/**` except `Scanning/DirectoryReader.cs`, `Native/Kernel32.cs` | `FindFirstFile*`, `NtQueryDirectoryFile*`, the `File…Directory…Info` classes, `Directory.Enumerate*/GetFiles/…`, `.EnumerateFiles(`…, `FileSystemEnumerable` |

The twinned `InteropLivesOnlyInNative` now also exempts `tests/HoldRig/**` (both copies): the rig is a process of its
own that reads metadata through handles, independent of Native on purpose.

## Prior art, corrected (review m8)

- A held-open file's directory entry lags the file: the listing reads 0 while GetFileSize reads the true size, and
  closing the handle brings `dir` and Explorer up to date (The Old New Thing, 2011-12-26,
  https://devblogs.microsoft.com/oldnewthing/20111226-00/?p=8813). Not on that page, and measured here: opening the
  file for its metadata from another process refreshes the entry too (SL-045).
- A per-file metadata query costs more than a listing: measured, not cited (U-012: 5.3 to 5.8 times a listing-only
  pass on C:, warm).
- A cached FileInfo reports a stale length once it has read one; the probe never goes through a caching .NET API and
  always opens a fresh handle (https://stackoverflow.com/questions/7828132/).

## Cases and their tests

| Case | Test |
|---|---|
| C-001 criterion 1 | `HeldOpenTreeTests.AFileAnotherProcessIsGrowingIsChargedAtItsOwnSize` |
| C-003 criterion 3 | `HeldOpenTreeTests.AFileHeldWithNoSharingIsChargedAtItsOwnSize` |
| C-006 criterion 6 | `HeldOpenTreeTests.TheScanChangesNoEntrysMetadata` |
| C-007 positive control | `HeldOpenTreeTests.APlainListingOfTheControlFolderMovesItsLastAccessTime` |
| C-008 the kept listing | `LastAccessTests.AListingThatKeepsTheLastAccessTimeLeavesItWhereAPlainOneMovesIt` |
| C-009 the ACL fallback (material, M7) | `LastAccessTests.AFolderThatRefusesWriteAttributesIsListedPlainlyAndCountedInFull` |
| C-010 criterion 7 | `HeldOpenTreeTests.EachFolderIsTheOnDiskBytesBelowIt` |
| C-011 the widened rule | `OnDiskSizeTests.ASmallFileWhoseListingSaysNothingIsChargedAtItsOwnSize`, `HardlinkTests.ALoneNameUnderTheRootIsChargedAtItsOwnSize` |
| C-012 complete only once applied | `ProbePassTests.TheScanEndsOnlyOnceEveryProbeIsApplied` |
| C-013 a later name met before the first name's probe | `ProbePassTests.ALaterNameMetBeforeTheFirstNamesProbeIsNeverProbedOrCharged` |
| M3 fault and cancel | `ProbePassTests.AProbeWorkerThatThrowsFailsTheScanInsteadOfHanging`, `…CancelWhileWorkersAreStuckEndsTheScanCancelledWithinTheBound`, `…CancelInterruptsTheCallAWorkerIsBlockedIn` |
