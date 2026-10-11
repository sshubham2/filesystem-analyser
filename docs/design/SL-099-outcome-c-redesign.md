# SL-099: SL-095 outcome C, the redesign, and how session 1's logs are read

Status: written with the slice. Nothing here measures anything on this PC's C: drive; the two sessions it prepares need a
person's Start > Restart and are filed at the end.

SL-095 went ahead on a fork it had not settled (O-028): assumption A, "excluding the container Layers store brings a cold
full C: scan back inside a tolerance". Spike #87 measured by hand on a true Restart boot (2026-10-04) and arm L, the listing
walk alone with the whole store excluded, was stopped by the 0.50 GB guard. That is outcome C by O-028's own rule (i). This
document is the redesign O-028 owes for C: what the growth is, what the old criterion measured, the rule by which the logs of
the person's session 1 are read, and where each item SL-095 handed on went. It changes no product code.

## (a) Outcome C stands, and nothing here claims the tens-of-GB report fixed

`docs/design/SL-095-container-layer-store.md` lines 11-15 already say so, and this document does not reopen it. The Layers
exclusion (ContainerLayerStore.Resolve at Scanner.cs:66, the backstop in DirectoryReader.cs:84-96) stays: it removed the wcifs
signature (no WC* rise in arm L), and the `wcifs-layer-store` entry in KnownWorkarounds stays the one checkbox for that outside
defect (Q-186). Q-188, what produces the reported ~21 GB paged and ~10 GB nonpaged, stays open. Q-183 stands: the Q-004 rule
(each first-seen file is sized from its own handle) is not touched; nothing in the data routes to outcome B.

## (b) What the growth in arm L is

The investigation (this slice, round 1) found that arm L's growth is Windows' file-system metadata cache, not a leak the
program makes. The first listing of a cold folder keeps about one set of state for it: the NTFS FCB (NtfF, NtxI, Ntfo), the
FileInfo and FltMgr stream contexts (FIcs, FMsl), the FltMgr name-cache entries (FMfn) and the directory index's stream file
object and section (File, MmSt, MmCa). Listing the same folders again adds about 0.

- Spike #87, from its report (its marks.jsonl and events.log are gone, see (c)): +393,064,448 B paged and +145,154,048 B
  nonpaged in 92 s over 669,824 directory opens, about 800 B each, with NtfF +81,867 allocations and 0 frees, FMfn and File
  the other large movers, and no WC* rise.
- The investigation (3,000 WinSxS folders on this PC, no admin; big folders in the Sandbox): a second listing of the same folders
  kept NtfF +2, FMsl +3, FIcs +3, MmCa +2 on this PC and exactly 0 in the Sandbox; in the Sandbox a standby purge and, with no
  purge, memory pressure deeper than the available memory both gave the state back (NtfF -2,460 and -2,385 of about 2,450).
- Spike 106 measured the same questions with narrower results: the host's repeat listing breached its bound on File (+47) and
  MmSt (+35) in 2 of 3 valid runs with NtfF flat; deep pressure freed the listing state (a re-listing re-created NtfF 100%); a
  standby purge on a 400 x 40 VHD set re-created 0% of NtfF; and the 15 named tags covered 85.4% (77.1% in a second run) of paged
  growth, under the 90% asked, with NtFs at 5.1% unnamed. That last is why this slice records 14 more tags.
- Spike 107 (the Sandbox, the app's own Scanner with ScanOptions.Default, so SizeProbe on every file): a warm repeat of a full
  probe scan nets about 0, on three cold sets, see (f).

Microsoft Q&A 5966064 (cited, 2026-08-04; not re-fetched here) is the outside report of the same tags. It gives about 1 KB of
pool per cold path and nothing added by a warm one, and also that on a 32 GB PC the same tags reached **24.4 GB combined pool
(13,626 MB paged and 10,786 MB nonpaged) after 47.41 hours, cleared only by a reboot**. That report does not show the state being
given back at that size, and release under deep pressure has been shown only in a 4 GB Sandbox (SC-231). So the cache
explanation does not close Q-188: Q-188 may be this very mechanism, with the app's cold scans as one contributor among the
processes that touch cold paths. The "no scanner change" decision (this slice changes nothing under src/) rests on that being
Windows' designed caching and on the app listing each folder once, as any listing tool does. It is expensive to reverse and a
scanner-tier slice, opened by Q-188 and SC-231, is where it would be revisited.

## (c) What the old criterion measured

T = 64 MiB per pool and the 0.50 GB guard counted reclaimable cache. "The guard stopped arm L" shows that a cold listing of
about 670,000 folders keeps cache; it does not show a leak, and it is not evidence that the app does anything a listing tool
does not. Spike #87's pool deltas and tag growth cannot be checked now: of its run folder only `L.json`, `dirs-count.txt` and
`skips.log` survive (docs/design/SL-095-runs/spike-87-2026-10-04), and the figures above are as its report recorded them.

The harness fault in the same report, a driver abort at 2 GiB measured from R_start (taken 1.3 min after boot, with about
1 GiB of boot settle in it) that fired before L.json was loaded, lived only in the scratch ScanArm/Protocol.cs. That rig was
never in the tree (`git ls-files` finds none of ScanArm, Protocol.cs, RigHooks, judge-input or result.txt, and
`git log --diff-filter=D` finds no deletion), so there is nothing in the tree to fix. The tree's PoolWatch.ps1 guard is per arm:
combined pool growth over the arm's own /before mark above 6 GiB, or Available under 3 GiB, kills the exe, and the arm still
writes /exit and, 10 minutes later, /settled. The one real fault of that kind, SC-130, is fixed here: an arm the guard stopped
and that was then interrupted is reported with the guard's reason and its net at the stop.

## (d) How session 1's logs are read

Session 1 is `PoolWatch.ps1 -Prepare`, a true Start > Restart, then `-Protocol` within 30 minutes. The record is read by a
person; PoolWatch judges nothing and prints no verdict word (fitness rule 'PoolWatch reports completeness, never a verdict').

**The series.** F1 to F5 are five full exe scans of C: with the same root, type and hook (`Invoke-Protocol`, `foreach ($name in
$FullArms)`, 0feac0e :609). The **warm set** is the F arms after the first F arm whose status is Completed, in order, counting
only those with a /settled mark; fewer than two decide nothing. If the 6 GiB guard stopped F1 (the predicted cold cost, 3 to
4.4 GiB, is close to it), F1 warmed only part of C: and F2, which pays the cold cost of the rest, is partly cold: **F2 is
left out of the repeat set and only F3 to F5 are read as repeats.** An arm without a /settled mark is skipped and does not count
towards the two.

**The reading.** Per tag, the live count of a mark is (allocs - frees) paged plus nonpaged, tags keyed case-exactly. d is the
live-count change from an arm's /before to its /settled. Accumulation is the change from the first warm arm's /settled to the
last warm arm's /settled.

- **NtfF** (per folder) and **Ntff** (per file; spike 107's constraint: NtfF rose 401 for 400 folders and had 0 allocations
  across 400,000 SizeProbe opens): bound = max(the tag's idle term, 10% of the cold rise). The idle term is the largest |movement|
  over a window of the same length inside I0 and I1 (each 1,200 s). For a span over 1,200 s (the accumulation span, two or three
  arms of scan plus 600 s settle each, about 1,500 to 2,600 s; or a cold F1 to /settled, up to about 1,900 s) there is no window of
  that length, and the idle term is the largest movement over the whole 1,200 s window **scaled by span / 1,200**. The cold rise
  is F1's rise from /before to /settled, **summed through the first Completed F arm** when F1 was guard-stopped.
- **The WC leak tags WCsc, WCse, WCss, WCfc, WCri, WCfn and FMwi**: bound = that tag's own idle term (same scaling) and nothing
  else, no cold term. A bound that grows with the cold pass would hide a per-pass leak whenever F1's WC rise is large.
  WCce, WCrb and WCro are recorded but not judged: on the scratch harness of spike #87 they allocated about 1.6 times a second at
  idle with frees matching.
- **A leak reading** is a tag over its bound on two or more warm arms, or an accumulation over its bound. Q-178's three
  conditions map onto it: (1) the WC leak tags above stay within idle across the series, not only the three that -Report prints;
  (2) the `dir /s` baseline is session 2 (Q-185); (3) the warm set adds nothing.
- **Reported, not deciding:** FMfn, File, MmSt, MmCa, FMsl, FIcs, NtxI, Ntfo, NtxF, NtFs, FSim, MmSm, Ntf0, MmRe, Ntce, Toke, FMfc.
  F1's absolute cost is reported beside Q-188 and not judged against any tolerance.
- **The WC reading and its idle figure.** A +5-per-walk leak (lesson #349: +5 WCsc/WCfc/WCri/FMwi on each of four repeat walks)
  can be seen only where the host's idle movement on that tag is under 5. Spike 107's A-large idle movements on the WC tags were
  8, 9, 9, 9, 9, 2 and 6 over 120 s (so +5 would have hidden in them); the only host figure is the scratch harness's 0 over two minutes on a different boot. **The
  person's reading reports the idle figure next to each WC reading.** An F1 rise on a WC leak tag on the host is not explained
  away (Q-178 (1)).

**Where the numbers are.** -Report's columns are WCsc, WCfc, WCri, FMwi, FMfc, FMfn, File, NtfF and Toke. **WCse, WCss, WCfn,
Ntff and every tag added by this slice are read from marks.jsonl**, until the -Report reader (carried, below) lands. Every
reader must match tag names case-exactly: NtfF and Ntff, NtxI and NtxF are different tags (spike 107 read NtfF 917 against Ntff
15,895 in one mark). That holds for the committed `docs/design/SL-095-runs/build-2026-10-05` marks as well: they were written
with a tag list that included SeAt and NtFC, and they already hold Ntff, listed ahead of NtfF in every line.

**Whether 0feac0e folded case (answered by grep).** It did not: Read-Mark filtered with `-ccontains` (0feac0e :207) and Tag()
compared with `-ceq` (:242). The one folding table was Get-Net's `[ordered]@{}`, keyed by tag name; it is an Ordinal
dictionary now, and `$NetTags` may hold NtfF and Ntff together.

## (e) Preconditions of the reading

| # | Precondition | Status | Why |
|---|---|---|---|
| 1 | The five repeats are the same workload | holds | `Invoke-Protocol` passes the same root, the exe-full type and a null hook to every F arm (0feac0e :609) |
| 2 | A warm full-probe repeat nets about 0 on NtfF, Ntff and the WC leak tags, with no accumulation | holds at Sandbox scale | spike 107, three valid sets, see (f). SC-230 and O-016 are discharged with it |
| 3 | The counter sees the objects each pass creates | fails for NtfF on files | NtfF counts folders (+401 for 400 folders and the root, 0 allocations across 400,000 probes); Ntff carries the per-file state and is added to the rule |
| 4 | The bound does not grow with the cold pass for a tag whose leak is a small per-pass rise | must-verify | holds by construction for the WC leak tags (idle term only); whether +5 per walk is visible depends on the host's idle movement on them, see (d) |
| 5 | F1 is cold | must-verify | -Protocol refuses a boot older than 30 minutes; services may warm parts of C: first, which only makes F1 look smaller |
| 6 | The cache F1 builds survives to F2..F5 | must-verify | the host has about 17 GB of standby memory and the 6 GiB per-arm guard with the 3 GB floor keep the run away from deep pressure. If it were evicted, the warm repeats would rebuild it and read as a leak: Q-178 (3) would fail wrongly and the reading would point at the Q-183 scanner path |
| 7 | A forced-reclaim step reliably frees the cache | fails | spike 106: a standby purge re-created 0% of NtfF on a VHD set; deep pressure released the state only in the Sandbox (SC-231). The design has no purge and no pressure step |

**SC-230 was a precondition of session 1** (a warm SizeProbe repeat netting about 0 on NtfF decides whether the deciding column
can decide at all; a NO would have needed a listing-only repeat arm in Invoke-Protocol before the person's one Restart). Spike 107
answered YES on all three valid sets, so no such arm is built and the measurement-preparation slice's listing-arm clause is
dropped. Had it been NO, that slice would have carried the arm.

## (f) What spike 107 measured, and its limits

The app's own `Scanner.Scan` with `ScanOptions.Default`, one process per arm, in a fresh Windows Sandbox (Windows 11
Enterprise 26100, SYSTEM, 4,095 MB RAM, 16 CPUs, ntfs.sys and fltmgr.sys 10.0.26100.1), Core copied byte-identical to
`src/FsAnalyser.Core` at 0feac0e. The runs are committed under `docs/design/SL-099-runs/spike-107-2026-10-12/` (a-large,
a-small, filerepository; see its README.md). Warm P2, P3 and P4 each in their own process, marks before and at exit + 60 s.

| Set | Cold P1 dNtfF | Warm P2/P3/P4 dNtfF | Bound b | Accumulation P1+60 to P4+60 | P2 at exit + 600 s | WC leak tags on warm repeats |
|---|---|---|---|---|---|---|
| A-large, 400 folders x 1,000 files | +401 | +0 / +0 / +0 | 40.1 | +4 | +3 | within idle |
| A-small, 400 folders x 40 files | +224 | +0 / +0 / +0 | 22.4 | +5 | +1 | within idle |
| FileRepository half H1 (646 files, system files) | +333 | +0 / +0 / +0 | 33.3 | +5 | +1 | +0 on all seven; cold arms rise (P1 WCsc +961) |

On A-large, Ntff (the per-file tag) rose +13,180 on the cold run and moved +48 / -72 / +44 on the warm repeats against
b = 1,318 (reference only, the criterion was written on NtfF).

Limits, none of which the host session can wave away: a 4 GB Sandbox, at most 400,000 files, ntfs.sys 10.0.26100.1 against the
host's 10.0.26100.9444; I1 was skipped or trimmed on all three because the tool ends a script at 1,800 s, so the 600 s idle
pairs the rule wants were not measured (SC-241; A-large had I0 300 s and I1 240 s, the other two I0 600 s and no I1); the sets
hold 0 reparse and 0 sparse files, where this PC's ICU files are both (lesson #349); the Sandbox's C: is wcifs-backed, so the
FileRepository cold arms did move WC (that is the rule working, not a failure of it). The 2.3 million files of a C: scan
exceed any of these sets, and where NTFS's count-capped close cache levels off on a 32 GB PC is SC-229.

## (g) What SL-095 handed on, and where each item went

The ids are SL-095's findings, as `docs/design/SL-095-container-layer-store.md` lines 219-229 lists them; they share letters
with this slice's own review ids and are not those. One row per item, 17 in all.

| id | disposition | reason |
|---|---|---|
| R3-1 | carried | the settle gate: when a /settled mark may be trusted. There is no gate in PoolWatch; the measurement-preparation slice builds it |
| R4-1 | carried | the settle gate's per-pool limits, with R3-1 |
| M6 | settled here | drift per gap: the idle term of (d), the largest same-length movement in I0 and I1, scaled for longer spans |
| N2 | settled here | the idle envelope: the same term, kept per tag |
| X5 | carried | WCIFS-UNATTRIBUTED: telling wcifs growth from other WC movement needs the reader and the closing layer control (Y3) |
| N1 | carried | the envelope check between arms |
| M3 | settled here | cache against retention under memory pressure: no pressure step, because a standby purge did not make a VHD set cold (spike 106) and pressure release is shown only at Sandbox scale; the warm repeats are the discriminator |
| N3 | settled here | the same as M3 |
| X2 | settled here | the pressure step's positive control: there is no pressure step, so no control |
| Y3 | carried | the closing layer control. Wanted, because (d) makes a WC-within-idle claim (SC-129's condition); built by the measurement-preparation slice with its case fed through PoolWatch |
| M2 | dropped | the listing arm first: spike 107 answered O-017 YES, a warm full-probe repeat nets about 0, so no listing-only arm |
| M4 | dropped | the unfixed afd021d arms: the scratch rig that ran them is gone and the record does not need them |
| Y1 | dropped | Y1's M4 part, with M4 |
| Q-185 | carried | the second Restart session: `dir /a /s` per sibling root of C:, never into the Layers store, after a true Restart and the same idle (a person's; the measurement-preparation slice builds the dir arm) |
| steps 3-5 | carried | switching behaviours off one at a time, and the other tools, of the slice's original plan |
| cancel trigger | carried | calibrating the store-relative cancel trigger on a real session |
| ScanArm | settled here | no ScanArm-like rig returns: it was scratch and never committed; the harness has a per-arm guard, no judge and no tolerance row |

## (h) What is built, and what is a person's

Built in this slice: Get-Missing judges a guard-stopped arm before the interrupted branch (SC-130, `tests/bugs/SL-099-*`);
Get-Net's table is Ordinal; PoolWatch records 14 more tags (MmSt, MmCa, FMsl, FIcs, NtxI, Ntfo, Ntff, NtxF, NtFs, FSim, MmSm,
Ntf0, MmRe, Ntce), all seven WC leak tags having been recorded already; two fitness entries keep its tag comparisons
case-exact (corpus: `tests/window/PoolWatchTagCompareCorpus.json`); PoolWatchCases gets guard-and-interrupt fixtures in P-003, P-004
and a scratch root that survives the engine's TEMP. No change under src/, and no new KnownWorkarounds entry: the cache is not an
outside defect the app steps around.

A person's, filed: **session 1** (-Prepare, Restart, -Protocol; logs committed under docs/design/SL-095-runs/ per Q-184, which
also takes the /settled reading for any arm the guard stopped); **session 2** for DONE (c) under Q-178, Q-184 and Q-185 if
session 1 reads as a leak or as a cost the `dir` baseline must be set against. DONE (c) stays pending until those logs exist.
Carried to the measurement-preparation slice, which has to land before session 1: a -Report reader for Ntff, WCse, WCss, WCfn
and the new tags; Y3; the session-2 dir arm; a guard-trip rehearsal through Invoke-ExeArm, which has never tripped for real;
R3-1, R4-1, X5, N1; the cancel trigger's calibration; Sandbox protocols that fit the tool's 1,800 s ceiling.
