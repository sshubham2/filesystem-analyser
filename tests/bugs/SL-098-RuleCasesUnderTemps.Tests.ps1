<#
SL-098: RuleCases.ps1 under the engine's TEMP and under an ordinary one. The engine's shells carry TEMP=TMP=C:\WINDOWS\TEMP, where
this user may create folders but not list them, so every Remove-Item of a scratch folder there was a terminating error: six
harness cases (C-006, C-007, C-008, C-009, C-012, C-014) stopped with 'Access is denied.' and the finally ended the script before its
summary line, on parity-only cases too (C-002 to C-005). This runs the real script in a child process whose TEMP and TMP are set
here (never through a quoted -Command, where the outer shell would expand them first), and holds it to what the design says:

  - it exits 0 with 'All checks held.', and no line says 'Access is denied', 'stopped:', 'cleanup:' or FAIL;
  - it prints where its scratch root is and which TEMP it saw ('scratch: <root> (TEMP <the temp folder>)'), and that TEMP is the one
    asked for, so a run under the wrong TEMP fails here instead of passing there;
  - the root is in the user's own LocalAppData\Temp, outside the Windows folder, under either TEMP, and is gone afterwards;
  - -Case C-001, which makes no scratch, prints no 'scratch:' line.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-098-RuleCasesUnderTemps.Tests.ps1 -Temp Engine
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-098-RuleCasesUnderTemps.Tests.ps1 -Temp Ordinary
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-098-RuleCasesUnderTemps.Tests.ps1 -Temp Engine -Case C-002

-Case All (the default) needs the engine: -EnginePython (default %APPDATA%\taad3\venv\Scripts\python.exe) and -EngineRoot (default
the taad_v3\backend checkout beside the projects folder); C-001 to C-005 need neither. About 4 minutes for All. An engine that is not
there is a failure, never a skip. Exit 0 when every check held, 1 when any failed.
#>

param(
    [ValidateSet('Engine', 'Ordinary')]
    [string] $Temp = 'Engine',
    [ValidateSet('All', 'C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-006', 'C-007', 'C-008', 'C-009', 'C-010', 'C-011', 'C-012', 'C-013', 'C-014')]
    [string] $Case = 'All',
    [string] $EnginePython = '',
    [string] $EngineRoot = ''
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Cases = Join-Path $Repo 'tests\rules\RuleCases.ps1'
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Output "  ok    $(($what -split "`n")[0])"
    }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

$windows = [Environment]::GetFolderPath('Windows')
$local = [Environment]::GetFolderPath('LocalApplicationData')
$asked = if ($Temp -eq 'Engine') { Join-Path $windows 'TEMP' } else { Join-Path $local 'Temp' }
$needsEngine = $Case -eq 'All' -or [int]$Case.Substring(2) -ge 6
if (-not $EnginePython) { $EnginePython = Join-Path $env:APPDATA 'taad3\venv\Scripts\python.exe' }
if (-not $EngineRoot) { $EngineRoot = [System.IO.Path]::GetFullPath((Join-Path $Repo '..\..\taad_v3\backend')) }

Write-Output "RuleCases.ps1 -Case $Case with TEMP and TMP set to $asked"
if ($needsEngine -and -not ([System.IO.File]::Exists($EnginePython) -and [System.IO.Directory]::Exists($EngineRoot))) {
    Check $false "the harness cases need the engine, and it is not at -EnginePython $EnginePython / -EngineRoot $EngineRoot"
}
else {
    $shell = (Get-Process -Id $PID).Path
    $saved = @{ TEMP = $env:TEMP; TMP = $env:TMP }
    $ErrorActionPreference = 'Continue'
    try {
        $env:TEMP = $asked
        $env:TMP = $asked
        $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $Cases -Case $Case -EnginePython $EnginePython -EngineRoot $EngineRoot 2>&1 | Out-String
        $exit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = 'Stop'
        $env:TEMP = $saved.TEMP
        $env:TMP = $saved.TMP
    }
    $bad = ((($output -split "`r?`n") | Where-Object { $_ -match 'Access is denied|stopped:|cleanup:|^\s+FAIL\b|Win32Exception' } | Select-Object -First 8) -join "`n")
    Check ($exit -eq 0) "it exits 0 (got $exit)`n$bad`n$(($output -split "`r?`n" | Select-Object -Last 6) -join "`n")"
    Check ($output -match '(?m)^All checks held\.\s*$') 'it reaches its summary line and says All checks held.'
    Check ([string]::IsNullOrEmpty($bad)) "no line says Access is denied, stopped:, cleanup: or FAIL`n$bad"

    $line = [regex]::Match($output, '(?m)^scratch: (?<root>.+) \(TEMP (?<temp>.+)\)\s*$')
    if ($Case -eq 'C-001') {
        Check (-not $line.Success) 'C-001 makes no scratch, so it prints no scratch: line'
    }
    else {
        Check $line.Success "it prints where its scratch root is and which TEMP it saw`n$(($output -split "`r?`n" | Select-Object -First 6) -join "`n")"
        if ($line.Success) {
            $root = $line.Groups['root'].Value
            $saw = $line.Groups['temp'].Value
            Check ($saw.TrimEnd('\') -ieq $asked.TrimEnd('\')) "the TEMP it saw is the one asked for: $asked (it saw $saw)"
            Check ((Split-Path -Leaf $root) -match '^fsa-rulecases-[0-9a-f]{10}$') "the root is fsa-rulecases-<10 hex> (got $root)"
            Check ((Split-Path -Parent $root) -ieq (Join-Path $local 'Temp')) "the root is directly in $(Join-Path $local 'Temp') (got $(Split-Path -Parent $root))"
            Check (-not ($root + '\').StartsWith($windows.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) "the root is outside the Windows folder $windows"
            Check (-not [System.IO.Directory]::Exists($root)) "the root is gone afterwards ($root)"
        }
    }
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
