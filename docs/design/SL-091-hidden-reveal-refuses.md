# SL-091 "A hidden item Explorer will not list is refused": design as built

'Open in Explorer' on an item that Explorer's own view settings hide no longer reports a landing that Explorer never
makes. Explorer's open returns S_OK for such an item, opens its parent and selects nothing (0 of 13 at Windows'
defaults, run 2285; 0 of 8 Hidden+System items under this PC's settings, SL-088), which the app reported as `Exact`
with a hint. Now the reveal is refused before anything opens, and the status line names the Folder Options setting that
hides the item. The app reads Explorer's settings and never changes them. Where this file and the design disagree, this
file is right.

## What changed in the product

- **A visibility gate in `ShellReveal.Walk`, between the resolve-back and the open.** After the parse and the check that
  the ID list stands for the target, `Walk` reads the target's own attributes (`File.GetAttributes` on the `\\?\` path,
  moved up from after the open; a volume root is exempt, and a read that throws still counts as not hidden). Only for a
  Hidden target it asks Explorer's view, once. When `ExplorerView.Shows(hidden, system)` is false it returns `Failed`,
  `RevealReason.HiddenByExplorerSettings`, HRESULT 0, `HiddenBy` and `View` set, and never calls the open; the ID list is
  freed by the same `finally` as every other path. The order of refusals is: name unaddressable, parse failure (gone or
  other), parsed as another item, hidden by Explorer's settings, open failure. A non-hidden item never reads the view.
- **Only the item's own attributes decide.** No folder above the item is read (O-017, settled by two go verdicts; see the
  table). A +H file directly inside a +H folder is refused on its own attribute.
- **`ExplorerView` and its read.** `ExplorerView(ShowsHidden, ShowsProtected)`;
  `Shows(hidden, system) = !hidden || (ShowsHidden && (!system || ShowsProtected))`; `Hides` gives the settings that hide
  it, `HiddenFiles = Hidden && !ShowsHidden`, `ProtectedFiles = Hidden && System && !ShowsProtected`.
  `ReadExplorerView()` calls `SHGetSetSettings` with a zeroed 64-byte buffer, mask 0x00040001 (SSF_SHOWALLOBJECTS |
  SSF_SHOWSUPERHIDDEN) and the set flag false, fresh at every hidden reveal, no cache; `FromShellState` reads bit 0 of
  the first DWORD as fShowAllObjects and bit 15 as fShowSuperHidden and ignores every other bit.
- **The test override can only hide.** `ShellReveal.OverrideExplorerView(ExplorerView? mask)`, internal, published with
  `Volatile`, is set once before the app's Main by the window hook (`FSA_HOOK_EXPLORER_VIEW`). The view a reveal is decided
  under is the real one ANDed with the mask, field by field (`Effective`), so no mask turns a refusal under the real
  settings into a landing (review m2). Null clears it.
- **`RevealOutcome` gains `HiddenBy` (`ExplorerHides`, flags `None | HiddenFiles | ProtectedFiles`) and `View`**
  (`ExplorerView?`, the view the reveal was decided under; null when none was read: a non-hidden item, a volume root, or
  a refusal before the attributes were read). `RevealKind` stays `Exact` or `Failed`.
- **The status line.** `Exact` says nothing: it now means Explorer lists the item, and the blanket note SL-047 added for
  every hidden item ("... only highlights it if it is set to show hidden files ...") is gone; it showed whatever the
  settings, including on this PC where hidden-only items are selected 5 of 5. A refusal reads
  "Nothing was opened: Explorer hides '<leaf>' (<label>[; <label>])." with the outcome and the item first, because the
  line is one NoWrap, CharacterEllipsis line cut at its end (review m1). Labels, hidden files first: `HiddenFiles` gives
  "Don't show hidden files, folders, or drives", `ProtectedFiles` gives "Hide protected operating system files".
- **Not a workaround.** The refusal is not a KnownWorkarounds entry and has no checkbox: Explorer not selecting an item
  its own view hides is Explorer obeying the person's setting, not a defect outside the project, and the false success
  was this project's own (Exact from S_OK), fixed rather than toggled. Refusing rather than opening the parent is the
  person's decision (Q-175, Q-198).

## The Folder Options labels, as this PC resolves them

Read-only, with `SHLoadIndirectString` on the `Text` values under
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Folder` (Windows 11 26200, en-US):

| Value | Indirect string | Text |
|---|---|---|
| `Hidden\NOHIDDEN` | `@shell32.dll,-30501` | Don't show hidden files, folders, or drives |
| `Hidden\SHOWALL` | `@shell32.dll,-30500` | Show hidden files, folders, and drives |
| `SuperHidden` | `@shell32.dll,-30508` | Hide protected operating system files (Recommended) |

The status line uses the first and the third without "(Recommended)". The same read gave the shell state 0x00000001 at
HKCU `Hidden=1`, `ShowSuperHidden=0`.

## What Explorer selects: the predicate table

`visible = !hidden || (ShowsHidden && (!system || ShowsProtected))`. A cell is (Hidden setting, ShowSuperHidden): 1 shows,
2 or 0 hides.

| Item | (1,0), this PC | (2,0), Windows' defaults | (1,1) | (2,1) |
|---|---|---|---|---|
| plain | selected: measured (SL-088 controls) | selected: measured (run 2285 controls, 4/4) | predicted | predicted |
| System only | selected: measured 8/8 (spike-91 cell 0) | selected: measured, `ProgramData\Microsoft` 6/6 (Sandbox) | predicted | predicted |
| Hidden only | selected: measured 5/5 (SL-088) | not selected: measured 0/13 with Hidden+System (run 2285) | predicted selected | predicted not |
| Hidden+System | not selected: measured 0/8 (SL-088) | not selected: measured (run 2285, incl. System Volume Information) | predicted selected (the one unmeasured Exact) | predicted not |
| plain, under a +H folder | selected | selected: measured 18/18 (Sandbox); run 2285's control under +H AppData four levels up | predicted | predicted |
| plain, under a +H+S folder | selected | selected: measured 18/18 (Sandbox) | predicted | predicted |
| +H file in a +H folder | - | that folder opens with nothing selected: measured 3/3 (Sandbox X1) | - | - |

Sandbox: spike hidden-ancestor-selection-at-defaults-in-windows-sandbox, Windows Sandbox build 26100 with a
High-integrity Explorer, shell state 0x8, the real ShellReveal compiled unchanged. Run 2285: the person's hand run at
Hidden=2 / ShowSuperHidden=0 (2026-09-29), read from the engine's record by spike run-2285-control-sat-under-hidden-appdata.

## Known limits

- **Windows 10 is not measured (Q-197).** The SHELLSTATE decode and Explorer's hidden-item filtering were measured on
  Windows 11 26200 (and the Sandbox's 26100) only; no Windows 10 machine or VM was available. A wrong assumption costs a
  refusal that was not needed, or a reveal that opens where the item cannot be seen; the app only reads, so no data is
  at risk. Whoever next has Windows 10 runs `ShellRevealTests` there (the decode and the registry cross-check) and the
  Hidden=2 sitting below. This repository keeps no release notes or known-limits list of its own, so the limit is
  recorded here.
- **The host's ancestor rows are not measured.** The +H and +H+S ancestor rows were measured in the Sandbox (26100, a
  High-integrity Explorer) and, for a +H folder four levels up, in run 2285 on this PC. A +H direct parent and +H+S
  ancestors on 26200 with this PC's Medium-integrity Explorer wait on the sitting below; if a class blocks there, it
  needs the ancestor rule in a new design (a data-model change: the refusal would name the hiding folder).
- **(1,1) and (2,1) are predicted.** ShowSuperHidden has never been 1 on this PC, so bit 15 of the shell state has only
  been seen at 0, and the one cell where the predicate claims a landing unmeasured is Hidden+System at (1,1).
- **A setting changed while the app runs** is read at the next hidden reveal, assuming `SHGetSetSettings` follows
  Explorer within the session (spike shgetsetsettings-tracks-midsession-toggle is undecided). If it does not, the
  fallback is a read-only HKCU read behind the same delegate, with no contract change.

## The one Hidden=2 sitting a person owes

Only a person may change Explorer's Hidden setting. In one sitting, with every Explorer window closed:

1. Set File Explorer Options > View > "Don't show hidden files, folders, or drives", keeping "Hide protected operating
   system files" on (Hidden=2, ShowSuperHidden=0).
2. Run the spike-92 host ancestor harness (`run.ps1`, then `verify.ps1`) on 26200: a +H direct parent and +H+S ancestors.
3. Re-run the 0/13 measurement through the shipped exe (`WindowCases.ps1 -Case R-001` and `-Case R-003`), adding
   `C:\Program Files\WindowsApps` and System-only items, with every control logging its full path and each ancestor's
   attributes. R-003's step (d) then clicks the Hidden-only targets too, which the defaults refuse.
4. Set "Show hidden files, folders, and drives" back (Hidden=1) and confirm Hidden=1 / ShowSuperHidden=0 in both the
   registry and the shell's state (`ShellRevealTests.ReadExplorerView_AgreesWithTheRegistryOnThisPc`).

Separately, a person runs R-001 through this build: its step 5 reveals `hs_file.sys` and `h_file.dat` with the
expectation derived from the settings in force, so under this PC's settings `h_file.dat` lands with an empty status line
and `hs_file.sys` is refused for the protected-files setting. The (1,1) and (2,1) cells, and the mid-session toggle, need
the same person's hands on Explorer's settings.

## The architecture record (Q-198, Q-125)

No stage can edit the engine's architecture record. Until the owner applies it, this replacement for C-004's reveal clause
is the agreed behaviour (Q-198). Find, in C-004 (Desktop UI):

> which asks the shell to open the item's parent with the item selected (for a hidden item, noting that Explorer
> highlights it only when set to show hidden files) or, when the item no longer exists, has a name the shell cannot
> address, parses as another item, or the shell call fails, says so on the status line and opens nothing.

Replace with:

> which asks the shell to open the item's parent with the item selected or, when the item no longer exists, has a name
> the shell cannot address, parses as another item, is hidden by Explorer's own view settings (Hidden while hidden files
> are not shown, or Hidden and System while protected operating system files are hidden, as read from the shell's
> settings and never changed; volume roots exempt), or the shell call fails, says so on the status line, naming the
> setting that hides it, and opens nothing.

## Architecture rules

- **Twinned** (a Fact in `ArchitectureRulesTests` and an entry above the first `# settled by` marker, `(?m)`,
  `including_comments: true`, review m4): `OnlyShellRevealReadsOrOverridesExplorersViewSettings` (no
  `SHGetSetSettings` or `OverrideExplorerView` in src outside ShellReveal.cs) and `TheAppNeverWritesExplorersSettings`
  (six single-line patterns over src). Plants F41-F48 must fire and P32-P33 stay quiet.
- **Settled**, one `# settled by SL-091` block: 'no window case or hook changes Explorer's view settings' over
  `tests/window/**`, nine single-line patterns. Plants S39-S42 must fire in Fitness and S43 stay quiet. Fitness blanks
  `#` comments in .ps1 (review m5: the engine's `_LINE_COMMENT` lists .ps1), so the patterns need no line anchors.
- **The write guards are closed as a class (review n2).** `tests/FsAnalyser.Core.Tests/ExplorerSettingsWriteCorpus.json`
  lists the spellings of a write to Hidden or ShowSuperHidden that a C# producer in src and a script or the hook in
  tests/window reach for, with the look-alikes they must leave and the known limits of a tripwire on names.
  `ExplorerSettingsWriteRuleTests` feeds each line to the guard of its scope, read from the Fact and from the yaml, and
  to the patterns the design declared: 63 rows, of which 24 (10 in src, 14 in tests/window) the declared patterns let
  through and the guards now catch, 17 look-alikes that stay quiet, and 6 known limits pinned as misses.
- Attempt 1's block (four rules, its write patterns spanning lines in Fitness and not in xUnit, and a `because` saying
  Fitness does not blank `#` in .ps1) is deleted (review n1). Its 'the Core tests never change the person's Explorer
  settings' rule over `tests/**/*.cs` is not carried: the design declared no such rule, and the Core tests only read.

## How it is checked

- `ShellRevealTests`, real temp items under a fake shell with an injected view and counts of every call: items that are
  not Hidden (plain, System-only) under all four views; plain items under a +H and a +H+S folder; Hidden-only items
  (a file, a folder, a +H file in a +H folder, past MAX_PATH) refused while hidden files are not shown; Hidden items
  whose every setting shows them landing; Hidden+System items refused naming each setting that hides them; a +H+S
  folder with a deny-all ACE; the real `System Volume Information` and every Hidden+System `*.sys` at the system root
  through the real parse on an STA thread (never skipped); the volume root; earlier failures on a Hidden+System target;
  the decode table; the real read against HKCU; and the override's AND over every real view and mask.
- `WindowCases.ps1 -Case R-003`: (a) Windows' defaults injected, five temp targets refused with the exact text, the
  200-character one the line B-007, B-008, S-001 and S-006 lay out, no Explorer window opened or changed; (b) a
  malformed injection refused before Main, nothing revealed; (c) the paging file from the results list of a C:\ scan
  stopped after its root listing (review n3), under 0|0; (d) the settings in force from HKCU, refusals clicked, landings
  reported owed to a person. `WindowMutants.ps1 -Mutant NoExplorerViewMember` renames the override, and R-003 fails on
  its armed check in (a) and (c) and on nothing else.
- B-007, B-008, S-001 and S-006 put the longest refusal on the status line at 560x644. The hook logs the text's
  untrimmed width against the line's and how many characters show (review n4): measured 1223.91 px in 512 px, the first
  141 of 323 characters, so the outcome and most of a 200-character name show and the labels are in the tooltip.
