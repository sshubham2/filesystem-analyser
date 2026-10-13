<#
SL-107: the cases behind the hand-off for the person's whole-C:\ W-001 run (docs/design/SL-107-c-drive-run.md). The run
itself is the person's (SC-136, SC-138); these are the cases the engine can make unattended, one at a time, none of which
scans C:\ whole:

  C-001  the command typed in cmd.exe as -Root "C:\" stops with 'is not a folder', exit 1, and writes no record
  C-002  SL-089-seams.md, where the slice text sends the person, carries that warning and points at the SL-107 doc
  C-003  the SL-107 doc carries the command, the console capture, every ending and the fields it tells the person to read
  C-004  W-001 over a whole volume other than C: (D:\) holds, and does not write the C: record path
  C-005  W-001 over a tree that holds the container layer store (C:\ProgramData\Microsoft\Windows): notScanned 1, rows = header
  C-006  W-001 over the stuck cloud placeholders of fsa-guard-tests: every row tallied as a code-363 leftover, none opened

C-007 and C-008 (a NothingListed row with code 1920, and a code-5 row whose folder opens) are rows F12 and F02 of
tests/window/W001OracleCorpus.json, run by W001OracleTests.TheOracleGivesEveryRowOfTheCorpusItsClass.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-107-HandOffCases.ps1 -Case C-001

Exit 0 when the case held, 1 when a check failed, 3 when it could not be judged (an elevated shell, or a folder this PC does
not have): never a pass.
#>
param(
    [Parameter(Mandatory)][ValidateSet('C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-006')][string] $Case
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$WindowCases = Join-Path $Repo 'tests\window\WindowCases.ps1'
$Seams = Join-Path $Repo 'docs\design\SL-089-seams.md'
$Doc = Join-Path $Repo 'docs\design\SL-107-c-drive-run.md'
$CRecord = Join-Path $Repo '.taad\slices\SL-089\c-drive-run.json'
$FolderRecord = Join-Path $Repo 'artifacts\w001-run.json'
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:Unjudged = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Host "  ok    $what" }
    else {
        Write-Host "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

function Unjudged([string] $why) {
    Write-Host "  ??    $why"
    $script:Unjudged.Add($why)
}

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]::new($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# What a path is, before and after a run: absent, or its hash. A record that is absent must stay absent, and one that is
# there (the person has committed theirs) must stay as it was.
function Get-State([string] $path) {
    if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { 'absent' }
}

# The text of one '## ' section of a markdown file, from its heading to the next.
function Get-Section([string] $file, [string] $heading) {
    $lines = [IO.File]::ReadAllLines($file)
    $start = [Array]::FindIndex($lines, [Predicate[string]] { param($l) $l -ceq $heading })
    if ($start -lt 0) { throw "$file has no '$heading'" }
    $end = $lines.Count
    for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i].StartsWith('## ')) { $end = $i; break } }
    ($lines[$start..($end - 1)]) -join "`n"
}

# W-001 over one root, in a child pwsh as the person would run it, with its console kept. Returns the output lines and the
# exit code, and the record the run wrote at artifacts\w001-run.json when it wrote one (it is overwritten by every run).
function Invoke-W001([string] $root) {
    $started = [DateTime]::UtcNow
    $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $WindowCases -Case W-001 -Root $root 2>&1 | ForEach-Object { "$_" })
    $exit = $LASTEXITCODE
    $record = $null
    if ((Test-Path -LiteralPath $FolderRecord) -and (Get-Item -LiteralPath $FolderRecord).LastWriteTimeUtc -ge $started.AddSeconds(-1)) {
        $record = Get-Content -LiteralPath $FolderRecord -Raw | ConvertFrom-Json
    }
    [pscustomobject]@{ Lines = $lines; Exit = $exit; Record = $record; Last = ($lines | Where-Object { $_.Trim() } | Select-Object -Last 1) }
}

function Test-C001 {
    $dir = Join-Path $Repo 'artifacts\sl107-cases'
    [void][IO.Directory]::CreateDirectory($dir)
    try {
        # First, what arrives: the same cmd.exe line against a script that only says what its -Root is. If it is not C:" the
        # run below could be a scan of the whole drive, so nothing is run.
        $echo = Join-Path $dir 'echo-root.ps1'
        [IO.File]::WriteAllText($echo, 'param($Root) [Console]::Out.WriteLine("ARRIVED[$Root]")')
        $probe = Join-Path $dir 'probe.cmd'
        [IO.File]::WriteAllText($probe, "@echo off`r`npwsh -NoProfile -ExecutionPolicy Bypass -File `"$echo`" -Root `"C:\`"`r`n")
        $arrived = @(& cmd.exe /c $probe 2>&1 | ForEach-Object { "$_" }) -join "`n"
        Check ($arrived.Contains('ARRIVED[C:"]')) "typed in cmd.exe, -Root `"C:\`" arrives as C:`" (saw: $($arrived.Trim()))"
        if (-not $arrived.Contains('ARRIVED[C:"]')) { return }

        $before = Get-State $CRecord
        $run = Join-Path $dir 'run.cmd'
        [IO.File]::WriteAllText($run, "@echo off`r`ncd /d `"$Repo`"`r`npwsh -NoProfile -ExecutionPolicy Bypass -File tests\window\WindowCases.ps1 -Case W-001 -Root `"C:\`"`r`nexit /b %ERRORLEVEL%`r`n")
        $output = @(& cmd.exe /c $run 2>&1 | ForEach-Object { "$_" })
        $exit = $LASTEXITCODE
        $text = $output -join "`n"
        CheckExit 1 $exit 'the run from cmd.exe exits 1'
        Check ($text.Contains('C:" is not a folder')) 'it says W-001: C:" is not a folder'
        Check ($text.Contains('FAILED: W-001')) 'it ends FAILED: W-001'
        Check (-not $text.Contains('PASSED')) 'it does not say PASSED anywhere'
        Check ((Get-State $CRecord) -ceq $before) "the whole-C:\ record is as it was before the run ($before)"
    }
    finally {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    }
}

function CheckExit([int] $want, $got, [string] $what) { Check ($got -eq $want) "$what (exit $got)" }

function Test-C002 {
    $section = Get-Section $Seams "## Criterion 3 on C:\ is a person's run"
    Check ($section.Contains('cmd.exe')) 'the seams doc warns about cmd.exe under "Criterion 3 on C:\ is a person''s run"'
    Check ($section.Contains('`-Root "C:\"` arrives as `C:"`')) 'it says what arrives'
    Check ($section.Contains('SL-107-c-drive-run.md')) 'it points at the SL-107 doc'
    Check ($section.Contains('-Case W-001 -Root C:\')) 'it still holds the command'
    Check (Test-Path -LiteralPath $Doc) 'the doc it points at exists'
}

function Test-C003 {
    $text = [IO.File]::ReadAllText($Doc)
    $musts = @(
        '-Case W-001 -Root C:\', 'Tee-Object', 'artifacts\c-drive-console.txt', 'PASSED: W-001', 'FAILED: W-001', 'INCONCLUSIVE: W-001',
        'TIMEOUT', 'LEAK:', 'openRefusedButOpenable', 'notScanned', 'leftoverRows',
        # review M1: the place to run and commit, and the folder the tee needs
        'New-Item -ItemType Directory -Force artifacts', "carries SL-107's merge", 'uat',
        # review M2: the floor measured in the same session
        'anchor-windows.json', 'anchor-programdata.json', 'anchor-temp.json', 'unmeasurable',
        # the guard figures: the reason is not rested on 0.5 GB, and the disagreement is recorded
        '0.5 GB', '3 GB', '537 MB', '3-4.4 GiB', 'under both',
        # Q-261, SC-136's correction, the precaution and its notice
        'Q-261', '27-28', 'notARefusedKeepShape', 'other-code', 'wcifs-layer-store', 'not scanned (1)', 'W001OracleCorpus.json',
        'tests/bugs/SL-107-RecordPathRule.Tests.ps1',
        # review R2-m2: the capture keeps a path outside the console's code page
        '[Console]::OutputEncoding = [Text.UTF8Encoding]::new()',
        # review m1: one rule for the code-363 count, the same-session anchor
        "equal to the same-session Temp anchor's code-363 count", "replaces Q-261's fixed 27-28 range for the code-363 count only"
    )
    foreach ($must in $musts) { Check ($text.Contains($must)) "the doc holds: $must" }
    $encoding = $text.IndexOf('[Console]::OutputEncoding = [Text.UTF8Encoding]::new()')
    $tee = $text.IndexOf('Tee-Object -FilePath artifacts')
    Check ($encoding -ge 0 -and $tee -ge 0 -and $encoding -lt $tee) 'the UTF-8 line comes before the teed command'
    Check (-not $text.Contains('code 363: **27-28**') -and -not $text.Contains('| rest / 27-28 /') -and -not $text.Contains('| 27-28 / 0 /')) 'no fixed 27-28 pass range for the code-363 count is left beside the anchor rule'
    $why = Get-Section $Doc '## Why this run is the person''s'
    Check ($why.Contains('3 GB') -and $why.Contains('0.5 GB') -and $why.Contains('disagree')) 'its reason section states that the 0.5 GB and 3 GB figures disagree'
    Check ($why.Contains('537 MB') -and $why.Contains('3-4.4 GiB') -and $why.Contains('restart')) 'and rests on the measured scan cost and on pool kept until a restart'
}

function Assert-W001Whole([string] $root, $expectScope) {
    if (Test-Elevated) { Unjudged 'this shell is elevated, and W-001 refuses to run elevated'; return $null }
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { Unjudged "$root does not exist on this PC"; return $null }
    $before = Get-State $CRecord
    $run = Invoke-W001 $root
    $run.Lines | Where-Object { $_ -match '^\s+(hook:|checker controls:|kernel pool:)|^\s+(ok|FAIL)\s' } | ForEach-Object { Write-Host "        $($_.Trim())" }
    CheckExit 0 $run.Exit 'W-001 exits 0'
    Check ($run.Last -ceq 'PASSED: W-001') "the last line printed is PASSED: W-001 (saw: $($run.Last))"
    Check (@($run.Lines | Where-Object { $_ -match 'record: .*artifacts\\w001-run\.json$' }).Count -eq 1) 'it names artifacts\w001-run.json as the record, not the C: record'
    Check ((Get-State $CRecord) -ceq $before) "the whole-C:\ record is as it was before the run ($before)"
    Check ($null -ne $run.Record) 'the run wrote artifacts\w001-run.json'
    if ($null -eq $run.Record) { return $null }
    $r = $run.Record
    Check ($r.scope -ceq $expectScope) "scope is $expectScope (saw $($r.scope))"
    Check ($r.verdict -ceq 'held') "verdict is held (saw $($r.verdict))"
    Check ($r.status -ceq 'Completed') "status is Completed (saw $($r.status))"
    Check ($r.rows -eq $r.header -and $r.header -eq $r.coreUnreadable) "rows $($r.rows) = header $($r.header) = coreUnreadable $($r.coreUnreadable)"
    Check ($r.oracle.openRefusedButOpenable -eq 0) 'no refused-then-listed folder is a row (openRefusedButOpenable 0)'
    Check ($r.emptyReasons -eq 0 -and $r.duplicates -eq 0) 'no empty reason, no folder twice'
    $r
}

function Test-C004 {
    $r = Assert-W001Whole 'D:\' 'whole-drive'
    if ($null -ne $r) { Write-Host "        D:\ is a whole volume: $($r.rows) rows, $($r.folders) folders, notScanned $($r.notScanned)" }
}

function Test-C005 {
    $r = Assert-W001Whole 'C:\ProgramData\Microsoft\Windows' 'folder'
    if ($null -ne $r) {
        Check ($r.notScanned -eq 1) "the container layer store is one notScanned folder, not rows (saw $($r.notScanned))"
        Check ($r.rows -gt 0) "the tree above the store has rows (saw $($r.rows)), so the header's N is read beside ', not scanned (1)'"
    }
}

function Test-C006 {
    $r = Assert-W001Whole (Join-Path $env:LOCALAPPDATA 'Temp\fsa-guard-tests') 'folder'
    if ($null -ne $r) {
        Check ($r.oracle.leftoverRows.code363 -eq $r.rows) "every row is tallied as a code-363 leftover ($($r.oracle.leftoverRows.code363) of $($r.rows))"
        Check ($r.rows -eq 27) "there are 27 of them, the count measured at bd6572f and quoted in the doc (saw $($r.rows))"
        Check ($r.oracle.leftoverRows.reparse -eq 0) 'no code-5 reparse leftover'
        Check (@($r.oracle.notARefusedKeepShape).Count -eq 0) 'no other code (1920 among them)'
        Check ($r.byKind.openedNothingListed -eq 0 -and $r.byKind.listedPartway -eq 0) 'none opened and failed, none listed partway'
        Check ($r.oracle.opened -eq 0) 'the oracle opened none of them'
    }
}

Write-Host $Case
& "Test-$($Case -replace '-', '')"
Write-Host ''
if ($script:Failures.Count) {
    Write-Host "FAILED: $Case ($($script:Failures.Count) check(s) failed)"
    exit 1
}
if ($script:Unjudged.Count) {
    Write-Host "INCONCLUSIVE: $Case ($($script:Unjudged.Count) check(s) could not be judged)"
    exit 3
}
Write-Host "PASSED: $Case"
exit 0
