# SL-089: the seams of SC-116

Status: built. Window cases N-005, N-006, T-009, W-001 and R-002 steps f and g in `tests/window/WindowCases.ps1`, the
hook's rows mode (`tests/window/FsaWindowHook/RowsMode.cs`), the stop-seam fixtures in `tests/window/StallRig` and
`tests/CfProvider`, and three Core.Tests classes (`HeldFolderReasonTests`, `WindowReportRuleTests`, `W001OracleTests`).
No product code changed: `core.unreadable` in the schema-1 report came with SL-095's merge, and SL-089 fences it with
the twinned rule `TheWindowReportReadsOnlyTheFailedFolderCount`.

## How the panel is read

The schema-1 report carries no row and never a path (Q-016). The hook's rows mode (`FSA_HOOK_ROWS=1`, with
`FSA_HOOK_MODE=rows`, which presses nothing and has no deadline, or with `stop`) selects the unreadable folders' tab when
the window raises `EndingShown`, reads every item of `UnreadableList` (path, kind, code, reason, and the texts its
container draws), and writes them to the hook's log at shutdown. The report adds the engine's count of its record,
`core.unreadable`, so the panel's rows, the header's distinct N and the record are three witnesses of one number.

## Measured at the build (2026-10-05, Windows 11 26200, unelevated)

| Case | Result |
|---|---|
| T-009 | Fixture probed 5/32 on h, 5/0 on c, 0/0 on p. One row, h, `NothingListed`, 32, drawn `could not be opened: in use by another program`; nothing says `access denied`; c and p no rows; `core.unreadable` 1. |
| N-005 control | No Stop, rig killed 8.0 s after the hold. a 5 `access denied`; b `ListedPartway` 59 `network or share error` (failpoint fired on page 3); s 59, t 53, u 53, all `network or share error`. |
| N-005 main | Stop 2 s after the hold, teardown 8.0 s after it (control 8022 ms, main 8015 ms). Cancelled. Rows exactly a and b once each; no row for s; t and u never opened by the server, no rows. No mapping to 127.0.0.1 left after either run. |
| N-006 controls | release (the provider completes the held fetch, and every later one, with `STATUS_CLOUD_FILE_UNSUCCESSFUL` through `CfExecute`, hr 0), kill (provider killed 8.0 s after the hold), timeout (the provider silent until the filter gave up on the held fetch, 60.4 s in, and the app asked again; the earlier, sequential runs waited out the filter's second timeout too, at 121 s, with the same result). Under all three: Completed, rows a (5) and b (`ListedPartway`, 59) only; **no row for s, t or u**; core files 711, folders 5, bytes 71,100 in every ending. The four runs go side by side on roots of their own, so the whole case takes about 70 s, inside the 120 s the engine gives a case. |
| N-006 main | Stop 2 s after the hold, freed at once (the hook saw the scan return 34 ms after Stop), release 8.0 s after the hold (control 8012 ms, main 8022 ms). Rows exactly a and b; no row for s, t or u. Every sync root unregistered and its folder gone after each run. |
| W-001, `C:\ProgramData\Microsoft\Windows\WER` | 446 rows = header 446 = `core.unreadable` 446, 7 drawn, all code 5 `access denied`; the oracle opened all 446 and every one was still refused; checker controls all held; kernel pool +2 MB. |
| W-001, `C:\Windows\Logs` | 1 row (code 5, still refused). Its top level alone holds 12 folders that refuse the keep-last-access open and list plainly: none is a row. |
| R-002 f | The 300-character deny-all folder from its panel row: `Exact`, hr 0, landed 1167 ms after the click on its parent, the folder alone selected, by Name; no dialog, no consent.exe. |
| R-002 g | The 1000-character list-denied folder from its panel row: `Exact`, hr 0, landed 913 ms after the click, same checks. |

## Q-177: the cloud half of criterion 1 is a platform limit

On this Windows build the cloud filter gives the reader no failed read. Whether the provider fails the held
`FETCH_PLACEHOLDERS` (the error completion O-001 asked about, measured here: `CfExecute` accepted it), dies, or stays
silent until the filter's own timeout, the listing the scanner gets back is a success with no entries, so the engine
records nothing for s, and nothing for t or u, whose fetches the provider failed the same way. The app therefore cannot
list these folders as unreadable, with Stop or without it. N-006 reports their absence after Stop as this limit, never
as a positive check. It fails if a later Windows returns an error for s under the kill or the timeout ending. Under the
release ending a row for s is not a failure: it becomes the positive control, and s's absence after Stop is then reported
as held beside it (Q-177).

It is also a silent undercount the app cannot detect: s, t and u were listed as empty under every ending, so whatever the
cloud holds under them is in no total and in no row. It is not fixed here.

## Criterion 3 on C:\ is a person's run

W-001 over the whole of `C:\` is not run by the engine (review M8, Q-176's reversal condition): SL-095 measured a cold
scan of C: moving about 537 MB of kernel pool even with the container layer store left out, past the engine's 0.5 GB
per-run guard, and SL-089's own first build attempt was stopped by that guard. A person runs it, unelevated, with the
wcifs-layer-store precaution on (it is at every launch), and W-001 writes `.taad/slices/SL-089/c-drive-run.json`
(counts, codes and reason texts only):

    pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/WindowCases.ps1 -Case W-001 -Root C:\

The trailing backslash is safe: every case hands the app its paths through `Format-AppArgument`, which doubles it as
`eng/read-window.ps1` does (review M1: quoted by hand, `"C:\" --report "<file>"` reached the app as one argument and no
report was ever written). `tests/window/AppArgumentCorpus.json` lists the roots, the launch-line plants and every script
that launches the app; `AppArgumentTests` runs them, and C-013 launches a temp root ending in a backslash in rows mode.
Measured at this build: W-001 with `-Root C:\Windows\Logs\` wrote its report, 1 row, verdict held.
