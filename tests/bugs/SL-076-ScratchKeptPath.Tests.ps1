<#
HF-026 (SL-076): tests/rules/RuleCases.ps1's Invoke-HarnessRaw removes the scratch copy a failed harness run
keeps, for the cases that expected the failure, only when it can find it again, and only a folder the harness
itself made directly in TMP or TEMP. It read the path with 'harness: scratch kept at (\S+)', which stops at the
first space, and it took only a folder under .NET's GetTempPath, which reads TMP first, where Python's
tempfile.mkdtemp, which made the folder, reads TEMP first. The first fix still compared the printed path, and
the harness prints it as TEMP spells it (an 8.3 short name, which GetFullPath expands on the other side: review
m1) and escaped to ASCII by say() (an account name with a non-ASCII letter: m2). Each of those left a full copy
of the repo in %TEMP% for every expected-failure harness run.

Each shape runs RuleCases.ps1's own Invoke-HarnessRaw, taken from its source, in a TEMP and TMP of its own. The
engine's python is a stand-in that does what harness.py does on a failed run: it makes fsa-rules-* in %TEMP%,
as Python picks it, says 'harness: scratch kept at <it>' escaped as say() escapes it, and exits 2. The Python
shape runs the real harness.py under the engine's interpreter instead, with an engine root that does not exist,
so Python's own tempfile picks the folder and say() prints it, for each of those spellings of TEMP. -Shape All
runs every shape but Python, which needs the engine and is asked for by name.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-076-ScratchKeptPath.Tests.ps1
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-076-ScratchKeptPath.Tests.ps1 -Shape Python `
      -EnginePython <the engine's python.exe>

Exit 0 when every check held, 1 when any failed.
#>

param(
    [ValidateSet('All', 'Space', 'TempNotTmp', 'ShortName', 'NonAscii', 'Refused', 'Python')]
    [string] $Shape = 'All',
    [string] $EnginePython = ''
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$CasesRel = 'tests\rules\RuleCases.ps1'
$script:Failures = [System.Collections.Generic.List[string]]::new()
$Base = Join-Path ([System.IO.Path]::GetTempPath()) ('fsa-sl076-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
$Given = $EnginePython
# An account name with a non-ASCII letter, built from [char] so the file stays ASCII under either shell.
$Jose = 'Jos' + [char]0xE9 + ' Smith'

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

# Text as harness.py's say() prints it: encode('ascii', 'backslashreplace'), so an e-acute is \xe9.
function Get-Said([string] $text) {
    $builder = [System.Text.StringBuilder]::new()
    foreach ($c in $text.ToCharArray()) {
        if ([int]$c -lt 128) { [void]$builder.Append($c) }
        elseif ([int]$c -lt 256) { [void]$builder.AppendFormat('\x{0:x2}', [int]$c) }
        else { [void]$builder.AppendFormat('\u{0:x4}', [int]$c) }
    }
    $builder.ToString()
}

# ---- The stand-in ----

# A failed harness run as harness.py ends one: the scratch folder, made where FSA_SL076_KEEP says or else
# in %TEMP% as Python's tempfile picks it and spelt as TEMP spells it, holding a file, then what say() prints
# and exit 2. python.cmd starts this host on it without a profile.
function New-StandIn {
    $dir = Join-Path $Base 'stand-in'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $dir 'harness.ps1'), (@(
                '$work = if ($env:FSA_SL076_KEEP) { $env:FSA_SL076_KEEP } else { $env:TEMP.TrimEnd(''\'') + ''\fsa-rules-sl076'' }',
                '[void][System.IO.Directory]::CreateDirectory($work)',
                '[System.IO.File]::WriteAllText($work + ''\README.md'', ''scratch'')',
                "function Get-Said { $(${function:Get-Said}) }",
                '[Console]::Out.WriteLine((Get-Said ''harness: CANNOT MEASURE: a stand-in for harness.py''))',
                '[Console]::Out.WriteLine((Get-Said "harness: scratch kept at $work"))',
                '[Console]::Out.WriteLine(''harness: exit 2'')',
                'exit 2'
            ) -join "`r`n") + "`r`n", [System.Text.Encoding]::ASCII)
    $path = Join-Path $dir 'python.cmd'
    [System.IO.File]::WriteAllText($path, (@(
                '@echo off',
                "`"$((Get-Process -Id $PID).Path)`" -NoProfile -ExecutionPolicy Bypass -File `"%~dp0harness.ps1`"",
                'exit /b %ERRORLEVEL%'
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

# The 8.3 spelling of a spaced folder, which Windows gives TEMP for such an account name. A volume with 8.3
# names turned off has none, and the shape then fails rather than feeding the long name.
function Get-ShortPath([string] $long) {
    New-Item -ItemType Directory -Force -Path $long | Out-Null
    (New-Object -ComObject Scripting.FileSystemObject).GetFolder($long).ShortPath
}

function Test-ShortPath([string] $long, [string] $short) {
    Check ($short -ne $long -and $short.Contains('~')) "the volume gives $long an 8.3 name (got $short)"
}

function Test-Removed($run, [string] $dir, [string] $what) {
    $said = Get-Said $dir
    Check ($run.Exit -eq 2) "$what exits 2`n          got exit $($run.Exit):`n$($run.Output)"
    Check ($run.Output.Contains("harness: scratch kept at $said")) "$what says the scratch kept at $said`n$($run.Output)"
    Check (-not (Test-Path -LiteralPath $dir)) "$what removes $dir`n$($run.Output)"
}

function Test-Left($run, [string] $dir, [string] $what) {
    Check ($run.Output.Contains("harness: scratch kept at $(Get-Said $dir)")) "$what says the scratch kept at $dir`n$($run.Output)"
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

function Shape-ShortName {
    Write-Output 'ShortName: TMP and TEMP the 8.3 short name of a spaced account''s folder, as Windows sets them (review m1)'
    $long = Join-Path $Base 'Users\John Smith\AppData\Local\Temp'
    $short = Get-ShortPath $long
    Test-ShortPath $long $short
    $run = Invoke-Kept $short $short
    Test-Removed $run (Join-Path $short 'fsa-rules-sl076') 'a run whose scratch path is an 8.3 short name'
}

function Shape-NonAscii {
    Write-Output 'NonAscii: TMP and TEMP under an account whose name has a non-ASCII letter, which say() escapes (review m2)'
    $temp = Join-Path $Base "Users\$Jose\AppData\Local\Temp"
    $run = Invoke-Kept $temp $temp
    Test-Removed $run (Join-Path $temp 'fsa-rules-sl076') 'a run whose scratch path is printed as \x escapes'
}

function Shape-Refused {
    Write-Output 'Refused: only a fsa-rules-* folder directly in TMP or TEMP is removed, whatever the line names'
    # No space in this TEMP, so each line is read whole by either pattern and only the guard decides.
    $temp = Join-Path $Base 'tmp-c'
    $cases = @(
        @('a fsa-rules-* folder in neither TMP nor TEMP', (Join-Path $Base 'elsewhere\fsa-rules-sl076')),
        @('a folder in TEMP that is not fsa-rules-*', (Join-Path $temp 'not-rules')),
        @('a fsa-rules-* folder in TEMP whose name mkdtemp never makes', (Join-Path $temp 'fsa-rules-sl076 copy')),
        @('a fsa-rules-* folder below a folder in TEMP', (Join-Path $temp 'deeper\fsa-rules-sl076'))
    )
    foreach ($case in $cases) {
        $what, $keep = $case
        $run = Invoke-Kept $temp $temp $keep
        Test-Left $run $keep $what
    }
}

function Shape-Python {
    Write-Output 'Python: the real harness.py under the engine''s interpreter, for each spelling of TEMP'
    if (-not $Given) { Check $false 'the Python shape needs -EnginePython, the engine''s python.exe'; return }
    $script:EnginePython = $Given
    $script:Harness = Join-Path $Repo 'eng\rules-harness\harness.py'
    $script:EngineRoot = Join-Path $Base 'no-such-engine'
    $spaced = Join-Path $Base 'Users\John Smith\AppData\Local\Temp-p'
    $long = Join-Path $Base 'Users\John Smith\AppData\Local\Temp-q'
    $short = Get-ShortPath $long
    Test-ShortPath $long $short
    $jose = Join-Path $Base "Users\$Jose\AppData\Local\Temp-r"
    $spellings = @(
        @('TEMP with a space, TMP another folder', (Join-Path $Base 'tmp-p'), $spaced),
        @('TMP and TEMP an 8.3 short name', $short, $short),
        @('TMP and TEMP under a non-ASCII account name', $jose, $jose)
    )
    foreach ($spelling in $spellings) {
        $what, $tmp, $temp = $spelling
        $run = Invoke-Kept $tmp $temp
        $said = "harness: scratch kept at $(Get-Said $temp.TrimEnd('\'))\fsa-rules-"
        Check ($run.Exit -eq 2 -and $run.Output.Contains("engine root $(Get-Said $EngineRoot)") -and $run.Output.Contains($said)) `
            "$what`: harness.py with no engine exits 2 and says its scratch is in TEMP, as '$said'`n$($run.Output)"
        $left = @(Get-ChildItem -LiteralPath (@($temp, $tmp) | Select-Object -Unique) -Directory -Filter 'fsa-rules-*' | ForEach-Object FullName)
        Check ($left.Count -eq 0) "$what`: no fsa-rules-* is left in TMP or TEMP (left: $($left -join ', '))"
    }
}

# ---- Main ----

$shapes = if ($Shape -eq 'All') { @('Space', 'TempNotTmp', 'ShortName', 'NonAscii', 'Refused') } else { @($Shape) }
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
