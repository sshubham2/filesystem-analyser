<#
SL-069: every rule-copy case of tests/rules/RuleCases.ps1 follows the counts of the tree it runs on, and a tree
it cannot count stops the cases that ask, by name.

The committed repro (SL-069-RuleCasesDeriveCounts.Tests.ps1) runs only C-001 on a tree with one more
single-scope rule. Each shape here copies the four files RuleCases.ps1 and eng/rules-parity.ps1 read into
%TEMP%, changes one thing there, and runs that copy's RuleCases.ps1 under Windows PowerShell 5.1 and pwsh 7:

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-069-EveryCaseFollowsCounts.Tests.ps1 -Shape TwoScope
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-069-EveryCaseFollowsCounts.Tests.ps1 -Shape EngineCases `
      -EnginePython <the engine's python.exe> -EngineRoot <taad_v3 checkout>\backend

No count of this repository is written here. Every expected line is the unchanged copy's own AGREE line, as
the parity tool prints it, plus what the shape did to the copy; that is a setup reading, never what the case
under test compares against (RuleCases.ps1 recounts the files itself). -Shape All runs every shape but
EngineCases and ImportRuleNames, which need the TAAD engine and a .NET 10 SDK and are asked for by name;
without the engine each fails as not reached, never passes. Exit 0 when every check held, 1 when any failed.
#>

param(
    [ValidateSet('All', 'SingleScope', 'TwoScope', 'ExpectFactsMismatch', 'C003Settled', 'SettledMarkers', 'NoMarker',
        'UnreadableCs', 'UnseenFact', 'HostParity', 'GuardRule', 'EngineCases', 'ImportRuleNames')]
    [string] $Shape = 'All',
    [string] $EnginePython = '',
    [string] $EngineRoot = ''
)

$ErrorActionPreference = 'Stop'

function Find-RepoRoot([string] $start) {
    $dir = Get-Item -LiteralPath $start
    while ($null -ne $dir) {
        if (Test-Path (Join-Path $dir.FullName 'FsAnalyser.slnx')) { return $dir.FullName }
        $dir = $dir.Parent
    }
    throw "No FsAnalyser.slnx found above $start."
}

$Repo = Find-RepoRoot $PSScriptRoot
$CasesRel = 'tests\rules\RuleCases.ps1'
$ParityRel = 'eng\rules-parity.ps1'
$YamlRel = '.taad\fitness.yaml'
$CsRel = 'tests\FsAnalyser.Core.Tests\ArchitectureRulesTests.cs'
$Copied = @($CasesRel, $ParityRel, $YamlRel, $CsRel)
# The tree SL-069 was cut from, whose RuleCases.ps1 pinned the parity counts.
$PreFix = 'a9e0d1d751fe9d7ddb519944755bb26b7223069d'
$PreFixHits = '35,240,243,305,326,370,408,600,625,633'
$GuardNames = @{
    'tests/rules/RuleCases.ps1' = 'the rule-case suite never pins a literal parity count'
    'tests/bugs/SL-069-*.ps1'   = 'the SL-069 count tests never pin a literal parity count'
}
$Utf8 = [System.Text.UTF8Encoding]::new($false)
$Shells = @('powershell.exe', 'pwsh')
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:Temps = [System.Collections.Generic.List[string]]::new()

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

function CheckRun($run, [int] $exit, [string] $what, [string[]] $contains = @(), [string[]] $lacks = @()) {
    Check ($run.Exit -eq $exit) "$what exits $exit`n          got exit $($run.Exit):`n$($run.Output)"
    foreach ($text in $contains) {
        Check ($run.Output.Contains($text)) "$what says '$text'`n$($run.Output)"
    }
    foreach ($text in $lacks) {
        Check (-not $run.Output.Contains($text)) "$what never says '$text'`n$($run.Output)"
    }
}

# ---- The copy ----

function New-TempDir([string] $prefix) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ($prefix + [guid]::NewGuid().ToString('N').Substring(0, 10))
    New-Item -ItemType Directory -Path $dir | Out-Null
    $script:Temps.Add($dir)
    $dir
}

function Copy-Into([string] $root, [string] $rel) {
    $target = Join-Path $root $rel
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath (Join-Path $Repo $rel) -Destination $target
}

function New-Scratch {
    $root = New-TempDir 'fsa-sl069-case-'
    foreach ($rel in $Copied) { Copy-Into $root $rel }
    $root
}

# Every file git lists in this repo, under a throwaway git init: what the harness needs to measure a copy.
function New-RepoScratch {
    $root = New-TempDir 'fsa-sl069-repo-'
    $listed = (& git -C $Repo ls-files -co --exclude-standard -z | Out-String) -split "`0"
    foreach ($rel in $listed) {
        if ($rel.Trim().Length -and (Test-Path -LiteralPath (Join-Path $Repo $rel) -PathType Leaf)) { Copy-Into $root $rel }
    }
    & git -C $root init -q
    if ($LASTEXITCODE) { throw "test setup: git init in $root failed" }
    $root
}

function Read-Copy([string] $root, [string] $rel) { [System.IO.File]::ReadAllText((Join-Path $root $rel), $Utf8) }
function Write-Copy([string] $root, [string] $rel, [string] $text) { [System.IO.File]::WriteAllText((Join-Path $root $rel), $text, $Utf8) }

# $text inserted just before the one occurrence of $anchor, in the file's own line endings.
function Add-Before([string] $root, [string] $rel, [string] $anchor, [string] $text) {
    $content = Read-Copy $root $rel
    $eol = if ($content.Contains("`r`n")) { "`r`n" } else { "`n" }
    $anchor = $anchor.Replace("`n", $eol)
    $at = $content.IndexOf($anchor, [StringComparison]::Ordinal)
    if ($at -lt 0) { throw "test setup: '$anchor' is not in $rel" }
    if ($content.IndexOf($anchor, $at + 1, [StringComparison]::Ordinal) -ge 0) { throw "test setup: '$anchor' is in $rel more than once" }
    Write-Copy $root $rel ($content.Substring(0, $at) + $text.Replace("`n", $eol) + $content.Substring($at))
}

function Edit-Copy([string] $root, [string] $rel, [string] $pattern, [string] $replacement) {
    $content = Read-Copy $root $rel
    if (-not [regex]::IsMatch($content, $pattern)) { throw "test setup: no match for '$pattern' in $rel" }
    Write-Copy $root $rel ([regex]::Replace($content, $pattern, $replacement))
}

$FactsAnchor = '    private static IEnumerable<string> Files('
$MarkerAnchor = '  # settled by SL-023'

# One more twinned rule, as a slice declares one: a Fact, and its one yaml entry above the settled marker.
function Add-SingleScope([string] $root) {
    Add-Before $root $CsRel $FactsAnchor @'
    [Fact]
    public void TheVolumeReadNamesNoBrandNewThing()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.Core\Scanning", "VolumeSpace.cs"),
            @"\bBrandNewThing\b");
    }


'@
    Add-Before $root $YamlRel $MarkerAnchor @'
  - name: 'TheVolumeReadNamesNoBrandNewThing (*.cs)'
    files: 'src/FsAnalyser.Core/Scanning/VolumeSpace.cs'
    must_not_match:
      - '(?m)\bBrandNewThing\b'
    including_comments: true
    because: 'A probe rule, twinned in both copies, as a later slice would declare one.'


'@
}

# One more rule split across two scopes, as SL-043 declared its own: one Fact, two yaml entries.
function Add-TwoScope([string] $root) {
    Add-Before $root $CsRel $FactsAnchor @'
    [Fact]
    public void NothingNamesBrandNewThing()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.Core", "*.cs").Concat(Files(@"src\FsAnalyser.App", "*.cs")),
            @"\bBrandNewThing\b");
    }


'@
    Add-Before $root $YamlRel $MarkerAnchor @'
  - name: 'NothingNamesBrandNewThing (Core)'
    files: 'src/FsAnalyser.Core/**/*.cs'
    must_not_match:
      - '(?m)\bBrandNewThing\b'
    including_comments: true
    because: 'A probe rule split across two scopes, one of its two entries.'

  - name: 'NothingNamesBrandNewThing (App)'
    files: 'src/FsAnalyser.App/**/*.cs'
    must_not_match:
      - '(?m)\bBrandNewThing\b'
    including_comments: true
    because: 'A probe rule split across two scopes, the other of its two entries.'


'@
}

# A Fact with no twin, under an attribute spelled $attribute.
function Add-Untwinned([string] $root, [string] $attribute) {
    Add-Before $root $CsRel $FactsAnchor @"
    $attribute
    public void UnseenBrandNewThing()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.Core\Scanning", "VolumeSpace.cs"),
            @"\bUnseenThing\b");
    }


"@
}

# The yaml cut at its first settled marker: only the twinned entries, and nothing that parts them from any.
function Remove-Marker([string] $root) {
    $lines = [System.IO.File]::ReadAllLines((Join-Path $root $YamlRel), $Utf8)
    $at = -1
    for ($i = 0; $i -lt $lines.Length; $i++) { if ($lines[$i] -cmatch '^\s*#\s*settled by SL-') { $at = $i; break } }
    if ($at -lt 1) { throw 'test setup: no settled marker in the yaml' }
    Write-Copy $root $YamlRel ((($lines[0..($at - 1)]) -join "`r`n") + "`r`n")
}

# ---- Running ----

function Invoke-Native([string] $shell, [string[]] $arguments) {
    $ErrorActionPreference = 'Continue'
    $output = & $shell -NoProfile -ExecutionPolicy Bypass -File @arguments 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Output = $output.Trim() }
}

function Invoke-Cases([string] $shell, [string] $root, [string] $case, [string[]] $more = @()) {
    Invoke-Native $shell (@((Join-Path $root $CasesRel), '-Case', $case) + $more)
}

# The counts the parity tool prints for a copy: read before the shape changes it, and as a setup check after.
function Get-Agree([string] $root) {
    $run = Invoke-Native 'pwsh' @((Join-Path $root $ParityRel), '-Root', $root)
    $m = [regex]::Match($run.Output, 'rules-parity: AGREE (\d+) facts <- (\d+) twinned entries, (\d+) patterns; (\d+) settled entries not compared')
    if ($run.Exit -ne 0 -or -not $m.Success) { throw "test setup: the parity tool does not AGREE on $root (exit $($run.Exit)): $($run.Output)" }
    [pscustomobject]@{ Facts = [int]$m.Groups[1].Value; Twinned = [int]$m.Groups[2].Value; Patterns = [int]$m.Groups[3].Value; Settled = [int]$m.Groups[4].Value }
}

# The AGREE line of $base moved by what a shape did, spelt as eng/rules-parity.ps1 spells it.
function Get-Line($base, [int] $df, [int] $dt, [int] $dp, [int] $ds) {
    "rules-parity: AGREE $($base.Facts + $df) facts <- $($base.Twinned + $dt) twinned entries, $($base.Patterns + $dp) patterns; " +
    "$($base.Settled + $ds) settled entries not compared"
}

function Get-Mismatch([int] $found) {
    "the reader found $found Facts where the test class has $($found + 1)"
}

# A copy changed by $change, checked to AGREE on exactly the counts the shape says it moved.
function New-Changed([scriptblock] $change, [int] $df, [int] $dt, [int] $dp, [int] $ds) {
    $root = New-Scratch
    $base = Get-Agree $root
    & $change $root
    $after = Get-Agree $root
    $want = Get-Line $base $df $dt $dp $ds
    if ((Get-Line $after 0 0 0 0) -cne $want) { throw "test setup: the changed copy AGREEs on '$(Get-Line $after 0 0 0 0)', not '$want'" }
    [pscustomobject]@{ Root = $root; Base = $base }
}

# C-001, C-003, C-004 and C-005 on one changed copy, under each shell.
function Test-FourCases([string] $what, $tree, [int] $df, [int] $dt, [int] $dp) {
    $line = Get-Line $tree.Base $df $dt $dp 0
    $says = @{
        'C-001' = @($line)
        'C-003' = @((Get-Line $tree.Base $df $dt $dp 1))
        'C-004' = @($line)
        'C-005' = @($line, (Get-Mismatch ($tree.Base.Facts + $df)))
    }
    foreach ($shell in $Shells) {
        foreach ($case in @('C-001', 'C-003', 'C-004', 'C-005')) {
            CheckRun (Invoke-Cases $shell $tree.Root $case) 0 "$case on $what [$shell]" (@('All checks held.') + $says[$case])
        }
    }
}

# ---- The shapes ----

function Shape-SingleScope {
    Write-Output 'SingleScope: one more Fact and its one twinned entry: C-001, C-003, C-004 and C-005 stay green'
    $tree = New-Changed { param($root) Add-SingleScope $root } 1 1 1 0
    Test-FourCases 'a tree with one more single-scope rule' $tree 1 1 1
}

function Shape-TwoScope {
    Write-Output 'TwoScope: one more Fact whose twin is two entries (Facts +1, twinned +2): the four cases stay green'
    $tree = New-Changed { param($root) Add-TwoScope $root } 1 2 1 0
    Test-FourCases 'a tree with one more two-scope rule' $tree 1 2 1
}

function Shape-ExpectFactsMismatch {
    Write-Output 'ExpectFactsMismatch: C-005 passes one Fact more than the tree holds, on a tree of N Facts and of N+1'
    $clean = New-Changed { param($root) } 0 0 0 0
    $grown = New-Changed { param($root) Add-SingleScope $root } 1 1 1 0
    foreach ($tree in @(@('the unchanged copy', $clean, 0), @('a copy with one more Fact', $grown, 1))) {
        $what, $t, $df = $tree
        $found = $t.Base.Facts + $df
        # Each parity run C-005 makes, under each shell, exits 2 CANNOT-READ naming N found and N+1 expected.
        $rows = foreach ($shell in $Shells) {
            "  ok    a Fact count that differs from the reflected count [$shell] exits 2"
            "  ok    a Fact count that differs from the reflected count [$shell] says 'rules-parity: CANNOT-READ tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs: $(Get-Mismatch $found)'"
        }
        CheckRun (Invoke-Cases 'pwsh' $t.Root 'C-005') 0 "C-005 on $what" (@('All checks held.') + $rows)
    }
}

function Shape-C003Settled {
    Write-Output 'C003Settled: the added entry is the last above the marker; C-003 expects this tree''s settled count plus one'
    $tree = New-Changed { param($root) Add-SingleScope $root } 1 1 1 0
    $lines = [System.IO.File]::ReadAllLines((Join-Path $tree.Root $YamlRel), $Utf8)
    $last = $null
    foreach ($l in $lines) { if ($l -cmatch '^\s*#\s*settled by SL-') { break }; if ($l -cmatch '^  - name:') { $last = $l } }
    Check ($last -ceq "  - name: 'TheVolumeReadNamesNoBrandNewThing (*.cs)'") "the added entry is the last one above the first marker (last: $last)"
    foreach ($shell in $Shells) {
        CheckRun (Invoke-Cases $shell $tree.Root 'C-003') 0 "C-003 on a tree with one more single-scope rule [$shell]" @(
            'All checks held.',
            "  ok    the twin of a one-scope Fact deleted [pwsh] exits 1",
            "  ok    the same entry appended below the marker, as the engine appends [pwsh] says '$(Get-Line $tree.Base 1 1 1 1)'")
    }
}

function Shape-SettledMarkers {
    Write-Output 'SettledMarkers: this tree''s yaml, several settled markers with SL-069''s last: twinned stops at the first'
    $markers = @([System.IO.File]::ReadAllLines((Join-Path $Repo $YamlRel), $Utf8) -cmatch '^\s*#\s*settled by SL-')
    Check ($markers.Count -ge 2 -and $markers[-1] -ceq '  # settled by SL-069') "the yaml holds several settled markers, SL-069's last ($($markers.Count), last '$($markers[-1])')"
    $tree = New-Changed { param($root) } 0 0 0 0
    $appended = New-Changed {
        param($root)
        [System.IO.File]::AppendAllText((Join-Path $root $YamlRel), "`r`n  # settled by SL-999`r`n  - name: probe rule`r`n    files: src/**/*.cs`r`n    must_not_match:`r`n    - ProbeThing`r`n    because: probe`r`n", $Utf8)
    } 0 0 0 1
    foreach ($shell in $Shells) {
        CheckRun (Invoke-Cases $shell $tree.Root 'C-001') 0 "C-001 on an unchanged copy [$shell]" @('All checks held.', (Get-Line $tree.Base 0 0 0 0))
        CheckRun (Invoke-Cases $shell $appended.Root 'C-001') 0 "C-001 on a copy with one more marker and entry [$shell]" @('All checks held.', (Get-Line $appended.Base 0 0 0 1))
    }
}

function Shape-NoMarker {
    Write-Output 'NoMarker: a yaml with no settled marker stops C-001 by name before any parity run; C-002 still runs'
    $root = New-Scratch
    Remove-Marker $root
    foreach ($shell in $Shells) {
        CheckRun (Invoke-Cases $shell $root 'C-001') 1 "C-001 on a yaml with no marker [$shell]" @(
            'C-001 the clean tree', "C-001 stopped: RuleCounts: no '# settled by SL-' marker in .taad\fitness.yaml") @('the repo [', 'All checks held.')
        CheckRun (Invoke-Cases $shell $root 'C-002') 0 "C-002 on the same yaml [$shell]" @('All checks held.') @('RuleCounts')
    }
}

function Shape-UnreadableCs {
    Write-Output 'UnreadableCs: no test class, no [Fact] line, or no pattern line stops C-001 by name before any parity run'
    $shapes = @(
        @('no ArchitectureRulesTests.cs', { param($root) Remove-Item -LiteralPath (Join-Path $root $CsRel) }, "RuleCounts: no $CsRel under"),
        @('no [Fact] line', { param($root) Edit-Copy $root $CsRel '\[Fact\]' '[Fect]' }, 'RuleCounts: no [Fact] or [Theory] line'),
        @('no pattern line', { param($root) Edit-Copy $root $CsRel '(?m)^(\s*)@"' '${1}/**/@"' }, 'RuleCounts: no verbatim @"..." pattern line')
    )
    foreach ($shape in $shapes) {
        $what, $change, $says = $shape
        $root = New-Scratch
        & $change $root
        foreach ($shell in $Shells) {
            CheckRun (Invoke-Cases $shell $root 'C-001') 1 "C-001 with $what [$shell]" @('C-001 the clean tree', "C-001 stopped: $says") @('the repo [', 'All checks held.')
        }
    }
}

function Shape-UnseenFact {
    Write-Output 'UnseenFact: a Fact the reader refuses, or one it is blind to, turns C-001 red; the recount counts both'
    # [Fact, Trait(...)] is not an attribute the reader reads: it refuses the file.
    $root = New-Scratch
    $base = Get-Agree $root
    Add-Untwinned $root '[Fact, Trait("Kind", "Unseen")]'
    foreach ($shell in $Shells) {
        CheckRun (Invoke-Cases $shell $root 'C-001') 1 "C-001 with a [Fact, Trait(...)] Fact [$shell]" @(
            "FAIL  the repo [$shell] exits 0", 'rules-parity: CANNOT-READ', "the repo with -ExpectFacts $($base.Facts + 1) [$shell]")
    }

    # [Fact(Skip = ...)] is read by the real reader; this copy's tool is made blind to it, so it AGREEs on the
    # counts it had. C-001 recounts the file and passes the Fact it cannot see to -ExpectFacts.
    $root = New-Scratch
    $base = Get-Agree $root
    Add-Untwinned $root '[Fact(Skip = "unseen")]'
    # $$ is one $ in the replacement text, where $_ would be the whole file. Read-Facts returns its array as one
    # object, so it is enumerated before it is filtered.
    Edit-Copy $root $ParityRel '(?m)^    \$facts = Read-Facts(?=\r?$)' '    $$facts = @((Read-Facts) | Where-Object { -not $$_.Name.StartsWith(''Unseen'') })'
    $blind = Get-Agree $root
    Check ((Get-Line $blind 0 0 0 0) -ceq (Get-Line $base 0 0 0 0)) "the blind tool AGREEs on the unchanged counts ('$(Get-Line $blind 0 0 0 0)')"
    foreach ($shell in $Shells) {
        CheckRun (Invoke-Cases $shell $root 'C-001') 1 "C-001 with a Fact the tool cannot see [$shell]" @(
            "FAIL  the repo [powershell.exe] says '$(Get-Line $base 1 0 1 0)'",
            "FAIL  the repo with -ExpectFacts $($base.Facts + 1) [pwsh] exits 0", (Get-Mismatch $base.Facts))
    }
}

function Shape-HostParity {
    Write-Output 'HostParity: the same two-scope tree, both files behind a UTF-8 BOM, counted alike by each shell'
    $tree = New-Changed { param($root) Add-TwoScope $root } 1 2 1 0
    foreach ($rel in @($YamlRel, $CsRel)) {
        $path = Join-Path $tree.Root $rel
        [System.IO.File]::WriteAllBytes($path, [byte[]](0xEF, 0xBB, 0xBF) + [System.IO.File]::ReadAllBytes($path))
    }
    $line = Get-Line $tree.Base 1 2 1 0
    $labels = @{}
    foreach ($shell in $Shells) {
        $run = Invoke-Cases $shell $tree.Root 'C-001'
        CheckRun $run 0 "C-001 run by $shell" @('All checks held.', "the repo with -ExpectFacts $($tree.Base.Facts + 1) [pwsh] says '$line'")
        $labels[$shell] = (($run.Output -split "`r?`n") | Where-Object { $_ -like '  ok    *' }) -join "`n"
    }
    Check ($labels['powershell.exe'] -ceq $labels['pwsh']) "both shells expect the same lines`n$($labels['powershell.exe'])`n--`n$($labels['pwsh'])"
}

# The two fitness entries SL-069 adds, as the yaml spells them.
function Get-Guard([string] $name) {
    $lines = [System.IO.File]::ReadAllLines((Join-Path $Repo $YamlRel), $Utf8)
    $at = [array]::IndexOf($lines, "  - name: $name")
    if ($at -lt 0) { throw "test setup: no entry '$name' in $YamlRel" }
    $files = $null
    $patterns = [System.Collections.Generic.List[string]]::new()
    $inList = $false
    for ($i = $at + 1; $i -lt $lines.Length -and $lines[$i] -cnotmatch '^  (- |#)'; $i++) {
        if ($lines[$i] -cmatch '^    files: (.+)$') { $files = $Matches[1].Trim("'") }
        if ($lines[$i] -cmatch '^    [a-z_]+:') { $inList = $lines[$i] -cmatch '^    must_not_match:'; continue }
        if ($inList -and $lines[$i] -cmatch '^ {4,}- (.+)$') { $patterns.Add($Matches[1]) }
    }
    [pscustomobject]@{ Files = $files; Patterns = $patterns.ToArray() }
}

function Get-Hits([string[]] $patterns, [string[]] $text) {
    $found = @()
    for ($i = 0; $i -lt $text.Length; $i++) {
        foreach ($p in $patterns) { if ([regex]::IsMatch($text[$i], $p)) { $found += $i + 1; break } }
    }
    , $found
}

function Shape-GuardRule {
    Write-Output 'GuardRule: the literal-count rule fires on every pinned count SL-069 removed, and on nothing in this tree'
    $guards = @{}
    foreach ($files in $GuardNames.Keys) {
        $g = Get-Guard $GuardNames[$files]
        Check ($g.Files -ceq $files) "'$($GuardNames[$files])' covers $files (files: $($g.Files))"
        $guards[$files] = $g
    }
    $patterns = $guards['tests/rules/RuleCases.ps1'].Patterns
    Check ($patterns.Count -ge 9) "the rule has its patterns ($($patterns.Count))"
    Check (($patterns -join "`n") -ceq ($guards['tests/bugs/SL-069-*.ps1'].Patterns -join "`n")) 'both entries forbid the same patterns'

    $ErrorActionPreference = 'Continue'
    $before = @(& git -C $Repo show "${PreFix}:tests/rules/RuleCases.ps1" 2>&1)
    $shown = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Check ($shown -eq 0) "git shows RuleCases.ps1 at $PreFix`n$($before -join "`n")"
    if ($shown -eq 0) {
        $found = Get-Hits $patterns ([string[]] $before)
        Check (($found -join ',') -ceq $PreFixHits) "the rule hits the pre-fix file at $PreFixHits (hit: $($found -join ','))"
    }
    $found = Get-Hits $patterns ([System.IO.File]::ReadAllLines((Join-Path $Repo $CasesRel), $Utf8))
    Check ($found.Count -eq 0) "the rule hits nothing in this tree's RuleCases.ps1 (hit: $($found -join ','))"
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $Repo 'tests\bugs') -Filter 'SL-069-*.ps1')) {
        $found = Get-Hits $patterns ([System.IO.File]::ReadAllLines($file.FullName, $Utf8))
        Check ($found.Count -eq 0) "the rule hits nothing in $($file.Name), comments included (hit: $($found -join ','))"
    }

    # Built with -f, so this file carries none of them.
    $pinned = @(
        ('rules-parity: AGREE {0} facts <- {1} twinned entries, {2} patterns; ' -f 12, 16, 28),
        ('the repo with -ExpectFacts {0} [pwsh]' -f 11), ("@('-ExpectFacts', '{0}')" -f 11), ("@('-ExpectFacts', {0})" -f 12),
        ('$Twinned = {0}' -f 15), ('xunit {0} Facts, 0 hits' -f 11),
        ('the reader found {0} Facts where the test class has {1}' -f 11, 12), ('Format-AgreeLine -Facts {0} -Twinned {1}' -f 12, 16),
        ('$Facts = {0}' -f 11), ('Patterns = {0}' -f 27)
    )
    foreach ($sample in $pinned) { Check ((Get-Hits $patterns @($sample)).Count -eq 1) "the rule hits '$sample'" }
    $derived = @(
        'rules-parity: AGREE $($counts.Facts + $DFacts) facts <- $($counts.Twinned + $DTwinned) twinned entries, ',
        '"$($counts.Patterns + $DPatterns) patterns; $settled settled entries not compared"',
        'the repo with -ExpectFacts $facts [$shell]', '@(''-ExpectFacts'', $facts)', '@(''-ExpectFacts'', $more)',
        'Format-AgreeLine -DTwinned -1', 'Get-ExpectFacts -DFacts 1',
        '"xunit $(Get-ExpectFacts) Facts, 0 hits"', '(\d+) twinned entries, \d+ patterns; (\d+) settled',
        'C-001 the clean tree: its Facts, twinned entries and patterns, the settled entries below the marker'
    )
    foreach ($sample in $derived) { Check ((Get-Hits $patterns @($sample)).Count -eq 0) "the rule does not hit '$sample'" }

    # SL-053's rule against a pinned settled count stays silent on this tree too.
    CheckRun (Invoke-Native 'pwsh' @((Join-Path $Repo 'tests\rules\SettledCountProbe.ps1'), '-Shape', 'GuardRule')) 0 `
        "SettledCountProbe -Shape GuardRule" @('All checks held.')
}

function Shape-EngineCases {
    Write-Output 'EngineCases: C-007 and C-008 through the harness, on this tree and on a copy with one more two-scope rule'
    if (-not $EnginePython -or -not $EngineRoot) {
        Check $false 'not reached: EngineCases needs -EnginePython and -EngineRoot; without them C-007 and C-008 are not run'
        return
    }
    # A copy builds through its own eng/dotnet.ps1, which finds an SDK on PATH before it would download one
    # into the copy. This repo's gitignored .dotnet/ is offered, for this process and its children only.
    $local = Join-Path $Repo '.dotnet'
    if (Test-Path -LiteralPath (Join-Path $local 'dotnet.exe')) { $env:PATH = "$local;$env:PATH" }
    $engine = @('-EnginePython', $EnginePython, '-EngineRoot', $EngineRoot)

    $base = Get-Agree $Repo
    $copy = New-RepoScratch
    Add-TwoScope $copy
    $grown = Get-Agree $copy
    Check ((Get-Line $grown 0 0 0 0) -ceq (Get-Line $base 1 2 1 0)) "the two-scope copy AGREEs on '$(Get-Line $base 1 2 1 0)'"
    foreach ($tree in @(@('this tree', $Repo, 0, 0, 0), @('a copy with one more two-scope rule', $copy, 1, 2, 1))) {
        $what, $root, $df, $dt, $dp = $tree
        foreach ($shell in $Shells) {
            # Only an ok row counts: a FAIL row's label carries the same expected text.
            CheckRun (Invoke-Cases $shell $root 'C-007' $engine) 0 "C-007 on $what [$shell]" @(
                'All checks held.', "  ok    the clean scenario on this repo says 'xunit $($base.Facts + $df) Facts, 0 hits'")
            CheckRun (Invoke-Cases $shell $root 'C-008' $engine) 0 "C-008 on $what [$shell]" @(
                'All checks held.',
                "  ok    the tests scope of InteropLivesOnlyInNative deleted from yaml says '$(Get-Line $base $df ($dt - 1) $dp 0)'",
                "  ok    the tests scope of InteropLivesOnlyInNative dropped from xUnit says '$(Get-Line $base $df $dt $dp 0)'")
        }
    }
}

function Shape-ImportRuleNames {
    Write-Output 'ImportRuleNames: C-007 finds every import rule named NOT RUN however yaml spells its name, and counts no empty one'
    if (-not $EnginePython -or -not $EngineRoot) {
        Check $false 'not reached: ImportRuleNames needs -EnginePython and -EngineRoot; without them C-007 is not run'
        return
    }
    $local = Join-Path $Repo '.dotnet'
    if (Test-Path -LiteralPath (Join-Path $local 'dotnet.exe')) { $env:PATH = "$local;$env:PATH" }
    $engine = @('-EnginePython', $EnginePython, '-EngineRoot', $EngineRoot)

    # More import rules on the provider's folder, which the harness's scratch copy (no code graph) leaves
    # unran, each name spelt a way yaml allows: single-quoted with its apostrophe doubled, double-quoted,
    # double-quoted with escapes, wrapped onto a second line, followed by a comment (quoted and plain), folded,
    # and holding characters past ASCII (an e-acute and an em dash). The last entry's must_not_import is
    # empty, so it is no import rule and is never named.
    $copy = New-RepoScratch
    $base = Get-Agree $copy
    $rules = @'

  # settled by SL-999
  - name: 'the provider''s helper never imports Core'
    files: tests/CfProvider/**
    must_not_import:
    - FsAnalyser.Core*
    must_not_match:
    - BrandNewThing
    because: A probe import rule whose single-quoted name doubles its apostrophe.
  - name: "the provider's stub never imports Core"
    files: tests/CfProvider/**
    must_not_import:
    - FsAnalyser.Core*
    must_not_match:
    - BrandNewThing
    because: A probe import rule whose double-quoted name holds an apostrophe.
  - name: "the provider's \"escaped\" probe never imports Core"
    files: tests/CfProvider/**
    must_not_import:
    - FsAnalyser.Core*
    must_not_match:
    - BrandNewThing
    because: A probe import rule whose double-quoted name escapes its quotes.
  - name: the provider's wrapped probe never imports Core, and its name runs on past the end of its
      first line
    files: tests/CfProvider/**
    must_not_import:
    - FsAnalyser.Core*
    must_not_match:
    - BrandNewThing
    because: A probe import rule whose plain name wraps onto a second line.
  - name: 'the provider''s noted probe never imports Core' # a note after a quoted name
    files: tests/CfProvider/**
    must_not_import:
    - FsAnalyser.Core*
    must_not_match:
    - BrandNewThing
    because: A probe import rule whose quoted name is followed by a comment.
  - name: the provider's plain noted probe never imports Core # a note after a plain name
    files: tests/CfProvider/**
    must_not_import:
    - FsAnalyser.Core*
    must_not_match:
    - BrandNewThing
    because: A probe import rule whose plain name is followed by a comment.
  - name: >-
      the provider's folded probe never imports Core
    files: tests/CfProvider/**
    must_not_import:
    - FsAnalyser.Core*
    must_not_match:
    - BrandNewThing
    because: A probe import rule whose name is a folded block.
  - name: the provider's caf{E-ACUTE} probe {EM-DASH} never imports Core
    files: tests/CfProvider/**
    must_not_import:
    - FsAnalyser.Core*
    must_not_match:
    - BrandNewThing
    because: A probe import rule whose name holds characters past ASCII, which the harness prints escaped.
  - name: the provider's empty-import probe is no import rule
    files: tests/CfProvider/**
    must_not_import: []
    must_not_match:
    - BrandNewThing
    because: A probe rule whose must_not_import is empty, which the engine does not run as an import rule.
'@
    # Made here, so this file stays ASCII whichever shell reads it.
    $rules = $rules.Replace('{E-ACUTE}', [string][char]0xE9).Replace('{EM-DASH}', [string][char]0x2014)
    $added = @($rules -split "`r?`n" -cmatch '^  - name:').Count
    [System.IO.File]::AppendAllText((Join-Path $copy $YamlRel), ($rules.Replace("`r`n", "`n").Replace("`n", "`r`n")), $Utf8)
    $grown = Get-Agree $copy
    Check ((Get-Line $grown 0 0 0 0) -ceq (Get-Line $base 0 0 0 $added)) "the copy AGREEs on '$(Get-Line $base 0 0 0 $added)'"
    # A block list under must_not_import is an import rule; the empty [] is not.
    $imports = @([System.IO.File]::ReadAllLines((Join-Path $copy $YamlRel), $Utf8) -cmatch '^    must_not_import:\s*$').Count
    foreach ($shell in $Shells) {
        CheckRun (Invoke-Cases $shell $copy 'C-007' $engine) 0 "C-007 on a copy with import rules named every way yaml allows [$shell]" @(
            'All checks held.', "  ok    the $imports import rule(s) are named NOT RUN, and the verdict is not pass (unnamed: )")
    }
}

# ---- Main ----

$shapes = if ($Shape -eq 'All') {
    @('SingleScope', 'TwoScope', 'ExpectFactsMismatch', 'C003Settled', 'SettledMarkers', 'NoMarker', 'UnreadableCs', 'UnseenFact',
        'HostParity', 'GuardRule')
}
else { @($Shape) }
try {
    foreach ($s in $shapes) {
        try { & "Shape-$s" }
        catch { Check $false "$s stopped: $($_.Exception.Message)" }
    }
}
finally {
    foreach ($dir in $script:Temps) { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
