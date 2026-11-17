<#
SL-107 (design review m1 and m2), moved by SL-003: the counts-only rule over the committed proof-run records.

Bug: the first draft of the rule in .taad/fitness.yaml, 'a committed proof-run record carries counts, codes and reasons,
never a path' (SL-107 wrote it over the one whole-C:\ record, SL-003 over docs/proof-runs/*.json), fired on a drive letter followed by an escaped backslash pair or a slash, and on a "path" key. Write-W001Record
refuses any string with a backslash or a slash, so the rule was weaker than the writer it backs: a UNC path, a relative path,
a %VAR%-rooted path, a path typed by hand with single backslashes (valid JSON when they read as \t or \n) and a separator
spelled as a \uXXXX escape all passed it, in a file that publishes the person's folder names once it is committed (Q-016).

Expected: the rule is the writer's own test stated over the file text, as a closed grammar: a valid record holds no '/' and no
backslash except the JSON escapes for a quote and for a code unit, so every path spelling fires and every string the writer
accepts stays quiet.

Actual (before the fix, the 'before' column of the corpus): every row of expect fire whose before is 0 got no hit from either
pattern of the first draft.

This reads the patterns from .taad/fitness.yaml (never a copy of them), the rows from tests/bugs/SL-107-RecordPathRuleCorpus.json,
and applies the patterns to each row's file text as .NET regexes (the patterns use a character class, an alternation and a
lookahead, which mean the same in Python's re, the engine's dialect). It also loads Write-W001Record from
tests/window/WindowCases.ps1 by its parse and checks the writer and the rule agree on every writer row, and applies the rule
to the records that exist on this machine: artifacts/w001-run.json and every docs/proof-runs/*.json. The engine's own
Fitness is run over planted copies by eng/rules-harness (rows S91-S94 and P41 of plants.json, planted at
docs/proof-runs/w001-c-drive.json).

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-107-RecordPathRule.Tests.ps1

Exit 0 when every check held, 1 when any failed.
#>

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Fitness = Join-Path $Repo '.taad\fitness.yaml'
$Corpus = Join-Path $PSScriptRoot 'SL-107-RecordPathRuleCorpus.json'
$Cases = Join-Path $Repo 'tests\window\WindowCases.ps1'
$RuleName = 'a committed proof-run record carries counts, codes and reasons, never a path'
$RecordGlob = 'docs/proof-runs/*.json'
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $what" }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

# The must_not_match patterns of one entry of .taad/fitness.yaml, by its whole '  - name: ...' header line: the scalars on lines of
# their own after its '    must_not_match:' line, single-quoted ('' read as one quote) or plain (as the engine appended them, which
# YAML reads as written). Also its files glob.
function Read-YamlEntry([string[]] $lines, [string] $header) {
    $entry = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].TrimEnd() -ceq $header) { $entry = $i; break } }
    if ($entry -lt 0) { throw "$Fitness has no entry '$header'" }
    $files = $null
    $key = -1
    for ($i = $entry + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch '^  - name:') { break }
        if ($lines[$i] -cmatch '^    files: (.+?)\s*$') { $files = $Matches[1] }
        if ($lines[$i].TrimEnd() -ceq '    must_not_match:') { $key = $i; break }
    }
    if ($key -lt 0) { throw "the entry '$header' has no must_not_match" }
    $found = New-Object System.Collections.Generic.List[string]
    for ($n = $key + 1; $n -lt $lines.Count; $n++) {
        $m = [regex]::Match($lines[$n], '^ {4}- (.+?)\s*$')
        if (-not $m.Success) { break }
        $scalar = $m.Groups[1].Value
        if ($scalar.Length -ge 2 -and $scalar.StartsWith("'") -and $scalar.EndsWith("'")) { $scalar = $scalar.Substring(1, $scalar.Length - 2).Replace("''", "'") }
        $found.Add($scalar)
    }
    if ($found.Count -eq 0) { throw "the entry '$header' has no patterns under must_not_match" }
    [pscustomobject]@{ Files = $files; Patterns = $found.ToArray() }
}

function Hits([regex[]] $patterns, [string] $text) { @($patterns | Where-Object { $_.IsMatch($text) }).Count }

# The text of the record file a row stands for: a writer row is {"<key>": <value>} through ConvertTo-Json, which is how
# Write-W001Record writes; a hand row is the text typed or pasted into the file, verbatim.
function Get-RowText($row) {
    if ($row.how -ceq 'writer') { return ([ordered]@{ ($row.key) = [string]$row.value } | ConvertTo-Json) }
    if ($row.PSObject.Properties['lines']) { return (@($row.lines) -join "`n") }
    [string]$row.raw
}

$yaml = [IO.File]::ReadAllLines($Fitness)
$entry = Read-YamlEntry $yaml ("  - name: $RuleName")
$rule = @($entry.Patterns | ForEach-Object { [regex]::new($_) })
Write-Output "$($rule.Count) patterns read for the counts-only rule, files $($entry.Files)"
Check ($entry.Files -ceq $RecordGlob) "the rule covers $RecordGlob"
$casesText = Get-Content -LiteralPath $Cases -Raw
Check ($casesText.Contains('docs\proof-runs') -and -not $casesText.Contains('.taad\slices')) 'the rule covers the folder W-001 writes its records to (WindowCases.ps1 names docs\proof-runs, and not the engine''s slice folder)'

$corpus = Get-Content -LiteralPath $Corpus -Raw | ConvertFrom-Json
$firstDraft = @($corpus.before_patterns | ForEach-Object { [regex]::new($_) })
$rows = @($corpus.rows)
Check ($corpus.rule -ceq $RuleName) 'the corpus names the rule it tests'
Check (@($rows | ForEach-Object { $_.id } | Sort-Object -Unique).Count -eq $rows.Count) 'no row id appears twice'

$missedBefore = 0
foreach ($row in $rows) {
    $text = Get-RowText $row
    $now = Hits $rule $text
    $was = Hits $firstDraft $text
    $label = "$($row.id) [$($row.family)] $($row.note)"
    switch ($row.expect) {
        'fire' { Check ($now -ge 1) "$label fires the rule, $now hit(s), the first draft $was" }
        'quiet' { Check ($now -eq 0) "$label stays quiet, $now hit(s)" }
        'residual' { Check ($now -eq 0) "$label is a named false negative and stays unmatched, $now hit(s)" }
        default { Check $false "$($row.id): expect '$($row.expect)' is not fire, quiet or residual" }
    }
    Check ([int]$row.before -eq $was) "$($row.id) the recorded first-draft hit count ($($row.before)) is what the first draft's patterns give ($was)"
    if ($row.expect -ceq 'fire' -and $was -eq 0) { $missedBefore++ }
}
Check ($missedBefore -ge 10) "the corpus holds $missedBefore members of the class the first draft let through (at least 10)"
foreach ($family in 'drive-letter', 'row-key', 'unc', 'relative', 'variable-rooted', 'hand-typed', 'escaped-separator', 'residual', 'record') {
    Check (@($rows | Where-Object family -ceq $family).Count -ge 1) "the corpus has a row of the $family family"
}

# The writer and the rule must agree on every writer row: Write-W001Record, loaded from the script's own parse, refuses a
# reason with a slash or a backslash, and the rule fires on exactly those.
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Cases, [ref]$null, [ref]$null)
$writer = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Write-W001Record' }, $true)
Check ($null -ne $writer) 'Write-W001Record is in WindowCases.ps1'
if ($null -ne $writer) {
    Invoke-Expression $writer.Extent.Text
    $scratch = Join-Path $Repo 'artifacts\sl107-record-test'
    try {
        foreach ($row in @($rows | Where-Object { $_.how -ceq 'writer' -and $_.key -ceq 'reason' })) {
            $file = Join-Path $scratch ($row.id + '.json')
            $wrote = $true
            try { Write-W001Record $file ([ordered]@{ reason = [string]$row.value }) @([string]$row.value) } catch { $wrote = $false }
            Check ($wrote -eq ($row.expect -cne 'fire')) "$($row.id) the writer $(if ($wrote) { 'writes' } else { 'refuses' }) it and the rule $(if ($row.expect -ceq 'fire') { 'fires' } else { 'stays quiet' })"
            if ($wrote) {
                $onDisk = [IO.File]::ReadAllText($file)
                Check ((Hits $rule $onDisk) -eq 0) "$($row.id) the file the writer wrote is quiet under the rule"
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    }
}

# The records that exist on this machine: the last W-001 record under artifacts, and every record under docs/proof-runs.
$live = @(@{ Rel = 'artifacts/w001-run.json'; What = 'the last W-001 record on this machine' })
$proofRuns = Join-Path $Repo 'docs\proof-runs'
if (Test-Path -LiteralPath $proofRuns) { foreach ($f in Get-ChildItem -LiteralPath $proofRuns -Filter '*.json' -File) { $live += @{ Rel = "docs/proof-runs/$($f.Name)"; What = 'a committed proof-run record' } } }
foreach ($entryLive in $live) {
    $path = Join-Path $Repo ($entryLive.Rel -replace '/', '\')
    if (Test-Path -LiteralPath $path) {
        Check ((Hits $rule ([IO.File]::ReadAllText($path))) -eq 0) "$($entryLive.What) ($($entryLive.Rel)) is quiet under the rule"
    }
    else { Write-Output "  --    $($entryLive.What) ($($entryLive.Rel)) does not exist here, so there is nothing to apply the rule to" }
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
