<#
SL-099 review M5: tests/window/PoolWatchCases.ps1 under the engine's TEMP. The engine's shells carry TEMP=TMP=C:\WINDOWS\TEMP,
where this user may create folders but not list them, so a fixture rooted there cannot be cleaned up: Remove-Item on it is a
terminating error that -ErrorAction SilentlyContinue does not suppress, and what was left needs an administrator to find.
PoolWatchCases roots its fixtures through Get-ScratchParent (the temp folder, unless that is under the Windows folder, then
LocalAppData\Temp). This holds it to that, in child processes whose TEMP and TMP are set here (never through a quoted -Command,
where the outer shell would expand them first):

  1. First, before any case runs, Get-ScratchParent as PoolWatchCases.ps1 defines it (read through the parser, not copied) is
     called under TEMP=C:\WINDOWS\TEMP and must answer a folder outside the Windows folder. A script whose answer is the Windows
     TEMP stops here, having created nothing there.
  2. Then PoolWatchCases -Case P-003 and -Case P-004 run under that TEMP. Each exits 0 with its PASSED line, prints
     'scratch: <root> (TEMP <the temp folder>)' with the TEMP it was given and a root under LocalAppData\Temp, and leaves no
     fixture folder behind there.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-099-CasesUnderEngineTemp.Tests.ps1

Exit 0 when every check held, 1 when any failed. About 15 s.
#>

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Cases = Join-Path $Repo 'tests\window\PoolWatchCases.ps1'
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $(($what -split "`n")[0])" }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

$windows = [Environment]::GetFolderPath('Windows')
$local = [Environment]::GetFolderPath('LocalApplicationData')
$asked = Join-Path $windows 'TEMP'
$shell = (Get-Process -Id $PID).Path

# Runs a script file in a child process with TEMP and TMP set to $asked, and answers its exit code and output lines.
function Invoke-UnderTemp([string[]] $arguments) {
    $saved = @{ TEMP = $env:TEMP; TMP = $env:TMP }
    $ErrorActionPreference = 'Continue'
    try {
        $env:TEMP = $asked; $env:TMP = $asked
        $out = @(& $shell -NoProfile -ExecutionPolicy Bypass @arguments 2>&1 | ForEach-Object { "$_" })
        [pscustomobject]@{ Exit = $LASTEXITCODE; Lines = $out }
    }
    finally {
        $ErrorActionPreference = 'Stop'
        $env:TEMP = $saved.TEMP; $env:TMP = $saved.TMP
    }
}

function Test-Outside([string] $path) {
    $full = [System.IO.Path]::GetFullPath($path).TrimEnd('\') + '\'
    -not $full.StartsWith($windows.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

Write-Output "PoolWatchCases.ps1 with TEMP and TMP set to $asked"

# 1. The function alone.
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Cases, [ref]$tokens, [ref]$errors)
$fn = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Get-ScratchParent' }, $true))
Check ($fn.Count -eq 1) 'PoolWatchCases.ps1 defines Get-ScratchParent once'
$probe = Join-Path ([System.IO.Path]::Combine($local, 'Temp')) ('fsa-sl099-probe-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.ps1')
$answer = $null
if ($fn.Count -eq 1) {
    try {
        Set-Content -LiteralPath $probe -Value ($fn[0].Extent.Text + "`nWrite-Output ('answer: ' + [System.IO.Path]::GetTempPath() + '|' + (Get-ScratchParent))") -Encoding UTF8
        $r = Invoke-UnderTemp @('-File', $probe)
        $line = @($r.Lines | Where-Object { $_ -like 'answer: *' }) | Select-Object -First 1
        if ($line) { $parts = $line.Substring('answer: '.Length) -split '\|'; $answer = $parts[1]; Check ($parts[0].TrimEnd('\') -ieq $asked) "the child saw TEMP $($parts[0])" }
    }
    finally { Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue }
}
Check ($answer -and (Test-Outside $answer)) "under TEMP=$asked Get-ScratchParent answers a folder outside the Windows folder ($answer)"

# 2. The cases, only when the function held: a script that roots in the Windows TEMP must not be run there.
if ($answer -and (Test-Outside $answer)) {
    $started = Get-Date
    foreach ($case in 'P-003', 'P-004') {
        Write-Output $case
        $r = Invoke-UnderTemp @('-File', $Cases, '-Case', $case)
        Check ($r.Exit -eq 0) "$case exits 0 under that TEMP (exit $($r.Exit))"
        Check (@($r.Lines | Where-Object { $_ -ceq "PASSED: $case" }).Count -eq 1) "$case prints its PASSED line"
        Check (@($r.Lines | Where-Object { $_ -match '^  FAIL ' }).Count -eq 0) "$case prints no FAIL line (access denied reads as one)"
        $scratch = @($r.Lines | Where-Object { $_ -like '  scratch: *' })
        Check ($scratch.Count -eq 1) "$case prints one 'scratch:' line ($($scratch -join ' | '))"
        if ($scratch.Count -eq 1 -and $scratch[0] -match '^  scratch: (.+) \(TEMP (.+)\)$') {
            Check ($Matches[2].TrimEnd('\') -ieq $asked) "$case saw the TEMP it was given ($($Matches[2]))"
            Check (Test-Outside $Matches[1]) "$case roots its fixtures outside the Windows folder ($($Matches[1]))"
        }
    }
    $left = @(Get-ChildItem -LiteralPath (Join-Path ([System.IO.Path]::Combine($local, 'Temp')) 'fsa-poolwatch') -Directory -Filter 'case-*' -ErrorAction SilentlyContinue | Where-Object { $_.CreationTime -ge $started })
    Check ($left.Count -eq 0) "no fixture folder made by these runs is left in LocalAppData\Temp ($(@($left | ForEach-Object Name) -join ', '))"
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
