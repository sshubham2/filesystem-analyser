# SL-107: the whole-C:\ W-001 run, a person's to make

Status: built as a recipe. The slice changes no product code and no case. It hands the person the command, what to expect
from it and how to read the record; the run itself and the record it writes, `.taad/slices/SL-089/c-drive-run.json`, are
the person's. It discharges SL-089's criterion 3 and, with it, the backlog items SC-136 and SC-138, which this page
replaces as the place the command, the expected counts and the precaution are written down. No engine stage waits on the
run, and none may make it.

## Why this run is the person's

Under either figure for the engine's kernel-pool guard. Older notes (lesson #357, `SL-089-seams.md`, the comment above
`Test-W001`) give 0.5 GB per run; the engine's watch today stops an agent at 3 GB of kernel growth. The two disagree, and
this run is the person's under both:

- The brief names it the owner's run, and a whole-drive scan needs the person's allowance.
- A cold full scan has measured about 537 MB of kernel pool with the container layer store left out (SL-095), and lesson
  #348 and Q-188 predict 3-4.4 GiB for the shipped exe, past either figure.
- Pool that the kernel keeps until a restart would hold every engine agent until the person releases it, and only the
  person can restart the machine.
- The Windows Sandbox cannot stand in: it runs as SYSTEM, which is elevated, so W-001 ends INCONCLUSIVE before it scans,
  and its C:\ is not this PC's.

## 1. Before you start

1. Open an ordinary, **unelevated PowerShell 7 (`pwsh`) window**. Not cmd.exe, and not "Run as administrator".
   *Why*: typed in cmd.exe, `-Root "C:\"` reaches the script as `C:"` (the backslash escapes the closing quote), W-001 stops
   with `W-001: C:" is not a folder`, prints `FAILED: W-001`, exits 1 and writes no record. It fails loudly, with no wrong
   record. An elevated shell makes W-001 end `INCONCLUSIVE` before anything is scanned.
2. `cd` to a **checkout of the branch that carries SL-107's merge**: the main checkout on `uat` once SL-107 has merged, the
   SL-107 worktree only while it still exists. The record lands in whichever checkout runs the script (`$Repo` is the folder
   two levels above `tests/window/WindowCases.ps1`), and it has to be committed on the branch that goes on: a record committed
   to `slice/SL-107` after that branch is merged never reaches `uat`.
3. Nothing else runs on the machine: no TAAD engine stage, no other `WindowCases.ps1`, `RuleCases.ps1`, `WindowMutants.ps1` or
   `Mutants.ps1`, and no heavy app (browser with many tabs, a build, a game). Other cases plant ACLs in `%TEMP%` that a
   whole-drive scan then meets, and their pool growth is not this run's.
4. Plan for 5-10 minutes (a cold C: scan has taken 400-465 s in the shipped exe, lesson #161; the wait allows 1,200 s), and
   **leave the app window alone**: W-001 presses nothing, and closing or stopping it ends the run as a failure.
5. The first run in a checkout builds the hook and publishes the app through `eng/dotnet.ps1`: one to three minutes, and a
   download of about 300 MB for the repo-local SDK when `.dotnet/` is missing. Never use plain `dotnet`.
6. Expect kernel pool to grow by about 537 MB or more, and maybe to stay up until a restart. The run prints the figure.

## 2. The command

From the checkout's root:

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null
[Console]::OutputEncoding = [Text.UTF8Encoding]::new()
pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/WindowCases.ps1 -Case W-001 -Root C:\ 2>&1 | Tee-Object -FilePath artifacts\c-drive-console.txt
```

The first line is there because a checkout that has never built has no `artifacts\`, and `Tee-Object` does not create it. The
tee is the only place a leaked row's path survives (the hook's log is deleted when the run ends, and the record holds no path
at all); `artifacts/` is gitignored, so the capture never reaches a commit.

The second line is there because the window decodes what the run prints with its console's output code page (OEM 437 on this
PC), and a character outside it in a leaked path (a CJK or accented letter in an app or user folder name) would come out as `?`
or a lookalike, in the capture and on the screen alike, and the folder could not be found again. Set to UTF-8 for this window,
the run writes UTF-8 and the window reads UTF-8, so the path survives. It lasts for this window only.

Judge the ending by the **last line printed**, not by the pipeline's exit code. `-Root C:` (no backslash) is also read as the
whole drive, but this page names only `C:\`. The precaution below is on at every launch; there is nothing to set.

## 3. How the run can end

| Last line printed | What it means | What to do |
|---|---|---|
| `PASSED: W-001`, after a `record: ...c-drive-run.json` line | Every check held and the verdict is `held`. | Read the record (4), judge criterion 5 (5), commit (7). |
| `FAILED: W-001`, and a `record:` line above it | The scan reached `Completed` but a check failed, or a row leaked. The record is written, verdict `failed`. | Read the `FAIL` lines. A `LEAK:` line is criterion 3 failing: see "A leak" below. The record is true evidence; criterion 1 is unmet. |
| `FAILED: W-001`, no `record:` line, `TIMEOUT` or `exit code` in the lines above | The app was still running at 1,200 s and was killed, or it exited non-zero, so there is no report. Nothing was written. | Re-run once on a quiet machine. If it times out again, the one-line follow-up is the 1,200 in `Invoke-HookedRun 'rows'` in `Test-W001`; touching `WindowCases.ps1` makes `WindowMutants.ps1 -Mutant All` a required validate check (Q-114). |
| `FAILED: W-001` with `C:" is not a folder` | You typed it in cmd.exe. Nothing was scanned or written. | Use pwsh. |
| `FAILED: W-001` and the record, if one was written, has `status` `Cancelled` | Someone stopped or closed the app. The record has verdict `failed`, and it **overwrites** an earlier file at that path. | **Do not commit it.** Re-run. |
| `INCONCLUSIVE: W-001` before any scan | The shell is elevated. | Open an ordinary window. |
| `INCONCLUSIVE: W-001` after a `record:` line, verdict `inconclusive` | One of the checker's three controls did not behave, so a run with no leak says nothing. | Do not commit it. Read `checkerControls`, fix the cause, re-run. |
| The window closed or the machine restarted | Nothing is known. | Re-run. |

A run that did not reach `core.status` `Completed` is re-run, not committed.

**A leak.** The line is `        LEAK: a row with code 5 that opens: <path>`. Copy the path out of
`artifacts\c-drive-console.txt` into a note kept **outside git**. The fix starts by adding the row's shape to
`tests/window/W001OracleCorpus.json` (rows F01-F16), which `W001OracleTests` runs, and never by patching the script alone
(lesson #361). The oracle opens only code-5 `NothingListed` rows, with the scanner's plain mask and no listing; it tallies,
and never opens, a code-363 row, a reparse point, any other code, and a row whose folder was made or changed after the scan
began. It is inconclusive unless C:\Windows opens, a folder this user denied itself listing gives 5, and a folder that
refuses only the keep-last-access open gives 5 for that open and 0 for a plain one.

## 4. Reading the record (criteria 1-4)

`.taad/slices/SL-089/c-drive-run.json` holds counts, codes, the panel's reason texts, the commit and the kernel-pool figures,
and no path (`Write-W001Record` throws rather than write one, and the `.taad/fitness.yaml` rule 'the committed whole-C:\
record carries counts, codes and reasons, never a path' fences the committed file).

| Criterion | Field | Must be |
|---|---|---|
| 1. the run was made as specified | `scope`, `elevated`, `checkerElevated`, `status` | `whole-drive`, `false`, `false`, `Completed` |
| | `checkerControls.openable`, `.denied`, `.refusedThenListed` | all `true` |
| | `verdict` | `held` |
| 2. the panel is the engine's list | `rows`, `header`, `coreUnreadable` | all the same number |
| 3. no refused-then-listed folder is a row | `oracle.openRefusedButOpenable` | `0`, and no `LEAK:` line in the console capture |
| 4. no empty reason, no folder twice | `emptyReasons`, `duplicates` | `0`, `0` |

On criterion 3: about 306,000 folders on C: refuse the keep-last-access open and list fine (U-098, U-102). The record
carries no count of them, because the schema-1 report has none (exposing it is filed for later). In the run the evidence is
the oracle together with W-001's own `control-refused-keep` folder, planted before the scan starts inside the scanned tree: if
the engine ever made such a folder a row, it would print `LEAK:`.

Also expected, from the same record:

- `notScanned` is **1**: the container layer store, left out by the `wcifs-layer-store` precaution (below). The tab header then
  reads `Unreadable folders (N), not scanned (1)`, and N is what `header` holds.
- `byKind.openedNothingListed` is **0** and `byKind.listedPartway` **0**. The 27-28 code-363 rows are cloud-placeholder
  fixtures that fail at `CreateFile` (Q-164, lesson #465); a non-zero `openedNothingListed` is news (Q-258), not those rows.
- `oracle.leftoverRows.code363` is **equal to the same-session Temp anchor's code-363 count** (27-28 at bd6572f; section 5) and
  `oracle.leftoverRows.reparse` is **0**; `oracle.notARefusedKeepShape` is `[]`
  or holds one entry, code 1920, with `count` 2 or less. **SC-136's "about 30 leftover rows" is not the sum of the first two
  numbers**: a code-1920 row is classed `other-code`, so it can only ever appear under `notARefusedKeepShape`.
- `kernelPoolBytes` (before and after) is the pool growth. Q-193 (still open) can cite it, with `notScanned` and `header`.

### The precaution, and what the record owes to it

The defect is outside this project: `wcifs.sys`, Windows' container filter, leaks kernel pool and a System handle for each
container-layer-store entry a program touches, until the next full restart
(microsoft/Windows-Containers#646). The app's `wcifs-layer-store` workaround (`KnownWorkarounds`, own checkbox in Settings,
**on at every launch**) walks around it: a scan above `%ProgramData%\Microsoft\Windows\Containers\Layers` leaves the store
out, never opens it, and says so in a "Not scanned" row and in the header's `, not scanned (1)`. Turning the checkbox off asks
first, in a window that states the pool consequence, and lasts one session. W-001 fails a whole-drive run unless the report
shows exactly that entry, on. Nothing here changes it. The store's folders are **not rows** and **not in the size totals**;
that is why this run's `rows` differs from the 682 earlier scans measured (item (a) below).

## 5. Criterion 5: a reconciliation, not a match to 682-684 (Q-261)

The 682-684 rows (652 code 5, 28 code 363, 2 code 1920; U-098, U-110, U-111, U-112) were measured **with the layer store
entered**. This run leaves it out, so the figure cannot be reproduced. Q-261, decided by a model while the owner was away,
accepts criterion 5 as met when the committed record shows all of:

- `notScanned` exactly **1**;
- code 363: **equal to the same-session Temp anchor's code-363 count** (27-28 at bd6572f), in `byCode` and in
  `oracle.leftoverRows.code363`;
- code 1920: **0-2** rows;
- `rows` at most **682** (about 660 expected);
- and every difference from 682 is named below, by path group.

The same-session anchor (the floor, below; review M2) **replaces Q-261's fixed 27-28 range for the code-363 count only**: that
range was the count on this PC at bd6572f, and later cloud cases leave more placeholders (lesson #360). Every other count keeps
Q-261's range.

If any count is outside its range, or any part of the gap cannot be named, criterion 5 is **not met, and that is a finding
about the scanner**, not a reconciliation. For code 363 that means a C: count that differs from the Temp anchor's; a count
above 28 that equals the anchor's is fixture growth, named as such, and criterion 5 is met. The decision changes if the owner
wants the like-for-like figure (a scan with the precaution off, which brings the pool growth back) or if the run lands outside
the ranges.

Start from 682 and name each difference:

| | Difference | Direction | Measured or bounded |
|---|---|---|---|
| (a) | Rows under `Containers\Layers`, now one `notScanned` folder | down by at least 25 | U-112's three groups under the store: 10, 8, 7. The rest is **not measurable**: the path lists of those scans (`failed-folders-C.csv` and the like) are gone, so the old Layers share cannot be re-derived. |
| (b) | WER: the whole of `ProgramData\Microsoft\Windows\WER` is 438 rows now; `ReportArchive` alone was 451 | down by 13 or more | survey at bd6572f, W-001 over WER (438 = header = `core.unreadable`, all code 5) |
| (c) | Code 1920: the guard tests' stand-in folders are no longer reparse points | 2 down to 0 (at most 2) | spike 3: the 0x123 tag cannot be planted unelevated |
| (d) | Code 363: the stuck cloud placeholders under `Temp\fsa-guard-tests`, plus `fsa-validate-2\dirtag\nonempty` | the Temp anchor's count (27-28 at bd6572f) | survey: 27 in W-001 over `fsa-guard-tests`; the 28th folder exists on this PC |
| (e) | W-001's own `control-denied` folder under `Temp\fsa-window-cases\<guid>`, code 5, still refused | up by 1 | `Get-CheckerControls` |
| (f) | Other cases' leftovers under `Temp\fsa-window-cases` (12 in U-112), and whatever else changed on the drive | up or down | measured the same day by the Temp anchor below |

Items (a) and (f) are the open ones, and (a) alone could be made to absorb any shortfall, **including a scanner that fails to
record a folder at all**: criteria 2-4 compare the panel with the engine's own list and cannot see that. So give the
reconciliation a floor measured in the same session.

### The floor: three bounded runs, straight after the C:\ run

While the caches are warm (a warm scan adds about 0 kernel pool, lesson #403), run the same W-001 over three disjoint roots,
and copy each record before the next run overwrites it (a folder-root run writes `artifacts\w001-run.json`):

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/WindowCases.ps1 -Case W-001 -Root C:\Windows
Copy-Item artifacts\w001-run.json artifacts\anchor-windows.json
pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/WindowCases.ps1 -Case W-001 -Root C:\ProgramData
Copy-Item artifacts\w001-run.json artifacts\anchor-programdata.json
pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/WindowCases.ps1 -Case W-001 -Root "$env:LOCALAPPDATA\Temp"
Copy-Item artifacts\w001-run.json artifacts\anchor-temp.json
```

Each anchor must itself end `PASSED: W-001` (an anchor that leaks is a finding on its own). Spike 2 measured C:\Windows at 52
rows, 72 s cold and 34 s warm; `C:\ProgramData` scans with the store left out, so its record also says `notScanned` 1.

Then:

1. **The floor.** `rows` of the C:\ record is at least the sum of the three anchors' `rows`, within churn. The Temp anchor
   contains its own `control-denied` row, as the C: run did, so the +1 of item (e) is on both sides and cancels. Churn is rows
   that appeared or vanished in the minutes between the runs (a case's ACL cleanup, a cache folder); more than a handful is a
   finding to look at, not to put down to churn.
2. **Code 363 is the day's fixture count, and this step is the rule for it.** The C: record's `byCode` 363 count must equal the
   Temp anchor's (and the other two anchors have none). 27-28 is what that count was on this PC at bd6572f; later cloud cases
   leave more placeholders (lesson #360). More than 28 on C: with the Temp anchor showing the same is fixture growth, named as
   such, and not a scanner finding; a C: count that differs from the Temp anchor's is a finding, at 27 or at 40.
3. **The remainder** is `rows(C:)` minus the three anchors: the rows outside them (the folders a standard user cannot open in
   `C:\` itself, in the rest of `C:\Users` and in Program Files, `$Recycle.Bin`, `System Volume Information`). Expect tens,
   not hundreds (U-098 listed 9 under `C:\` itself). A remainder over about 50 is split further by running W-001 over `C:\Users`,
   `C:\Program Files` and `C:\Program Files (x86)` until it is named.
4. **The ceiling** is Q-261's: `rows` at most 682, and item (a) says why it should be well under: up to 682, minus at least
   25 for the store, minus at least 13 for WER, plus 1, is about 645.

Record the three anchor counts and the remainder in the result table below. The old Layers share (a) is stated as what it is:
unmeasurable, and bounded only below.

## 6. What to commit

1. Only `.taad/slices/SL-089/c-drive-run.json`, from a run that reached `Completed` (`held` or `failed`), **and the result table
   below filled in**, on the branch that goes on (see 1.2). Not the anchors' JSON, not `artifacts\`.
2. Do not hand-edit `.taad/slices/SL-089/index.json`: it is an export of the engine's store and nothing reads it back (Q-106).
   Criterion 3 of SL-089 is discharged by this committed record and the check of it (SC-136, SC-138).
3. Before committing, `pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-107-RecordPathRule.Tests.ps1` applies the
   path rule to the record. It must end `All checks held.`

## Result of the run (fill in, then commit with the record)

| | Expected | Measured |
|---|---|---|
| Last line printed | `PASSED: W-001` | |
| `verdict`, `status`, `scope` | `held`, `Completed`, `whole-drive` | |
| `rows` = `header` = `coreUnreadable` | one number, at most 682 (about 645-660) | |
| `notScanned` | 1 | |
| `oracle.openRefusedButOpenable`, `LEAK:` lines | 0, none | |
| `emptyReasons`, `duplicates` | 0, 0 | |
| code 5 / 363 / 1920 (`byCode`) | rest / equal to the Temp anchor's (27-28 at bd6572f) / 0-2 | |
| `oracle.leftoverRows` code363 / reparse, `notARefusedKeepShape` | equal to the Temp anchor's (27-28 at bd6572f) / 0 / `[]` or code 1920 (2 or less) | |
| `byKind.openedNothingListed`, `listedPartway` | 0, 0 | |
| `kernelPoolBytes` growth | about 537 MB or more | |
| Anchors `rows`: C:\Windows / C:\ProgramData / Temp | about 52 / about 500 (WER alone is 438) / about 40 (27-28 code 363, 1 control, the fixtures) | |
| Remainder `rows(C:)` minus the anchors | tens | |
| Criterion 5 | met if every difference from 682 is named | |
