# SL-096: the three red last-access tests, triaged

Status: built. This is the measured triage Q-190 asks for. Whether it closes Q-190 is a person's decision.

The report was a title only: three tests failed at afd021d and through all six SL-095 validations, always called
"pre-existing" and never diagnosed. This record assumes the slice owes three things: a measured verdict for each test, a
full suite that is green as the engine runs it (TEMP=C:\WINDOWS\TEMP), and a scan side effect in the last-access area
that stays visible in the routine suite rather than in the noise.

## Verdict: both

All three are an environment fault. One real defect of this project sits underneath.

- The engine's shells carry TEMP=TMP=C:\WINDOWS\TEMP, so TempTree built the fixtures in C:\WINDOWS\TEMP\fsa-tests.
- In C:\Windows\Temp on this PC, opening a child file or folder with any write-class access (FILE_WRITE_ATTRIBUTES,
  WRITE_DATA, DELETE, WRITE_DAC and others) and OPEN_EXISTING moves the PARENT folder's last-access time to now, inside
  the child's CreateFile. Read-class opens do not. In %LOCALAPPDATA%\Temp on the same NTFS volume nothing moves.
- The fixtures back-dated each parent before its children with .NET's Set*Time, which opens each child with
  FILE_WRITE_ATTRIBUTES, so each child reset its parent.
- Suite before this slice: 20/23 of the two classes and 689/692 overall under C:\WINDOWS\TEMP, against 23/23 and 692/692
  under the user's own TEMP. fsutil reports "DisableLastAccess = 2 (System Managed, Last Access Time Updates ENABLED)" in
  both places.

| Test | Failed at | Verdict |
|---|---|---|
| HeldOpenTreeTests.APlainListingOfTheControlFolderMovesItsLastAccessTime | precondition :96-97, "the control was not back-dated" | Environment only. The control was back-dated, then control\c.txt, which reset it. No scan side effect. |
| HeldOpenTreeTests.TheScanChangesNoEntrysMetadata | precondition :68, "&lt;root&gt; was not back-dated" | The same fault, hiding a real side effect. Back-dated deepest first, it fails at the metadata diff (:78): root, plain and links move during the scan (spike 2, 3 of 3 runs). |
| LastAccessTests.AFolderThatRefusesWriteAttributesIsListedPlainlyAndCountedInFull | :87, Root's last access 2026-10-06 instead of 2026-10-04 | Fixture order plus the same side effect. Back-dated deepest first it still fails at :87: Root moves during the scan (spike 2, instrumented, 3 of 3 runs). |

**The real defect.** The scanner keeps a folder's last access by opening it with access|FILE_WRITE_ATTRIBUTES and
setting LastAccessTime = -1 through that handle. That keep-open is itself a write-class open of each child folder, so in
C:\Windows\Temp it moves the parent's last access, which the parent's own -1 handle (closed before the children are
opened) cannot prevent. The scan still counted every folder as kept: (asked, kept) = (7, 7) while 3 of 7 folders (the
root, plain and links, which have child folders) had moved, in 5 of 5 nested trees. Flat folders never moved, and file
opens (read-class probes) moved nothing.

**Which driver.** luafv.sys (UAC File Virtualization) is the prime suspect: its pool tag 'Luaf' allocated 2 blocks per
write-attributes child open under C:\WINDOWS\TEMP (60 for 30, with 30 of 30 parents moved) and none for read-attributes
opens there or write-attributes opens in the user's TEMP. That is correlation: fltmc and tracing need an administrator.
The process is not elevated and its token has virtualization disabled, so this is not luafv's redirection path.

## Ruled out

- DisableLastAccess=2 as the cause: it resolves to ENABLED in both places, and the plain-listing control passes under the
  user's TEMP.
- The kernel-memory lead from SL-095 (SC-070/074): over 30 triggering opens 'Luaf' allocated 60 and freed 59 within the
  window. Measured only at this scale.

## What changed

- **Scanner** (`Scanner.CountListingOpen`): when a folder's keep-open succeeds and the folder is at or under the Windows
  folder (held once per scan as `LongPath.NormalizeRoot(GetFolderPath(Windows)).Win32`, compared with the existing
  `IsAtOrUnder`, without case and at a separator), its parent, if it was counted kept, is counted out, once. A refused
  child open counts nothing out: spike 1 measured that it moves nothing, and that a rule keyed on attempted opens
  under-counts (kept 0 against 1). No open, mask or gate changed. The counters stay internal (Q-103, Q-159).
- **Contract** (`ScanResult.LastAccessKept`): worded as the rule, not as an observation. Nothing reads a time back.
- **TempTree**: when %TEMP% is at or under the Windows folder, trees go to %LOCALAPPDATA%\Temp\fsa-tests
  (`TempTree.ChooseTestsRoot`). A run whose own %LOCALAPPDATA% is under the Windows folder too (a SYSTEM or service
  profile) is refused by name. The NTFS and compression refusals apply to whichever root is chosen. A new constructor
  takes its root, for the one class that builds in C:\Windows\Temp on purpose.
- **Fixtures**: HeldOpenTreeTests and LastAccessTests back-date the deepest entries first, so that where the effect
  exists they fail at the assertion that names it, not at a precondition that hides it.
- **LastAccessUnderSystemRootTests**: default-suite tests that build in C:\Windows\Temp\fsa-tests on purpose, never
  skipped, and assert LastAccessKept equals the folders still old, which fails in both directions.
- **Rules** (.taad/fitness.yaml, settled by SL-096): those tests are never skipped or traited; no other Core test builds
  in %SystemRoot%\Temp, by spelling or through the inherited temp folder (Path.GetTempPath and the other .NET routes).
  Each is closed as a class in a corpus fed to it by LastAccessRuleCorpusTests: SystemRootTempRouteCorpus.json and
  WindowsFolderFactSkipCorpus.json. What no line pattern can see is checked by reflection.

## Measured by this build

All on this PC, Windows 11 Pro 10.0.26200, not elevated.

- Acceptance: `eng/dotnet.ps1 test tests/bugs/SL-096-LastAccessUnderWindowsTemp/...csproj` exits 0 (it exited 1 before).
- Full suite `eng/dotnet.ps1 test FsAnalyser.slnx`: 789 passed, 0 failed, under TEMP=TMP=C:\WINDOWS\TEMP, and again
  under the user's own TEMP (set in the shell, and through `dotnet test -e`).
- LastAccessUnderSystemRootTests under C:\WINDOWS\Temp\fsa-tests: kept 4 of 7, 4 of 7 folders still old, moved root,
  plain and links, for the root as-is, in lower case and with a trailing separator. The refusing child: kept 1 of 2,
  1 of 2 still old, nothing moved (the must-verify of tree (d): kept 1 equals the truth, 1). Outside the Windows folder:
  kept 7 of 7, 7 still old.
- With the correction taken out (`_lastAccessKept--` made a no-op, Scanner.cs touched so it rebuilds), all three
  spellings fail with "(asked, kept) = (7, 7), but 4 of the 7 folders ... still read at least 47 h old". The same
  happens with the Windows folder held without `NormalizeRoot` (the review's m1).
- The real Scanner on C:\Windows\Web asked 14 folders and kept 0, and on C:\Windows\Fonts asked 1 and kept 0: system
  folders refuse write-attributes, so the correction's set stays empty there.

## Limits, not measured

- Only C:\Windows\Temp on Windows 11 26200. %ProgramData% and %ProgramFiles%, also in luafv's namespace as recalled from
  Russinovich, are not measured and not corrected. Windows 10 is unmeasured (Q-192, Q-205).
- The rule counts a parent out by structure. It is wrong when nothing moved (the parent's time within NTFS's hour, or
  updates off), and it misses a parent whose child's write-attributes open succeeded but whose -1 set failed.
- The Windows folder is matched by how the root is spelled: a subst letter, junction, mount point or volume path to a
  folder under it skips the correction (SC-176). The root's own parent, outside the scan, moves too and is never counted.
- The scan does not avoid the move. Whether it should (plain-open children under %SystemRoot%, or holding parent handles)
  is product work Q-159 defers to the file-age features.
- The opt-in Cloud=CF-SYNC-ROOT rig (SyncRootRig, CloudRig) still roots in the inherited temp folder and fails 4 of 4
  under C:\WINDOWS\TEMP (spike 4). It is exempt by name from the route rule until a slice routes it through TempTree.
- One survey probe folder, C:\Windows\Temp\fsa-sl096survey-&lt;8 hex&gt;\ (an empty child\ and a 10-byte f.txt), is left
  in a folder this user cannot list. Only an administrator can find and remove it.
