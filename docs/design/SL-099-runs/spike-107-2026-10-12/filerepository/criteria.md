# Experiment sandbox-filerepository-warm-probe-repeat-ntff-wc (spike 107) -- the criterion as handed over, written before (b)

Assumption under test:
Test machine: a fresh Windows Sandbox (build 26100, run as SYSTEM). The set (B) is the Sandbox's own C:\Windows\System32\DriverStore\FileRepository, cold because the Sandbox has just booted. Its immediate subfolders are sorted ordinally, shuffled with System.Random(20261012) Fisher-Yates, and split into two disjoint halves, H1 and H2.
On H1, warm probe repeats P2, P3 and P4 of the app's own Scanner.Scan with ScanOptions.Default net NtfF within the bound b from /before to exit + 60 s on at least 2 of the 3. The bound is b = max(the largest idle |dNtfF| over a same-length I0/I1 sub-window, 10% of P1's dNtfF). NtfF from P1 exit + 60 s to P4 exit + 60 s is within b, and P2 from /before to exit + 600 s is within b.
Any WC leak-tag rise on H1 is either absent or has the lesson #349 shape: a rise on each of P2, P3 and P4 while the created sets show WC at idle movement. Meanwhile the listing-only control A2 (a warm repeat of H2 after its cold A1) stays within its bound on NtfF.

Method: same host prep, console, mark format, ordinal case-sensitive tag comparison and live-count formula as sandbox-above-cap-warm-probe-repeat-ntff (Core copy SHA-256 identical to SL-099 src/FsAnalyser.Core, or stop).
(a) log OS build, RAM, driver versions and `fltmc instances`, recording whether wcifs or bindflt is attached to C:.
(b) enumerate FileRepository's immediate subfolder names once; sort ordinally; shuffle with seed 20261012; split into H1 (first half) and H2 (second half); write both lists to the output before any arm runs. The console scans each listed folder as its own root, in list order, within one process per arm.
(c) the quiet pre-wait from the earlier Sandbox spikes. If it is not reached, the set is void.
(d) I0: 600 s with a mark every 60 s.
(e) arms, each its own process: P1, P2, P3, P4 = probe over H1. A1 = list over H2 (ProbeRule off), cold. A2 = list over H2 again, warm. A mark before each arm and at exit + 60 s. P2 also gets a mark at exit + 600 s. NtfF sampled every 2 s during each arm. Every root each arm opens is logged. Any H2 root opened by P1-P4 voids the control.
(f) I1: 600 s, a mark every 60 s.
(g) after I1 only: a census of H1 and H2 files by reparse tag (WCI 0x80000018 / 0x90001018 among them) and by sparse attribute.

Live count = (pa - pf) + (na - nf), each difference taken mod 2^32. Tags in an ordinal, case-sensitive dictionary (NtfF and Ntff differ only by case).

VALIDITY (any failure voids the set):
- P1 dNtfF >= max(50, 3 x the idle |dNtfF| over a same-length window);
- P1 to P4 Completed with sizeProbeCalls >= 90% of H1's file count (taken from P1);
- A1 and A2 Completed with sizeProbeCalls = 0;
- the pre-wait was reached;
- the Core copy is identical.
If A1 raises NtfF by less than max(50, 3 x idle), the CONTROL is void. Then this set can falsify YES but cannot select the NO branch.
Idle movement for a window of length w: the largest |d| of that tag between any two I0 marks, or any two I1 marks, w apart. w is rounded up to the next 60 s and capped at 600 s.

WC: per arm, record the live delta of WCsc, WCse, WCss, WCfc, WCri, WCfn and FMwi against that tag's idle movement. Record also whether A1/A2 move them (listing versus SizeProbe). Any WC result here describes the Sandbox's own C: layering, not this PC's ICU files, and is reported with the census and the fltmc line.

This proves the assumption FALSE if you observe, on a valid set:
- NtfF |d| from /before to exit + 60 s exceeds b on at least 2 of P2, P3 and P4;
- |NtfF(P4 exit + 60) - NtfF(P1 exit + 60)| exceeds b;
- P2's NtfF |d| from /before to exit + 600 s exceeds b;
- a WC leak tag rises above its idle movement on some, but not all, of P2-P4;
- a WC leak tag rises above its idle movement on any warm repeat while the created sets (A-large, A-small) also show WC above idle.
Which way it fails decides the next step:
- If it fails on NtfF while A2 is within b on NtfF (with a valid control), that is the fork's NO: a listing-only repeat arm type is needed in Invoke-Protocol before session 1.
- If A2 is also over b, neither branch applies; report 'listing repeat also not about 0' and escalate.
- A WC rise on every one of P2-P4, with the created sets at idle, is NOT a falsifier. It is recorded as per-pass WC from system files: the rule detecting what it was built for.
A void set falsifies nothing.

## Operational choices made by the runner before the run (they do not move the criterion above)
- Fisher-Yates = Durstenfeld: for i = n-1 down to 1, j = rng.Next(i+1), swap(a[i], a[j]); rng = new System.Random(20261012). H1 = first n/2 (integer division), H2 = the remaining n - n/2.
- The listed folders are full paths under FileRepository; each is passed to its own Scanner.Scan(root, ..., ScanOptions.Default) call (probe arms) or to Scanner.Scan with ProbeRule off and a counting Probe wrapper (list arms), in list order, in ONE process per arm.
- "H1's file count (taken from P1)" = Totals.Files summed over the roots in P1's result. 90% of that is the sizeProbeCalls floor for P1-P4.
- "rises above its idle movement" = signed live delta d(before -> exit+60) > the tag's largest idle |d| at the same w. Bytes are reported next to it.
- Planned lengths (a short smoke run in an earlier, discarded Sandbox showed the arms take 1-2 s each, FileRepository has 690 subfolders, 646 files in H1 and 638 in H2): pre-wait cap 300 s; I0 = 600 s (it alone gives an exact 600-s idle pair for the two long windows); arms with 60 s after exit and 600 s after P2; I1 = whatever budget is left (probably none). The census (g) then runs after the last arm or I1.
- The Sandbox tool ends a script at 1800 s, so I0 + P2's 600 s + I1 + 6 arms cannot all run at full length. The script shortens I0 and I1 (never P2's exit + 600 s mark, never an exit + 60 s mark) and logs the lengths it used. Where no idle pair exactly w apart exists the analysis uses the longest idle pair available and says so.
