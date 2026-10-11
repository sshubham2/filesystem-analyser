<#
SL-099: the outcome-C record (docs/design/SL-099-outcome-c-redesign.md) and the evidence committed beside it.

  1. The record disposes of every item SL-095 handed to SC-128 (docs/design/SL-095-container-layer-store.md, the section 'Not
     judged here: handed to SC-128'): one table row '| <id> | settled here|carried|dropped | reason |' for each of the 14 finding
     ids and the 3 items that have none, 17 rows, the ids captured by the pattern being exactly those 17 and none twice (a
     duplicated row must not make up for a dropped item); and every one of the 17 is named in the SL-095 section, so a label is
     not invented here.
  2. SL-095's hand-off section points at the record.
  3. The record says what its reviews required it to say: SC-230 a precondition of session 1 (M6), the 24.4 GB / 47.41 h figure
     beside the 1 KB-per-path one and that the source shows no release (m6), F2 left out of the repeat set after a guard-stopped
     F1 (m7), the idle term scaled for spans over 1,200 s and the cold rise summed through the first Completed arm (N5), the WC
     reading's idle figure (N6), the full WC leak set and where WCse, WCss and WCfn are read from (m5), and that readers of the
     committed build-2026-10-05 marks must be case-exact (m1).
  4. Spike 107's three runs are committed under docs/design/SL-099-runs/spike-107-2026-10-12/<set>/ (analysis.txt, arms.jsonl,
     log.txt, marks.jsonl, and for FileRepository roots.jsonl, H1.txt and H2.txt), or a missing.txt names a run folder that a
     search of %APPDATA%\taad3\spikes\8\spike-107 also does not find.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-099-RecordAndEvidence.Tests.ps1

Exit 0 when every check held, 1 when any failed. Reads files only.
#>

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $(($what -split "`n")[0])" }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

$recordPath = Join-Path $Repo 'docs\design\SL-099-outcome-c-redesign.md'
$sl095Path = Join-Path $Repo 'docs\design\SL-095-container-layer-store.md'
$record = [IO.File]::ReadAllText($recordPath)
$sl095 = [IO.File]::ReadAllText($sl095Path)

# 1. The disposition table. The ids are the ones the SL-095 section names; the last three are items without an id, named here
# by a short label and found in the section by the words next to them.
$ids = @('R3-1', 'R4-1', 'M6', 'N2', 'X5', 'N1', 'M3', 'N3', 'X2', 'Y3', 'M2', 'M4', 'Y1', 'Q-185', 'steps 3-5', 'cancel trigger', 'ScanArm')
Write-Output 'the disposition table'
$pattern = '(?m)^\| (' + (($ids | ForEach-Object { [regex]::Escape($_) }) -join '|') + ') \| (settled here|carried|dropped) \|'
$captured = @([regex]::Matches($record, $pattern) | ForEach-Object { $_.Groups[1].Value })
$any = @([regex]::Matches($record, '(?m)^\| ([^|]+?) \| (settled here|carried|dropped) \|') | ForEach-Object { $_.Groups[1].Value })
Check ($ids.Count -eq 17) 'there are 17 items to dispose of'
Check ($captured.Count -eq 17) "the pattern matches 17 rows ($($captured.Count))"
$twice = @($captured | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
Check ($twice.Count -eq 0) "no id has two rows ($($twice -join ', '))"
$dropped = @($ids | Where-Object { $captured -cnotcontains $_ })
Check ($dropped.Count -eq 0) "every item has a row (no row for: $($dropped -join ', '))"
$extra = @($any | Where-Object { $ids -cnotcontains $_ })
Check ($extra.Count -eq 0) "no row is for anything else ($($extra -join ', '))"

$start = $sl095.IndexOf('## Not judged here: handed to SC-128')
$end = $sl095.IndexOf('## The excluded store', [Math]::Max(0, $start))
Check ($start -ge 0 -and $end -gt $start) 'SL-095 has the hand-off section'
$section = if ($start -ge 0 -and $end -gt $start) { ($sl095.Substring($start, $end - $start) -replace '\s+', ' ') } else { '' }
$named = [ordered]@{
    'R3-1' = 'R3-1'; 'R4-1' = 'R4-1'; 'M6' = 'M6'; 'N2' = 'N2'; 'X5' = 'X5'; 'N1' = 'N1'; 'M3' = 'M3'; 'N3' = 'N3'; 'X2' = 'X2'; 'Y3' = 'Y3'
    'M2' = 'M2'; 'M4' = 'M4'; 'Y1' = 'Y1'; 'Q-185' = 'Q-185'; 'steps 3-5' = "steps 3 to 5"; 'cancel trigger' = 'store-relative cancel trigger'; 'ScanArm' = 'ScanArm-like rig'
}
$unnamed = @($named.Keys | Where-Object { -not $section.Contains($named[$_]) })
Check ($unnamed.Count -eq 0) "SL-095's section names each of the 17 ($($unnamed -join ', ') not found)"

# 2. The pointer.
Write-Output 'the pointer'
Check ($sl095.Contains('docs/design/SL-099-outcome-c-redesign.md')) "SL-095's hand-off section points at the record"

# 3. What the reviews required it to say.
Write-Output 'what the record says'
$flat = $record -replace '\s+', ' '
$must = [ordered]@{
    'M6: SC-230 was a precondition of session 1'              = 'SC-230 was a precondition of session 1'
    'm6: the 24.4 GB figure and its duration'                  = '24.4 GB combined pool'
    'm6: ... after 47.41 hours'                                = '47.41 hours'
    'm6: the source shows no release at that size'             = 'does not show the state being given back at that size'
    'm6: Q-188 may be this mechanism'                          = 'Q-188 may be this very mechanism'
    'm7: F2 left out after a guard-stopped F1'                 = 'F2 is left out of the repeat set and only F3 to F5 are read as repeats'
    'N5: the idle term scaled by span / 1,200'                 = 'scaled by span / 1,200'
    'N5: the cold rise summed through the first Completed arm' = 'summed through the first Completed F arm'
    'N5: arms without /settled do not count'                   = 'does not count towards the two'
    'N6: +5 per walk visible only under 5 idle'                = 'only where the host''s idle movement on that tag is under 5'
    'N6: the idle figure reported beside each WC reading'      = 'reports the idle figure next to each WC reading'
    'm5: the full WC leak set'                                 = 'WCsc, WCse, WCss, WCfc, WCri, WCfn and FMwi'
    'm5: WCse, WCss and WCfn are read from marks.jsonl'        = 'WCse, WCss, WCfn, Ntff and every tag added by this slice are read from marks.jsonl'
    'm1: readers of build-2026-10-05 must be case-exact'       = 'docs/design/SL-095-runs/build-2026-10-05'
    'm1: 0feac0e did not fold case (Read-Mark, Tag)'           = 'It did not: Read-Mark filtered with `-ccontains`'
    'precondition 1 holds, citing Invoke-Protocol'             = 'the exe-full type and a null hook to every F arm'
}
foreach ($k in $must.Keys) { Check ($flat.Contains($must[$k])) $k }

# 4. The evidence.
Write-Output 'the committed spike 107 runs'
$evidence = Join-Path $Repo 'docs\design\SL-099-runs\spike-107-2026-10-12'
$sets = [ordered]@{ 'a-large' = @(); 'a-small' = @(); 'filerepository' = @('roots.jsonl', 'H1.txt', 'H2.txt') }
$runIds = @{ 'a-large' = 'run-20261012-002418'; 'a-small' = 'run-20261012-015214'; 'filerepository' = 'run-20261012-011147' }
$spikes = Join-Path $env:APPDATA 'taad3\spikes\8\spike-107'
foreach ($set in $sets.Keys) {
    $dir = Join-Path $evidence $set
    $want = @('analysis.txt', 'arms.jsonl', 'log.txt', 'marks.jsonl') + $sets[$set]
    $missing = @($want | Where-Object { -not (Test-Path -LiteralPath (Join-Path $dir $_)) -or (Get-Item -LiteralPath (Join-Path $dir $_)).Length -eq 0 })
    if ($missing.Count -eq 0) { Check $true "$set holds $($want -join ', ')" }
    else {
        $note = Join-Path $dir 'missing.txt'
        $stillThere = @(Get-ChildItem -LiteralPath $spikes -Recurse -Directory -Filter $runIds[$set] -ErrorAction SilentlyContinue)
        Check ((Test-Path -LiteralPath $note) -and $stillThere.Count -eq 0) "$set is missing $($missing -join ', '); that is only allowed with a missing.txt and no $($runIds[$set]) left under $spikes ($($stillThere.Count) found)"
    }
}
Check (Test-Path -LiteralPath (Join-Path $evidence 'README.md')) 'the folder has a README.md naming each source run folder'

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
