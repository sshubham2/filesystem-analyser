<#
SL-003 (criterion 8): W-002 on its own rig. Runs tests/window/WindowCases.ps1 -Case W-002 -IncludeCloud, which registers a sync root
under %TEMP% served by tests/CfProvider's 'tree' scenario (dehydrated placeholders under cloud-tagged folders, one with a second hard-link
name), lists it, scans it with the app in report mode, lists it again and counts the provider's FETCH_DATA callbacks from the app's own
process, and unregisters the root again. This script reads the record the case wrote at docs/proof-runs/w002-rig.json and checks it:
files were online-only before the scan, none was hydrated by it, the provider logged no fetch from the app, and the keys that name
the online-only counts and the hydration are the ones the record promises, one each. It removes the record, however it ends.

It registers a real cldflt sync root on this PC (removed by the case at its end), so it is a person's or validate's to run, never
-Case All's, and it needs the interactive desktop the window cases need.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/SL-003-W002OnTheCloudRig.Tests.ps1

Exit 0 when every check held, 1 when any failed.
#>

$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$failures = [System.Collections.Generic.List[string]]::new()
function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $what" } else { Write-Output "  FAIL  $what"; $failures.Add($what) }
}

$record = Join-Path $Repo 'docs\proof-runs\w002-rig.json'
$recordsFolderExisted = Test-Path -LiteralPath (Join-Path $Repo 'docs\proof-runs')
try {
    $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'tests\window\WindowCases.ps1') -Case W-002 -IncludeCloud 2>&1 | ForEach-Object { [string]$_ })
    $exit = $LASTEXITCODE
    $lines | ForEach-Object { Write-Output "        $_" }
    Check ($exit -eq 0) "W-002 on the rig exits 0 (it exited $exit; 3 is inconclusive)"
    Check (Test-Path -LiteralPath $record) 'the record is at docs/proof-runs/w002-rig.json'
    if (Test-Path -LiteralPath $record) {
        $text = [System.IO.File]::ReadAllText($record)
        $r = $text | ConvertFrom-Json
        Check ($r.source -ceq 'rig' -and $r.verdict -ceq 'held') "the case held on the rig ($($r.source), $($r.verdict))"
        Check ($r.onlineOnlyBefore -gt 0) "files were online-only before the scan ($($r.onlineOnlyBefore))"
        Check ($r.hydrated -eq 0 -and $r.dataFetches -eq 0) "the scan hydrated none and the provider logged no fetch from the app ($($r.hydrated), $($r.dataFetches))"
        $keys = @($r.PSObject.Properties.Name)
        Check (@($keys | Where-Object { $_ -match 'online.?only.*before' }).Count -eq 1 -and @($keys | Where-Object { $_ -match 'online.?only.*after' }).Count -eq 1 -and @($keys | Where-Object { $_ -match 'hydrat' }).Count -eq 1) 'one key counts the online-only files before, one after, and one the hydrations'
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
