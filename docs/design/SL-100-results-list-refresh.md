# SL-100: Ctrl+Enter on a results row after the live refresh

Status: built. The live scan's refresh no longer leaves the results list without a selected or focused row. The rows and
their containers are still replaced whole on every refresh (Q-078 keeps it that way); the selection and the keyboard focus
are put back on the row by its Path.

## What was wrong

`MainWindow.ShowTopLevel` assigns a new `TopLevelRow[]` to `TopLevelList.ItemsSource` about every 250 ms while a scan
runs. WPF treats that as a Reset: every item container is unlinked (its DataContext becomes `{DisconnectedItem}`), the
keyboard focus that sat on the focused row's container drops onto the ListBox, whose DataContext is null, and a selected row
the tracker rebuilt as a new `TopLevelRow` loses its selection (the Selector keeps a selection only by reference). Ctrl+Enter
(`MainWindow.OpenInExplorer`) resolves its row from `Keyboard.FocusedElement.DataContext`, so from the ListBox it found no
row, `CanExecute` was false and the press did nothing.

## What the window does now

In `ShowTopLevel`, before the swap, it takes the selected row's Path and, if the keyboard focus is on one of the list's
row containers, that row's Path. It then assigns the new items, and in the same call:

1. Reselects the row with that Path (`StringComparison.Ordinal`: a case-sensitive folder can hold two names that differ
   only by case). Nothing waits for layout, so a frame is never drawn without the selection, and a stop that lands between a
   tick's swap and its Loaded work still finds the row selected.
2. In the Loaded-priority callback that already restores the scroll offset: scroll restore, then, if a focus was
   captured and `TopLevelList.ItemsSource` is still the array this call assigned, `FocusRow`. It lays out the list once
   (`UpdateLayout`), looks up the row's new container and focuses it. A row outside the restored viewport has no container
   and is left as it is: it stays selected, the list keeps the focus and the refresh never scrolls. Then `LastRefresh` and
   `_skipNextRefresh` are set exactly as before. The stale-array guard skips only the refocus, never the scroll restore or the
   two timing lines, so the SL-039 droppable-refresh rule (a refresh over 100 ms skips the next tick) is unchanged and
   now also counts the refocus's cost.

The focus is only ever moved from where WPF leaves it when the container it would go to is gone (`FocusIsAdrift`): the list
itself, and, in an active window, the window itself or nothing. That second case was measured here (it is not in the design)
and is what a closed row menu, a window activated again after Alt+Tab or after the Explorer window Ctrl+Enter opened, and a
closed modal dialog all leave behind when a refresh unlinked the container they would have returned the focus to. A refresh
never takes the focus from a button, an open row menu, the folder tree, another window or an inactive window. The next
refresh, while the scan runs, puts the focus on the selected row. After the last refresh (a stopped scan) nothing moves
it until the person clicks a row; the filed follow-up that resolves the list's selected row for Ctrl+Enter closes that.

The Tag / `CommandTarget` = window workaround of the row context menu (`OnRowContextMenuOpened`) is kept unchanged, as
Q-078 decided. Containers still detach on every refresh, so the R-001 `top-live` step still sees `detached == true`.
`TopLevelList.Items` is still `TopLevelRow`, so `WindowReport`, the window hook and the SL-063 tests read it as before.

## Not a workaround for an outside defect

WPF unlinking containers on a Reset, moving the focus to the nearest focusable ancestor and keeping a selection by
reference are documented, by-design behaviours. The defect is this project's own wholesale replacement of the items, so there
is no KnownWorkarounds entry and no setting.

## The checks

- The SL-100 project, `tests/bugs/SL-100-ResultsListRefresh` (outside FsAnalyser.slnx, as SL-063's is): the repro Fact
  unchanged, and one Fact per shape in `ResultsListShapeTests.cs` and `FocusReturnTests.cs`, on the real MainWindow
  driven in-process. `FocusReturnCorpus.json` enumerates the class "keyboard focus returning to an element a refresh
  detached" (the review's m7), one row per ordinary way of leaving and coming back, and `FocusReturnTests` runs every row.
  Rows marked `gap` pin what is measured on a list that no longer refreshes.
- Regression guards at validate, run serially under TEMP=TMP=%LOCALAPPDATA%\Temp (set by a wrapper script, not by a
  `pwsh -Command` assignment), about 30 minutes:
  1. `pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 test tests/bugs/SL-100-ResultsListRefresh/SL-100-ResultsListRefresh.Tests.csproj`
  2. `pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 test tests/bugs/SL-063-DoneRowFinality/SL-063-DoneRowFinality.Tests.csproj` (10 of 10 at HEAD)
  3. `pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 test FsAnalyser.slnx`, the default suite (three LastAccess / HeldOpenTree reds predate this slice)
  4. `tests/window/WindowMutants.ps1 -Mutant All` (Q-114, about 415 s)
  5. The cases that drive the live top-level list: publish (`pwsh -NoProfile -ExecutionPolicy Bypass -File eng/dotnet.ps1 publish src/FsAnalyser.App/FsAnalyser.App.csproj -c Release -o artifacts/publish`), then `tests/window/WindowCases.ps1 -Case L-001`, `-Case L-002` and `-Case L-003`, one after another. L-002 matters most: it slows every refresh (FSA_HOOK_SLOW_REFRESH_MS) so a tick skips, which is the droppable-refresh timing the refocus's layout now sits inside.
- R-001 step 16 (person-run, it opens real Explorer windows): `tests/window/WindowCases.ps1 -Case R-001`. The hook's
  `top-key` reveal target focuses a row that is still being read, waits for three live refreshes, raises Ctrl+Enter through
  the command's own KeyGesture on the focused element, and the step asserts the focused Path, the selected Path,
  `CanExecute`, and a reveal of that Path in Explorer. The hook reads after a `Flush()` (Ui() runs at Normal priority, above
  the Loaded priority the restore runs at) and logs a line of its own for each way a run can fail to make its premise:
  `scan-ended` (with `refreshes`), `no-reading-row`, `row-not-found`, `row-not-realised`, `focus-failed` (with `active`),
  `no-gesture`. The tree is `New-TopKeyTree`: twelve folders of 4,000 empty folders, so the scan outlasts the three refreshes.
  The step has also been run, unattended, against the published exe over a tree of Hidden folders with
  `FSA_HOOK_EXPLORER_VIEW=0|0`, so the press ends in a refusal and opens no Explorer window.

## Gaps, recorded and not designed around

- A real hardware Ctrl+Enter that arrives in the few milliseconds between a tick's swap and its Loaded callback is not
  measurable here (SC-254). A key queued at Input priority in that gap reaches the row (`AKeyQueuedRightAfterTheSwapReachesTheRow`).
- A focused row pushed out of view has a dead Ctrl+Enter until a refresh has it in view again; the same for a stopped list
  after a menu close or an activation (corpus rows F02, F05, F08). The filed follow-up resolves
  `TopLevelList.SelectedItem` for Ctrl+Enter when the focus is on the list itself.
- The cost at 100,000 rows, one focused and rebuilt row: median refresh 25.8 ms (max 40.6) with the restore, 22.4 ms
  (max 40.8) without, one probe of 12 refreshes each.
- Derived criteria 1 and 5 (containers not detached; the Tag/CommandTarget pattern no longer needed) are not met, by
  decision Q-078.
