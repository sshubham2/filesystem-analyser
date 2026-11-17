<#
SL-002: the existing stall analysis shows no stall at startup. The scan T-005 starts at launch, on a window 644 px tall, is traced
from the process's start (FSA_T005_TRACE, EventPipe), and tests/window/StallStacks.ps1 reads that trace with no -Run, as a plain
T-005 summary has no run field. SL-080 read this very run as RENDER-PATH, about 92% in WPF's synchronous render, with the first
move holding the UI thread 334 to 500 ms; the analysis now has to say that no phase waited 250 ms (T005Batch.ps1's line).

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/SL-002-StartupStallStacks.Tests.ps1

It runs WindowCases.ps1 -Case T-005 twice, unmodified: the first run is a warm-up (the published exe and the hook are built, the
file cache is warm) and is not judged; the second is traced and its summary line is read. Their own exit codes are printed, not
judged: T-005 has bars of its own (max < 1000 ms, p95 < 200 ms), and this one asks what the analysis says of the run.
It checks that the run closed cleanly (exitReason 'closed'), that the scan phase was reached, that StallStacks read the trace
(a scan stretch with its wait placed on the trace's clock: an analysis that read nothing says nothing about a stall), that
stalled is false at the top level and on the scan, and that no stretch which did stall is RENDER-PATH. A scan stretch that says
CANNOT-READ because no busy stretch covers its wait stays allowed: an idle UI thread during the wait is itself no stall.

Exit 0 when every check held, 1 when any failed. Needs what T-005 needs: 100,000 files and 20,000 folders under %TEMP%, and
dotnet-trace, which StallStacks.ps1 installs into artifacts/tools through eng/dotnet.ps1 when it is missing. Everything it writes
is in one folder under %TEMP%\fsa-finish, removed at the end.
#>

$ErrorActionPreference = 'Stop'
$Cases = Join-Path $PSScriptRoot 'WindowCases.ps1'
$Stacks = Join-Path $PSScriptRoot 'StallStacks.ps1'
$script:Failures = New-Object System.Collections.Generic.List[string]

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $what" }
    else { Write-Output "  FAIL  $what"; $script:Failures.Add($what) }
}

# The checks are a function so that one the rest depend on can stop them with return: at the top of a script, return ends it with exit 0.
function Test-Startup($work) {
    $summaryPath = Join-Path $work 'runs.jsonl'
    $traceFolder = Join-Path $work 'traces'
    $stacksPath = Join-Path $work 'stacks.json'

    Write-Output '  warm-up run of T-005 (not judged)'
    $env:FSA_T005_HEIGHT = '644'
    Remove-Item Env:FSA_T005_SUMMARY, Env:FSA_T005_TRACE -ErrorAction SilentlyContinue
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $Cases -Case T-005 2>&1 | ForEach-Object { "      $_" }
    Write-Output "  warm-up: T-005 exit $LASTEXITCODE"

    Write-Output '  traced run of T-005'
    $env:FSA_T005_SUMMARY = $summaryPath
    $env:FSA_T005_TRACE = $traceFolder
    & pwsh -NoProfile -ExecutionPolicy Bypass -File $Cases -Case T-005 2>&1 | ForEach-Object { "      $_" }
    Write-Output "  traced run: T-005 exit $LASTEXITCODE"

    $lines = if (Test-Path -LiteralPath $summaryPath) { @(Get-Content -LiteralPath $summaryPath | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json -DateKind String }) } else { @() }
    Check ($lines.Count -eq 1) "the traced run wrote one summary line ($($lines.Count))"
    if ($lines.Count -ne 1) { return }
    $line = $lines[-1]
    Check ($line.exitReason -ceq 'closed') "the app was closed by the hook and exited 0 (exitReason $($line.exitReason))"
    Check ($null -eq $line.run) 'the summary line has no run field, as a plain T-005 writes it'
    $scan = @($line.phases | Where-Object { $_.name -ceq 'scan' }) | Select-Object -First 1
    Check ($null -ne $scan -and $scan.reached -eq $true) "the scan phase was reached ($(if ($null -ne $scan) { "longest wait $($scan.longestWaitMs) ms" } else { 'no scan phase' }))"
    Check ($line.traceFile -and (Test-Path -LiteralPath $line.traceFile -PathType Leaf)) "the run's trace was written ($($line.traceFile))"
    if (-not ($line.traceFile -and (Test-Path -LiteralPath $line.traceFile -PathType Leaf))) { return }

    & pwsh -NoProfile -ExecutionPolicy Bypass -File $Stacks -Trace $line.traceFile -Summary $summaryPath -Out $stacksPath | Out-Null
    Write-Output "  StallStacks (no -Run) exit $LASTEXITCODE"
    Check ((Test-Path -LiteralPath $stacksPath -PathType Leaf)) 'StallStacks wrote its reading'
    if (-not (Test-Path -LiteralPath $stacksPath -PathType Leaf)) { return }
    $stacks = Get-Content -LiteralPath $stacksPath -Raw | ConvertFrom-Json -Depth 64
    Write-Output "        verdict $($stacks.verdict): $($stacks.reason)"

    $scanStretch = @($stacks.stretches | Where-Object { $_.matchedPhase -ceq 'scan' }) | Select-Object -First 1
    Check ($null -ne $scanStretch -and $null -ne $scanStretch.windowStartMs) "StallStacks read the trace: the scan's wait is placed on its clock (window start $(if ($null -ne $scanStretch) { $scanStretch.windowStartMs } else { 'none' }) ms)"
    Check ($stacks.stalled -is [bool] -and $stacks.stalled -eq $false) "no phase stalled: stalled is false at the top level ($($stacks.stalled))"
    Check ($null -ne $scanStretch -and $scanStretch.stalled -is [bool] -and $scanStretch.stalled -eq $false) "the scan did not stall: stalled is false on its stretch ($(if ($null -ne $scanStretch) { "$($scanStretch.waitMs) ms, $($scanStretch.verdict)" } else { 'no scan stretch' }))"
    # SL-080's stall read RENDER-PATH. A wait under 250 ms made mostly of a hook move can read it too, which is no stall, so only a
    # stretch that stalled is held to it.
    foreach ($stretch in @($stacks.stretches)) {
        Check (-not ($stretch.stalled -eq $true -and $stretch.verdict -ceq 'RENDER-PATH')) "$($stretch.matchedPhase): no stalled stretch reads RENDER-PATH ($($stretch.verdict), stalled $($stretch.stalled))"
    }
    Check (-not ($stacks.stalled -eq $true -and $stacks.verdict -ceq 'RENDER-PATH')) "the run's verdict is not a stall in WPF's render ($($stacks.verdict))"
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-finish\{0}" -f [guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($work)
$saved = @{}
foreach ($name in 'FSA_T005_SUMMARY', 'FSA_T005_TRACE', 'FSA_T005_HEIGHT') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    Test-Startup $work
}
finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:Failures.Count -gt 0) {
    Write-Output "FAILED: $($script:Failures.Count) check(s)"
    exit 1
}
Write-Output 'PASSED: no stall at startup in the existing stall analysis'
exit 0
