<#
Bug (SL-062): O-030 is recorded as DISCHARGED in the build ledger, but no test has ever
fed its shape.

.taad/slices/SL-038/build-slice-2.json lists obligation O-030 ("done": true) for the
stalled-share baseline (StalledShareTests, Network=SMB-LOOPBACK) on the rig extracted into
Rig/SmbRig.cs. The very same record's own "how" text says: "It has NEVER run on the
extracted rig, by me or anyone. It is person-run under the contract and remains owed to
the person." Code review (SL-038 code-review-2.json, finding m1) and validate
(SL-038 validate-slice-4.json, O-038) both flagged this as a contradiction that was never
corrected before the slice merged.

Expected: an obligation whose own evidence says a test "has NEVER run" and "remains owed
to the person" must not also be recorded as done/discharged (done: false).

Actual: the ledger entry for O-030 has "done": true, so a record that admits the test was
never run is nonetheless marked as satisfied.
#>

$ErrorActionPreference = 'Stop'

function Find-RepoRoot {
    param([string]$Start)
    $dir = Get-Item -LiteralPath $Start
    while ($null -ne $dir) {
        if (Test-Path (Join-Path $dir.FullName 'FsAnalyser.slnx')) {
            return $dir.FullName
        }
        $dir = $dir.Parent
    }
    throw "No FsAnalyser.slnx found above $Start."
}

$repoRoot = Find-RepoRoot -Start $PSScriptRoot
$ledgerPath = Join-Path $repoRoot '.taad/slices/SL-038/build-slice-2.json'
if (-not (Test-Path $ledgerPath)) {
    throw "Ledger file not found: $ledgerPath"
}

$ledger = Get-Content -LiteralPath $ledgerPath -Raw | ConvertFrom-Json
$obligation = $ledger.payload.obligations | Where-Object { $_.key -eq 'O-030' }
if ($null -eq $obligation) {
    throw "No obligation with key O-030 found in $ledgerPath"
}

$neverRunAndOwed = ($obligation.how -match 'NEVER run') -and ($obligation.how -match 'remains owed to the person')
if (-not $neverRunAndOwed) {
    throw "O-030's evidence text no longer admits it never ran / remains owed to the person; test needs updating. how: $($obligation.how)"
}

if ($obligation.done -ne $false) {
    throw "O-030 is recorded as done=$($obligation.done) in $ledgerPath, but its own evidence says the test 'has NEVER run on the extracted rig, by me or anyone' and 'remains owed to the person'. An obligation admitted as never run must be recorded as owed (done: false), not discharged. how: $($obligation.how)"
}

Write-Host 'OK: O-030 is correctly recorded as owed to the person, not discharged.'
exit 0
