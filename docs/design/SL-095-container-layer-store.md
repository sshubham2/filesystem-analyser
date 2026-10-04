# SL-095 "Kernel pool left behind after C: scans": design as built

A scan of C: walked into the live Windows container layer under
`%ProgramData%\Microsoft\Windows\Containers\Layers\<id>\Files`, and wcifs.sys, Microsoft's container isolation
minifilter, keeps kernel pool and a System-process handle for every layer entry opened for the first time since a
restart (microsoft/Windows-Containers#646). The app now never opens that store, and a root at or inside it is refused.
Whether that is the whole answer to the reported ~30 GB is what the person-run protocol below decides; the tolerance
row is written here, before any run. Where this file and the design disagree, this file is right.

## What changed in the product

- **The store is found by its path, once per scan, before anything is listed** (`ContainerLayerStore.Resolve`, the
  only file in src that spells where the store is). First a string check with no open: root and store in `\\?\` form,
  trailing separators trimmed, compared without case. Only when that decides nothing, two `FILE_READ_ATTRIBUTES` opens
  through the guarded open, the root and the store's anchor (`%ProgramData%`, or the planted store's parent under the
  test override; never the store itself), each asked for its final path (`GetFinalPathNameByHandleW`). That catches a
  subst drive, a junction or symbolic link, an 8.3 name and a `\\?\Volume{GUID}\` root, and maps the store back into the
  root's own spelling. A root that cannot be opened, or a final path Windows will not give (a share), leaves the string
  verdict standing (None) and the scan goes on to its usual RootUnreadable. An empty or relative known folder is no
  store, never every path.
- **A scan above the store stops at it.** The walk compares each folder it pops with the one excluded path, for
  equality, without case, as listed (never through GetFullPath, which would fold `Layers.` or `Layers ` onto it). The
  store completes as a folder with nothing listed, flagged `EntryFlags.NotScanned`, recorded in
  `ScanResult.SkippedStores` and never in `ScanResult.Unreadable`, so nothing that reads or retries unreadable folders
  can reach it (review M5, X1). Its size is in no total; it falls into the coverage gap. Nothing below it is pushed,
  listed or probed, and `AfterFolder` does not fire for it. A Cancelled result keeps the record when the walk got there.
- **A backstop at the one listing call.** `DirectoryReader.Read` refuses, unopened, any folder at or under the excluded
  store and counts it: `ScanResult.OpensUnderExcludedStore`, 0 whenever the walk's own check held (review m4, M6).
- **A root at or inside the store is refused** (Q-179, Q-182): `ScanStatus.Refused`, `ScanRefusal.ContainerLayerStore`,
  a result and never an exception, with nothing listed or probed. The window shows `Not scanned: <root>` and says why,
  with no dialog and no scanning state. The report writes `outcome: "refused"`, `refusal: "container-layer-store"`, and
  exits 0.
- **One precaution, on by default, that a person can turn off** (`Workarounds.ContainerLayerStore`, id
  `wcifs-container-layers`, in Core's one list `Workarounds.All`). The window's **Precautions...** button lists every
  entry with the outside defect and where it lives, what the app does, and what turning it off brings back; unticking
  asks first with that consequence in the question. The app saves nothing to disk (stack decision), so a precaution
  turned off is on again at the next start. Off restores the plain behaviour exactly: the store is walked like any
  folder and a root inside it is scanned (`ContainerLayerStoreTests.WithThePrecautionOffTheStoreIsWalkedAndARootInsideItIsScanned`).
- **The app says so beside the result.** A scan that left the store out adds an outcome note, `Not scanned, size not
  counted: <store> (Windows container layers)`, with the defect and the precaution's name; the results list row reads
  `Layers  (size not counted: Windows container layers)`; the tree note and the unreadable tab's own row say the same.
  The tab header counts it apart: `Not scanned (1)`, or `Unreadable folders (n), not scanned (1)`. No row of the store
  offers 'Show in Explorer' (review Y6): browsing the layer is what the precaution avoids.
- **The schema-1 report, additively:** `core.unreadable`, `core.skippedStores`, `core.opensUnderExcludedStore`,
  `outcome`, `refusal`, `precautions` (id and on/off), `topLevel.notScanned`, `window.OutcomeNotes`,
  `window.PrecautionsButton`. Counts and ids only, never a path (Q-016). `eng/read-window.ps1 -Outcome` prints one line
  of them.

Unchanged on purpose: FILE_FLAG_OPEN_REPARSE_POINT is not added (0 valid measurements, spike 85), and SizeProbe
(SC-011), the last-access suppression (SC-070, SC-074), the reparse re-open (SC-067) and the CancelSynchronousIo path
(SC-017) are as they were (Q-183 keeps Q-004's probe-every-file rule). The protocol's arms S and P separate the per-file
probe from a plain open, and its cancelled runs exercise the cancel path; SC-070/074 and SC-067 are not narrowed one by
one here (follow-up if the protocol routes to B or C).

Not covered, and not looked for: the same store over a loopback share (`\\localhost\C$`), and other WCI stores such as
`ProgramData\Docker\windowsfilter`.

## DONE (a): the growing tags and who owns them

Owners by `PoolWatch.ps1 -Attribute` on 5 Oct 2026 (findstr over `System32\drivers\*.sys` and `System32\*.exe`, no
pooltag.txt on this machine; `docs/design/SL-095-runs/build-2026-10-05/attribute.txt`). Counts from the repro stage's
mark `e1` on the boot of 29 Sep 2026, 27 h old, FsAnalyser not running (`first-touch-2026-10-01/e1.mark`).

| Tag | Owner (measured) | Allocs / frees on that boot | Bytes | Attributed to |
|-----|------------------|-----------------------------|-------|---------------|
| WCsc | wcifs.sys 10.0.26100.9278, Microsoft | 210,816 / 0 (nonpaged) | 81 MB | the layer walk (#646) |
| WCfc | wcifs.sys | 210,734 / 0 (nonpaged) | 44 MB | the layer walk |
| WCri | wcifs.sys | 211,250 / 516 (paged) | 114 MB | the layer walk |
| FMwi | fltMgr.sys 10.0.26100.1, Microsoft, for wcifs | 214,803 / 3,969 | 27 MB | the layer walk |
| FMfc | fltMgr.sys, for wcifs | 539,780 / 328,131 | 41 MB | the layer walk, in part |
| FMfn | fltMgr.sys | 261.1M / 258.9M paged | 1.22 GB | not attributed |
| File | ambiguous by findstr (872 files); the I/O manager in ntoskrnl by pooltag.txt, recalled | 520.0M / 517.4M | 1.04 GB | not attributed |
| NtfF | ntfs.sys 10.0.26100.1, Microsoft | 3.30M / 3.02M | 453 MB | not attributed |
| Toke | ambiguous by findstr (488 files); tokens, ntoskrnl, recalled | 53.6M / 53.3M | 828 MB | not attributed |

The wcifs group is about 0.33 GB of the 8.3 GB pool on that boot, plus one System handle per WCsc (211,070 at access
0x120089). It is capped by first touch: repeat walks of the same entries add 0 WC* (`deltas.txt`: e2_1 to e2_5, e4_21 to
e4_26, e5_31a to e5_37b all WCsc 0/0; e3_11 to e3_15, an untouched subtree, WCsc +5/0). So wcifs alone does not explain
pool that "built up scan after scan" to 30 GB. FMfn, File, NtfF and Toke are the largest tags and are not attributed to
the app by anything measured yet: the protocol's arms are what attribute them.

The same tags after a full boot, 5 Oct 2026 00:41 (`build-2026-10-05/marks.jsonl`): WCsc 3,585 live, System 3,636
handles at 0x120089, pool 2.24 GB paged and 1.61 GB nonpaged, against WCsc 211,022, 211,087 handles, 6.01 GB and 4.09 GB
read before it (spike attempt 3's R0). A full boot (LastBootUpTime moved) gave all of it back. On that boot the build's
scans of `...\Containers` and `...\Microsoft\Windows` (the repro test, window cases X-001, X-002 and X-004, 461
unreadable folders and the store left out) left WCsc at 3,585 and the 0x120089 handles at 3,636 from the first mark to
the last (`build-2026-10-05/build-marks.jsonl`, uptime 3,816 s to 5,071 s): listing the store's siblings allocated no
WC*. That boot's layer was not proved armable by a control (that would
spend it), so this is evidence, not the protocol's verdict.

## The environment (criterion 3)

`build-2026-10-05/environment.json`, unelevated: Windows 11 Pro 26200; wcifs.sys 10.0.26100.9278; wcifs, CldFlt,
WdFilter, bindflt, FileInfo, luafv and storqosflt running; minifilter instances in the registry: bindflt 409800, WdFilter
328010, CldFlt 180451, FileInfo 40500 (wcifs attaches at run time and lists none); antivirus Kaspersky (three entries),
Windows Defender and McAfee; OneDrive running; HiberbootEnabled 1 (Fast Startup on); one layer, 1f35079a. `fltmc filters`
needs an administrator and is not here: a person who chooses to runs it in an elevated window of their own and pastes it
into `fltmc.txt` in the run folder, which `-Environment` appends. `-Report` prints a `FILTERS` line apart from the pool
verdict: `recorded` when `fltmc.txt` (in the run folder or its `prepare` folder) or an environment record holds the
filter list itself, its `Filter Name  Num Instances` header and no refusal; `UNDECIDED` otherwise, never recorded. A
paste of what unelevated fltmc prints here ('Filter listing failed with error: 0x80070005', 'Access is denied.') reads
`FILTERS UNDECIDED: fltmc.txt holds no filter list (...)`, and the environment record keeps it as `not a filter list`.

## The tolerance row (written 5 Oct 2026, before any protocol run)

Per pool, Paged and Nonpaged: **T = max(64 MiB, 3 x |idle drift| over a window of the same length)**. Idle drift comes
from I0 and I1, 20 minutes each, per gap between marks, with any gap whose rate is over 3x the median gap's left out as
a burst. I0 begins only once the boot has settled: after R0, a `settle/NN` mark every minute until two gaps in a row
each move both pools slower than 3 x spike 3's idle drift of 10,936 B/s, 32,808 B/s or about 1.9 MiB a minute, for at
most 30 minutes (spike 3 measured about 1 GiB of boot settle on this PC). The gate is as strict as the VOID it protects:
I0 drifting more than 3x I1 VOIDs the run, so the 16 MiB-a-gap gate first built (spike 3's V2 rule) let a boot still
growing 2-16 MiB a minute start a session that VOIDs at its end (review R3-1). R0 stays the boot-age and guard
reference; the pre-scan baseline is `L/before`, which the post-pressure figure is judged from. Every arm's reading is
taken 10 minutes after its process exits, net of drift. An arm the guard stopped (L, S, P or an exe run) counts as over
T. A ScanArm the guard killed prints no summary, so a stopped S or P is over T with no projection (review R3-2): S
stopped while P is not is B; P stopped, with S stopped or only projecting over T, is C, the probe by nothing beyond it.

**PASS (outcome A)** needs all of: all ten exe runs there (five full, each exiting 0 with a Completed report, and five
cancelled or closed that each stopped before the end: the hook pressed Cancel or closed the window, and each Cancel's
report says Cancelled), each within T; the 10 together within T; the cold projection
L_net + s x N_full within T for the per-file probe; every arm's WCsc rise inside the idle envelope of the neighbouring
gaps and its 0x120089 handle rise no more than its WCsc rise; every exe report's `opensUnderExcludedStore` 0 and every
full run's `skippedStores` 1; and the closing control armable: its WCsc rise at least half the entries it listed, with
the handle rise matching.

Otherwise, in this order (`PoolWatch.ps1 -Report`, pinned by `PoolWatchReportTests`):

| Outcome | When | Records |
|---------|------|---------|
| VOID | R0 more than 30 min after the boot; the boot not settled within 30 min of R0 (-Protocol stops there); I0 and I1 drift more than 3x apart; the closing control not armable; an arm's process did not exit | nothing; the run decides nothing |
| UNDECIDED | a mark or a reading a criterion needs is missing or null (no settle marks; tags, or the class 64 0x120089 handle count of an arm or the closing control); the ten exe runs not all there with their before and settled marks; a full run that did not exit 0 with a Completed report, unless the guard stopped it; a cancelled or closed run whose hook never acted (the scan ended before the trigger, the app shut down before it, or its deadline passed), or a cancelled run with no report or one that does not say Cancelled, unless the guard stopped it; L over T with no pressure step, or with one that took nothing back (INEFFECTIVE) | nothing; never PASS |
| WCIFS-UNATTRIBUTED | an arm's WCsc or handle rise outside the idle envelope, or an exe run that opened under the store or (full run) did not record it | blocks A until a person looks |
| C | L over T, and what L kept survives the pressure step; or the probe and a plain open both project over T, alike; or the plain open stopped by the guard, with the probe stopped too or over T | DONE (c) OS-recorded only if Q-178's three conditions hold (Q-184), with the session-2 non-app walk (Q-185); otherwise FAIL |
| A (reclaimed) | L over T before the pressure step, within it after: the retention was cache | continues to the remaining clauses |
| B | the probe projects over T (or the guard stopped it) where a plain open does not, or by more than half T beyond it, or the guard stopped it where a plain open over T was not stopped | FAIL; Q-183's new design opens with this run's data |
| FAIL / FAIL-cancel | a full exe run, or the 10 together, over T / a cancelled or closed run over T while the full runs are within | FAIL; FAIL-cancel implicates the cancel path (follow-up) |

**Result: pending.** No protocol run exists. DONE (c) stays open until `docs/design/SL-095-runs/session-1/result.txt`
(and, if L is over T, `session-2`) does.

## The protocol (person-run)

1. On the boot as it is: `pwsh -NoProfile -ExecutionPolicy Bypass -File tests\window\PoolWatch.ps1 -Prepare`. It builds
   every binary the protocol runs (ScanArm, the window hook, the fixed app, and the unfixed app from afd021d through
   `git archive`), takes Rpre, the environment, the store's size bound, the closing control's subtrees (none holding a
   directory link; Windows\Globalization first, which the repro found untouched late in a boot), checks every hook
   trigger fires against both builds, checks the no-link walker, and checks the pressure step reclaims a cold %TEMP%
   tree's retention. The no-link check uses the walker's own flags and can fail: a planted 'All Users' junction (and a
   directory symlink where this user may make one) must count none of its target's bytes and list none of its folders
   at three levels; the same listing without `/XJ` must go through it, or the check could not see a followed link; and
   a three-level walk of the real `C:\Users`, every other child left out with `/XD`, must not list
   `All Users\Microsoft`. `-NoLinkCheck` runs it alone. Nothing is built after the Restart.
2. Start > Restart (not Shut down), sign in, open nothing but one PowerShell window, and within 30 minutes run
   `powershell -NoProfile -ExecutionPolicy Bypass -File tests\window\PoolWatch.ps1 -Protocol`. It refuses to start on an
   older boot, or when prepare.json does not record the no-link walker as safe (`noLinkWalkerSkipsAllUsers`). About 4
   hours, left alone: R0; the settle marks until the boot has settled (it stops after 30 min if it has not); I0; arm L, the app's listing walk of all of C: with the store left out,
   with a progress log and a mark every minute; arm S, the app's SizeProbe on half A of L's file list; arm P, a plain
   open of half B (both from L's list, so neither can reach the store); 5 full exe scans; 5 cancelled or closed (Cancel
   500 and 1,000 folders past the store and `C:\Users`, Cancel 200,000 files into the size pass, close 5,000 folders past
   the store, close 400,000 files into the size pass); the pressure step (memory committed down to 4,608 MB available,
   above the 4,096 MB guard); I1; the closing control (`dir /a /s /b` on the layer subtrees picked in -Prepare); then,
   only after it, the unfixed app: two full scans and one cancelled past the store, which walk the live layer (review
   Y1). -Report runs at the end. Commit the run folder.
3. Only if L was over T: a second Restart and `-Protocol -Session 2`, the non-app walk of C: per top-level folder with
   robocopy `/L /E /XJ /XD <store>`, which never follows a directory link (Q-178 condition 2, Q-185). cmd's `dir /s`
   follows them, and `C:\Users\All Users` links to `C:\ProgramData`, so `dir /s C:\Users` would walk the live layer.

Guard: an arm is killed and recorded as over T if Available falls below 4,096 MB or Paged + Nonpaged rise 4 GiB above
R0. The script never elevates and never runs poolmon, wpr, xperf or fltmc (architecture rule).

## The excluded store's size

Unmeasured. Q-180 and Q-181 decided that leaving the whole store out, shown as a 'size not counted' row, is an honest
shortfall under the counting rule. That covers `Layers\<id>\Files` as well as Hives, Bindings, Specialization and
dotnet.bcf: the Files tree is not only placeholders of host files (the earlier precondition, O-002), since Microsoft
describes the dynamic base image as holding real pristine copies of the mutable OS files, so real bytes go uncounted.
`-Prepare` records an upper bound once, on the pre-Restart boot that has walked it already, with robocopy's
`/L /E /XJ /BYTES` total (`storeBytesUpperBound` in `prepare.json`). It is a reference only and is never added to any
total.

## The workaround, and when the precautions change

- **Start > Restart gives the leaked pool back.** Measured once: the full boot of 5 Oct 2026 above. **Shut down does
  not**, with Fast Startup on (HiberbootEnabled 1 here): that half is sourced from KB3211190 and a #646 comment, not
  measured here (review N4).
- **Do not browse the layer store** in Explorer or the folder picker: they list it before the app can refuse anything.
- There is no fixed wcifs.sys for 25H2 (#646; microsoft/WSL#41296's fix has not shipped).
- **When the refusal can be lifted** (Q-179): a valid PoolWatch run showing that enumeration with
  FILE_FLAG_OPEN_REPARSE_POINT causes no wcifs growth on an untouched layer subtree. **When the exclusion can narrow**
  to `Layers\<id>\Files` (Q-180, Q-181): a post-Restart run showing that listing Hives, Bindings, Specialization and
  dotnet.bcf adds no WC* and no System handles. Each is a one-predicate change in `ContainerLayerStore`.
- The cancelled-run and protocol rigs (`tests/window/ScanArm`, its InternalsVisibleTo line in FsAnalyser.Core.csproj)
  are deleted once O-003's outcome is recorded (stack decision).
