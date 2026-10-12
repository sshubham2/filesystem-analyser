# SL-106: a folder that opened and then failed is not a folder that could not be opened

Status: built. The scan keeps which phase of a folder's read failed, stamps `UnreadableKind` once from it, and the
unreadable-folders list, the folder tree and the results list's top-level row all read that stamp and word it through one
function. This closes SL-087's review M2 (`NothingListed` could not tell a refused open from an open whose first listing
page failed) and m1 (the tree and the top-level row worked the kind out again from `ChildCount` and worded a folder listed
partway 'could not be read in full' where the list said 'only partly read'). The two surfaces change together, with the
stamp, because changing one alone makes them disagree about the same folder (lesson #329).

## What was wrong

`DirectoryReader.Read` returned the open's Win32 error and the listing's through one int. `Scanner.FolderDone` stamped
the kind from `ChildCount(folder) > 0`: partway if anything was kept, `NothingListed` otherwise. `FolderTreeNode.Note` and
`TopLevelTracker.FolderExited` each worked the same rule out again. So a folder that **opened** and failed its first page
before any entry came back was recorded, drawn and worded exactly as a folder whose **open was refused**: 'could not be
opened', which is false of it.

## What the scan keeps now

| Folder | `Opened` | Entries kept | Kind | Worded |
|---|---|---|---|---|
| open refused (5, 32, 53, 363, 1920 ...) | false | 0 | `NothingListed` | could not be opened |
| opened, first page failed (59, 5, an unasked 995 ...) | true | 0 | `OpenedNothingListed` | opened, but nothing could be listed |
| opened, failed after entries were kept | true | > 0 | `ListedPartway` | only partly read |

`DirectoryReader.Opened` is true once `Read`'s open returned a handle, the keep-last-access open or the plain open it
falls back to when the folder refuses `FILE_WRITE_ATTRIBUTES`; it is false for a refused open and for a folder at or under
the excluded container layer store. `Scanner.FailedKind(error, opened, listed)` is the one rule: no error is no kind;
entries kept make it partway; otherwise the open decides, and never the code. `FolderDone` computes it once and passes the
same value to the `UnreadableFolder` record and to `TopLevelTracker.FolderExited`. An aborted listing (a 995 with a cancel
issued) returns before any stamp, as before: it leaves no record.

`UnreadableKind.OpenedNothingListed` is appended as value 2, so `NothingListed` = 0 and `ListedPartway` = 1 keep their
values. `NothingListed` keeps its name; its doc now says it means a refused open.

All three surfaces word through `UnreadableRows.KindText`, the only App file that holds the words (a settled architecture
rule). Its fallback, for a kind with no arm and for no kind, is the plain 'could not be read', which is true of every failed
folder: `TopLevelRowText.TitleOf` runs in the results list's converter while a scan is on, outside the per-view catch of
Q-158, and Q-071 rules out a global `Handled = true` handler, so a throw there could end the app. A missed arm is made
loud before ship by `UnreadableRowsTests`, which asserts every member of the enum has words of its own. An Unreadable row
is never left as its bare name, which is how the list words a Done row.

The tree gets the kind from the scan's record: `FolderTreeSession` is given `result.Unreadable` (required), indexes the
first kind per entry as `UnreadableRows.Build` keeps the first row, and answers `FailedKind(entry)`; null for an entry not
in the record and once the session is closed. `ScanStore` gains nothing.

## What was measured, and what it says about the code-363 rows

The slice began from 28 code-363 rows 'worded could not be opened with the failing phase unmeasured'. Measured on this PC
(Windows 11 26200, cldflt.sys 10.0.26100.9444), read-only:

- `%TEMP%\fsa-guard-tests` holds 429 folders: **27** fail with 363 (not 28), and all 27 fail **at the open**
  (`CreateFileW` for attributes and for `FILE_LIST_DIRECTORY` both return 363), none at the first directory query, 402 list
  clean. SL-089 also counted 27. The 28th row came from a whole-C: scan, not from this folder. No 1920 rows remain.
- In Windows Sandbox (cldflt.sys 10.0.26100.9444, the same file version), folders given a cloud reparse tag by hand
  (0x9000001A, 0x9000101A, 0x9000301A, 0x9000F01A, with 0, 16 or 64 bytes of data, attributes 0x410) list normally while
  cldflt is detached and return **363 at the open** once it is attached, whether the provider is running, the folders were
  planted before or after the sync root was attached, or the root was unregistered. The scan records them `NothingListed`.
- The CfProvider rig cannot make a 363 folder: connected, killed-but-registered and unregistered all list without error.
- An SMB server that answers CREATE and fails the first QUERY_DIRECTORY (the real Windows redirector against the SMBLibrary
  rig): `CreateFileW` succeeds, the first query fails (5, or 59 once the server stops). The scan stamped such a folder
  `NothingListed` before this change (SL-089's N-005 control: `s`, 59, worded 'could not be opened') and stamps it
  `OpenedNothingListed` now.

So 'could not be opened' is true of every real 363 row and stays true of it; they are not instances of the defect. The only
natural opened-then-failed folder known is the SMB shape above. These measurements are of Windows 11 only (builds 26200 and
26100.9457); Windows 10 and a production SMB server were not measured.

## What this change does not do

- The scan **root** is out of scope. An opened root whose first page fails still ends `RootUnreadable` with no record: it
  has no `UnreadableFolder`, no top-level row and no tree node, so none of the three surfaces shows it. The root is passed
  `opened: true` to `FolderDone`, which only uses it when entries were kept. Filed for a later slice.
- The published exe never draws the new kind in the tree. `SynchronousIoCancel.FailListingOf` refuses page 1 by design
  (it makes the 'partly read' row, T-007), and relaxing it would touch the high-risk `SynchronousIoCancel.cs` (lesson #214)
  for one drawn row. The words and the lookup are pinned without WPF (`UnreadableRowsTests`, `FolderTreeSessionTests`), the
  session wiring by T-007 at `ChildCount` above 0 and at 0. The doc of `FailListingOf`, which said a failed first page is
  'a folder never opened' and named the tree's old words, is corrected; its behaviour is not changed.
- Nothing here works around a defect outside the project: the reader dropped the phase, which is this project's own bug, so
  no `KnownWorkarounds` entry and no setting.

## Where it lives

| What | Where |
|---|---|
| the bit | `DirectoryReader.Opened` |
| the rule and the stamp | `Scanner.FailedKind`, `Scanner.FolderDone(..., opened)` |
| the kind | `UnreadableKind.OpenedNothingListed` (`ScanResult.cs`) |
| the row | `TopLevelTracker.FolderExited(..., UnreadableKind? failed, ...)`, `TopLevelRow.FailedKind` (replaces `ListedPartway`) |
| the words | `UnreadableRows.KindText(UnreadableKind?)`; `TopLevelRowText.TitleOf`; `FolderTreeNode.Note` |
| the tree's lookup | `FolderTreeSession(store, unreadable)`, `MainWindow.ShowFolderTree` |
| the rules | `TheAppNeverDecidesAFailedFolderKind` now names any member (`\w+`); settled: only `Scanner.cs` names a kind in Core, only `UnreadableRows.cs` writes the kind words in the App |
| the harness | `WindowCases.ps1` (T-007 'only partly read', `CheckSeamRow`, `Get-OracleRow` class `opened-nothing-listed`, W-001's three-kind sum), `W001OracleCorpus.json` F16, `RowsMode.cs` doc |

## Prior art, corrected

The design's first draft cited POSIX/BSD `fts(3)` as keeping an unreadable directory as its own node state, 'rather than
leaving the caller to infer it from an empty child list'. That is wrong for the fts GNU `find` and `du` use: gnulib's fts
treats 'no items read' the same as a directory that cannot be accessed and sets `fts_errno` to `FTS_DNR` (its readdir-error
commit says so). It conflates the two phases exactly as this repository did, which is the conflation this change removes. It
is a counter-example, not support; the design rests on the spikes, not on fts.

## Checks

- `pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 test tests/bugs/SL-106-OpenedThenFailedKind/SL-106-OpenedThenFailedKind.Tests.csproj`
  (the acceptance command; its refused-open half is the control that the fix does not reword a true refusal).
- The Core suite, `ScannerFailedKindTests`, `UnreadableFolderTests` (including the fallback open, which asserts the scan
  asked to keep the folder's last-access time and was refused before 59 is injected), `UnreadableRowsTests`,
  `FolderTreeSessionTests`, `TopLevelRowTextTests`, `W001OracleTests`.
- `tests/bugs/SL-063-DoneRowFinality` (its reflection fixture sets `FailedKind` for an Unreadable row).
- Because the diff touches `src/FsAnalyser.App/**` and `src/FsAnalyser.Core/TopLevel*.cs` (Q-114) and the folder tree
  (Q-128): `WindowMutants.ps1 -Mutant All`, `WindowCases.ps1 -Case T-003`, `WindowCases.ps1 -Case T-007`, run one at a time;
  and the Core suite with `--filter Network=TEST-NET-1` (lesson #99). `Network=SMB-LOOPBACK` changes network state and is a
  person's run.
