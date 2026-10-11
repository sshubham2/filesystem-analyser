# Experiment sandbox-below-cap-warm-probe-repeat-ntff (spike 107) -- the criterion as handed over, written before the run

Assumption under test:
Test machine: a fresh Windows Sandbox (build 26100, run as SYSTEM). The set (A-small) is a created NTFS VHDX of 400 folders x 40 files, 16,000 files with unique 46-character names, made cold by flush, detach and re-attach. On it, warm probe repeats P2, P3 and P4 of the app's own Scanner.Scan with ScanOptions.Default, each in its own process, net NtfF within the bound b from /before to exit + 60 s on at least 2 of the 3. The bound is b = max(the largest idle |dNtfF| over a same-length I0/I1 sub-window, 10% of P1's dNtfF). NtfF from P1 exit + 60 s to P4 exit + 60 s is within b, and P2 from /before to exit + 600 s is within b. Every WC leak tag (WCsc, WCse, WCss, WCfc, WCri, WCfn, FMwi) stays within its idle movement on every warm repeat.

Method: Use the same host prep, console, mark format, ordinal case-sensitive tags, live-count formula and pre-registered criteria file as sandbox-above-cap-warm-probe-repeat-ntff (Core copy SHA-256 identical to SL-099 src/FsAnalyser.Core, or stop).

SANDBOX SCRIPT (its own fresh launch, not shared with A-large):
(a) Log build, RAM and driver versions, and `fltmc instances`.
(b) Create a 2 GB expandable VHDX with diskpart, partition it GPT, format it NTFS and mount it as V:. Run `create V:\set 400 40 46`. Flush, detach and re-attach. Nothing touches V: before P1.
(c) Apply the quiet pre-wait from the earlier Sandbox spikes. If it is not reached, the set is void.
(d) I0: 600 s with a mark every 60 s.
(e) Arms, each its own process: P1, P2, P3 and P4 probe, then L list (ProbeRule off).
- Take a mark before each arm and at exit + 60 s. P2 also gets a mark at exit + 600 s.
- Sample NtfF every 2 s during each arm.
(f) I1: 600 s with a mark every 60 s.

VALIDITY (any failure voids this set):
- P1 dNtfF >= max(50, 3 x idle |dNtfF| over a same-length window). Note that 100 x 40 failed this gate at +28 in spike 106, which is why the set is 400 x 40.
- P1 to P4 Completed with sizeProbeCalls >= 14,400.
- L Completed with sizeProbeCalls = 0.
- The pre-wait was reached.
- The Core copy is identical.
Idle movement is computed as in the A-large experiment.

(Idle movement in the A-large experiment: for a window of length w, the largest |d| of that tag between any two I0 marks, or any two I1 marks, w apart; w is rounded up to the next 60 s and capped at 600 s. Live count = (pa - pf) + (na - nf), each difference taken mod 2^32. Tags in an ordinal, case-sensitive dictionary.)

This set is the below-cap control. Read next to A-large, it tells whether an NtfF breach is tied to the cap: a breach in A-large and not here points at above-cap churn.

This proves the assumption FALSE if you observe:
  The assumption is FALSE on a valid set if any of the following holds:
- NtfF |d| from /before to exit + 60 s exceeds b on at least 2 of P2, P3 and P4;
- |NtfF(P4 exit + 60) - NtfF(P1 exit + 60)| exceeds b;
- P2's NtfF |d| from /before to exit + 600 s exceeds b;
- any WC leak tag moves more than its idle movement on any warm repeat. A created VHDX has no wcifs, so such a move would mean WC per pass regardless of the file kind.
Which way it fails decides the next step:
- If L's NtfF |d| is within b at the same time, that is the fork's NO (a listing-only repeat arm type is needed before session 1).
- If L is also over b, neither branch applies; report 'listing repeat also not about 0' and escalate.
- A single P-repeat breach with no accumulation, but a P2 exit + 600 s reading over b, is reported as undecided, not YES.
A void set falsifies nothing.
