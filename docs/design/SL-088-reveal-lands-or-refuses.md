# SL-088 "A reveal lands or is refused": design as built

'Open in Explorer' on a tree or biggest-files row opens the item's parent with the item selected, at any path length and
for folders this user cannot open, or it says it could not and opens nothing. Where this file and the design disagree,
this file is right.

## What changed in the product

- **The nearest-ancestor fallback is gone (reverses SL-047).** `ShellReveal.Walk` asks the shell for the item once:
  `SHParseDisplayName`, then `SHOpenFolderAndSelectItems`, then `ILFree`, on RevealAsync's STA thread as before. A parse
  that fails refuses the reveal with its HRESULT, `NoLongerExists` for 0x80070002 and 0x80070003; an open that fails
  refuses it with the open's HRESULT. No other folder is tried. SL-047 climbed to the first ancestor that parsed, and the
  build check measured that with the item's folder deleted the grandparent still parses S_OK, so the app opened another
  folder in the item's place and called it a highlight. `RevealKind` is now `Exact` or `Failed`; the settled rule 'a
  reveal that cannot land never falls back to an ancestor' keeps the old kind from coming back.
- **A name the shell cannot address is refused too.** `ShellReveal.Plan` returns the one target, `RevealPlan.Target`, or
  no target and the name when a component ends in a dot or a space, or is a device name as a whole (`dup.`, `trail `,
  `con`, `COM1`). Measured with the real parse: `dup.` parses S_OK as `dup`, `trail ` as not found, `con` and `nul` as
  0x800704B0. SL-047 opened that component's parent instead; that is another folder in the item's place, which criterion
  6 rules out, so the reveal is `Failed`, `NameUnaddressable`, with 0x8007007B, and the shell is not asked at all. The
  design did not name this path; deleting the ancestor kind decided it, and R-001's step for `dup.` expects the refusal.
  A device name with an extension (`con.txt`, `aux.h`, `nul.log`, `com1.txt`, `prn.md`, `lpt1.dat`) is not refused up
  front (review R2-m2): on this Windows 11 each parses S_OK and resolves back to the very file, so it goes to the shell.
- **A parse must stand for the item itself.** After an S_OK parse, `Walk` turns the ID list back into a path
  (`SHGetPathFromIDListEx`) and refuses the reveal, `ParsedAsAnotherItem`, when that path is not the target, compared as
  text and then with every 8.3 alias expanded (`GetLongPathNameW` on both sides), since past MAX_PATH the shell gives the
  path back in 8.3 form. This is the backstop for any Windows that parses a name as another item (Windows 10 and
  `con.txt` were not measured) with no rule written per version. Measured: files, a deny-all folder, a junction, file and
  folder symlinks, `C:\`, the device names with an extension and paths of 161, 300 and 1000 characters all resolve back;
  `dup.` resolves to `dup` and is refused.
- **No existence check before the reveal.** The parse's HRESULT is the check: it answers S_OK for a folder with a
  deny-all ACE (build check, and the spikes' 36/36 and 12/12 landings on deny-all leaves), where a check of its own could
  refuse exactly the folders criterion 1 is about, and race the reveal besides.
- **No manifest change and no `\\?\` rewriting.** The spikes landed at 161, 300 and 1000 characters from an exe with no
  longPathAware entry, from a long-path-aware testhost and from pwsh alike, and on a volume with no 8.3 names.
- **The status line says what happened.** A reveal that lands says nothing, as before (a hidden item still gets its
  note). `NoLongerExists`: "'<leaf>' no longer exists where the scan found it, so Explorer was not opened. Rescan to
  refresh." Any other refusal: "Couldn't show '<leaf>' in Explorer (<why>). Nothing was opened.", where <why> is
  Windows' message for a FACILITY_WIN32 HRESULT, the HRESULT in hex for any other, for a name it cannot address what
  Explorer cannot do with it, and for a parse that stands for another item 'Explorer reads its path as another item'.
  No dialog.

## What the product does not check (criterion 4)

`Exact` still comes from the shell's S_OK alone. The app does not read Explorer's windows to see that the item is
selected; the window cases do (R-001, R-002). Criterion 4 names those tests as its check, and reading Explorer from the
shipped exe would mean automating another program. Whether the app itself should confirm the landing is an open
question for a person.

## Hidden and system items are out of this slice

The spike on Hidden+System targets (System Volume Information, pagefile.sys) did not finish: under this PC's settings
(Hidden=1, ShowSuperHidden=0) it selected 0 of 8 Hidden+System targets and 5 of 5 Hidden-only ones, and the run at
Windows' default settings waits on a person. No criterion covers them. For such an item the app reports `Exact` with
nothing selected in Explorer, the known gap a follow-on slice owes once that spike has run.

## How it is checked

- `ShellRevealTests`: fake shell calls for a target that parses and lands, is gone (0x80070002, 0x80070003), fails to
  parse otherwise (0x80070005, 0x8007007B, 0x80070035, 0x80004005), parses and then fails to open (the ID list freed,
  nothing else tried), parses to another item or to none (refused before the open), and has a name the shell cannot
  address (the shell never asked). `Walk_OnRealTree_RefusesGoneTargetsAndLandsThePresentOne`
  runs the real parse on an STA thread over a file whose folder was deleted and a file deleted from a folder that is
  still there, beside a present file and a deny-all folder that must reach the (recording) open, so the refusals cannot
  pass because the parse never worked.
- `NothingInTheAppHandsExplorerItsSelectSwitch`: twinned in ArchitectureRulesTests and .taad/fitness.yaml, every
  spelling and case of Explorer's select switch anywhere in src, comments included (U-114: given a long path, Explorer
  silently opens another folder). `SelectSwitchRuleTests` and harness plants F31-F35 and P25-P27 feed it the shapes.
- `tests/window/WindowCases.ps1 -Case R-002`: the published exe under the hook, checked in Explorer's own windows through
  Shell.Application with both sides resolved by GetLongPathNameW. Steps, in this order: (b) files of exactly 161 and 300
  characters from their biggest-files rows, the 161 one the run's first reveal, each within 1000 ms of the click;
  (a) a short deny-all folder from its tree row, no dialog of explorer.exe or the app and no consent.exe; (c) a deny-all
  folder of exactly 300 characters from its tree row, within 1000 ms, with (a)'s watch; (d) a 1000-character file into
  an Explorer already on its parent with a sibling selected, within 1000 ms, as the only selection; (e) the item's
  folder deleted after the scan: refused, the status says so, and no Explorer window opens, moves or changes selection
  for 2 s. The click's time is the hook's `revealStartQpc` (a class handler for the command's PreviewExecuted); the
  landing's is the pwsh-side QPC of the first matching read, on the same system-wide clock. R-001's step 10 is padded to
  exactly 300 characters, and both cases close only the Explorer windows they opened on their own trees.