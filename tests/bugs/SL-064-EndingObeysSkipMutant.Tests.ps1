<#
Bug (SL-064): tests/window/WindowMutants.ps1 has no standing mutant proving the guard in
MainWindow.ShowFinalTopLevel that always shows the scan's final TopLevel snapshot, even when
a refresh skip is armed. The slice's own build showed, by hand, that mutating that guard so
the ending obeys the skip rule makes L-002 fail (topLevel.final False, reading 1, bytes
393216 vs the core's 524288) -- but nothing in the suite runs that mutant, so a regression of
the guard would pass every automated check.

Expected: WindowMutants.ps1 declares an 'EndingObeysSkip' mutant (in -Mutant's ValidateSet and
in $CaseMutants) targeting L-002, whose Find pattern matches exactly once inside
MainWindow.ShowFinalTopLevel's real, current source, and whose Replace makes that method
consult _skipNextRefresh (the same flag RefreshTopLevel already obeys), so a stale, non-final
snapshot can again reach the screen -- reproducing the manual failure, including the
topLevel.final mismatch.

Actual: -Mutant's ValidateSet and $CaseMutants have no 'EndingObeysSkip' entry at all, so the
guard's necessity is proven only by memory of a manual run, not by the suite.
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

# Pull one top-level `$name = ...` assignment's source text out of the mutants script without
# running it (its param block is Mandatory, and the body publishes a whole app).
function Get-AssignmentText {
    param([System.Management.Automation.Language.Ast]$Ast, [string]$VarName)
    $found = $Ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.Left.VariablePath.UserPath -eq $VarName
        }, $true)
    if ($null -eq $found) { throw "no top-level assignment to `$$VarName found" }
    $found.Extent.Text
}

$repoRoot = Find-RepoRoot -Start $PSScriptRoot
$mutantsPath = Join-Path $repoRoot 'tests/window/WindowMutants.ps1'
if (-not (Test-Path -LiteralPath $mutantsPath)) { throw "not found: $mutantsPath" }
$mutantsText = Get-Content -LiteralPath $mutantsPath -Raw

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseInput($mutantsText, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw "WindowMutants.ps1 does not parse: $($parseErrors[0])" }

# 1) -Mutant's ValidateSet must offer EndingObeysSkip.
$mutantParam = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Mutant' }
if ($null -eq $mutantParam) { throw "WindowMutants.ps1 has no -Mutant parameter any more; test needs updating" }
$validateSet = $mutantParam.Attributes | Where-Object { $_.TypeName.Name -eq 'ValidateSet' }
$allowedValues = @($validateSet.PositionalArguments | ForEach-Object { $_.Value })
if ($allowedValues -notcontains 'EndingObeysSkip') {
    throw "WindowMutants.ps1's -Mutant ValidateSet has no 'EndingObeysSkip' value (has: $($allowedValues -join ', ')). No standing mutant proves the guard that keeps a stale, non-final TopLevel row off the screen at the scan's ending."
}

# 2) $CaseMutants must have an EndingObeysSkip entry for L-002. Materialize just the two
# assignments it needs ($MainWindow, $CaseMutants) -- pure string/regex expressions, no I/O.
$mainWindowAssign = Get-AssignmentText -Ast $ast -VarName 'MainWindow'
$caseMutantsAssign = Get-AssignmentText -Ast $ast -VarName 'CaseMutants'
$scriptBlock = [scriptblock]::Create(@"
$mainWindowAssign
$caseMutantsAssign
[pscustomobject]@{ CaseMutants = `$CaseMutants; MainWindow = `$MainWindow }
"@)
$materialized = & $scriptBlock
$caseMutants = $materialized.CaseMutants
$mainWindowRelative = $materialized.MainWindow

if (-not $caseMutants.Contains('EndingObeysSkip')) {
    throw "`$CaseMutants in WindowMutants.ps1 has no 'EndingObeysSkip' key. No standing mutant proves the guard that keeps a stale, non-final TopLevel row off the screen at the scan's ending."
}
$mutant = $caseMutants['EndingObeysSkip']

if ("$($mutant.Case)" -ne 'L-002') {
    throw "EndingObeysSkip is wired to case '$($mutant.Case)', not L-002 (the endings/skip case it must prove)"
}
if ("$($mutant.File)" -ne $mainWindowRelative) {
    throw "EndingObeysSkip mutates '$($mutant.File)', not MainWindow.xaml.cs ('$mainWindowRelative'), where ShowFinalTopLevel lives"
}

# 3) The mutation must land exactly once inside the real, current ShowFinalTopLevel method, and
# must make it consult _skipNextRefresh -- the flag RefreshTopLevel already obeys mid-scan --
# so the ending can once again show a stale, non-final snapshot when a skip is armed.
$sourcePath = Join-Path $repoRoot $mainWindowRelative
if (-not (Test-Path -LiteralPath $sourcePath)) { throw "not found: $sourcePath" }
$sourceText = Get-Content -LiteralPath $sourcePath -Raw

$methodStart = $sourceText.IndexOf('private void ShowFinalTopLevel(TopLevelSnapshot? final)')
if ($methodStart -lt 0) { throw "ShowFinalTopLevel not found in $sourcePath any more; test needs updating for the guard's new shape" }
$methodEnd = $sourceText.IndexOf('private void ShowTopLevel(TopLevelSnapshot snapshot)', $methodStart)
if ($methodEnd -lt 0) { throw "could not bound the ShowFinalTopLevel method in $sourcePath" }
$methodText = $sourceText.Substring($methodStart, $methodEnd - $methodStart)

$hits = [regex]::Matches($methodText, $mutant.Find)
if ($hits.Count -ne 1) {
    throw "EndingObeysSkip's Find pattern matches $($hits.Count) times inside ShowFinalTopLevel, not once, so it does not target that guard on the source as it stands today"
}

$mutatedMethod = [regex]::Replace($methodText, $mutant.Find, $mutant.Replace)
if ($mutatedMethod -eq $methodText) {
    throw "EndingObeysSkip's Replace leaves ShowFinalTopLevel unchanged"
}
if ($mutatedMethod -notmatch '_skipNextRefresh') {
    throw "EndingObeysSkip's mutation does not make ShowFinalTopLevel consult _skipNextRefresh, so it does not make the ending obey the skip rule the way the manual run did"
}

# 4) The mutant's documented failure must mention topLevel.final: that field going False is
# exactly what the manual run showed and what distinguishes this from any other L-002 failure.
$failPatterns = @($mutant.Fail)
if (-not ($failPatterns | Where-Object { $_ -match 'final' })) {
    throw "EndingObeysSkip's Fail patterns ($($failPatterns -join ', ')) do not mention topLevel.final, but the manual run this mutant is meant to stand for showed topLevel.final go False"
}

Write-Host 'OK: WindowMutants.ps1 has a standing EndingObeysSkip mutant for L-002 that targets ShowFinalTopLevel and documents the topLevel.final failure.'
exit 0
