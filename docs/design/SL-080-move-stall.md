# SL-080: the move/resize stall while a scan starts

SL-044 left two things open: whether its 860 px default window height made the stall at the first move after a
scan starts worse, and what blocks the UI thread while it lasts beyond "WPF and the display answering a resize".
This slice measured both on real D:\, with the tree T-005 builds, and changed nothing in `src/`.

## What was measured instead of the brief's A/B

The brief asked for de51999 against HEAD in one batch. Risk-spike settled that it cannot be done: de51999 has no
FolderTree, so T-005's hook refuses it ("the window has no FolderTree"), and 33 files (+3,185/-438 lines) separate
the two builds beyond the window's height, so no difference between them could be put on the height. The question
is answered on HEAD alone, with the height as the only thing changed between the two arms of each pair.

"Real D:\" is read as T-005's own tree (100,000 files, 20,000 folders) built on D:\ by pointing TEMP and TMP at
`D:\fsa-sl080-temp`, the set-up spike stall-rides-competing-cpu-load showed stalling in 18 of 18 runs. A scan of
what is really on D:\ was not run; whether the brief meant that is still an open question for a person.

The low arm is 644 px, not 620. 620 was the window's default before SL-044 (de51999: `Height="620"
MinHeight="620"`; a68ae95 raised `Height` to 860). SL-047 (8c47eb7) raised `MinHeight` to 644, so at HEAD a
620 px window cannot be had: asked for 620, it is laid out at 644. The hook now refuses such a height ("height not
applied (620/644)") and the batch stops on it, measured below. 644 is the smallest height the window takes.

`$DefaultHeight = 860` in WindowCases.ps1 (review m5) feeds no launch: it is the constant T-001's default-size
checks compare the window against. T-005 never set a height; it ran at the XAML default. The height knob already
existed: FSA_HOOK_HEIGHT, used by T-003 at 4000 ("as tall as the work area"). It is reused, not duplicated. It is now
set in a Loaded class handler, which runs ahead of the window's own Loaded handler and so before that handler starts
the scan; the "sized" line says whether the scan had started ("scanning": false in all 35 runs of the second batches,
sized 333 to 696 ms into the hook's clock), and a window sized too late is refused. Until the code review's M1 it was set
after the window had loaded, that is after the scan had started (below). New in it too is the refusal of a height
within the work area that the window does not take; asking past the work area still clamps, as T-003 needs.

## The tools

- `tests/window/T005Batch.ps1`: runs WindowCases.ps1 -Case T-005, unmodified, once per scheduled run, in ABBA
  pairs after a discarded warm-up, and turns the runs into a verdict under rules written into its header before
  any run. It stops the batch (exit 1) rather than leave a hole in a pair: no summary line, an ending other than
  the hook closing the window, another height, render mode or tracing than the arm's, or a phase never reached. The
  same holds when a batch is read again (-Reread): every scheduled run must have exactly one line that stands in its
  pair. A live batch refuses an -Out that already holds a runs.jsonl; -Out is made absolute; each run's process gets
  none of the calling shell's FSA_T005_*, FSA_HOOK_*, DOTNET_EventPipe* or DOTNET_STARTUP_HOOKS variables, only its
  arm's. `-SelfTest` runs 29 canned batches through the same code (tests/window/fixtures/T005Batch).
- `tests/window/StallStacks.ps1`: turns one traced run into what the UI thread was in or blocked under for the
  longest wait of the scan and of the rescan, with the rules in its header. `-SelfTest` runs 13 canned timelines
  (tests/window/fixtures/StallStacks), two of them sampled every 10 ms.
- WindowCases.ps1's T-005 writes one summary line per run when FSA_T005_SUMMARY is set (ConvertTo-T005Summary),
  before its own checks. FSA_T005_TRACE traces the app from its start with EventPipe; the variables go to the app's
  process alone through Start-Process -Environment, never into the script's `$env:`, into a file named with the
  app's pid. Both folders are made absolute first: the app runs in %TEMP%, where a relative one is another folder.
- The hook logs a "facts" line in GAP mode: the UI thread's OS id, the priority class, the runtime, the render mode
  and tier, and when its clock started. Each phase's longest wait now has its end ("maxEndAt") beside its start.
  FSA_HOOK_RENDER=software sets `RenderOptions.ProcessRenderMode` to SoftwareOnly before the app's Main.
- dotnet-trace 10.0.745401, installed by eng/dotnet.ps1 as a tool into the gitignored artifacts/tools/dotnet-trace,
  converts a trace to Speedscope. Traces, runs.jsonl and verdict.json stay in the gitignored artifacts/sl080.

Every run was at BelowNormal priority: this machine's sandbox starts every child process there (spike
stall-rides-competing-cpu-load saw the same). Render tier 2 in every run. Runtime .NET 10.0.12.

## The first batches set the height after the scan had started

height-1, trace-1 and render-1 (artifacts/sl080) ran with the hook sizing the window once it had loaded, and the
window's own Loaded handler had started the scan by then. So every run moved the window to the top of the work area
between the scan's start and the first probe, and in the 644 px arm resized it by 216 px too. That move took the
scan's first-move stall on itself: the scan phase's longest wait was 76 to 251 ms in the first batches' hardware runs
(apart from the 1.3 s kind below and one 497.8 ms), and 334 to 377 ms at 860 px once the height was set before the
scan. The two height arms also differed in more than their
height during the scan. Those three batches are superseded by height-2, trace-2 and render-2 below, run with the
height set before the scan; their verdicts were no-measurable-difference, render-path and gpu-present-side. The
second batches were started from a shell holding FSA_T005_TRACE, FSA_T005_SAMPLE_MS=5, FSA_T005_RENDER=software,
FSA_HOOK_HEIGHT=700 and two DOTNET_EventPipe* variables; no run was traced, sized or rendered other than its arm says.

## Does 860 px make it worse? Inconclusive on the rule; the scan phase alone, not decided on, says yes

`T005Batch.ps1 -Plan Height` (artifacts/sl080/height-2): 16 runs after the warm-up, all stalling.

| pair | runs | first | 860 px | 644 px | 860/644 |
|---|---|---|---|---|---|
| 1 | 1, 2 | 860 | 519.3 | 703.4 | 0.738 |
| 2 | 3, 4 | 644 | 415.7 | 402.0 | 1.034 |
| 3 | 5, 6 | 860 | 567.3 | 831.3 | 0.682 |
| 4 | 7, 8 | 644 | 381.5 | 388.7 | 0.982 |
| 5 | 9, 10 | 860 | 719.7 | 810.4 | 0.888 |
| 6 | 11, 12 | 644 | 399.7 | 642.6 | 0.622 |
| 7 | 13, 14 | 860 | 409.3 | 465.4 | 0.879 |
| 8 | 15, 16 | 644 | 673.8 | 643.9 | 1.046 |

The figure is each run's primaryMs, the larger of the scan's and the rescan's longest wait, in ms, fixed as the one
figure decided on before any run. 860 px was higher in 2 of 8 pairs, median ratio 0.884: **inconclusive**. The
spread of the pairs' log ratios is 0.197, and the smallest ratio this batch could have told from 1 is **1.25**. The
order check did not fire (the first run of a pair higher in 2 of 8). primaryMs was the rescan's in 15 of the 16 runs.

The scan phase, reported and not decided on, reads differently:

| pair | 860 px scan | 644 px scan | 860/644 |
|---|---|---|---|
| 1 | 346.1 | 151.0 | 2.292 |
| 2 | 346.5 | 223.4 | 1.551 |
| 3 | 354.1 | 225.1 | 1.573 |
| 4 | 335.1 | 243.4 | 1.377 |
| 5 | 336.3 | 223.1 | 1.508 |
| 6 | 356.8 | 106.7 | 3.345 |
| 7 | 360.9 | 465.4 | 0.775 |
| 8 | 337.0 | 106.6 | 3.161 |

860 px higher in 7 of 8 pairs, median ratio 1.562: on the scan phase alone the height rule's numbers would read
860-worsens. It is not a verdict. The rule was fixed on primaryMs before the runs so that a batch is not read to
taste afterwards, and picking the one phase that shows a difference after seeing it is exactly that. It is a finding
for the next batch to test with the scan phase fixed as its figure beforehand. At 860 px the scan's longest wait was
333.6 to 377.3 ms in every hardware run of height-2, trace-2 and render-2 but one (render-2 run 7, 663.0 ms on a later
move); at 644 px it was 106.6 to 243.4 ms in 7 of 8 runs, and 465.4 ms once.

Where the stalls come, on the hook's clock and in the traces:

- The scan's: its first move, about 130 ms after the app's own StartScan (trace-2: 127.9 to 130.7 ms). The probe was
  posted 7 to 16 ms after the move began and waited 334 to 377 ms at 860 px, while the move itself held the hook
  128 to 173 ms: the move's messages go on after the Ui call returns.
- The rescan's: its first move, 55 to 62 ms after the rescan's StartScan; 380 to 830 ms, in two groups, about 380 to
  420 ms and about 500 to 830 ms.
- A third kind, about 1.3 s on one later move: 6 of the 17 runs of the superseded height-1 (1,292 to 1,381 ms), and
  in the second batches only render-2 run 8, in software (1,264.1 ms, one move holding 1,195 ms). The brief's
  1,318.5 ms is this kind. It is not D:\'s alone: T-005 run plainly at the end of the first build, its tree in %TEMP%
  on C:\ and no SL-080 variable set, failed its own 1 s bar with a scan-phase wait of 1,352.6 ms.

## What the UI thread is blocked under: WPF's synchronous render flush on a window move or resize

`T005Batch.ps1 -Plan Trace` (artifacts/sl080/trace-2), all at 860 px: traced runs stalled 4 of 4 (median
543.9 ms), untraced 4 of 4 (median 527.4 ms), so tracing did not mask the stall. The EventPipe sampler resolved the
UI thread's frames: at most 3% of any matched stretch was unreadable, and every wait matched a busy stretch that was
busy for all of it (the hook's clock put on the trace's by the two UTC origins; offsets 41.0 to 46.7 ms). Three of
the four traced runs read **RENDER-PATH** and one SPLIT; the batch verdict is **render-path**. trace-1 read
render-path too, 4 of 4, and a single run traced at 10 ms (artifacts/sl080/sample10) read RENDER-PATH.

- The scan's first move (4 runs, 333.6 to 343.9 ms): 91.8 to 92.9% of the wait in `DUCE+Channel.SyncFlush`, reached
  from `HwndSource.HwndTargetFilterMessage` (62 to 63%) and `MediaContextNotificationWindow.MessageFilter` (29 to
  31%); 1.7 to 2.9% inside SetWindowPos.
- The rescan's first move: in runs 1 and 5 (395.4 and 383.8 ms) 94% SyncFlush, the same way. In runs 4 and 8 (692.4
  and 796.0 ms) 54% and 47% SyncFlush, and 43% and 51% with `Window.UpdateHwndSizeOnWidthHeightChange` as the deepest
  managed frame, that is inside the native SetWindowPos the hook's `Height` set calls, under
  `DependencyObject.SetValue`. Run 8 is under half in the flush, so only the pump is common to 90% of it: SPLIT. The
  longer group of rescan stalls is the one with the SetWindowPos share.

`Channel.SyncFlush` is the UI thread waiting for WPF's render thread to take its batch and answer; a move or resize
of the window makes `HwndTarget` flush synchronously, so any time the render side takes is spent on the UI thread.
EventPipe does not see the render thread or the desktop compositor, so the trace alone cannot say what the render
side was doing.

`T005Batch.ps1 -Plan Render` (artifacts/sl080/render-2) took the GPU away: 4 pairs, software and hardware
interleaved, at 860 px, untraced.

| pair | runs | first | software | hardware | software/hardware |
|---|---|---|---|---|---|
| 1 | 1, 2 | software | 404.0 | 715.4 | 0.565 |
| 2 | 3, 4 | hardware | 611.2 | 399.1 | 1.532 |
| 3 | 5, 6 | software | 177.3 | 514.0 | 0.345 |
| 4 | 7, 8 | hardware | 1,264.1 | 663.0 | 1.907 |

Software lower in 2 of 4 pairs, median ratio 1.048: **render-thread-or-dwm-not-gpu** by the rule stated before the
runs. The spread of the log ratios is 0.812 and the smallest ratio this batch could have told from 1 is **5.41**:
this batch rules nothing out, and whether the render side's wait is the GPU's is not settled. The superseded render-1
read gpu-present-side (median 0.305), with the hook's own resize in every scan. The phases, reported and not decided
on, pull apart: in software the scan's first move was 68.4 and 90.2 ms in two runs against 341.9 to 377.3 ms in
hardware (663.0 ms once, on a later move), but the other two software runs stalled 611.2 and 1,264.1 ms on a later scan move; the rescan was lower in
software in 4 of 4 pairs (47.0 to 404.0 ms against 399.1 to 715.4 ms, median ratio 0.455).

## Against SL-044's narrowing

SL-044 found it was not the FolderTree (de51999 stalled too, 419 to 511 ms with moves, 10 ms without) and not
StartScan (2.8 to 7.4 ms). Those figures are context here, not an A/B: different builds, another day. This slice
adds: not GC (spike rescan-stall-is-gc-suspension, under 1%); not CPU load from outside (spike
stall-rides-competing-cpu-load); on the UI thread, WPF's synchronous render flush on the window's move or resize
messages, with the SetWindowPos call itself in the longer rescan stalls. The height is inconclusive on the rule
(detectable 1.25x), with a scan-phase difference to test. Whether the flush waits on the GPU is not settled.

## Programmatic moves, and a person's drag

Every stack here comes from the hook setting Left, Top, Width and Height on the window from its own thread through the
dispatcher: SetWindowPos from managed code, with WPF's HwndTarget answering the WM_WINDOWPOSCHANGED that follows. A
person dragging the title bar or a border enters DefWindowProc's modal move/size loop instead (SDL #1059), which pumps
messages itself; WPF's HwndTarget still answers each position and size change the loop makes with the same
UpdateWindowSettings and SyncFlush. So the render-side wait, 92% of the scan's first-move stall, is expected to carry
over to a drag, once per size change. The part inside SetWindowPos under `DependencyObject.SetValue`, up to half of the
longer rescan stalls, is the hook's call, and no trace of a drag was taken.

## One follow-on fix candidate

Give the render thread less to flush in the first half-second of a scan, since every stall read here is the UI thread
waiting in SyncFlush on a window move 55 to 130 ms after StartScan. Before a fix can be priced it needs two batches
this slice has the tools for, with their figures fixed before the runs: a Height batch on the scan phase, which the
second batch suggests 860 px worsens (7 of 8 pairs, median 1.562, not decided on), and a Render batch of 8 pairs,
because 4 pairs could not tell software from hardware (detectable 5.41x). The main window in software
(`HwndTarget.RenderMode = RenderMode.SoftwareOnly`) stays the one change with a measured effect in any batch (render-1
median 0.305), and that batch is the superseded one.

## The in-process fallback, contract only (not built)

Built only if a Trace batch reads cannot-read, or tracing-masks again at -SampleMs 10. Neither happened.

- FSA_HOOK_MSGLOG=1 (tree mode, with FSA_HOOK_GAP): the hook adds an HwndSource hook on the main window and
  Dispatcher.Hooks OperationStarted/OperationCompleted handlers, on the UI thread, and records every window message
  (WM_* id, wParam) and every dispatcher operation (priority, the delegate's method name) with its start and
  duration on the hook's clock. After the ending it logs, for each phase's longest wait, one JSON line per message
  or operation that overlapped [maxAt, maxEndAt]: {"kind":"msg","phase","what":"WM_WINDOWPOSCHANGED"|"op:<method>",
  "priority","startMs","durMs"}.
- The CANNOT-READ reasons that send a batch to it: more than 20% of a stretch unresolved or with nothing below the
  pump, no busy stretch covers 80% of the wait, the UI thread without samples, dotnet-trace unable to install or run,
  or tracing-masks at a 10 ms interval.

## Measured at build, and what changed from the design because of it

- The refusal, live: `T005Batch.ps1 -Plan Height -LowHeight 620` (artifacts/sl080/live/height-620) ran the warm-up and
  run 1 at 860 px; run 2, the first at 620 px, ended with the hook's `height not applied (620/644)` and the batch
  stopped there, exit 1, before run 3.
- DOTNET_EventPipeThreadSamplingRate is read by runtime 10.0.12 as milliseconds in hexadecimal: "A" gave 10.1 ms
  between samples, "5" 5.1 ms, "10" (16 ms) 30.9 ms; "2710" (10 s) held the app's exit 7.5 s, and "989680" hung it.
  The option is FSA_T005_SAMPLE_MS (decimal ms, written in hex), not the design's FSA_T005_SAMPLE_NS. The nettrace
  header records the interval in ns: 1,000,000 at the default, and 10,000,000 in a T-005 run traced with
  FSA_T005_SAMPLE_MS=10, whose UI-thread samples came 10.29 ms apart (artifacts/sl080/sample10). The reader takes the
  header's and reports the spacing it sees beside it. The runtime is .NET 10.0.12 in every run.
- StallStacks' idle floor is 25%, not the design's 60%: the first traced run was idle 49.4%, every idle sample in
  Dispatcher.GetMessage, because the hook's moves hold the UI thread in SyncFlush for about half the run.
- A stretch is matched on time, and its duration is only checked as busy for at least 80% of the wait. The design's
  duration match (within 20%) would miss a stretch the probe was let through in the middle of: the scan's stretch in
  the first run was 201 ms around an 83 ms wait.
- A stretch that is half or more in the render flush reads RENDER-PATH before SPLIT is considered: the rescan
  stretches of the first batch named only the pump at 90% and were still 51 to 77% SyncFlush.
- AppDomain.GetCurrentThreadId gives the managed thread id on .NET (2 in the first run); the hook reads
  Thread.CurrentOSThreadId by reflection instead. InteropLivesOnlyInNative (tests) rules out a P/Invoke.
- ConvertFrom-Json turns the facts line's UTC time into a DateTime that loses its milliseconds when cast back to
  text (an offset of -225 ms instead of +204 ms): summary lines are read with -DateKind String.
- dotnet-trace's Speedscope names one module "PresentationCore.il" and "presentationcore.il" in different frames;
  the reader folds them into one method.
- trace-1's stacks were read after its runs with -Reread, the same rules on the same files: the driver's reader had
  a variable (`$Stacks`, the script's path) shadowed by the verdict's table of the same name. The reader now deletes
  a run's stacks-<n>.json before reading it again, and a reader that fails is CANNOT-READ in its own words.
- The second batches ran with relative -Out folders (artifacts\sl080\height-2, trace-2, render-2), and each traced
  run's trace landed in trace-2\traces: before -Out was made absolute, a relative one sent every trace to a folder
  under %TEMP% and every traced run read "no trace written" (code review R2-M2).
