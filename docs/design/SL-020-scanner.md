# SL-020 "Point it at a drive or folder": design as built

This amends SL-020's design so it matches the code, the person's decisions (O-001, and the review
findings accepted as O-002 to O-013), and what the build measured. Where this file and the original
design disagree, this file is right. Do not "correct" the code back to the original text.

## Amended components

**ScanStore** (`src/FsAnalyser.Core/Store/ScanStore.cs`)
- Repeated FileIds are found by a custom open-addressing table (`int[]` of entry index + 1, linear
  probing, splitmix64 over the FileId columns, load at or under 70%). It is **not** a BCL
  `HashSet<int>`. That was O-001 rule (c). It is the only structure in the store that rehashes as it grows.
- Layout rules from O-001, all held: (a) columns in fixed 65,536-entry pages and names in fixed
  1M-char pages, with page directories preallocated so that nothing moves or doubles; (b) no
  per-entry object, and paths rebuilt from `Parent`; (c) the table above. Measured at U-001's 5M shape:
  555.9 MiB retained, 569.9 MiB peak (`ScanStoreBudgetTests`).
- **Byte accessors (O-002).** `ChargedSize(i)` and `ChargedOnDisk(i)` return 0 for `Link` and for
  `HardlinkDuplicate` entries, and `Size`/`OnDisk` for every other entry. Anything that adds bytes up
  (rollups, largest-first order, the kind breakdown) sums these, never `Size()`/`OnDisk()`. Summed over
  entries 1..Count-1 they equal `Totals.Bytes`/`Totals.BytesOnDisk`, and every scan in the tests
  asserts that (`Scans.AssertChargedBytesAreTheTotals`). A duplicate's own `Size` is its listing value,
  which can be stale. A display accessor that follows a duplicate to its charged record is left to
  the tree slice, the first reader that shows per-entry sizes.

**EntryClassifier.** A Link is a reparse point with tag 0xA0000003 (junction or mount point),
0xA000000C (symlink) or **0x8000000A (DFS)** (O-005). Entering a DFS link would continue the scan on
another server's volume, and both the FileId dedup key (no volume part) and the listing class chosen
once per scan assume the scan never leaves its volume. `CloudOnly` is set on **files only**. A folder
carrying RECALL attributes is a Folder and is entered.

**SizeProbe** (O-004). It runs in **three** cases, never twice for one file, and never for a
cloud-only file:
1. a WOF suspect (listing AllocationSize 0, EndOfFile at least 4096, not sparse);
2. a **large file**: listing EndOfFile or AllocationSize at least 64 MiB (`LargeFileThreshold`);
3. the first repeat of a FileId, unless the charged record is already probed, probe-failed or cloud-only.

**DirectoryReader.** An ERROR_FILE_NOT_FOUND on the first (RestartInfo) call is an empty listing, as
well as ERROR_NO_MORE_FILES. A volume root has no "." or "..", so an empty FAT or exFAT stick answers
this way. Confirming it on a real stick is left to validate.

**Main window.** It has **four** endings: Finished, Stopped, Could not read, and **Scan failed** (O-008).
Scan failed covers any exception other than the ArgumentException for a relative or empty path, which
the window recognises by `ParamName == "root"` (O-015). Any other ArgumentException, including an
ArgumentOutOfRangeException from the store, is a bug and gets Scan failed. It
shows the exception type and message and re-enables the pickers. On a drive-root scan, Finished and
Stopped also show the volume's used space (DriveInfo TotalSize − TotalFreeSpace) and how much of it
the scan did not see (O-013). The optional path argument goes through `CommandLine.RootArgument`
(O-009). Windows argv turns a typed `"C:\"` into `C:"`, and a trailing quote can only come from that,
so it becomes `\` again.

**TempTree fixture.**
- A denied probe needs the raw mask `SYNCHRONIZE | FILE_READ_ATTRIBUTES` (0x100080), written through
  SDDL. A deny-ReadAttributes ACE does not stop a FILE_READ_ATTRIBUTES open: the parent folder's list
  right grants it (measured, error 0). `FileSystemAccessRule` also silently drops SYNCHRONIZE from
  deny rules.
- The sparse file with 4 KiB written must be flushed (FlushFileBuffers) before scanning. Until then
  NTFS itself, through both the listing and an opened handle, reports nothing allocated.
- Error 1314 when creating a symlink has no workaround. .NET already asks for unprivileged creation,
  which works only under Developer Mode. The fixture fails with a message saying Developer Mode is off
  or blocked by policy (O-010). There is no CreateSymbolicLinkW fallback, and nothing is skipped (Q-001).

**Solution scaffold.**
- `App.csproj` sets `ServerGarbageCollection=false`. The design's `ServerGarbageCollector` is not an
  MSBuild property.
- `eng/dotnet.ps1` installs a pinned **SDK 10.0.401**, the version measured, with `-Version`, not
  `-Channel 10.0`. It runs the downloaded `dotnet-install.ps1` only if `Get-AuthenticodeSignature`
  reports `Valid` with an `O=Microsoft Corporation` signer (O-011). `global.json` is unchanged:
  10.0.100, rollForward latestFeature.

## Amended contracts

- `ScanResult.Root` (public): the root as normalised for the scan (for example `C:\`). The UI names it
  in every ending.
- `ScanTotals` count every entry **under** the root. The root itself is not counted.
- `ScanStore.ChargedSize(int)` and `ScanStore.ChargedOnDisk(int)` are the public byte accessors (see above).
- `CommandLine.RootArgument(string) -> string` (public, Core): the argument mapping above.
- A pre-cancelled token returns `Cancelled` with an empty store (Count 0) and does no I/O.
- Internal to tests only: `ScanResult.Probes`, `ScanStore.FileIdTableSlots`.

## Key decisions added or changed

- **Large files are probed (O-004, option a).** Measured on this PC: after a writer flushes, the
  directory entry of a file it still holds open matches the file (64 MiB listed and real). Between an
  unflushed write and the lazy writer, the entry lagged: 1 MiB listed against 64 MiB real, caught up
  within 2 s. The rule catches large files that keep growing, the ones that carry a drive's bytes.
  It does not catch a file listed under 64 MiB in that short window, and that is accepted. On C:
  (warm cache, WSL not running) the rule added 964 probes to 259,041 (the rest are WOF suspects and
  first hardlink repeats). None of the 964 disagreed with its listing at the time, and the scan took
  61.7 s. The new probe failures are the paging files (error 32, sharing violation) and two protected
  large files (error 5). Their listing values are kept, and the Finished note counts them.
- **The coverage gap is shown but not explained (O-013).** A drive-root scan shows used space and the
  bytes it did not see. Explaining the gap (NTFS metafiles such as $MFT, a breakdown of the unreadable
  folders, the coverage panel) is left to a later slice: the architecture's "panel for unreadable
  folders and the coverage gap". The registered read-denied-folders operation can measure how much of
  the gap the unreadable folders hold.
- **Win32 paths.** Every CreateFileW takes a `LongPath.Child` or normalised-root `\\?\` path. The
  hardlink probe opens the repeat name's own path in the folder being listed. It does not open the
  charged record's path. The extended long-path case now probes a WOF file and a hardlink pair under a
  path over 400 characters with trailing-dot names. A `Win32Path` type and `ScanStore.GetNativePath`
  wait for the slice that adds a third Win32 caller (open in Explorer, delete).
- **Cancel interrupts a blocked call (SL-024, replacing the m7 deferral).** Every Win32 filesystem call
  the scan makes goes through the scan thread's `SynchronousIoCancel`, which brackets it. Cancel
  interrupts the call in progress with CancelSynchronousIo, sent every 25 ms from a background
  `scan-cancel` thread until the call returns. The token's callback only starts that thread, so
  Cancel() on the UI thread never blocks. A call that a cancel was issued during, or that started after
  Cancel, is **discarded and reported as 995**, whatever Windows returned. A cancelled cloud listing
  returns SUCCESS with nothing in it (measured), so only this recorded intent tells it from an empty
  folder.
  - **Under a requested cancel, 995 is cancellation, never an unreadable folder.** The folder it hit
    commits nothing and ends as the scan leaves every folder it never reached. With no cancel requested,
    995 is `Unreadable(995)` like any other error.
  - **Probes run before their folder is appended**, so an aborted probe aborts its folder whole. The
    probe of a first repeat runs while the folder is appended. If that probe is aborted, the charged
    record stays as it would be had the repeat never been reached, and the folder is committed whole.
  - **Handles of aborted calls are closed on a pool thread**, because on a share that stopped answering
    the close can wait on the server.
  - **The guard belongs to one thread.** `Enter` throws on any other thread. A later slice that lists
    in parallel gives each worker its own guard.
  - **What is guaranteed.** Cancel() returns promptly on the UI thread in every case. The scan returns
    within 2 s of Cancel for the shapes that were measured:
    - an open blocked on an unreachable share (spike 1: 17 ms or less);
    - a local synchronous pipe wait (the test suite);
    - a listing blocked on a cloud placeholder whose provider never answers (build-slice 2 re-spike, run
      with the scanner's real calls: 1 ms or less, and CloseHandle 0 ms afterwards).

    Two shapes are **not** guaranteed, because nobody could measure them here: a share that stops
    answering once its session is established (spike 3 never ran), and I/O held by a filter driver
    (CancelSynchronousIo can itself wait behind on-access antivirus).
  - **The TEST-NET-1 share tests are opt-in (m5).** `CancelReturnsFromAScanBlockedOnAnUnreachableShare`
    and `CancelReturnsFromTheSameUnreachableShareScannedTwice` carry `Trait("Network", "TEST-NET-1")`.
    The test project's default `VSTestTestCaseFilter` leaves them out of every run that names no filter.
    The accepted risk: a UNC path by IP address signs in with NTLM, so on a hostile network whoever
    answers on port 445 gets this user's NTLMv2 response, and a network that fails fast turns them red.
    The default suite proves the same interrupt with a stand-in instead. That test is
    `CancelReturnsFromAScanStuckInItsFirstCall`, whose first call is stuck in a synchronous pipe wait.

## Architecture rules (widened; enforced by `tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs`)

| Rule | Files | Must not match |
|---|---|---|
| The scanner core does not know about the screen | `src/FsAnalyser.Core/**` | `using System.Windows*`, `PresentationFramework*` or `FsAnalyser.App*`; `<UseWPF>\s*true`; `<UseWindowsForms>\s*true`; `<ProjectReference[^>]*FsAnalyser\.App` |
| Only the classifier reads the reparse attribute (**widened**, O-006) | `src/**` except `Scanning/EntryClassifier.cs` and `Native/Kernel32.cs` | `FileAttributes\.ReparsePoint`, `FILE_ATTRIBUTE_REPARSE_POINT`, `IsReparsePoint`, `0x0*400\b` |
| The scanner lists directories only through DirectoryReader | `src/FsAnalyser.Core/Scanning/**` | `Directory\.(Enumerate\w*\|GetFiles\|GetDirectories\|GetFileSystemEntries)`, `new\s+(DirectoryInfo\|FileInfo)\b`, `FileSystemEnumerable`, `EnumerationOptions`, `AttributesToSkip` |
| Counting-rule tests are never skipped (**extended**, O-007) | `tests/FsAnalyser.Core.Tests/**` | `\[(Fact\|Theory)\([^)]*\bSkip\w*\s*=`, `Assert\.Skip\w*\(`, `Skip\.(If\|IfNot\|Unless)`, `\bExplicit\s*=\s*true`, `Skip(?:Exception)` |
| Interop lives only in Native (**new**, O-003) | `src/**`, `tests/**` except `src/FsAnalyser.Core/Native/**` and `tests/FsAnalyser.Core.Tests/TestNative.cs` | `(Library\|Dll)Import` |
| Filesystem calls go through the cancel guard (**new**, SL-024) | `src/**` except `Native/Kernel32.cs` and `Scanning/SynchronousIoCancel.cs` | `\bKernel32\.(CreateFile\|GetFileInformationByHandleEx)\s*\(` |
| The scanner makes no BCL file-system calls (**new**, SL-024) | `src/FsAnalyser.Core/Scanning/**` | `\b(File\|Directory\|DirectoryInfo\|FileInfo\|DriveInfo)\.` (not `Path`: lexical, no I/O) |

xUnit stays on v2 (2.9.3, from `dotnet new xunit` under SDK 10.0.401). `--fail-skips` waits for a move to v3.
