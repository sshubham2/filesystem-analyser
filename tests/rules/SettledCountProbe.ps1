# The settled count that RuleCases C-001 and C-003 expect (SL-053). Each shape copies the four files those
# cases read into %TEMP%, changes one thing there, runs that copy's RuleCases.ps1 under both Windows
# PowerShell 5.1 and pwsh 7, and checks its exit code and what it said:
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/rules/SettledCountProbe.ps1 -Shape AppendTwo
#
# A yaml that grows the way the engine grows it must keep C-001 green, and a parity summary line that is
# wrong in any part must turn it red, so an expected count read back from the tool, or counted per
# '# settled by' marker, fails here. GuardRule applies the fitness rule that forbids a pinned count to the
# RuleCases.ps1 this slice started from and to the one in this tree. Exit 0 when every check held, 1 when
# any failed.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('All', 'AppendOne', 'AppendTwo', 'CountPlusOne', 'NotAgree', 'Misclassified', 'C003Base',
        'OracleUnavailable', 'BothShells', 'GuardRule')]
    [string] $Shape
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$CasesRel = 'tests\rules\RuleCases.ps1'
$ParityRel = 'eng\rules-parity.ps1'
$YamlRel = '.taad\fitness.yaml'
$CsRel = 'tests\FsAnalyser.Core.Tests\ArchitectureRulesTests.cs'
$Copied = @($CasesRel, $ParityRel, $YamlRel, $CsRel)
# The tree SL-053 was cut from, whose RuleCases.ps1 pinned the count at :205 and :268.
$PreFix = '0647ead17aa99f2aace803624966ca4b1edab65b'
$GuardName = 'the rule-case suite never pins a literal settled-entry count'
$Utf8 = [System.Text.UTF8Encoding]::new($false)
$Shells = @('powershell.exe', 'pwsh')
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:Temps = [System.Collections.Generic.List[string]]::new()
$script:Counts = $null

# RuleCases.ps1's own Get-RuleCounts (SL-069), taken from its source rather than copied, so the line this
# probe expects carries this tree's Facts, twinned entries and patterns as C-001 recounts them.
$cases = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $Repo $CasesRel), [ref]$null, [ref]$null)
# Also its Get-ScratchParent and Remove-ScratchDir (SL-098): under the engine's TEMP (C:\WINDOWS\TEMP) a folder made directly in
# it cannot be listed by this user, and the cmdlet that removes folders is denied there, so the copies go to the parent
# RuleCases.ps1 picks and are removed the way it removes its own.
foreach ($name in 'Get-RuleCounts', 'Get-ScratchParent', 'Remove-ScratchDir') {
    $found = $cases.Find({
            param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true)
    if (-not $found) { throw "probe setup: no function $name in $CasesRel" }
    . ([scriptblock]::Create($found.Extent.Text))
}

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

function CheckRun($run, [int] $exit, [string] $what, [string[]] $contains = @()) {
    Check ($run.Exit -eq $exit) "$what exits $exit`n          got exit $($run.Exit):`n$($run.Output)"
    foreach ($text in $contains) {
        Check ($run.Output.Contains($text)) "$what says '$text'`n$($run.Output)"
    }
}

# ---- The copy ----

function New-Scratch {
    $root = Join-Path (Get-ScratchParent) ('fsa-settled-probe-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
    New-Item -ItemType Directory -Path $root | Out-Null
    $script:Temps.Add($root)
    foreach ($rel in $Copied) {
        $target = Join-Path $root $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Repo $rel) -Destination $target
    }
    $root
}

function Edit-Copy([string] $root, [string] $rel, [string] $old, [string] $new) {
    $path = Join-Path $root $rel
    $text = [System.IO.File]::ReadAllText($path, $Utf8)
    $at = $text.IndexOf($old, [StringComparison]::Ordinal)
    if ($at -lt 0) { throw "probe setup: '$old' is not in $rel" }
    [System.IO.File]::WriteAllText($path, $text.Substring(0, $at) + $new + $text.Substring($at + $old.Length), $Utf8)
}

# Settled entries appended under one marker, in the shape the engine appends them.
function Add-Settled([string] $root, [int] $count) {
    $entries = "`r`n  # settled by SL-998`r`n"
    for ($k = 1; $k -le $count; $k++) {
        $entries += "  - name: probe rule $k`r`n    files: src/**/*.cs`r`n    must_not_match:`r`n    - ProbeThing$k`r`n    because: probe`r`n"
    }
    [System.IO.File]::AppendAllText((Join-Path $root $YamlRel), $entries, $Utf8)
}

# The yaml cut after its first $keepSettled settled entries; 0 cuts it just after the first settled marker,
# which stays, since a yaml with no marker is one RuleCases cannot part into twinned and settled (SL-069).
function Limit-Yaml([string] $root, [int] $keepSettled) {
    $path = Join-Path $root $YamlRel
    $lines = [System.IO.File]::ReadAllLines($path, $Utf8)
    $marker = -1
    $seen = 0
    $cut = $lines.Length
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($marker -lt 0) {
            if ($lines[$i] -cmatch '^  # settled by SL-') { $marker = $i; if ($keepSettled -eq 0) { $cut = $i + 1; break } }
            continue
        }
        if ($lines[$i] -cmatch '^  - name:') {
            if ($seen -eq $keepSettled) { $cut = $i; break }
            $seen++
        }
    }
    if ($marker -lt 0) { throw 'probe setup: no settled marker in the yaml' }
    [System.IO.File]::WriteAllText($path, (($lines[0..($cut - 1)] -join "`r`n") + "`r`n"), $Utf8)
}

# ---- Running ----

function Invoke-Cases([string] $shell, [string] $root, [string] $case) {
    $ErrorActionPreference = 'Continue'
    $output = & $shell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root $CasesRel) -Case $case 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Output = $output.Trim() }
}

# What the parity tool says is settled in a copy nobody has changed yet: the base a shape grows from.
function Get-ToolSettled([string] $root) {
    $ErrorActionPreference = 'Continue'
    $output = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root $ParityRel) -Root $root 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $m = [regex]::Match($output, '; (\d+) settled entries not compared')
    if ($exit -ne 0 -or -not $m.Success) { throw "probe setup: the parity tool did not AGREE on the unchanged copy (exit $exit): $output" }
    [int] $m.Groups[1].Value
}

# The AGREE line of a copy of this tree with $settled settled entries, its twinned entries moved by $dTwinned.
function Get-Line([int] $settled, [int] $dTwinned = 0) {
    $counts = Get-RuleCounts
    "rules-parity: AGREE $($counts.Facts) facts <- $($counts.Twinned + $dTwinned) twinned entries, $($counts.Patterns) patterns; " +
    "$settled settled entries not compared"
}

# One copy, one change, C-001 under each shell.
function Test-C001([string] $what, [scriptblock] $change, [int] $exit, [scriptblock] $says) {
    foreach ($shell in $Shells) {
        $root = New-Scratch
        $base = Get-ToolSettled $root
        & $change $root
        CheckRun (Invoke-Cases $shell $root 'C-001') $exit "C-001 on $what [$shell]" @(& $says $base)
    }
}

# The parity tool's summary line, as eng/rules-parity.ps1 prints it on AGREE.
$VerdictText = '"rules-parity: AGREE $($facts.Count) facts <- '
$TwinnedText = '$($entries.Count) twinned entries, $patternCount patterns; " +'
$SettledText = '"$($yaml.Settled) settled entries not compared'

# ---- The shapes ----

function Shape-AppendOne {
    Write-Output 'AppendOne: one settled entry appended below the marker, as the engine appends: C-001 stays green'
    Test-C001 'a yaml with one more settled entry' { param($root) Add-Settled $root 1 } 0 { param($base) (Get-Line ($base + 1)) }
}

function Shape-AppendTwo {
    Write-Output 'AppendTwo: two settled entries under one marker, as one slice appends them: C-001 stays green'
    Test-C001 'a yaml with two more settled entries under one marker' { param($root) Add-Settled $root 2 } 0 { param($base) (Get-Line ($base + 2)) }
}

function Shape-CountPlusOne {
    Write-Output 'CountPlusOne: the parity tool prints one settled entry more than the yaml holds: C-001 fails'
    Test-C001 'a tool that prints settled+1' {
        param($root)
        Edit-Copy $root $ParityRel $SettledText '"$($yaml.Settled + 1) settled entries not compared'
    } 1 { param($base) (Get-Line ($base + 1)), 'FAIL  the repo', '4 check(s) failed.' }
}

function Shape-NotAgree {
    Write-Output 'NotAgree: the parity tool prints another verdict with the same counts: C-001 fails'
    Test-C001 'a tool that prints DRIFT on its AGREE line' {
        param($root)
        Edit-Copy $root $ParityRel $VerdictText '"rules-parity: DRIFT $($facts.Count) facts <- '
    } 1 { param($base) (Get-Line $base).Replace('AGREE', 'DRIFT'), 'FAIL  the repo', '4 check(s) failed.' }
}

function Shape-Misclassified {
    Write-Output 'Misclassified: the parity tool counts one twinned entry as settled (twinned-1, settled+1): C-001 fails'
    Test-C001 'a tool that moves one twinned entry to settled' {
        param($root)
        Edit-Copy $root $ParityRel $TwinnedText '$($entries.Count - 1) twinned entries, $patternCount patterns; " +'
        Edit-Copy $root $ParityRel $SettledText '"$($yaml.Settled + 1) settled entries not compared'
    } 1 { param($base) (Get-Line ($base + 1) -1), 'FAIL  the repo', '4 check(s) failed.' }
}

function Shape-C003Base {
    Write-Output 'C003Base: C-003 appends one entry to its own copy and expects this tree''s count plus one'
    $appended = 'the same entry appended below the marker'
    foreach ($shell in $Shells) {
        $root = New-Scratch
        $base = Get-ToolSettled $root
        CheckRun (Invoke-Cases $shell $root 'C-003') 0 "C-003 on an unchanged copy [$shell]" @((Get-Line ($base + 1)))

        # The tool does not move with the appended entry, or moves by two: C-003 must fail on its append only.
        foreach ($shift in @(@('- 1', 'a tool that does not move'), @('+ 1', 'a tool that moves by two'))) {
            $root = New-Scratch
            Edit-Copy $root $ParityRel $SettledText ('"$($yaml.Settled ' + $shift[0] + ') settled entries not compared')
            CheckRun (Invoke-Cases $shell $root 'C-003') 1 "C-003 on $($shift[1]) [$shell]" @("FAIL  $appended", '2 check(s) failed.')
        }
    }
}

function Shape-OracleUnavailable {
    Write-Output 'OracleUnavailable: a yaml the count cannot come from fails C-001 by name, and a case that does not ask still runs'
    foreach ($shell in $Shells) {
        $root = New-Scratch
        Remove-Item -LiteralPath (Join-Path $root $YamlRel)
        CheckRun (Invoke-Cases $shell $root 'C-001') 1 "C-001 with no yaml [$shell]" @(
            'C-001 the clean tree', 'C-001 stopped: ExpectedSettled: fitness rule count unavailable')

        # Only the twinned entries: the count would be 0, which the oracle refuses. C-002 never asks for it.
        $root = New-Scratch
        Limit-Yaml $root 0
        CheckRun (Invoke-Cases $shell $root 'C-001') 1 "C-001 on a yaml with no settled entries [$shell]" @(
            'C-001 the clean tree', 'C-001 stopped: ExpectedSettled: derived count 0 < 1')
        $run = Invoke-Cases $shell $root 'C-002'
        CheckRun $run 0 "C-002 on the same yaml [$shell]" @('All checks held.')
        Check (-not $run.Output.Contains('ExpectedSettled')) "C-002 on the same yaml never mentions ExpectedSettled [$shell]`n$($run.Output)"
    }
}

function Shape-BothShells {
    Write-Output 'BothShells: C-001 under each shell on a yaml of many settled entries and one of exactly one'
    Test-C001 'an unchanged copy' { param($root) } 0 { param($base) (Get-Line $base) }
    Test-C001 'a yaml with one settled entry' { param($root) Limit-Yaml $root 1 } 0 { param($base) (Get-Line 1) }
}

function Shape-GuardRule {
    Write-Output "GuardRule: '$GuardName' fires on the pinned counts and on nothing in this tree"
    $lines = [System.IO.File]::ReadAllLines((Join-Path $Repo $YamlRel), $Utf8)
    $at = [array]::IndexOf($lines, "  - name: $GuardName")
    if ($at -lt 0) { throw "probe setup: no entry '$GuardName' in $YamlRel" }
    $files = $null
    $patterns = [System.Collections.Generic.List[string]]::new()
    $inList = $false
    for ($i = $at + 1; $i -lt $lines.Length -and $lines[$i] -cnotmatch '^  (- |#)'; $i++) {
        if ($lines[$i] -cmatch '^    files: (.+)$') { $files = $Matches[1].Trim("'") }
        if ($lines[$i] -cmatch '^    [a-z_]+:') { $inList = $lines[$i] -cmatch '^    must_not_match:' ; continue }
        if ($inList -and $lines[$i] -cmatch '^ {4,}- (.+)$') { $patterns.Add($Matches[1].Trim("'")) }
    }
    Check ($files -ceq 'tests/rules/RuleCases.ps1') "the rule covers tests/rules/RuleCases.ps1 (files: $files)"
    Check ($patterns.Count -gt 0) "the rule has a pattern"

    $hits = {
        param([string[]] $text)
        $found = @()
        for ($i = 0; $i -lt $text.Length; $i++) {
            foreach ($p in $patterns) { if ([regex]::IsMatch($text[$i], $p)) { $found += $i + 1; break } }
        }
        , $found
    }

    $ErrorActionPreference = 'Continue'
    $before = @(& git -C $Repo show "${PreFix}:tests/rules/RuleCases.ps1" 2>&1)
    $shown = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Check ($shown -eq 0) "git shows RuleCases.ps1 at $PreFix`n$($before -join "`n")"
    if ($shown -eq 0) {
        $found = & $hits ([string[]] $before)
        Check (($found -join ',') -ceq '205,268') "the rule hits the pre-fix file at lines 205 and 268 (hit: $($found -join ','))"
    }
    $found = & $hits ([System.IO.File]::ReadAllLines((Join-Path $Repo $CasesRel), $Utf8))
    Check ($found.Count -eq 0) "the rule hits nothing in this tree's RuleCases.ps1 (hit: $($found -join ','))"

    foreach ($sample in @('; 28 settled entries not compared', 'held 1 settled entry')) {
        Check ((& $hits @($sample)).Count -eq 1) "the rule hits '$sample'"
    }
    foreach ($sample in @('; $(Get-ExpectedSettled) settled entries not compared', '; $settled settled entries not compared',
            '(\d+) twinned entries, \d+ patterns; (\d+) settled')) {
        Check ((& $hits @($sample)).Count -eq 0) "the rule does not hit '$sample'"
    }
}

# ---- Main ----

$shapes = if ($Shape -eq 'All') {
    @('AppendOne', 'AppendTwo', 'CountPlusOne', 'NotAgree', 'Misclassified', 'C003Base', 'OracleUnavailable', 'BothShells', 'GuardRule')
}
else { @($Shape) }
try {
    foreach ($s in $shapes) {
        try { & "Shape-$s" }
        catch { Check $false "$s stopped: $($_.Exception.Message)" }
    }
}
finally {
    foreach ($dir in $script:Temps) { Remove-ScratchDir $dir }
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
