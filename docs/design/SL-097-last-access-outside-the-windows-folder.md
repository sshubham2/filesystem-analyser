# SL-097: does a child's keep-open move its parent's last access outside the Windows folder?

Status: built. This records what was measured for the precondition SL-096 left open (its O-015): that the parent move seen in
C:\Windows\Temp is, or is not, the same in %ProgramData% and %ProgramFiles%, and on Windows 10. Nothing here changes what
the scanner does. Whether it closes Q-205 or Q-208 is a person's decision.

## Verdict

On Windows 11 26200, in a scan run by an ordinary user, a kept child folder moves no parent's last access in %ProgramData%,
in %ProgramFiles(x86)%, or in the one folder of %ProgramFiles% that this user can write-open. C:\Windows\Temp, measured in the
same runs, still moves root, plain and links. So `Scanner.CountListingOpen` is right to correct only at or under
%SystemRoot%, and `LastAccessKept` is truthful in those locations. It is not widened.

Open, and filed for a person: Windows 10 (Q-205) and an elevated scan.

## Environment

Windows 11 Pro 10.0.26200.9457 (25H2), not elevated, NTFS. luafv Running, start Automatic. `fsutil behavior query
disablelastaccess` is 2 (system managed, last-access updates enabled). TEMP=TMP=C:\WINDOWS\TEMP, the engine's value.

## Method

- The scanner's own keep-open: CreateFile of an existing child folder with FILE_LIST_DIRECTORY | FILE_WRITE_ATTRIBUTES
  (0x101), OPEN_EXISTING, backup semantics.
- A parent's last access was read with GetFileAttributesExW, which gives the in-memory value, before and after the open. A
  parent was not listed in between: a listing sets its last access to now.
- Read-attribute opens (0x80) are the control for a write-class open. Pool-tag `Luaf` deltas (NtQuerySystemInformation,
  SystemPoolTagInformation) show whether luafv took part in an open.
- Fresh trees go through the project's own code: TempTree, the 48 h deepest-first back-dating and the real
  `Scanner.Scan`, as `LastAccessUnderSystemRootTests` does.

Each sample set is reported on its own and never summed with another: the survey's and the investigation's existing-folder
candidates came from the same walk, so a total would count some parents twice.

## Results

| Location | Set | Parents tried | Parents moved |
|---|---|---|---|
| %ProgramData%, fresh trees through the real Scanner | risk spike 2, 3 trees of 7 folders | 9 (root, plain, links, in each tree) | 0; each tree `kept 7 of 7`, asked 7 |
| %ProgramData%, fresh tree, the new Fact | this build | 3 | 0; `kept 7 of 7; 7 of 7 folders still at least 47 h old; moved: none` |
| %ProgramData%, existing folders | investigation (la-probe2 and first-open) | at least 34 distinct | 0 (owners include this user, Administrators, SYSTEM) |
| %ProgramData%, existing folders | survey | 20 (two batches of 10; the second with a `Luaf` delta of +3) | 0 |
| %ProgramFiles(x86)%, existing folders | investigation | 21 | 0 |
| %ProgramFiles(x86)%, existing folders | survey | 20 (two batches of 10; the second with a `Luaf` delta of +10) | 0 |
| %ProgramFiles%, existing folder | investigation, survey | 1, the same folder (EaseUS Data Recovery Wizard\ensdata) | 0 |
| C:\Windows\Temp, fresh trees, the positive control | every run of this slice | 3 per run | 3 per run: `kept 4 of 7 ... moved: <root>, plain, links` |
| %LOCALAPPDATA%\Temp, fresh tree, the negative control | every run of this slice | 3 | 0: `kept 7 of 7 ... moved: none` |

- A read as a no-move measurement needs the Windows\Temp line of the same run. Where luafv is stopped or last-access updates
  are off, nothing moves anywhere and the %ProgramData% line reads `moved: none` whatever the truth. The Windows-folder
  Facts are red there (Q-210), so a green %ProgramData% Fact beside red Windows\Temp ones is not a measurement.
- %ProgramFiles% has no fixture: a standard user cannot create a tree there, and a standing probe of existing folders would
  move real install folders' last access on every run. An ACL walk to depth 3 (AccessCheck against this token) found 1
  write-grantable folder among 10,442 in %ProgramFiles%, and 1,193 among 10,648 in %ProgramFiles(x86)%. So a non-elevated
  scan's keep-open is refused almost everywhere in %ProgramFiles%; those folders are listed plainly and are not counted kept,
  and a parent move there would hardly show in `LastAccessKept`.
- luafv takes part in some opens outside C:\Windows\Temp (the `Luaf` deltas above; +0 in the idle windows around them), and
  still no parent moved. The documented UAC file-virtualization namespace (%ProgramFiles%, %ProgramData%, %SystemRoot%
  minus some subfolders) does not predict where the move happens on this PC.
- The probe sees a move when there is one: run on a fresh back-dated tree, `first-open.ps1` reported moved for 3 of 3
  parents under C:\Windows\Temp and for 0 of 3 under %LOCALAPPDATA%\Temp, in 3 runs. Its walk lists each parent just before
  the probe reads it, so its "moved" there is a 58-172 ms change after that listing and not 48 h to now, which shows that a
  move right after a listing is visible. The survey's reasoning that such a probe "cannot show anything" was not measured and is
  not carried here.

## A re-scan of the same C:\Windows\Temp tree (risk spike 3)

- Re-back-dated at +180 s and at +20 min, the scan moved root, plain and links again, and `kept` (4 of 7) equalled the folders
  still old.
- Not re-back-dated, at +30 s, the scan moved nothing: every folder's change was 0.000 s. `kept` was still 4, so 3 folders
  were counted out although none moved. That is the gap `ScanResult.LastAccessKept` and the SL-096 limits already record,
  that a parent is counted out even when nothing moved, seen on a re-scan. It is not a new limit, and both of those places
  stay as they are. `LastAccessKept` is internal (Q-159), so no result a person sees changes.
- It was not isolated why nothing moved at +30 s. No re-scan test is added.

## Evidence for Q-208, not acted on here

The investigation's first write-class opens of existing folders under C:\Windows outside Temp moved the parent in 7 of 12:
spool\drivers from color, Registration from CRMLog, System32\com and SysWOW64\com from dmp, System32\spool from PRINTERS,
Crypto\RSA from MachineKeys (also with the scanner's own 0x101, about 20 minutes later), and C:\Windows from nscCF.tmp. It
moved none for C:\Windows from tracing, nor for C:\Windows, System32 and SysWOW64 from Tasks. Read-attribute opens and repeat
opens within about a second moved none. That bears on Q-208 (does the correction over-apply to writable folders under
C:\Windows other than Temp); this slice neither answers it nor changes the correction.

## Not measured, and why

- **Windows 10.** No machine here runs it. luafv is present on Windows 10 1607 to 22H2 (SentinelOne's CVE-2026-27929 page lists
  them), so the question is about a component that runs. The Windows Sandbox on this PC is Windows 11 Enterprise 26100 with
  EnableLUA=0, and luafv refuses to load there (error 1275, "This driver has been blocked from loading"): as SYSTEM, 0 of 28
  parents moved in every location, C:\Windows\Temp included, so it cannot stand in for the luafv-on condition.
- **An elevated scan** with luafv loaded, in %ProgramData% and %ProgramFiles%. It needs an administrator on this PC. It matters
  only if `LastAccessKept` stops being internal (Q-159) or the app is run elevated, which its constraints rule out.
- **A fresh tree in %ProgramFiles%**, and %ProgramFiles% below depth 3 (not walked).
- **%ProgramFiles% on Windows 10.** The command below does not cover it. Its consequence is bounded by the refused keep-open.

## The Windows 10 hand-off (for a person)

On a Windows 10 PC with luafv running and last-access updates on, as a non-elevated user, from the repository root:

```
pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 test tests/FsAnalyser.Core.Tests/FsAnalyser.Core.Tests.csproj --filter "FullyQualifiedName~LastAccessUnderSystemRootTests|FullyQualifiedName~LastAccessTests|FullyQualifiedName~HeldOpenTreeTests" --logger "console;verbosity=detailed"
```

Report every `moved:` line. On this PC the Windows\Temp lines read `moved: <root>, plain, links` and the %LOCALAPPDATA% and
%ProgramData% lines read `moved: none`.

- Windows\Temp and %ProgramData% as on Windows 11: the caveat Q-205 records goes for those locations.
- A red %ProgramData% line: the scanner over-counts there on that machine, to be fixed in a new slice (`LastAccessKept` would
  need the correction widened to %ProgramData%).
- A red Windows\Temp line: the intended signal of Q-210. Where parents do not move, the structural correction under-counts.
- A red line with the message "Could not create ...": this user cannot create the tests root (Q-210); it names the folder.

## What was built

- `LastAccessUnderSystemRootTests.InProgramDataNoKeptFolderMovesItsParent`: the class's 7-folder tree under
  `<CommonApplicationData>\fsa-tests\<guid>` (the known-folder call, never the ProgramData variable, which an agent host may
  strip), back-dated 48 h, scanned by the real Scanner. It asserts asked == 7, kept == 7 and still-old == kept, each with its own
  message. Default suite, never skipped, no Trait. The `<guid>` tree is removed by `TempTree.Dispose`; the empty `fsa-tests`
  parent stays in %ProgramData% (visible to every user), as it does in C:\Windows\Temp and %LOCALAPPDATA%\Temp. A real scan
  of C:\ProgramData made while the test runs would see its tree.
- `TempTree.ChooseProgramDataTestsRoot`: a known folder that is blank, not fully qualified or at or under the Windows folder
  (or a Windows folder that cannot be told) is refused by name.
- `TempTree.CreateRoot` refuses by name a tests root this user may not create (Q-210), for every deliberate root. The
  compression refusal is unchanged.
- `LastAccessRuleCorpusTests.TheWindowsFolderFactsRunInTheDefaultSuite` pins the new Fact and now asserts that the methods
  carrying a Fact or Theory are exactly the pinned list, so a test added to the class is red there until it is pinned.
- The `ScanResult.LastAccessKept` remark and the SL-096 limits say what was measured, and
  `TheMeasuredLastAccessLimitIsWrittenWhereTheTreeSaidUnmeasured` pins that.

## If the %ProgramData% Fact goes red

A red is first a finding to investigate, not noise to make opt-in. Record the run's `kept K of 7; S of 7 still old; moved: ...`
line and the same run's Windows\Temp lines. Only a red shown to come from outside the scanner (Defender or the indexer
touching a fresh tree in a machine-wide folder between back-dating and the scan) justifies moving the Fact. That move is a
re-design, not an edit: SL-096's settled rule forbids any Trait in this file and the reflection guard allows only stock
attributes, so it means a class of its own with a Trait, a `FsaDefaultTestFilter` clause, an `OptInTraitTests` pin, shared
`BackDateAndScan` and `BuildNested` helpers, and a Windows 10 filter that names the new class. Three runs in a row and the full
suite were clean on this PC when it was built.
