<#
SL-099 review M1 and N3: the two fitness entries that keep tests/window/PoolWatch.ps1 from comparing pool tag names without
regard to case ('PoolWatch never compares pool tag names without regard to case', the first one written, and 'PoolWatch
compares pool tags case-exactly', this slice's) are held to a corpus of the lines that can turn a case-exact lookup into a
folding one: tests/window/PoolWatchTagCompareCorpus.json. NtfF and Ntff, NtxI and NtxF, differ only by case, and the outcome-C
reading decides on both, so one folded comparison writes one's count in the other's column.

This reads the entries' must_not_match patterns from .taad/fitness.yaml (never a copy of them) and applies them, as .NET
regexes, the way Fitness applies them to a file's text, to every corpus line:

  - a 'fire' row is matched by at least one pattern of the entries read; a 'quiet' row by none;
  - a 'gap' row is what no line pattern sees (aliases, a table keyed by tag): it is printed, never failed, and moves to 'fire'
    when a pattern learns it;
  - PoolWatch.ps1 itself, its whole-line and trailing comments blanked, is matched by none of the patterns.

-Entries names the entries to read, by their fitness names, so the corpus can be run against one entry alone: against the
first entry alone the rows only the second one closes fail, which is the state before this slice's patterns.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-099-TagCompareRuleCorpus.Tests.ps1
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-099-TagCompareRuleCorpus.Tests.ps1 -Entries 'PoolWatch never compares pool tag names without regard to case'

Exit 0 when every check held, 1 when any failed. Reads files only.
#>

param(
    [string[]] $Entries = @('PoolWatch never compares pool tag names without regard to case', 'PoolWatch compares pool tags case-exactly'),
    [string] $Fitness = '',
    [string] $Corpus = '',
    [string] $Target = ''
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Fitness) { $Fitness = Join-Path $Repo '.taad\fitness.yaml' }
if (-not $Corpus) { $Corpus = Join-Path $Repo 'tests\window\PoolWatchTagCompareCorpus.json' }
if (-not $Target) { $Target = Join-Path $Repo 'tests\window\PoolWatch.ps1' }
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $(($what -split "`n")[0])" }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

# The must_not_match patterns of the entry called $name: the '    - ' lines after its '    must_not_match:' line. The entries
# read here hold plain scalars; a single-quoted one is unquoted as YAML does, a double-quoted one is refused.
function Read-Patterns([string[]] $lines, [string] $name) {
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -ceq "  - name: $name") { $start = $i; break } }
    if ($start -lt 0) { throw "no entry named '$name' in $Fitness" }
    $patterns = New-Object System.Collections.Generic.List[string]
    $inList = $false
    for ($i = $start + 1; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        if ($l -match '^  - name:' -or $l -match '^\S') { break }
        if ($l -ceq '    must_not_match:') { $inList = $true; continue }
        if ($inList -and $l.StartsWith('    - ')) {
            $v = $l.Substring(6)
            if ($v.StartsWith('"')) { throw "a double-quoted pattern in '$name' is not read here: $v" }
            if ($v.StartsWith("'") -and $v.EndsWith("'") -and $v.Length -ge 2) { $v = $v.Substring(1, $v.Length - 2).Replace("''", "'") }
            $patterns.Add($v)
        }
        elseif ($inList) { break }
    }
    , $patterns.ToArray()
}

$yaml = [IO.File]::ReadAllLines($Fitness)
$regexes = New-Object System.Collections.Generic.List[object]
foreach ($e in $Entries) {
    $found = Read-Patterns $yaml $e
    Check ($found.Count -ge 3) "the entry '$e' holds $($found.Count) patterns"
    foreach ($p in $found) { $regexes.Add([pscustomobject]@{ Entry = $e; Pattern = $p; Regex = [regex]::new($p) }) }
}
Write-Output "$($regexes.Count) patterns from $($Entries.Count) entries in $Fitness"

$rows = @((Get-Content -LiteralPath $Corpus -Raw | ConvertFrom-Json).rows)
Check ($rows.Count -ge 50) "the corpus holds $($rows.Count) rows"
Check (@($rows | Group-Object id | Where-Object Count -gt 1).Count -eq 0) 'no row id appears twice'

function Get-Hits([string] $text) { @($regexes | Where-Object { $_.Regex.IsMatch($text) }) }

foreach ($kind in 'fire', 'quiet') {
    Write-Output "$kind rows"
    foreach ($row in @($rows | Where-Object { $_.expect -ceq $kind })) {
        $hits = Get-Hits $row.line
        if ($kind -ceq 'fire') { Check ($hits.Count -ge 1) "$($row.id) fires: $($row.line)   [$($row.note)]" }
        else { Check ($hits.Count -eq 0) "$($row.id) stays quiet: $($row.line)   [$($row.note); matched by: $(@($hits | ForEach-Object { $_.Pattern }) -join ' || ')]" }
    }
}
Write-Output 'known gaps (printed, not failed)'
foreach ($row in @($rows | Where-Object { $_.expect -ceq 'gap' })) {
    $hits = Get-Hits $row.line
    Write-Output ("  gap   {0} {1}: {2}   [{3}]" -f $row.id, $(if ($hits.Count) { 'is now caught' } else { 'is not caught' }), $row.line, $row.note)
}
Check (@($rows | Where-Object { $_.expect -notin 'fire', 'quiet', 'gap' }).Count -eq 0) "every row is fire, quiet or gap"

# The file the entries guard. Whole-line comments and trailing ones are blanked as Fitness blanks a .ps1 file's comments.
Write-Output 'the guarded file'
$text = [IO.File]::ReadAllText($Target)
$blanked = [regex]::Replace([regex]::Replace($text, '(?m)^[ \t]*#.*$', ''), '(?m)(?<=\s)#[^\r\n]*$', '')
$fileHits = @(Get-Hits $blanked)
$where = foreach ($h in $fileHits) { foreach ($m in $h.Regex.Matches($blanked)) { 'line ' + (($blanked.Substring(0, $m.Index) -split "`n").Count) + ': ' + $m.Value.Trim() } }
Check ($fileHits.Count -eq 0) "no pattern matches $Target ($(@($where) -join ' | '))"

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
