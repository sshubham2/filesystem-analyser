# Spike 107: does a warm full-probe repeat net about 0? (2026-10-12)

The answer to fork O-017 (SC-230, O-016) for SL-099: the app's own `Scanner.Scan` with `ScanOptions.Default`, a cold first
run P1 and warm repeats P2 to P4 in their own processes, a listing-only control, in a fresh Windows Sandbox (Windows 11 26100,
SYSTEM). Read with `docs/design/SL-099-outcome-c-redesign.md` (f). These are the spike's own run folders, copied whole except
`output.txt` (a duplicate of `log.txt`) and files of 0 bytes; each holds `criteria.md` (what was asked), `analysis.txt` (what was
found), `arms.jsonl` and `marks.jsonl` (pool-tag marks before each arm, at exit + 60 s, and for P2 at exit + 600 s; tags keyed
case-exactly, NtfF and Ntff separate).

| folder | set | source run folder (outside the repository, under %APPDATA%) |
|---|---|---|
| `a-large` | 400 folders x 1,000 files on a created VHDX, above the NTFS close-cache cap | `taad3\spikes\8\spike-107\sandbox-above-cap-warm-probe-repeat-ntff\.taad-scratch\sandbox\run-20261012-002418\out` |
| `filerepository` | the Sandbox's `C:\Windows\System32\DriverStore\FileRepository`, half H1 probed, half H2 the listing control (`roots.jsonl`, `H1.txt`, `H2.txt`, `sorted.txt`) | `taad3\spikes\8\spike-107\sandbox-filerepository-warm-probe-repeat-ntff-wc\.taad-scratch\sandbox\run-20261012-011147\out` |
| `a-small` | 400 folders x 40 files on a created VHDX, below the cap | `taad3\spikes\8\spike-107\sandbox-below-cap-warm-probe-repeat-ntff\.taad-scratch\sandbox\run-20261012-015214\out` |

Limits: I1 was skipped or trimmed in all three (the Sandbox tool ends a script at 1,800 s); at most 400,000 files; 4 GB RAM;
0 reparse and 0 sparse files. The session logs of the person's Restart sessions do not go here: they go under
`docs/design/SL-095-runs/` (Q-184).
