<#
HF-026 (SL-076): tests/rules/RuleCases.ps1's Invoke-HarnessRaw removes the scratch copy a failed harness run
keeps, for the cases that expected the failure, only when it can read the path back and the folder is the
harness's own. It read the path with 'harness: scratch kept at (\S+)', which stops at the first space, and it
took only a folder under .NET's GetTempPath, which reads TMP first, where Python's tempfile.mkdtemp, which made
the folder, reads TEMP first. A %TEMP% with a space in it (a local account such as C:\Users\John Smith), or a
TEMP that is not TMP, left a full copy of the repo in %TEMP% for every expected-failure harness run.

Each shape runs RuleCases.ps1's own Invoke-HarnessRaw, taken from its source, in a TEMP and TMP of its own. The
engine's python is a stand-in that does what harness.py does on a failed run: it makes fsa-rules-* in %TEMP%,
as Python picks it, says 'harness: scratch kept at <it>' and exits 2. The Python shape runs the real harness.py
under the engine's interpreter instead, with an engine root that does not exist, so Python's own tempfile
picks the folder. -Shape All runs every shape but Python, which needs the engine and is asked for by name.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-076-ScratchKeptPath.Tests.ps1
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-076-ScratchKeptPath.Tests.ps1 -Shape Python `
      -EnginePython <the engine's python.exe>

Exit 0 when every check held, 1 when any failed.
#>

param(
    [ValidateSet('All', 'Space', 'TempNotTmp', 'Refused', 'Python')]
    [string] $Shape = 'All',
    [string] $EnginePython = ''
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$CasesRel = 'tests\rules\RuleCases.ps1'
$script:Failures = [System.Collections.Generic.List[string]]::new()
$Base = Join-Path ([System.IO.Path]::GetTempPath()) ('fsa-sl076-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
$Given = $EnginePython

# RuleCases.ps1's own Invoke-HarnessRaw, taken from its source rather than copied. It reads $EnginePython,
# $EngineRoot and $Harness from this script, which each shape sets.
$raw = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $Repo $CasesRel), [ref]$null, [ref]$null).Find({
        param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Invoke-HarnessRaw' }, $true)
if (-not $raw) { throw "test setup: no function Invoke-HarnessRaw in $CasesRel" }
. ([scriptblock]::Create($raw.Extent.Text))

# ---- Checks ----

function Check([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Output "  ok    $(($what -split "`n")[0])"
    }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

# ---- The stand-in ----

# A failed harness run as harness.py ends one: the scratch folder, made where FSA_SL076_KEEP says or else
# in %TEMP% as Python's tempfile picks it, holding a file, then the two lines it says and exit 2.
function New-StandIn {
    $dir = Join-Path $Base 'stand-in'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir 'python.cmd'
    [System.IO.File]::WriteAllText($path, (@(
                '@echo off',
                'set "work=%TEMP%\fsa-rules-sl076"',
                'if defined FSA_SL076_KEEP set "work=%FSA_SL076_KEEP%"',
                'mkdir "%work%"',
                'echo scratch> "%work%\README.md"',
                'echo harness: CANNOT MEASURE: a stand-in for harness.py',
                'echo harness: scratch kept at %work%',
                'echo harness: exit 2',
                'exit /b 2'
            ) -join "`r`n") + "`r`n", [System.Text.Encoding]::ASCII)
    $path
}

# One run of Invoke-HarnessRaw with TMP and TEMP set as given, and FSA_SL076_KEEP when $keep is.
function Invoke-Kept([string] $tmp, [string] $temp, [string] $keep = '') {
    foreach ($dir in @($tmp, $temp)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $saved = @{ TMP = $env:TMP; TEMP = $env:TEMP; FSA_SL076_KEEP = $env:FSA_SL076_KEEP }
    try {
        $env:TMP = $tmp
        $env:TEMP = $temp
        $env:FSA_SL076_KEEP = if ($keep) { $keep } else { $null }
        $sees = [System.IO.Path]::GetTempPath()
        $run = Invoke-HarnessRaw @('--engine-root', $EngineRoot)
    }
    finally {
        $env:TMP = $saved.TMP
        $env:TEMP = $saved.TEMP
        $env:FSA_SL076_KEEP = $saved.FSA_SL076_KEEP
    }
    $run | Add-Member -NotePropertyName TempPath -NotePropertyValue $sees -PassThru
}

function Test-Removed($run, [string] $dir, [string] $what) {
    Check ($run.Exit -eq 2) "$what exits 2`n          got exit $($run.Exit):`n$($run.Output)"
    Check ($run.Output.Contains("harness: scratch kept at $dir")) "$what says the scratch kept at $dir`n$($run.Output)"
    Check (-not (Test-Path -LiteralPath $dir)) "$what removes $dir`n$($run.Output)"
}

function Test-Left($run, [string] $dir, [string] $what) {
    Check ($run.Output.Contains("harness: scratch kept at $dir")) "$what says the scratch kept at $dir`n$($run.Output)"
    Check (Test-Path -LiteralPath (Join-Path $dir 'README.md')) "$what leaves $dir alone`n$($run.Output)"
}

# ---- The shapes ----

function Shape-Space {
    Write-Output 'Space: a %TEMP% under a local account with a space in its name, TMP the same folder'
    $temp = Join-Path $Base 'Users\John Smith\AppData\Local\Temp'
    $run = Invoke-Kept $temp $temp
    Test-Removed $run (Join-Path $temp 'fsa-rules-sl076') 'a run whose scratch path holds a space'
}

function Shape-TempNotTmp {
    Write-Output 'TempNotTmp: TMP and TEMP name different folders, and Python made the scratch in TEMP'
    $tmp = Join-Path $Base 'tmp-a'
    $temp = Join-Path $Base 'tmp-b'
    $run = Invoke-Kept $tmp $temp
    Check ($run.TempPath.TrimEnd('\') -ieq $tmp) "GetTempPath reads TMP first, so it named $tmp (got $($run.TempPath))"
    Test-Removed $run (Join-Path $temp 'fsa-rules-sl076') 'a run whose scratch is in TEMP, not TMP'
}

function Shape-Refused {
    Write-Output 'Refused: only a fsa-rules-* folder directly in TMP or TEMP is removed, whatever the line names'
    # No space in this TEMP, so each line is read whole by either pattern and only the guard decides.
    $temp = Join-Path $Base 'tmp-c'
    $cases = @(
        @('a fsa-rules-* folder in neither TMP nor TEMP', (Join-Path $Base 'elsewhere\fsa-rules-sl076')),
        @('a folder in TEMP that is not fsa-rules-*', (Join-Path $temp 'not-rules')),
        @('a fsa-rules-* folder below a folder in TEMP', (Join-Path $temp 'deeper\fsa-rules-sl076'))
    )
    foreach ($case in $cases) {
        $what, $keep = $case
        $run = Invoke-Kept $temp $temp $keep
        Test-Left $run $keep $what
    }
}

function Shape-Python {
    Write-Output 'Python: the real harness.py under the engine''s interpreter, TEMP with a space and not TMP'
    if (-not $Given) { Check $false 'the Python shape needs -EnginePython, the engine''s python.exe'; return }
    $script:EnginePython = $Given
    $script:Harness = Join-Path $Repo 'eng\rules-harness\harness.py'
    $script:EngineRoot = Join-Path $Base 'no-such-engine'
    $tmp = Join-Path $Base 'tmp-a'
    $temp = Join-Path $Base 'Users\John Smith\AppData\Local\Temp'
    $run = Invoke-Kept $tmp $temp
    $kept = [regex]::Match($run.Output, '(?m)^harness: scratch kept at (.+?)\s*$')
    Check ($run.Exit -eq 2 -and $run.Output.Contains("engine root $EngineRoot") -and $kept.Success) `
        "harness.py with no engine exits 2, names the engine root and keeps its scratch`n$($run.Output)"
    if ($kept.Success) {
        $dir = $kept.Groups[1].Value
        Check ((Split-Path -Parent $dir) -ieq $temp) "Python made its scratch in TEMP ($temp), not TMP: $dir"
        Check (-not (Test-Path -LiteralPath $dir)) "the scratch Python kept at $dir is removed`n$($run.Output)"
    }
    $left = @(Get-ChildItem -LiteralPath $temp, $tmp -Directory -Filter 'fsa-rules-*' | ForEach-Object FullName)
    Check ($left.Count -eq 0) "no fsa-rules-* is left in TMP or TEMP (left: $($left -join ', '))"
}

# ---- Main ----

$shapes = if ($Shape -eq 'All') { @('Space', 'TempNotTmp', 'Refused') } else { @($Shape) }
try {
    New-Item -ItemType Directory -Path $Base | Out-Null
    $EnginePython = New-StandIn
    $EngineRoot = Join-Path $Base 'no-such-engine'
    $Harness = Join-Path $Repo 'eng\rules-harness\harness.py'
    foreach ($s in $shapes) {
        try { & "Shape-$s" }
        catch { Check $false "$s stopped: $($_.Exception.Message)" }
    }
}
finally {
    Remove-Item -LiteralPath $Base -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
