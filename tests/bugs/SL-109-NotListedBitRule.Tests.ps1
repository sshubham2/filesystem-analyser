<#
SL-109 review m1: the fitness entry 'no code outside the store spells the not-listed bit as an entry flag' keeps a test of
bit 0x80 of the flags byte out of every src file but ScanStore.cs. Outside the store ScanStore.Flags() masks that bit off, so
such a test compiles and is always false: it reads every folder a stopped scan never listed as listed, and nothing fails. The
first draft of the entry named two spellings (a cast of 0x80 or 128 to EntryFlags). The entry is held to a corpus of the ways
the same test is written, tests/bugs/SL-109-NotListedBitRuleCorpus.json.

This reads the entry's must_not_match patterns from .taad/fitness.yaml (never a copy of them) and applies them, as .NET
regexes, to every corpus row:

  - a 'fire' row is matched by at least one pattern; a 'quiet' row by none;
  - a 'residual' row is a known false negative, bounded out in the corpus: it must stay unmatched, and one that starts to match
    fails here until it is reclassified to 'fire';
  - each row's 'before' is the number of hits under the corpus's before_patterns (the first draft), checked here, so that a fire
    row with before 0 is a member the first draft let through;
  - no file of src outside ScanStore.cs matches, with // and /* */ comments blanked as Fitness blanks them.

-Before applies before_patterns in place of the entry's, which is the state before this slice's widening: the fire rows the first
draft let through fail. -Python <python.exe> also applies the patterns with that interpreter's re module, the engine's regex
dialect, and requires its hit counts to equal .NET's on every row.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-109-NotListedBitRule.Tests.ps1
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-109-NotListedBitRule.Tests.ps1 -Before
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-109-NotListedBitRule.Tests.ps1 -Python "$env:APPDATA\taad3\venv\Scripts\python.exe"

Exit 0 when every check held, 1 when any failed. Reads files only.
#>

param(
    [string] $Entry = 'no code outside the store spells the not-listed bit as an entry flag',
    [string] $Fitness = '',
    [string] $Corpus = '',
    [string] $SrcRoot = '',
    [string] $Python = '',
    [switch] $Before
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Fitness) { $Fitness = Join-Path $Repo '.taad\fitness.yaml' }
if (-not $Corpus) { $Corpus = Join-Path $Repo 'tests\bugs\SL-109-NotListedBitRuleCorpus.json' }
if (-not $SrcRoot) { $SrcRoot = Join-Path $Repo 'src' }
$script:Failures = New-Object System.Collections.Generic.List[string]

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $(($what -split "`n")[0])" }
    else {
        Write-Output "  FAIL  $(($what -split "`n")[0])"
        $script:Failures.Add($what)
    }
}

# The must_not_match patterns of the entry called $name: the '    - ' lines after its '    must_not_match:' line. The entry read
# here holds single-quoted scalars, unquoted as YAML does; a double-quoted one is refused.
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

$data = Get-Content -LiteralPath $Corpus -Raw | ConvertFrom-Json
$rows = @($data.rows)
Check ($data.rule -ceq $Entry) "the corpus is for the entry '$Entry'"
Check ($rows.Count -ge 90) "the corpus holds $($rows.Count) rows"
Check (@($rows | Group-Object id | Where-Object Count -gt 1).Count -eq 0) 'no row id appears twice'
Check (@($rows | Where-Object { $_.expect -notin 'fire', 'quiet', 'residual' }).Count -eq 0) 'every row is fire, quiet or residual'
foreach ($family in 'cast', 'enum-api', 'mask', 'quiet', 'residual') {
    Check (@($rows | Where-Object family -ceq $family).Count -ge 4) "the corpus has at least 4 '$family' rows"
}

$yaml = [IO.File]::ReadAllLines($Fitness)
$sources = if ($Before) { @($data.before_patterns) } else { Read-Patterns $yaml $Entry }
if ($Before) { Write-Output 'BEFORE: the corpus is applied to before_patterns, the first draft of the entry' }
Check ($sources.Count -ge 1) "the entry holds $($sources.Count) patterns"
$regexes = @($sources | ForEach-Object { [regex]::new($_) })
$beforeRegexes = @(@($data.before_patterns) | ForEach-Object { [regex]::new($_) })
Write-Output "$($regexes.Count) patterns from $(if ($Before) { 'before_patterns' } else { $Fitness })"

function Get-Hits($set, [string] $text) { @($set | Where-Object { $_.IsMatch($text) }).Count }

foreach ($kind in 'fire', 'quiet') {
    Write-Output "$kind rows"
    foreach ($row in @($rows | Where-Object { $_.expect -ceq $kind })) {
        $hits = Get-Hits $regexes $row.line
        $shown = ($row.line -replace "`n", ' \n ')
        if ($kind -ceq 'fire') { Check ($hits -ge 1) "$($row.id) fires: $shown   [$($row.note)]" }
        else { Check ($hits -eq 0) "$($row.id) stays quiet: $shown   [$($row.note)]" }
    }
}
Write-Output 'residual rows (known false negatives, bounded out: they stay unmatched)'
foreach ($row in @($rows | Where-Object { $_.expect -ceq 'residual' })) {
    $hits = Get-Hits $regexes $row.line
    Check ($hits -eq 0) "$($row.id) is still a known gap: $($row.line)   [$($row.note)]$(if ($hits) { '   IT IS NOW CAUGHT: reclassify it to fire' })"
}

Write-Output 'before: what the first draft saw'
$beforeBad = @($rows | Where-Object { (Get-Hits $beforeRegexes $_.line) -ne $_.before })
Check ($beforeBad.Count -eq 0) "every row's before equals its hits under before_patterns ($(@($beforeBad | ForEach-Object { $_.id }) -join ', '))"
$letThrough = @($rows | Where-Object { $_.expect -ceq 'fire' -and $_.before -eq 0 })
$caught = @($rows | Where-Object { $_.expect -ceq 'fire' -and $_.before -ge 1 })
Check ($letThrough.Count -ge 20) "the first draft let $($letThrough.Count) of $($letThrough.Count + $caught.Count) fire rows through, and the entry closes them"

if ($Python) {
    Write-Output 'the engine regex dialect (Python re)'
    $payload = [pscustomobject]@{ patterns = @($sources); rows = @($rows | ForEach-Object { [pscustomobject]@{ id = $_.id; line = $_.line } }) } | ConvertTo-Json -Depth 4 -Compress
    $code = 'import json,re,sys; d=json.load(sys.stdin); ps=[re.compile(p) for p in d[''patterns'']]; print(json.dumps({r[''id'']: sum(1 for p in ps if p.search(r[''line''])) for r in d[''rows'']}))'
    $out = $payload | & $Python -c $code
    Check ($LASTEXITCODE -eq 0) 'the interpreter ran'
    $counts = $out | ConvertFrom-Json
    $differ = @($rows | Where-Object { [int]$counts.($_.id) -ne (Get-Hits $regexes $_.line) })
    Check ($differ.Count -eq 0) "Python re and .NET agree on every row ($(@($differ | ForEach-Object { $_.id }) -join ', '))"
    $pyFire = @($rows | Where-Object { $_.expect -ceq 'fire' -and [int]$counts.($_.id) -lt 1 })
    $pyQuiet = @($rows | Where-Object { $_.expect -cin 'quiet', 'residual' -and [int]$counts.($_.id) -gt 0 })
    Check ($pyFire.Count -eq 0 -and $pyQuiet.Count -eq 0) "under Python re every fire row fires and every quiet or residual row stays quiet ($(@(@($pyFire) + @($pyQuiet) | ForEach-Object { $_.id }) -join ', '))"
}

# The tree the entry guards: every src file but the store, with // and /* */ comments blanked as Fitness blanks them in .cs.
Write-Output 'the guarded tree'
$files = @(Get-ChildItem -LiteralPath $SrcRoot -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.FullName -notmatch '\\Store\\ScanStore\.cs$' })
Check ($files.Count -ge 40) "$($files.Count) src files are read"
$hitsIn = New-Object System.Collections.Generic.List[string]
foreach ($f in $files) {
    $text = [IO.File]::ReadAllText($f.FullName)
    $blanked = [regex]::Replace([regex]::Replace($text, '(?s)/\*.*?\*/', ''), '(?m)//[^\r\n]*', '')
    foreach ($re in $regexes) {
        foreach ($m in $re.Matches($blanked)) { $hitsIn.Add("$($f.Name): $($m.Value.Trim())") }
    }
}
Check ($hitsIn.Count -eq 0) "no pattern matches a src file outside ScanStore.cs ($(@($hitsIn) -join ' | '))"

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
