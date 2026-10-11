# Experiment sandbox-above-cap-warm-probe-repeat-ntff (spike 107) -- the criterion as handed over, written before the run

Assumption under test:
Test machine: a fresh Windows Sandbox (build 26100, run as SYSTEM). The set (A-large) is a created NTFS VHDX of 400 folders x 1,000 files, 400,000 files in all, with unique 46-character names. It is made cold by flush, detach and re-attach. On it, P1 is a cold run of the app's own Scanner.Scan with ScanOptions.Default (SizeProbe on every file the default ProbeRule selects). Its NtfF live count, sampled every 2 s, flattens, which shows the NTFS close-cache cap was reached. After that, the warm probe repeats P2, P3 and P4, each in its own process, net NtfF within the bound b from /before to exit + 60 s on at least 2 of the 3. The bound is b = max(the largest idle |dNtfF| over an I0/I1 sub-window of the same length, 10% of P1's dNtfF from /before to exit + 60 s). NtfF from P1 exit + 60 s to P4 exit + 60 s is also within b. P2 from /before to exit + 600 s is within b. Each WC leak tag (WCsc, WCse, WCss, WCfc, WCri, WCfn, FMwi) moves no more than its own idle movement on every warm repeat.

Method (summary of the Sandbox script): (a) log OS build, RAM, ntfs.sys and fltmgr.sys versions, `fltmc instances`. (b) diskpart: 8 GB expandable VHDX, GPT, NTFS quick format, V:. `create V:\set 400 1000 46`. Flush, detach, re-attach as V:. Nothing lists V: between the re-attach and P1. (c) quiet pre-wait the earlier Sandbox spikes used; if the settle is not reached the set is void. (d) I0: 600 s, a mark every 60 s. (e) Arms, each a separate process, in this order: P1 probe, P2 probe, P3 probe, P4 probe, then L list (ProbeRule off). A mark immediately before each arm and at exit + 60 s. P2 also gets a mark at exit + 600 s, and P3 starts only after that mark. During every arm, NtfF live and alloc counts are sampled every 2 s. (f) I1: 600 s, a mark every 60 s. (g) After I1 only: count the set's files by reparse tag and the sparse attribute.
Live count = (pa - pf) + (na - nf), each difference taken mod 2^32. Tags in an ordinal, case-sensitive dictionary (NtfF and Ntff differ only by case).

VALIDITY (any failure voids this set):
- V1: the Core copy is byte-identical.
- V2: P1 dNtfF (/before to exit + 60) >= max(50, 3 x the idle |dNtfF| over a same-length window).
- V3: P1 to P4 are each Completed with sizeProbeCalls >= 360,000.
- V4: L is Completed with sizeProbeCalls = 0.
- V5: the pre-wait was reached.
Idle movement for a window of length w: the largest |d| of that tag between any two I0 marks, or any two I1 marks, w apart. w is rounded up to the next 60 s and capped at 600 s.

PLATEAU: from P1's 2-s samples, the cap counts as reached if NtfF's live rise over the last 25% of P1's elapsed time is <= 10% of its rise from P1/before to P1's highest sample. Report NtfF allocs during P1 next to the live rise, to show churn. If the plateau is not reached, report it as 'cap not reached'. The set is still valid for a NO, but its YES does not cover above-cap behaviour.

This proves the assumption FALSE if you observe, on a valid set (V1-V5 hold), any of:
- NtfF |d| from /before to exit + 60 s exceeds b on at least 2 of P2, P3 and P4;
- |NtfF(P4 exit + 60) - NtfF(P1 exit + 60)| exceeds b (accumulation);
- P2's NtfF |d| from /before to exit + 600 s exceeds b;
- any WC leak tag moves more than its idle movement on any warm repeat.
Which way it fails decides the next step:
- If L's NtfF |d| (/before to exit + 60) is within b at the same time, that is the fork's NO. The record then reads NtfF only from listing-only repeats, and Invoke-Protocol needs a listing-only repeat arm type before session 1.
- If L is also over b, neither branch of the fork applies. Report it as 'listing repeat also not about 0 on NtfF'. That contradicts spike 106's flat NtfF and is escalated.
- If exactly 1 of P2-P4 is over b, there is no accumulation, but P2's exit + 600 s reading is over b, the set is undecided and is reported as such, not as YES.
A void set falsifies nothing.
