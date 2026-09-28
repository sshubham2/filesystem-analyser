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
existed: FSA_HOOK_HEIGHT, applied by TreeMode.Size before the scan and used by T-003 at 4000 ("as tall as the work
area"). It is reused, not duplicated. What is new in it is the refusal of a height within the work area that the
window does not take; asking past the work area still clamps, as T-003 needs.

## The tools

- `tests/window/T005Batch.ps1`: runs WindowCases.ps1 -Case T-005, unmodified, once per scheduled run, in ABBA
  pairs after a discarded warm-up, and turns the runs into a verdict under rules written into its header before
  any run. It stops the batch (exit 1) rather than leave a hole in a pair: no summary line, an ending other than
  the hook closing the window, another height or render mode than the arm's, or a phase never reached. `-SelfTest`
  runs 22 canned batches through the same code (tests/window/fixtures/T005Batch).
- `tests/window/StallStacks.ps1`: turns one traced run into what the UI thread was in or blocked under for the
  longest wait of the scan and of the rescan, with the rules in its header. `-SelfTest` runs 11 canned timelines
  (tests/window/fixtures/StallStacks).
- WindowCases.ps1's T-005 writes one summary line per run when FSA_T005_SUMMARY is set (ConvertTo-T005Summary),
  before its own checks. FSA_T005_TRACE traces the app from its start with EventPipe; the variables go to the app's
  process alone through Start-Process -Environment, never into the script's `$env:`, into a file named with the
  app's pid.
- The hook logs a "facts" line in GAP mode: the UI thread's OS id, the priority class, the runtime, the render mode
  and tier, and when its clock started. Each phase's longest wait now has its end ("maxEndAt") beside its start.
  FSA_HOOK_RENDER=software sets `RenderOptions.ProcessRenderMode` to SoftwareOnly before the app's Main.
- dotnet-trace 10.0.745401, installed by eng/dotnet.ps1 as a tool into the gitignored artifacts/tools/dotnet-trace,
  converts a trace to Speedscope. Traces, runs.jsonl and verdict.json stay in the gitignored artifacts/sl080.

Every run was at BelowNormal priority: this machine's sandbox starts every child process there (spike
stall-rides-competing-cpu-load saw the same). Render tier 2 in every run. Runtime .NET 10.0.12.

## Does 860 px make it worse? No difference this batch could see; it could only have seen about 2x

`T005Batch.ps1 -Plan Height` (artifacts/sl080/height-1): 16 runs after the warm-up, all stalling (the warm-up too,
1,323.8 ms).

| pair | runs | first | 860 px | 644 px | 860/644 |
|---|---|---|---|---|---|
| 1 | 1, 2 | 860 | 652.3 | 522.3 | 1.249 |
| 2 | 3, 4 | 644 | 1,381.3 | 725.9 | 1.903 |
| 3 | 5, 6 | 860 | 694.5 | 1,321.8 | 0.525 |
| 4 | 7, 8 | 644 | 716.7 | 698.0 | 1.027 |
| 5 | 9, 10 | 860 | 506.9 | 1,349.3 | 0.376 |
| 6 | 11, 12 | 644 | 1,292.2 | 525.6 | 2.459 |
| 7 | 13, 14 | 860 | 698.2 | 704.7 | 0.991 |
| 8 | 15, 16 | 644 | 1,380.6 | 703.6 | 1.962 |

The figure is each run's primaryMs, the larger of the scan's and the rescan's longest wait, in ms. 860 px was higher
in 5 of 8 pairs, median ratio 1.138: **no-measurable-difference**. The spread of the pairs' log ratios is 0.656, and
the smallest ratio this batch could have told from 1 is **2.13**: an 860 px effect under about 2x is not ruled out.
This is not "860 px does not worsen it".

The order check fired: the first run of a pair was higher in only 1 of 8. The pairs' ratios are driven by a second
kind of stall, about 1.3 s, that came in 6 of the 17 runs whatever the height: the warm-up and runs 4, 6, 10, 12 and 16
(1,292 to 1,381 ms), three of the five paired ones at 860 px and two at 644 px, all five in the second place of their
pair. Every other run sits at 507 to 726 ms. The two kinds differ in where they come:

- The 0.5 to 0.7 s stall is the rescan's first move. In 10 of the 11 runs of that kind the longest wait was posted
  within 1 ms of the rescan's first move (run 1: posted 4,184, first move 4,185; run 2: 5 ms after it), and that one
  move held the UI thread 288 to 510 ms (the hook's longestMoveMs). This is the stall the slice is about.
- The 1.3 s stall is a later move: posted 0.7 to 1.8 s after the scan's or the rescan's first move (run 4: first
  move 1,024, posted 2,228; run 12: 4,322 and 5,225), one move holding the UI thread 1,273 to 1,312 ms.

The brief's 1,318.5 ms is the second kind. It is not D:\'s alone: T-005 run plainly at the end of the build, its tree
in %TEMP% on C:\ and no SL-080 variable set, failed its own 1 s bar with a scan-phase wait of 1,352.6 ms.

## What the UI thread is blocked under: WPF's synchronous render flush, waiting on the GPU side

`T005Batch.ps1 -Plan Trace` (artifacts/sl080/trace-1), all at 860 px: traced runs stalled 4 of 4 (median
700.4 ms), untraced 4 of 4 (median 654.1 ms), so tracing did not mask the stall. The EventPipe sampler resolved the
UI thread's frames: at most 1.5% of a primary stretch was unreadable (up to 16% of the short scan-phase ones), and
every primary wait matched a busy stretch that was busy for all but at most 0.6 ms of it (the hook's clock put on the
trace's by the two UTC origins; offsets 39.3 to 50.5 ms). All four traced
runs read **RENDER-PATH**; the batch verdict is **render-path**.

- The rescan's first move (runs 1, 5, 8: 694.5, 706.3 and 678.2 ms; 56 to 62 ms after the rescan's StartScan):
  51 to 55% of the wait in `DUCE+Channel.SyncFlush`, reached from `HwndSource.HwndTargetFilterMessage` and
  `MediaContextNotificationWindow.MessageFilter`; 43 to 47% with `Window.UpdateHwndSizeOnWidthHeightChange` as the
  deepest managed frame, that is inside the native SetWindowPos the hook's `Height` set calls, under
  `DependencyObject.SetValue`. Only the pump is common to 90% of it, so it names no single frame, and more than
  half of it is the render flush.
- The 1.3 s move (run 4, 1,367.3 ms): 92% under one chain, `HwndSource.HwndTargetFilterMessage` >
  `HwndTarget.HandleMessage` > `HwndTarget.UpdateWindowPos` > `HwndTarget.UpdateWindowSettings` >
  `DUCE+Channel.SyncFlush`.

`Channel.SyncFlush` is the UI thread waiting for WPF's render thread to take its batch and answer; a move or resize
of the window makes `HwndTarget` flush synchronously, so any time the render side takes is spent on the UI thread.
EventPipe does not see the render thread or the desktop compositor, so the trace alone cannot say what the render
side was doing.

`T005Batch.ps1 -Plan Render` (artifacts/sl080/render-1) took the GPU away: 4 pairs, software and hardware
interleaved, at 860 px, untraced.

| pair | runs | first | software | hardware | software/hardware |
|---|---|---|---|---|---|
| 1 | 1, 2 | software | 343.9 | 506.6 | 0.679 |
| 2 | 3, 4 | hardware | 187.8 | 524.0 | 0.358 |
| 3 | 5, 6 | software | 169.3 | 673.0 | 0.252 |
| 4 | 7, 8 | hardware | 172.2 | 684.7 | 0.252 |

Software lower in 4 of 4 pairs, median ratio 0.305: **gpu-present-side**, by the rule stated before the runs. In
software the rescan's first move held the UI thread 179 to 198 ms instead of 286 to 462 ms. Most of the wait is
the hardware render path answering a resize (the GPU surface and its present), not work the app does. What is left
in software, 169 to 344 ms (one of four runs still over 250 ms, in the scan phase, 73 ms after its first move), is the render thread's and
SetWindowPos's own share. The 1.3 s kind did not occur in these nine runs, so this batch says nothing about it.

## Against SL-044's narrowing

SL-044 found it was not the FolderTree (de51999 stalled too, 419 to 511 ms with moves, 10 ms without) and not
StartScan (2.8 to 7.4 ms). Those figures are context here, not an A/B: different builds, another day. This slice
adds: not the window's height, within what 8 pairs could see (2.13x); not GC (spike rescan-stall-is-gc-suspension,
under 1%); not CPU load from outside (spike stall-rides-competing-cpu-load); and on the UI thread, WPF's synchronous
render flush on a window move or resize and the SetWindowPos call itself, which software rendering cuts to a third.

## Programmatic moves, and a person's drag

Every stack here comes from the hook setting Left, Top, Width and Height on the window from its own thread through the
dispatcher: SetWindowPos from managed code, with WPF's HwndTarget answering the WM_WINDOWPOSCHANGED that follows. A
person dragging the title bar or a border enters DefWindowProc's modal move/size loop instead (SDL #1059), which pumps
messages itself; WPF's HwndTarget still answers each position and size change the loop makes with the same
UpdateWindowSettings and SyncFlush. So the render-side wait is expected to carry over to a drag, once per size change,
but the part inside SetWindowPos under `DependencyObject.SetValue` is the hook's, and no trace of a drag was taken.

## One follow-on fix candidate

The main window drawn in software while a scan starts (`HwndTarget.RenderMode = RenderMode.SoftwareOnly` on the
window's HwndSource, or the process-wide `RenderOptions.ProcessRenderMode`), measured with T005Batch.ps1's rules
against hardware, and priced in what software drawing costs the folder tree at 100,000 rows. The measured lower
bound is here: 169 to 344 ms instead of 507 to 685 ms, still over 250 ms in one run in four. The 1.3 s kind needs
its own traced batch first; it was caught once (run 4) and its chain is the same SyncFlush under UpdateWindowPos.

## The in-process fallback, contract only (not built)

Built only if a Trace batch reads cannot-read, or tracing-masks again at -SampleMs 10. Neither happened.

- FSA_HOOK_MSGLOG=1 (tree mode, with FSA_HOOK_GAP): the hook adds an HwndSource hook on the main window and
  Dispatcher.Hooks OperationStarted/OperationCompleted handlers, on the UI thread, and records every window message
  (WM_* id, wParam) and every dispatcher operation (priority, the delegate's method name) with its start and
  duration on the hook's clock. After the ending it logs, for each phase's longest wait, one JSON line per message
  or operation that overlapped [maxAt, maxEndAt]: {"kind":"msg","phase","what":"WM_WINDOWPOSCHANGED"|"op:<method>",
  "priority","startMs","durMs"}.
- The CANNOT-READ reasons that send a batch to it: more than 20% of a stretch unresolved or with nothing below the
  pump, no stretch within 20% of the wait, the UI thread without samples, dotnet-trace unable to install or run, or
  tracing-masks at a 10 ms interval.

## Measured at build, and what changed from the design because of it

- The refusal, live: `T005Batch.ps1 -Plan Height -LowHeight 620` (artifacts/sl080/live/height-620) ran the warm-up and
  run 1 at 860 px; run 2, the first at 620 px, ended with the hook's `height not applied (620/644)` and the batch
  stopped there, exit 1, before run 3.

- DOTNET_EventPipeThreadSamplingRate is read by runtime 10.0.12 as milliseconds in hexadecimal: "A" gave 10.1 ms
  between samples, "5" 5.1 ms, "10" (16 ms) 30.9 ms; "2710" (10 s) held the app's exit 7.5 s, and "989680" hung it.
  The option is FSA_T005_SAMPLE_MS (decimal ms, written in hex), not the design's FSA_T005_SAMPLE_NS. The nettrace
  header records the interval in ns (1,000,000 at the default). The runtime is .NET 10.0.12 in every run.
- StallStacks' idle floor is 25%, not the design's 60%: the first traced run was idle 49.4%, every idle sample in
  Dispatcher.GetMessage, because the hook's moves hold the UI thread in SyncFlush for about half the run.
- A stretch is matched on time, and its duration is only checked as busy for at least 80% of the wait. The design's
  duration match (within 20%) would miss a stretch the probe was let through in the middle of: the scan's stretch in
  the first run was 201 ms around an 83 ms wait.
- A stretch that is half or more in the render flush reads RENDER-PATH before SPLIT is considered: the rescan
  stretches name only the pump at 90% and are still 51 to 77% SyncFlush.
- AppDomain.GetCurrentThreadId gives the managed thread id on .NET (2 in the first run); the hook reads
  Thread.CurrentOSThreadId by reflection instead. InteropLivesOnlyInNative (tests) rules out a P/Invoke.
- ConvertFrom-Json turns the facts line's UTC time into a DateTime that loses its milliseconds when cast back to
  text (an offset of -225 ms instead of +204 ms): summary lines are read with -DateKind String.
- dotnet-trace's Speedscope names one module "PresentationCore.il" and "presentationcore.il" in different frames;
  the reader folds them into one method.
- The first Trace batch's stacks were read after its runs with -Reread, the same rules on the same files: the driver's
  reader had a variable (`$Stacks`, the script's path) shadowed by the verdict's table of the same name.
