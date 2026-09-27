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
- **Byte accessors (O-002).** `ChargedSize(i)` and `ChargedOnDisk(i)` return 0 for `Link`, for
  `HardlinkDuplicate` entries and, since SL-042, for every `Folder` whatever size it states;
  `Size`/`OnDisk` for a File that is charged (see docs/design/SL-042-folder-totals.md). Anything that adds bytes up
  (rollups, largest-first order, the kind breakdown) sums these, never `Size()`/`OnDisk()`. Summed over
  entries 1..Count-1 they equal `Totals.Bytes`/`Totals.BytesOnDisk`, and every scan in the tests
  asserts that (`Scans.AssertChargedBytesAreTheTotals`). A duplicate's own `Size` is its listing value,
  which can be stale. A display accessor that follows a duplicate to its charged record is left to
  the tree slice, the first reader that shows per-entry sizes.

**EntryClassifier.** A Link is a reparse point whose tag has the **name-surrogate bit, 0x20000000**
(SL-043, criterion 4 and U-004; it replaced O-005's list of junction, symlink and DFS tags). The
attribute is tested first, and the tag is never read without it: on the fallback listing class that
field is EaSize. Junctions, mount points and symlinks carry the bit and stay links; so do WSL symlinks
and every other surrogate tag, which were files or folders before. Cloud tags (0x9000x01A), tag 0 and
**DFS (0x8000000A)** carry no surrogate bit, so they are entered or counted as data. What that means for
DFS (SL-043, m1, from the critique and not measured on this machine): through a share the SMB client
follows a DFS folder's referral, so the scan goes on in the target server's share, following the one
kind of link it does not stop at, and the FileId dedup key (no volume part) and the listing class chosen
once per scan then span two volumes. Whether a client's listing of a namespace share shows the DFS tag at
all was not measured either. The criterion asks for this rule, and it was accepted with that
consequence, which only DFS namespaces meet; an unreadable-folder record does not cover it, because
nothing fails. `CloudOnly` is set on **files only**. A folder carrying RECALL attributes is a Folder and
is entered.

**SizeProbe** (O-004). *Superseded by SL-056 (docs/design/SL-056-probe-every-file.md): every file the scan
charges is probed, never a later name of a hardlinked file and never a cloud-only file, after the listing walk and on
worker threads.* As SL-020 built it, it ran in **three** cases, never twice for one file, and never for a
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
the scan did not see (O-013). A scan that was cancelled, before or during that query, omits the volume
line (SL-024, O-031; see below). The optional path argument goes through `CommandLine.RootArgument`
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

- **Large files are probed (O-004, option a).** *Superseded by SL-056: the gap accepted below is closed by probing
  every file, since a file held open and growing can list 0 B for as long as it is held (U-018).* Measured on this PC: after a writer flushes, the
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
  charged record's path. *Since SL-056 the probe opens each charged file through its own name's `\\?\` path, built
  from the store after the walk, and a repeat name is never opened.* The extended long-path case now probes a WOF file and a hardlink pair under a
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
    *Superseded by SL-056: probes run on worker threads after the walk, a batch at a time, each worker with its own
    guard; a batch Cancel reaches commits nothing, as a folder Cancel aborts commits nothing.*
  - **Handles of aborted calls are closed on a pool thread**, because on a share that stopped answering
    the close can wait on the server.
  - **Only the guard's own abort empties a listing.** `LastCallAborted` means the call was refused, or a
    cancel was issued during it. A 995 that Windows returns with no cancel issued is an error like any
    other: the folder is `Unreadable(995)` and keeps what was listed before it (O-032).
  - **The volume's used space is read by the scan and waited for by nobody (O-031, placement moved by
    SL-043).** The guarantee is unchanged: a volume that does not answer cannot hold the scan or the
    window. Only where the read lives moved, because SL-043's criterion 6 has the scan record the figure
    alongside the tree. For a whole-drive root (`X:\`, after any `\\?\`: the App's old rule, moved and
    not widened), `Scanner` starts `VolumeSpace` as the first thing a scan does. It reads
    GetDiskFreeSpaceExW (total less free, measured equal to Get-Volume's Size minus SizeRemaining) on a
    background thread of its own, outside the cancel guard, with the critical-error box off for the call.
    The figure lands in `ScanStore.VolumeUsed`, a `Task<long?>`: null at once for a folder or a share,
    null when the read fails, and null when it has not answered within its bound (30 s). Why not guard
    it: on a stalled share even CancelSynchronousIo waits for the SMB client (62 to 66 s measured), so
    only not waiting for it frees everything on every shape.
    - Nothing in Core waits on the figure, and the thread is handed only the completion source: a read
      stuck on a dead drive parks that one thread and keeps no scan's store alive (architecture rules
      NothingWaitsOnTheVolumeFigure and TheVolumeReadHoldsNoScanStore; `VolumeUsageTests` shows a scan
      that completes, and one that is cancelled, both ending while the read is stalled, and the store of
      a dropped scan collected while it is).
    - The window shows the ending at once, without the volume line, and adds the line when the figure
      lands, from a continuation that holds the window and the scan's generation, never the result. A
      figure for a scan the window has moved on from is dropped. `EndingShown` waits for the line, so the
      report of a whole-drive ending carries it (window case V-001). A stopped whole-drive scan now shows
      the line too, when the figure landed; before SL-043 it never did.
  - **The guard belongs to one thread.** `Enter` throws on any other thread. A later slice that lists
    in parallel gives each worker its own guard.
  - **What is guaranteed.** Cancel() returns promptly on the UI thread in every case. The scan returns
    within 2 s of Cancel for the shapes that were measured:
    - an open blocked on an unreachable share (spike 1: 17 ms or less);
    - a local synchronous pipe wait (the test suite);
    - a listing blocked on a cloud placeholder whose provider never answers (build-slice 2 re-spike, run
      with the scanner's real calls: 1 ms or less, and CloseHandle 0 ms afterwards).

    Two shapes are **not** covered by the 2 s:
    - **A share that stops answering once its session is established.** Measured in SL-024 build 3 with
      `StalledShareTests`: an SMBLibrary server on 127.0.0.1, reached through `New-SmbMapping -TcpPort`,
      that stops answering at one folder's listing. Cancel() returned in about 1 ms. The SMB client held
      the listing, and the canceller's CancelSynchronousIo with it, until it gave up on the server, and
      the scan returned **62 to 66 s** after Cancel (two runs). It ended Cancelled, with the stalled folder not committed
      and its handle closed on a pool thread. Only abandoning the scan thread would return sooner, and
      SL-038 chose not to (below).
    - **I/O held by a filter driver.** CancelSynchronousIo can itself wait behind on-access antivirus.
      This is unmeasured.
  - **What Cancel does on a share that stopped answering: wait, and say so (SL-038).** The window waits
    for the scan to return and never abandons the scan thread. From 2 s after Cancel the Stopping state
    shows a line under its title (`StoppingDetail`, worded by `StoppingWait.For`, a pure function of the
    time since Cancel): "Still waiting for the drive to answer (N s). If a network connection dropped,
    this can take a minute or more. Closing the window quits at once." From 90 s it drops the estimate:
    "Still waiting for the drive to answer (N s). Closing the window quits at once." N counts up from a
    Stopwatch. There is no countdown, no end time and no new button. The line is derived on every 250 ms
    progress tick and cleared in the scan's one `finally`, however the scan ended.
    - *Why not abandon the thread (option a).* SL-038's spikes measured three things. No in-process cancel
      returns the call sooner: CancelSynchronousIo waited 57 to 66 s in 15 of 15 runs (error 121), and
      CancelIoEx on the find handle failed with error 6. The UI thread kept running through a 64 to 70 s
      stall (longest tick gap 237 ms, never hung). Closing the window ended the process during the stall
      (window gone within 29 ms, process within 345 ms, 6 of 6 runs). All abandoning would still buy is a
      new scan within that minute without relaunching. It would cost a late-write fence in the Scan
      Engine at the counting rules' risk tier, an abandoned store held for an unbounded time against the
      1 GB budget (699 MiB peak measured at 5M entries, U-001), and a scanner lifecycle change that is
      expensive to back out of. Waiting is a UI change that leaves the scanner alone and does not rule
      (a) out later.
    - *What is not measured.* No real disconnect was timed (Wi-Fi off, a VPN or Tailscale tunnel down,
      sleep and resume mid-scan: 0 of 9 runs, because they change the person's network state). "A minute
      or more" rests on the loopback rig and this PC's SMB SessionTimeout of 60 s, which is why the line
      gives no frequency and no upper bound. "Closing the window quits at once" also rests on the rig
      only, where the connection stays up and the server simply stops answering. The protocol a person
      runs to settle both: for each of Wi-Fi off, `tailscale down` and a sleep of about 2 minutes, 3 runs
      that scan a slow share on a second host, cut the link, press Cancel 3 s later and time Cancel to
      scan return with the link kept down; and one more run per mode that closes the window while the
      line shows and times how long until the window is gone and until `Get-Process` no longer lists the
      app.
    - *What would reopen it.* A real disconnect whose scan never returns or waits many minutes, or a
      close that hides the window while the process lingers with the scan still in memory.
    - *Who decided.* Option (b) was built as the design recommended, on the fork decision recorded as
      SL-038 O-002. The person's own acceptance (C2) is still open. Choosing (a) replaces this rather
      than extending it.
    - *How it is tested.* `StoppingWaitTests` (Core.Tests, which compiles `StoppingWait.cs` by link) pins
      the wording at 1.999 s, 2 s, 89.999 s, 90 s and negative spans; the rig cannot reach the 90 s
      wording, because its client gives up at about 60 s. The default window run proves only that the
      line does NOT appear on an ordinary cancel (C-006). `WindowCases.ps1 -IncludeNetwork` adds N-001
      (the line first shows 1.9 to 3 s after Cancel, counts up, the UI thread never goes a second
      unheard, the line is gone when the scan returns), N-002 (closing ends the app, with and without a
      Cancel first) and N-003 and N-004 (rig isolation). They change the machine's SMB mappings, so they
      are opt-in. `tests/window/HookCases.ps1` covers the hook's armed mode on a local tree, with no
      share: an armed close decides on ScanningPanel, not CancelButton (H-001), and a marker that never
      appears, an armed stop with no Cancel delay and an armed throw are refused with the reason (H-002).
      Two endings have no test at all: a scan that faults while the line shows, and a scan that finishes
      on its own after Cancel. Both go through the same single `finally` that clears the line, the
      shipped exe has no seam that could produce them, and the rule "the shipped app carries no test seam"
      forbids adding one. They are deferred, not tested. The cancelled ending with the line on screen is
      proved only by N-001.
    - *The person-run network check.* A person runs `WindowCases.ps1 -Case All -IncludeNetwork` under
      pwsh, then the Core.Tests project filtered to `Network=SMB-LOOPBACK` (StalledShareTests). The record
      carries the HEAD it ran at, pass or fail for N-001 to N-004 and StalledShareTests, and N-001's
      measured Cancel-to-return. No stage runs it. A stage that finds no record, or a stale one, reports
      it as owed to a person once and does not retry.
    - *When that pass goes stale.* Any change, between the HEAD the pass ran at and the HEAD being
      judged, to any of these paths makes it stale, whatever the change is. Nobody has to judge whether
      a change is "only a comment": `git diff --name-only <pass HEAD> <HEAD> -- <paths>` decides.
      - `src/FsAnalyser.App/MainWindow.xaml`, `src/FsAnalyser.App/MainWindow.xaml.cs`,
        `src/FsAnalyser.App/StoppingWait.cs`, `src/FsAnalyser.App/App.xaml.cs`
      - `src/FsAnalyser.Core/Scanning/**`
      - `tests/window/FsaWindowHook/**`, `tests/window/StallRig/**`, `tests/window/WindowCases.ps1` (the
        whole file)
      - `tests/FsAnalyser.Core.Tests/Rig/SmbRig.cs`, `tests/FsAnalyser.Core.Tests/StalledShareTests.cs`,
        `tests/FsAnalyser.Core.Tests/FsAnalyser.Core.Tests.csproj`
      - `eng/read-window.ps1`

      There is one named exemption. SL-038 build 2 reworded two comments in SmbRig.cs, from "on this
      machine" to "per sign-in session", with no code change and `RigLock.MachineName` left as it was. That
      takes SmbRig.cs from blob `d2e3435d138f91b3aeb0101356137d7277af367c` (at eafb1c3, where the person's
      N-001 to N-004 pass was run) to blob `8fdf6e87345668eafc91b09015bdf83997820801`. SmbRig.cs does not
      make the pass stale while `git rev-parse <HEAD>:tests/FsAnalyser.Core.Tests/Rig/SmbRig.cs` is
      exactly that second blob. The exemption names the file's content, not the commit, because the
      builder does not commit. StalledShareTests has never passed on the extracted rig, so the record is
      incomplete until a person runs it.
    - *The rig* (`tests/FsAnalyser.Core.Tests/Rig/SmbRig.cs`, also built as `tests/window/StallRig`)
      serialises runs with one mutex per sign-in session, because the SMB client allows one port per
      server name: a second run waits for the first, or fails saying so. Under the mutex it removes rig
      mappings a killed run left behind before making its own. That sweep is the only code in this slice
      that deletes anything on the person's machine. It deletes a mapping only when the one predicate
      `LoopbackShare.IsRigMapping` matches it: exactly `\\127.0.0.1\` followed by `fsa` in lower case and 8
      hex digits in either case. The default-run `SmbRigTests.OnlyTheRigsOwnMappingsAreSwept` calls that
      same predicate and must keep every other shape: another server (the person's own
      `\\100.109.48.75\collab`), 127.0.0.2 and 127.0.0.10, `localhost`, the IPv6 literal
      `--1.ipv6-literal.net`, 7 or 9 digits, a non-hex digit, a sub-path, `FSA` in capitals, another
      share name, a drive letter and the empty string.
    - *StallRig's launch.* `StallRig.exe --marker <file> [--wait-sec <n>]`. `--wait-sec` is how long a
      run waits for another run's lock. It defaults to 150 s (`LoopbackShare.WaitForOtherRun`, about two
      stalled-share runs), so a wait is always bounded. After that the rig exits 1 without mapping
      anything, and stderr carries the TimeoutException that names the lock (`Local\FsAnalyser-SmbRig`)
      and why two runs cannot share 127.0.0.1. Bad arguments exit 2 with a usage line. A failed mapping
      exits 1 with the error. When stdin closes, it unmaps its own share and exits 0. N-003 asserts the
      fail-at-once branch (`--wait-sec 2` exits 1 within 30 s) and the waiting branch (a run started
      while the first lives prints no share until the first ends) separately, rather than the 150 s
      default, which would add two and a half minutes to the person's run.
  - **The loopback SMB test is opt-in too.** `CancellingAScanOfAShareThatStoppedAnsweringNeverHangsInTheClose`
    carries `Trait("Network", "SMB-LOOPBACK")`. It needs the Windows 11 24H2 SMB client (`-TcpPort`)
    and takes about 70 s. It uses SMBLibrary and SMBLibrary.Win32 (LGPL-3.0-or-later) in the test
    project only. SMBServer's public `Start` listens only on port 445, so the fixture calls its private
    overload that takes a port.
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
| The classifier lists no link tags: the surrogate bit is the rule (**new**, SL-043) | `src/FsAnalyser.Core/Scanning/EntryClassifier.cs` | `(?i)IO_REPARSE_TAG_(MOUNT_POINT\|SYMLINK\|DFS)\b\|0x(A0000003\|A000000C\|8000000A)\b` |
| Nothing waits on the volume figure (**new**, SL-043, O-031) | `src/FsAnalyser.Core/**`, `src/FsAnalyser.App/**` | `(?i)VolumeUsed\w*\.(Task\.)?(Wait\|WaitAsync\|GetAwaiter\|Result)\b`, `(?i)\bawait\s+[\w.]*VolumeUsed` |
| The volume read holds no scan store (**new**, SL-043) | `src/FsAnalyser.Core/Scanning/VolumeSpace.cs` | `\bScanStore\b` |

xUnit stays on v2 (2.9.3, from `dotnet new xunit` under SDK 10.0.401). `--fail-skips` waits for a move to v3.
