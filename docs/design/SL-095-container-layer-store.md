# SL-095 "Kernel pool left behind after C: scans": design as built

A scan of C: walked into the live Windows container layer under
`%ProgramData%\Microsoft\Windows\Containers\Layers\<id>\Files`, and wcifs.sys, Microsoft's container isolation
minifilter, keeps kernel pool and a System-process handle for each layer entry a program opens, until a full restart,
and opening the same entries again was measured keeping more (microsoft/Windows-Containers#646; *DONE (a)* below).
While the precaution against that defect is on, as it is at every launch, the app never opens that store: a root at or
inside it is refused, and a scan above it leaves it out. A person who turns the precaution off in Settings, after a
confirmation that states the cost, lifts both for that session (Q-186).

The slice was narrowed (design 10, 5 Oct 2026): it judges no pool tolerance. **The tens-of-GB report is not claimed
fixed.** Spike #87 (4 Oct 2026, after a true Restart, store excluded) stopped arm L at its guard with +375 MiB paged and
+138 MiB nonpaged in NtfF, FMfn and File and no WC* rise: outcome C by fork O-028's own rule (i). So DONE (c), the
five-plus-five verdict, the settle gate and the idle-drift judgement go to SC-128 (below, *Not judged here*). Where this
file and the design disagree, this file is right.

## What changed in the product

- **The store is found by its path, once per scan, before anything is listed** (`ContainerLayerStore.Resolve`, the
  only file in src that spells where the store is). First a string check with no open: root and store in `\\?\` form,
  trailing separators trimmed, compared without case. Only when that decides nothing, two `FILE_READ_ATTRIBUTES` opens
  through the guarded open, the root and the store's anchor (`%ProgramData%`, or the planted store's parent under the
  test override; never the store itself), each asked for its final path (`GetFinalPathNameByHandleW`). That catches a
  subst drive, a junction or symbolic link, an 8.3 name and a `\\?\Volume{GUID}\` root, and maps the store back into the
  root's own spelling. A root through this PC's own administrative share of a drive (`\\localhost\C$\...`,
  `\\127.0.0.1\C$\...`, the `::1` form `0--1.ipv6-literal.net`, this PC's name, DNS name, full name or one of its own
  addresses) is compared as the drive path it names, in both steps, and so is a final path that comes back as such a
  share (a drive letter mapped to it, a symbolic link to it); the excluded path goes back into the root's own spelling
  (validate attempt 2 scanned `\\localhost\C$\...\Layers\<id>\Files\Windows\Media` as picked; O-084). Nothing is looked
  up on the network to decide it. Through the published exe on this PC's store, with the precaution on: that Media root
  is now refused, and WindowCases X-006 refuses `\\localhost\C$\...\Layers` and skips the store from
  `\\localhost\C$\...\Containers` (skippedStores 1); WCsc stayed at 3,608 and System handles at 0x120089 at 3,658 across
  all of it (`build-2026-10-05/loopback-media-marks.jsonl`, `x006-loopback-marks.jsonl`). A root that cannot be opened, or a final path Windows will not give, leaves the
  string verdict standing (None) and the scan goes on to its usual RootUnreadable. An empty or relative known folder is
  no store, never every path.
- **A root at or inside the store is refused while the precaution is on** (Q-179, Q-182, Q-186): `ScanStatus.Refused`,
  `ScanRefusal.ContainerLayerStore` and the precaution's id, a result and never an exception, with nothing listed or
  probed. The window shows `Not scanned: <root>`, says why in the precaution's own words (the defect, and where it lives:
  wcifs.sys), that a folder at or inside the store is not scanned while the Settings checkbox is ticked, and that the
  protection can be turned off in Settings. No dialog, no scanning state. The report writes `outcome: "refused"`,
  `refusal: "container-layer-store"`, and exits 0. Q-186 answered design 10's open question: the checkbox's off position
  lifts the refusal as well as the exclusion, for the session, after its confirmation.
- **A scan above the store stops at it, while the precaution is on.** The walk compares each folder it pops with the
  one excluded path, for equality, without case, as listed (never through GetFullPath, which would fold `Layers.` or
  `Layers ` onto it). The store completes as a folder with nothing listed, flagged `EntryFlags.NotScanned`, recorded in
  `ScanResult.SkippedStores` (with the precaution's id) and never in `ScanResult.Unreadable`, so nothing that reads or
  retries unreadable folders can reach it (review M5, X1). Its size is in no total; it falls into the coverage gap.
  Nothing below it is pushed, listed or probed, and `AfterFolder` does not fire for it. A Cancelled result keeps the
  record when the walk got there.
- **A backstop at the one listing call.** `DirectoryReader.Read`, the only caller of `SynchronousIoCancel.OpenDirectory`,
  refuses, unopened, any folder at or under the excluded store and counts it: `ScanResult.OpensUnderExcludedStore`, 0
  whenever the walk's own check held (review m4, M6). `TheReaderRefusesAndCountsAFolderUnderTheStoreWithNoOpen` drives it
  on a folder that denies its listing, so an issued open would come back access denied: it returns the skip with no
  call and counts 1 (review F4).
- **One list of precautions against outside defects** (`src/FsAnalyser.Core/Workarounds/KnownWorkarounds.cs`), its first
  entry `wcifs-layer-store`: the defect, where it lives (wcifs.sys, shipped by Microsoft, no fixed version on 25H2), what
  the app does, what turning it off does, on by default. A scan takes the ids that are on
  (`ScanOptions.EnabledWorkarounds`); only `ContainerLayerStore` reads this one. Off, `Resolve` answers None at once,
  with nothing compared or opened, which restores the plain walk exactly (Q-186): a scan above the store lists and
  counts it like any folder, with no not-scanned record and no refusals at the backstop
  (`WithThePrecautionOffTheStoreIsListedAndCountedLikeAnyFolder`), and a root at or inside it is scanned as picked,
  every entry counted (`WithThePrecautionOffARootAtOrInsideTheStoreIsScannedAsPicked`, which also shows the same root
  Refused with it on). Both run on a planted store only: off against the real store leaks until a Restart, so nothing
  runs it there.
- **Settings.** The window's **Settings...** button opens a window that lists every entry of that list as it is, one
  checkbox each with its plain-words text, naming no entry itself. Unticking one asks first in a confirmation window whose
  question states the consequence (the store read like any folder, a folder picked inside it scanned as picked, and
  wcifs.sys leaking kernel memory and a System handle for each store entry the scan touches, until you restart Windows,
  with every later scan of it able to add more); No leaves it ticked. The app saves nothing to disk (stack decision),
  so it is on again at the next start. WindowCases X-005 drives both answers through the hook's settings mode and reads
  the state from the report, and the next launch's.
- **The app says so beside the result.** A scan that left the store out adds an outcome note, `Not scanned, size not
  counted: <store> (Windows container layers). Skipped to avoid a leak in wcifs.sys, ...`, with the defect and the
  Settings checkbox that left it out; the store's own row in the unreadable tab reads `size not counted: Windows
  container layers, skipped to avoid a leak in Windows' wcifs.sys driver; see Settings`, and the tree note the same. The
  tab header counts it apart: `Not scanned (1)`, or `Unreadable folders (n), not scanned (1)`. No row of the store offers
  'Show in Explorer' (review Y6): browsing the layer is what the precaution avoids.
- **The schema-1 report, additively:** `core.unreadable`, `core.skippedStores`, `core.notScanned` (the same count under
  the name the pool protocol reads), `core.opensUnderExcludedStore`, `outcome`, `refusal`, `workarounds` (each entry's id
  and on/off), `topLevel.notScanned`, `window.OutcomeNotes`, `window.SettingsButton`. Counts and ids only, never a path
  (Q-016). `eng/read-window.ps1 -Outcome` prints one line of them.

Unchanged on purpose: FILE_FLAG_OPEN_REPARSE_POINT is not added (0 valid measurements, spike 85), and SizeProbe
(SC-011), the last-access suppression (SC-070, SC-074), the reparse re-open (SC-067) and the CancelSynchronousIo path
(SC-017) are as they were (Q-183 keeps Q-004's probe-every-file rule).

### Where the one-checkbox-per-defect rule comes from (review F1)

It is not written in this repository and has no Q-number. It is a standing rule of the TAAD engine's build stage, given
in the build instructions of every slice: a workaround for a defect outside the project is a setting, one checkbox per
known bug, on by default, with an in-app notice beside every result it changes, and one list of entries. Design 10 took
it from there. Q-186 then decided how it meets Q-182: the checkbox governs the exclusion and the refusal together. On,
Q-182 stands as decided; off, after the confirmation, a picked root inside the store is scanned as picked, because the
refusal exists only because of the outside defect and a scan of C: walks the store anyway once the exclusion is off.

## DONE (a): the growing tags and who owns them

Owners by findstr over `System32\drivers\*.sys` and `System32\*.exe` on 5 Oct 2026 (no pooltag.txt on this machine;
`docs/design/SL-095-runs/build-2026-10-05/attribute.txt`). Counts from the repro stage's mark `e1` on the boot of 29 Sep
2026, 27 h old, FsAnalyser not running (`first-touch-2026-10-01/e1.mark`; the e1..e5 and tok_a/tok_b files there are
probe-walk marks, not counter runs).

| Tag | Owner | Allocs / frees on that boot | Bytes | Attributed to |
|-----|-------|-----------------------------|-------|---------------|
| WCsc | wcifs.sys 10.0.26100.9278, Microsoft | 210,816 / 0 (nonpaged) | 81 MB | the layer walk (#646) |
| WCfc | wcifs.sys | 210,734 / 0 (nonpaged) | 44 MB | the layer walk |
| WCri | wcifs.sys | 211,250 / 516 (paged) | 114 MB | the layer walk |
| FMwi | fltMgr.sys 10.0.26100.1, Microsoft, for wcifs | 214,803 / 3,969 | 27 MB | the layer walk |
| FMfc | fltMgr.sys, for wcifs | 539,780 / 328,131 | 41 MB | the layer walk, in part |
| FMfn | fltMgr.sys (owner tag known) | 261.1M / 258.9M paged | 1.22 GB | holder unattributed |
| File | the I/O manager in ntoskrnl (pooltag.txt, recalled; findstr ambiguous, 872 files) | 520.0M / 517.4M | 1.04 GB | holder unattributed |
| NtfF | ntfs.sys 10.0.26100.1, Microsoft | 3.30M / 3.02M | 453 MB | holder unattributed |
| Toke | tokens, ntoskrnl (recalled; findstr ambiguous, 488 files) | 53.6M / 53.3M | 828 MB | holder unattributed |

The wcifs group is about 0.33 GB of the 8.3 GB pool on that boot, plus one System handle per WCsc (211,070 at access
0x120089). Whether it is capped by first touch is **not settled, and the app's text no longer says so**. The repro
stage's marks on the 29 Sep boot show repeat walks adding 0 WC* (`deltas.txt`: e2_1 to e2_5, e4_21 to e4_26, e5_31a to
e5_37b all WCsc 0/0; e3_11 to e3_15, an untouched subtree, WCsc +5/0). But validate's marks on the 5 Oct boot
(`validate-2026-10-05/marks.jsonl`, copied from validate's scratch) show four walks of the same layer subtree,
`Files\Windows\Globalization`, with the precaution off, each adding 5 WCsc, 5 WCri and 5 System handles at 0x120089
(L0 to L1 to L2, N0 to N1 to N2: 3,638, 3,643, 3,648, 3,653, 3,658), against 0 over both idle stretches beside them.
So the precaution's text says each entry touched, until a restart, and that later scans can add more. Even at first
touch only, the group is about 0.33 GB a boot, which alone does not explain pool that "built up scan after scan" to
30 GB.

**A Restart reclaims the wcifs leak, measured.** Spike #87's V1 after the 4 Oct 2026 Restart: WCsc 1,630 live and 1,661
System handles at 0x120089, against 211,022 and 211,087 before it. The build's full boot of 5 Oct 2026 read the same
(`build-2026-10-05/marks.jsonl`: WCsc 3,585, 3,636 handles). **Shut down** with Fast Startup on (HiberbootEnabled 1
here) is sourced from KB3211190 and a #646 comment as not reclaiming it, not measured (review N4).

**What spike #87's arm L kept** with the store excluded, L_start to the guard stop at 3,313,298 entries (92 s): paged
+375 MiB, nonpaged +138 MiB; NtfF +81,867 allocs with 0 frees (274 to 411 MB), FMfn 518 to 676 MB, File 107 to 154 MB;
WCsc, WCse, WCss, WCfc, WCri, WCfn and FMwi identical at L_start and L_exit. Those tags' owners are known; who holds
them, and whether memory pressure gives them back, is SC-128's.

## DONE (b)

The app change above, plus the documented workaround: **Start > Restart, not Shut down**, gives the leaked pool back;
**do not browse the layer store** in Explorer or the folder picker, which list it before the app can refuse anything.
There is no fixed wcifs.sys for 25H2 (#646; microsoft/WSL#41296's fix has not shipped).

## The store as the only live WCI store a C: walk reaches (review F3, fork O-085)

Cross-domain invariant 3 is narrowed to what was measured: **on spike #87's boot, the walk up to the guard cutoff
(3,313,298 entries) reached no live WCI entry with only `Layers` excluded.** Arm L's own record
(`SL-095-runs/spike-87-2026-10-04/L.json`): `skippedByRule` 1, `skippedPaths` `\\?\C:\ProgramData\Microsoft\Windows\Containers\Layers`,
0 opens under the store by its tripwire or its rule check; `skips.log` names the store. Its folder list (`dirs.log`,
counted in `dirs-count.txt`, not copied: it is every folder path on C:) lists `...\Windows\Containers` at line 23,422 of
669,824, and all 642,894 of its `C:\Users\` and `C:\Windows\` folders after it: L reached the store, skipped it and
walked past it, through `Containers\BaseImages` and `Containers\ContainerStorages`, and `C:\Users\All Users` was not
followed (links never are). Spike #87's `marks.jsonl` and `events.log` are no longer in its run folder
(`C:\WINDOWS\TEMP\sl095-o003-20261005-004311` holds only `L\`): a gap in the risk-spike stage's record; the WC* figures
above are quoted from its account.

Not measured: the tail of C: after the cutoff, and any other WCI store. Nobody looked for a running Windows Sandbox's
live storage, `ProgramData\Docker\windowsfilter` or another container runtime's layers (O-049). Roots through this PC's
own administrative shares are matched now (above, O-084); a share a person created over the store or one of its
ancestors (`\\localhost\ProgramData`) is not. **What reopens it:** a raw WC* rise in a person-run exe-full arm that
reports notScanned at least 1 and opensUnderExcludedStore 0; `-Report` prints each arm's raw WC* nets, so nothing new is
needed to see it. That evidence goes to a follow-up slice widening `Resolve` to several stores, not back into SL-095.

## The environment (criterion 3)

`build-2026-10-05/environment.json`, unelevated: Windows 11 Pro 26200; wcifs.sys 10.0.26100.9278; wcifs, CldFlt,
WdFilter, bindflt running; antivirus Kaspersky (three entries), Windows Defender and McAfee; OneDrive running. `-Prepare`
records the same into `prepare.json` (OS build, boot time, wcifs.sys version, those four drivers' state, antivirus,
OneDrive). `fltmc filters` needs an administrator and is not here: a person who chooses to runs it in an elevated
window of their own and pastes it into `fltmc.txt` in the run folder, at any time (review m1). `-Report` reads that file,
then `prepare\fltmc.txt`, each time it runs, then the record `-Prepare` froze, and names the one it used; `-Protocol`
carries a paste along when it starts a fresh run folder. It counts only when it is the list itself (`Filter Name  Num
Instances` and no refusal); otherwise `-Report` says `FILTERS UNDECIDED` (review R2-N3). PoolWatchCases P-001 feeds
each place a list and a refusal (review m2).

## The pool protocol (person-run): complete logs, no verdict

1. On the boot as it is: `pwsh -NoProfile -ExecutionPolicy Bypass -File tests\window\PoolWatch.ps1 -Prepare`. It
   publishes the app and builds the window hook (nothing is built after the Restart) and writes `prepare\prepare.json`
   with an Rpre reading and the environment record.
2. Start > Restart (not Shut down), sign in, open nothing but one PowerShell window, and within 30 minutes run
   `powershell -NoProfile -ExecutionPolicy Bypass -File tests\window\PoolWatch.ps1 -Protocol`. It refuses to start when
   prepare.json, the published app or the hook is missing, or when the boot is over 30 minutes old (review X4). It
   writes a plan line naming every arm, then R0; I0, 20 minutes idle; 5 full exe scans of C:; 4 cancelled (500 folders
   past the store, 1,000 past `C:\Users`, 200,000 and 400,000 files into the size pass) and 1 closed (5,000 folders past
   the store), through the hook's existing triggers; I1, 20 minutes idle. Each arm has /before, /exit and /settled
   (10 minutes after exit) marks, and one a minute while it runs: pool bytes, Available, the WC*, FMwi, FMfc, FMfn,
   File, NtfF and Toke tags as an array, the System handle count and its 0x120089 count. Each arm's line carries the exit
   code, the report's status, notScanned and opensUnderExcludedStore, the hook's acted or refused line and the folder
   count at the cancel. About 3 hours; commit the run folder.
3. `-Report` (it also runs at the end of `-Protocol`) prints `RUN COMPLETE`, or `RUN INCOMPLETE` and one line per
   missing item: an arm in the plan but not in arms.jsonl; a /before, /exit or /settled mark missing, or a field in one
   null (review m7); a guard stop, with the arm's net at the stop; an arm not started because the guard was already
   exceeded, named as the cascade of the arm that stopped (review R4-2); not exactly 5 full and 5 cancelled or closed
   runs; a full run without exit 0, a report, status Completed, notScanned at least 1 and opensUnderExcludedStore 0; a
   cancel or close whose hook never acted (its refused line quoted: the scan ended first, the app shut down first, or
   the trigger's deadline passed; review R2-R3-3); a cancel whose report does not say Cancelled; an arm whose /before
   mark is written but whose arm line is not, and an arm marked interrupted (review R2-n2: a terminating error or
   Ctrl+C while an arm runs kills FsAnalyser.exe in the arm's `finally` and still writes its line); and each line of
   marks.jsonl or arms.jsonl that is not JSON, a torn last line or a NUL-filled tail, read line by line so that such a
   line never stops `-Report` (review R2-n1). Then the raw nets of every arm and idle window, the environment, and the
   FILTERS line. It exits 0 complete, 2 incomplete, and prints no judgement word (fitness rule;
   `tests/window/PoolWatchCases.ps1` asserts it on every fixture: C-053 to C-056, C-027 for a missing mark, a null
   field and a burst printed raw, P-001 to P-003; C-011 shows `-Protocol` refusing this old boot having written nothing).
4. `-Protocol -Rehearse` runs the same code over a generated %TEMP% tree of 30,150 folders, which the app takes many
   250 ms window ticks to list, so the folder-count triggers (10% to 25% of the folders, a 120 s trigger deadline) act
   mid-scan (review F5); windows of 30 s, marks every 5 s, /settled 15 s after exit; no boot-age check; every line says
   rehearsal; the scratch is deleted unless `-Keep`. PoolWatchCases C-052 runs it and requires RUN COMPLETE. The design
   said 60 s / 30 s windows; they are 30 s / 15 s so the unattended run stays near five minutes.

**The machine guard is per arm** (review R4-2, F2): FsAnalyser.exe is killed and the arm records `guardStop` when the
combined pool grows more than **6 GiB over that arm's own /before mark**, or Available falls under **3 GiB**. The pool
ceiling sits above a cold full C: scan's predicted cost rather than at the 2 GiB design 10 named: spike #87's arm L kept
537 MB at 669,824 directory listings, about 800 B each, so C:'s ~920,000 folders cost about 0.7 GiB to list; SizeProbe
then opens each of 2.35M to 3.7M files for the first time, at about 1 KB each (the cited 25H2 report), 2.3 to 3.6 GiB:
3 to 4.4 GiB in all, which 2 GiB would cut off at the first cold full scan, the measurement SC-128 most needs. Nobody has
measured that cost cold, so no figure is claimed to hold: if the guard stops an arm anyway, -Report lists it as
incomplete with its net at the stop. Available under 3 GiB is what protects the machine. An arm that would start with
Available already under it is not started (`guardAtStart`).

## Not judged here: handed to SC-128

SC-128 redoes the design for outcome C. It receives, by finding id: R3-1 and R4-1 (the settle gate, and its per-pool
limits; there is no gate in this slice), M6, N2 and X5 (drift per gap, the idle envelope, WCIFS-UNATTRIBUTED), the N1
envelope check between arms, M3, N3 and X2 (cache against retention under memory pressure, and the pressure step's
positive control), Y3 (the closing layer control), M2 (the listing arm first), M4 and Y1's M4 part (unfixed afd021d
arms), Q-185's second Restart session, the slice's steps 3 to 5 (switching behaviours off one at a time, the other
tools), the store-relative cancel trigger's calibration on a real session, and whether a ScanArm-like rig returns.
ScanArm, the L/S/P arms, the settle gate, the drift judgement, the tolerance, the pressure step, the dir arm, the
store-size walk and -Attribute are not in this slice; ScanArm and its InternalsVisibleTo line are deleted (stack line:
deleted once O-003 is decided; spike #87 decided it).

SL-099 took the redesign up: `docs/design/SL-099-outcome-c-redesign.md` disposes of each item above, one row each (settled
there, carried to the measurement-preparation slice, or dropped), and fixes how session 1's logs are read.

## The excluded store's size

Not measured as a whole. Q-180 and Q-181 decided that leaving the whole store out, shown as a 'size not counted' row,
is an honest shortfall under the counting rule. That covers `Layers\<id>\Files` as well as Hives, Bindings,
Specialization and dotnet.bcf: the store is not only placeholders of host files (the earlier precondition, O-002, is
false). Validate read `dotnet.bcf` alone at 557,268 B (quoted from its account, not re-read here), and Microsoft
describes the dynamic base image as holding real pristine copies of the mutable OS files, about 500 MB (recalled, not
measured), so real bytes go uncounted. No walk measures it: on a boot whose layer is untouched, walking the store leaks
about 210,000 entries, and Q-181 already decided the trade.

## When the precautions change

- **When the refusal can be lifted for everyone** (Q-179): a valid pool run showing that enumeration with
  FILE_FLAG_OPEN_REPARSE_POINT causes no wcifs growth on an untouched layer subtree. Until then a person lifts it, for
  one session, only by turning the precaution off (Q-186).
- **When the exclusion can narrow** to `Layers\<id>\Files` (Q-180, Q-181): a post-Restart run showing that listing
  Hives, Bindings, Specialization and dotnet.bcf adds no WC* and no System handles. Each is a one-predicate change in
  `ContainerLayerStore`.
