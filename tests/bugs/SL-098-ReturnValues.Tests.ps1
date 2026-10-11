<#
SL-098 review m2, closed as a class: tests/rules/RuleCases.ps1 has functions whose caller uses their return value (New-TempDir's
folder, Invoke-HarnessRaw's object), and a function that prints inside one of them hands its caller an array of the printed text and
the value, or has the text swallowed so that a failure is counted and never shown. The corpus, SL-098-ReturnValueCorpus.json, lists the
class: a closed census of every function in RuleCases.ps1 (a function with no row, or a row with no function, fails), the spellings
of what prints (the file's own reporting functions, cmdlets whose default output is an object, Write-Output and its aliases, a
script block called through &), fire and quiet plants fed to the checker, and rows that run the real functions and count what they
put in the pipeline.

This test reads RuleCases.ps1 through the PowerShell parser: the checker walks every call in a returning or silent function and
fails the ones whose output reaches the function's own output; the dynamic rows define the functions from the source and run them.
-Source runs it on another copy of RuleCases.ps1 (the way the code before the fix is shown to fail the rows).

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-098-ReturnValues.Tests.ps1
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-098-ReturnValues.Tests.ps1 -Source <a copy of RuleCases.ps1>

Under pwsh it repeats itself under Windows PowerShell 5.1 (RuleCases.ps1 runs under both). Exit 0 when every check held, 1 when any failed.
#>

param([string] $Source = '', [switch] $NoRecurse)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$CasesPath = if ($Source) { $Source } else { Join-Path $Repo 'tests\rules\RuleCases.ps1' }
$CorpusPath = Join-Path $PSScriptRoot 'SL-098-ReturnValueCorpus.json'
$script:Bad = [System.Collections.Generic.List[string]]::new()

function Verify([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Output "  ok    $(($what -split "`n")[0])"
    }
    else {
        Write-Output "  FAIL  $what"
        $script:Bad.Add($what)
    }
}

$corpus = [System.IO.File]::ReadAllText($CorpusPath) | ConvertFrom-Json
$kinds = @{}
foreach ($row in $corpus.census) { $kinds[$row.function] = $row.kind }
$emitting = @($corpus.census | Where-Object { $_.kind -eq 'emits' } | ForEach-Object { $_.function.ToLowerInvariant() }) + @($corpus.outputting)
$sinks = @($corpus.sinks)

# ---- The checker ----

function Get-EnclosingFunction($node) {
    for ($up = $node.Parent; $up; $up = $up.Parent) {
        if ($up -is [System.Management.Automation.Language.FunctionDefinitionAst]) { return $up }
    }
    $null
}

# Whether the output of this command is consumed before it can reach the output of the function it is in.
function Test-Consumed($command) {
    foreach ($redirection in $command.Redirections) {
        if ($redirection -is [System.Management.Automation.Language.FileRedirectionAst] -and $redirection.Location.Extent.Text -eq '$null' -and
            "$($redirection.FromStream)" -in 'Output', 'All') { return $true }
    }
    $node = $command
    while ($node.Parent) {
        $up = $node.Parent
        switch ($up.GetType().Name) {
            'PipelineAst' {
                $at = [array]::IndexOf(@($up.PipelineElements), $node)
                for ($later = $at + 1; $later -lt $up.PipelineElements.Count; $later++) {
                    $next = $up.PipelineElements[$later]
                    if ($next -is [System.Management.Automation.Language.CommandAst] -and "$($next.GetCommandName())".ToLowerInvariant() -in $sinks) { return $true }
                }
            }
            'AssignmentStatementAst' { return $true }
            'ConvertExpressionAst' { if ($up.StaticType -eq [void]) { return $true } }
            'CommandAst' { return $true }
            'MemberExpressionAst' { return $true }
            'InvokeMemberExpressionAst' { return $true }
            'IndexExpressionAst' { return $true }
            'ExpandableStringExpressionAst' { return $true }
            'ThrowStatementAst' { return $true }
            'IfStatementAst' { return $true }
            'WhileStatementAst' { return $true }
            'DoWhileStatementAst' { return $true }
            'DoUntilStatementAst' { return $true }
            'ForEachStatementAst' { return $true }
            'ForStatementAst' { return $true }
            'SwitchStatementAst' { return $true }
            'ReturnStatementAst' { return $false }
            'StatementBlockAst' { if ($up.Parent -isnot [System.Management.Automation.Language.ArrayExpressionAst] -and $up.Parent -isnot [System.Management.Automation.Language.SubExpressionAst]) { return $false } }
            'NamedBlockAst' { return $false }
            'ScriptBlockAst' { return $false }
            'FunctionDefinitionAst' { return $false }
        }
        $node = $up
    }
    $false
}

# Every call in a returning or silent function whose output reaches that function's output.
function Find-Leaks([string] $text, [hashtable] $kindOf) {
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$null, [ref]$errors)
    if ($errors -and $errors.Count) { throw "the source does not parse: $($errors[0].Message)" }
    $leaks = [System.Collections.Generic.List[string]]::new()
    foreach ($command in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true)) {
        $function = Get-EnclosingFunction $command
        if (-not $function -or -not $kindOf.ContainsKey($function.Name) -or $kindOf[$function.Name] -eq 'emits') { continue }
        $name = $command.GetCommandName()
        $prints = if ($name) { $name.ToLowerInvariant() -in $emitting } else { $command.InvocationOperator -eq 'Ampersand' }
        if ($prints -and -not (Test-Consumed $command)) {
            $leaks.Add("$($function.Name) (line $($command.Extent.StartLineNumber)): $($command.Extent.Text.Split("`n")[0].Trim()) reaches the success stream")
        }
    }
    , $leaks
}

# ---- The checker, shown able to tell ----

Write-Output 'the checker: each plant is fired on, or let through, as the corpus says'
foreach ($plant in $corpus.plants) {
    $leaks = Find-Leaks $plant.source @{ F = 'returns'; G = 'emits' }
    $fired = $leaks.Count -gt 0
    Verify ($fired -eq ($plant.expect -eq 'fire')) "$($plant.id) $($plant.note): the checker $(if ($fired) { 'fires' } else { 'lets it through' }), and the corpus says $($plant.expect)"
}

# ---- The census, and RuleCases.ps1 against it ----

Write-Output "the census and $(Split-Path -Leaf $CasesPath)"
$text = [System.IO.File]::ReadAllText($CasesPath)
$parsed = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$null, [ref]$null)
$functions = @($parsed.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | ForEach-Object { $_.Name })
$missing = @($functions | Where-Object { -not $kinds.ContainsKey($_) })
$stale = @($kinds.Keys | Where-Object { $_ -notin $functions })
Verify ($missing.Count -eq 0) "every function has a census row (without one: $($missing -join ', '))"
Verify ($stale.Count -eq 0) "every census row names a function (without one: $($stale -join ', '))"
$leaks = Find-Leaks $text $kinds
Verify ($leaks.Count -eq 0) "no returning or silent function lets output reach its own output`n$($leaks -join "`n")"

# ---- The real functions, run ----

$CsRel = 'tests\FsAnalyser.Core.Tests\ArchitectureRulesTests.cs'
$YamlRel = '.taad\fitness.yaml'
foreach ($name in 'Show', 'Check', 'Get-ScratchParent', 'New-TempDir', 'Remove-ScratchDir', 'Copy-RepoFile', 'New-ParityRoot', 'Invoke-HarnessRaw') {
    $found = $parsed.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true)
    if (-not $found) { throw "test setup: no function $name in $CasesPath" }
    . ([scriptblock]::Create($found.Extent.Text))
}
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:Temps = [System.Collections.Generic.List[string]]::new()
$script:ScratchRoot = $null
$script:Hosted = [System.Collections.Generic.List[string]]::new()
# What Out-Host would print, kept: the code under test sends its FAIL lines there, and they are looked for, not shown as this test's.
function Out-Host {
    param([Parameter(ValueFromPipeline = $true)] $InputObject)
    process { if ($null -ne $InputObject) { $script:Hosted.Add([string]$InputObject) } }
}

$work = $null
$held = $null
$saved = @{ TEMP = $env:TEMP; TMP = $env:TMP }
try {
    Write-Output 'the real functions, counting what they put in the pipeline'
    $first = @(New-TempDir 'fsa-ret-')
    Verify ($first.Count -eq 1 -and $first[0] -is [string] -and [System.IO.Directory]::Exists($first[0]) -and $script:ScratchRoot -and $first[0].StartsWith($script:ScratchRoot)) "D01 New-TempDir, the first call, returns one string, the folder (got $($first.Count) item(s): $($first -join ' | '))"
    $second = @(New-TempDir 'fsa-ret-')
    Verify ($second.Count -eq 1 -and $second[0] -is [string] -and [System.IO.Directory]::Exists($second[0])) "D02 New-TempDir, the second call, returns one string (got $($second.Count): $($second -join ' | '))"
    $root = @(New-ParityRoot)
    Verify ($root.Count -eq 1 -and $root[0] -is [string] -and [System.IO.File]::Exists((Join-Path $root[0] $CsRel)) -and [System.IO.File]::Exists((Join-Path $root[0] $YamlRel))) "D03 New-ParityRoot returns one string, a folder holding the two files (got $($root.Count): $($root -join ' | '))"
    $parent = @(Get-ScratchParent)
    Verify ($parent.Count -eq 1 -and $parent[0] -is [string]) "D04 Get-ScratchParent returns one string (got $($parent.Count))"

    $work = Join-Path $script:ScratchRoot 'work'
    [void][System.IO.Directory]::CreateDirectory($work)
    $gone = @(Remove-ScratchDir (Join-Path $work 'never-made'))
    Verify ($gone.Count -eq 0) "D05 Remove-ScratchDir of a folder that is gone puts nothing in the pipeline (got $($gone.Count): $($gone -join ' | '))"
    $tree = Join-Path $work 'tree'
    [void][System.IO.Directory]::CreateDirectory((Join-Path $tree 'a'))
    [System.IO.File]::WriteAllText((Join-Path $tree 'a\f.txt'), 'x')
    $removed = @(Remove-ScratchDir $tree)
    Verify ($removed.Count -eq 0 -and -not [System.IO.Directory]::Exists($tree)) "D06 Remove-ScratchDir of a tree puts nothing in the pipeline, and the tree is gone (got $($removed.Count): $($removed -join ' | '))"
    $locked = Join-Path $work 'locked'
    [void][System.IO.Directory]::CreateDirectory($locked)
    $held = [System.IO.File]::Open((Join-Path $locked 'f.txt'), [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $before = $script:Failures.Count
    $lines = @(Remove-ScratchDir $locked)
    Verify ($lines.Count -eq 1 -and "$($lines[0])" -match '^\s+FAIL\s+cleanup: ' -and $script:Failures.Count -eq $before + 1) "D07 Remove-ScratchDir of a tree with a held file puts exactly its FAIL line in the pipeline and counts it once (got $($lines.Count): $($lines -join ' | '))"

    # Invoke-HarnessRaw with no engine: the early return.
    $script:EnginePython = ''
    $script:EngineRoot = ''
    $before = $script:Failures.Count
    $script:Hosted.Clear()
    $run = @(Invoke-HarnessRaw @('--x'))
    Verify ($run.Count -eq 1 -and $run[0].Exit -eq -1 -and $script:Failures.Count -eq $before + 1 -and @($script:Hosted | Where-Object { $_ -match 'FAIL\s+harness cases need' }).Count -eq 1) "D08 Invoke-HarnessRaw with no engine returns one object, Exit -1, and its FAIL line is counted and sent to the host (got $($run.Count) item(s))"

    # Invoke-HarnessRaw whose kept scratch cannot be removed: a file in it is held open.
    $tmp = Join-Path $work 'tmp'
    $kept = Join-Path $tmp 'fsa-rules-sl098ret'
    [void][System.IO.Directory]::CreateDirectory($kept)
    $holder = [System.IO.File]::Open((Join-Path $kept 'f.txt'), [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $engine = Join-Path $work 'engine.cmd'
    [System.IO.File]::WriteAllText($engine, "@echo off`r`necho harness: scratch kept at %TEMP%\fsa-rules-sl098ret`r`nexit /b 2`r`n", [System.Text.Encoding]::ASCII)
    $script:EnginePython = $engine
    $script:EngineRoot = $work
    $script:Harness = 'harness.py'
    $env:TEMP = $tmp
    $env:TMP = $tmp
    $before = $script:Failures.Count
    $script:Hosted.Clear()
    try { $run = @(Invoke-HarnessRaw @('--engine-root', $work)) }
    finally {
        $env:TEMP = $saved.TEMP
        $env:TMP = $saved.TMP
        $holder.Dispose()
    }
    Verify ($run.Count -eq 1 -and $run[0].Exit -eq 2 -and $script:Failures.Count -gt $before -and @($script:Hosted | Where-Object { $_ -match 'FAIL\s+cleanup: ' }).Count -gt 0) "D09 Invoke-HarnessRaw whose kept scratch cannot be removed returns one object, Exit 2, and its FAIL line is counted and sent to the host (got $($run.Count) item(s), $($script:Failures.Count - $before) counted)"
}
finally {
    $env:TEMP = $saved.TEMP
    $env:TMP = $saved.TMP
    if ($held) { $held.Dispose() }
    $ErrorActionPreference = 'Continue'
    foreach ($dir in $script:Temps) { Remove-ScratchDir $dir | Out-Null }
    if ($script:ScratchRoot) { Remove-ScratchDir $script:ScratchRoot | Out-Null }
    $ErrorActionPreference = 'Stop'
}
Verify ((-not $script:ScratchRoot) -or -not [System.IO.Directory]::Exists($script:ScratchRoot)) 'this test left nothing behind'

if (-not $NoRecurse -and -not $Source -and $PSVersionTable.PSEdition -eq 'Core') {
    Write-Output 'the same checks under Windows PowerShell 5.1'
    $ErrorActionPreference = 'Continue'
    $inner = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -NoRecurse 2>&1 | Out-String
    $innerExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Verify ($innerExit -eq 0 -and $inner -match '(?m)^All checks held\.\s*$') "powershell.exe runs every check above and they hold (exit $innerExit)`n$inner"
}

Write-Output ''
if ($script:Bad.Count) {
    Write-Output "$($script:Bad.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
