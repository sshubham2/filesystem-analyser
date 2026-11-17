<#
SL-003 (criterion 9): W-003 on its loopback rig. Runs tests/window/WindowCases.ps1 -Case W-003 -IncludeNetwork, which serves the stop-seam tree
from tests/window/StallRig through a non-persistent SMB mapping on 127.0.0.1, holds s's listing, presses Stop 2 s after the hold, kills the
server 8 s after it and times Stop to the stopped ending on the hook's one clock. This script reads the record the case wrote at
docs/proof-runs/w003-rig.json and checks it: the time is above 0, no row is a folder Stop cut short (s, t, u or code 995), the labels
are the rig's, and the record holds no path. It removes the record, however it ends.

It changes this machine's SMB client mappings while it runs (the case removes its own mapping at its end), so it is a person's or
validate's to run, never -Case All's, and it needs the interactive desktop the window cases need.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/SL-003-W003OnTheStallRig.Tests.ps1

Exit 0 when every check held, 1 when any failed.
#>

$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$failures = [System.Collections.Generic.List[string]]::new()
function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $what" } else { Write-Output "  FAIL  $what"; $failures.Add($what) }
}

$record = Join-Path $Repo 'docs\proof-runs\w003-rig.json'
$recordsFolderExisted = Test-Path -LiteralPath (Join-Path $Repo 'docs\proof-runs')
try {
    $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'tests\window\WindowCases.ps1') -Case W-003 -IncludeNetwork 2>&1 | ForEach-Object { [string]$_ })
    $exit = $LASTEXITCODE
    $lines | ForEach-Object { Write-Output "        $_" }
    Check ($exit -eq 0) "W-003 on the rig exits 0 (it exited $exit; 3 is inconclusive)"
    Check (Test-Path -LiteralPath $record) 'the record is at docs/proof-runs/w003-rig.json'
    if (Test-Path -LiteralPath $record) {
        $text = [System.IO.File]::ReadAllText($record)
        $r = $text | ConvertFrom-Json
        Check ($r.network -ceq 'rig' -and $r.link -ceq 'stall' -and $r.verdict -ceq 'held') "the case held on the rig ($($r.network), $($r.link), $($r.verdict))"
        Check ($null -ne $r.stopToEndingMs -and $r.stopToEndingMs -gt 0) "Stop to the stopped ending was timed ($($r.stopToEndingMs) ms)"
        Check ($r.cutFolderRows -eq 0 -and $r.status -ceq 'Cancelled') "no folder Stop cut short is a row, and the scan was Cancelled ($($r.cutFolderRows), $($r.status))"
        Check ($text -notmatch '/' -and $text -notmatch '\\(?!["u])' -and $text -notmatch '"path"\s*:') 'the record holds no path'
    }
}
finally {
    if (Test-Path -LiteralPath $record) { Remove-Item -LiteralPath $record -Force }
    $folder = Join-Path $Repo 'docs\proof-runs'
    if (-not $recordsFolderExisted -and (Test-Path -LiteralPath $folder) -and -not (Get-ChildItem -LiteralPath $folder -Force)) { Remove-Item -LiteralPath $folder -Force }
}

Write-Output ''
if ($failures.Count) { Write-Output "$($failures.Count) check(s) failed."; exit 1 }
Write-Output 'All checks held.'
exit 0
