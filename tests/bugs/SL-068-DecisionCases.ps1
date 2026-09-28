# SL-068's closure of the sort-on-read question, case by case. SL-068 changes two documents only: it adds
# docs/design/SL-068-sort-on-read.md, the decision record, and appends one pointer line to
# docs/design/SL-042-folder-totals.md. Every case here only reads files and git objects. None runs the timing test it
# talks about, and none changes anything on the machine.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-068-DecisionCases.ps1 -Case C-004
#
# C-001  The build had no write path to the engine's architecture record. The record says the edit is not applied,
#        labels its wording as submitted text, and no other file carries the new sentence as a stand-in.
# C-002  The engine line may no longer hold the old clause. The submitted find/replace, applied the way the record
#        says, gives the expected sentence on the line as handed, and applies nothing to an already edited, a
#        reworded or a doubled line.
# C-003  The pointer in the SL-042 doc is two added lines after the paragraph that ends at line 126, and nothing
#        else in that file moved. `git diff --numstat $Base` reads 2 0.
# C-004  The measurements quoted equal the reads listed, the brief's 9-40 ms is only quoted as the brief's, and the
#        SL-042 and SL-044 figures match their own docs without being merged into the Core range.
# C-005  Whether the rule arrived with its result: git finds SL-042's line 45 and the figures at its line 88 in one
#        commit or two, and the record says which. The ledger times it quotes are the ledger's.
# C-006  The trip wire runs by default: the timing test has no Trait the default filter leaves out and no Skip, and
#        the record says which configuration a default run uses, and that window case T-003, which no default run
#        reaches, is what checks the 500 ms cold open.
# C-007  The record's Not covered section keeps live-refresh re-sorting (SL-044's no-go) out of the closure.
# C-008  This slice changed the two documents and this script and nothing else, no fitness rule's files glob reaches
#        docs/design, and nothing in src, tests or eng other than this script names either document.
#
# -Case All runs every case in a few seconds. Exit 0 when every check held, 1 when any failed.
#
# What this slice touched is read from the engine's own commits (subject starting 'SL-068 ' or 'SL-068:'), plus the
# uncommitted tree while slice/SL-068 is checked out, so C-008 still means "this slice" after the merge. C-003
# compares against $Base. A later slice that edits either document, or the engine line's wording, retires the cases
# that read it in the same commit.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('All', 'C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-006', 'C-007', 'C-008')]
    [string] $Case
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Failures = [System.Collections.Generic.List[string]]::new()

# HEAD when SL-068 was opened, the merge of SL-065.
$Base = '656653629761b142b51396973f92075807b6c231'
$Decision = 'docs/design/SL-068-sort-on-read.md'
$Sl042Doc = 'docs/design/SL-042-folder-totals.md'
$Sl044Doc = 'docs/design/SL-044-folder-tree.md'
$TimingTest = 'tests/FsAnalyser.Core.Tests/ScanStoreRollupTimingTests.cs'
$TestProject = 'tests/FsAnalyser.Core.Tests/FsAnalyser.Core.Tests.csproj'
$ThisScript = 'tests/bugs/SL-068-DecisionCases.ps1'
$Pointer = 'Closed in SL-068 (2026-09-28) on these measurements; see docs/design/SL-068-sort-on-read.md.'
$SubmittedHeading = "The engine's Scan Store line: text submitted for the edit"
$NewSentence = 'No child order is stored'
$Allowed = @($Decision, $Sl042Doc, $ThisScript)
# The engine's own records of this slice.
$AllowedUnder = '.taad/slices/SL-068/'

function Show($value) {
    if ($null -eq $value) { return '<null>' }
    $text = [string]$value
    $builder = [System.Text.StringBuilder]::new()
    foreach ($c in $text.ToCharArray()) {
        if ([int]$c -lt 32 -or [int]$c -gt 126) { [void]$builder.AppendFormat('\u{0:X4}', [int]$c) } else { [void]$builder.Append($c) }
    }
    "'$builder'"
}

function Check([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Output "  ok    $(($what -split "`n")[0])"
    }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

function CheckEqual($expected, $actual, [string] $what) {
    $same = ($null -eq $expected -and $null -eq $actual) -or
        ($null -ne $expected -and $null -ne $actual -and [string]$expected -ceq [string]$actual)
    Check $same "$what`n          expected $(Show $expected)`n          actual   $(Show $actual)"
}

# Not named Git: PowerShell names are case-insensitive, so a function Git would call itself for git.
function Invoke-Git {
    $output = & git -C $Repo @args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') exited $LASTEXITCODE"
    }
    $output
}

# git grep -l, where no match (exit 1) is an answer and not an error.
function Find-Files([string] $fixed, [string[]] $paths) {
    $output = & git -C $Repo grep --untracked -l -F -e $fixed -- @paths
    if ($LASTEXITCODE -gt 1) {
        throw "git grep $fixed exited $LASTEXITCODE"
    }
    @($output | Where-Object { $_ })
}

# A file of the working tree with its line endings read as LF.
function Read-Tree([string] $path) {
    [System.IO.File]::ReadAllText((Join-Path $Repo $path)).Replace("`r`n", "`n")
}

function Flat([string] $text) { ($text -replace '\s+', ' ').Trim() }

# The body of the '## <heading>' section, up to the next '## ' heading. $null when there is no such heading.
function Get-Section([string] $text, [string] $heading) {
    $lines = $text -split "`n"
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].StartsWith("## $heading", [StringComparison]::Ordinal)) { $start = $i; break }
    }
    if ($start -lt 0) { return $null }
    $end = $lines.Count
    for ($i = $start + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i].StartsWith('## ', [StringComparison]::Ordinal)) { $end = $i; break }
    }
    ($lines[($start + 1)..($end - 1)]) -join "`n"
}

# The find and replace texts, the two indented lines of the submitted-text section.
function Get-Submitted {
    $section = Get-Section (Read-Tree $Decision) $SubmittedHeading
    if ($null -eq $section) { return $null }
    $blocks = @($section -split "`n" | Where-Object { $_ -match '^    \S' } | ForEach-Object { $_.Substring(4) })
    if ($blocks.Count -ne 2) { return $null }
    [pscustomobject]@{ Find = $blocks[0]; Replace = $blocks[1]; Section = $section }
}

# The edit as the record words it: replace the find text when it is there exactly once, and otherwise apply
# nothing and report the text found.
function Invoke-SubmittedEdit([string] $line, [string] $find, [string] $replace) {
    $count = 0
    $at = $line.IndexOf($find, [StringComparison]::Ordinal)
    for ($i = $at; $i -ge 0; $i = $line.IndexOf($find, $i + 1, [StringComparison]::Ordinal)) { $count++ }
    if ($count -ne 1) {
        return [pscustomobject]@{ Applied = $false; Occurrences = $count; Text = $line }
    }
    [pscustomobject]@{ Applied = $true; Occurrences = 1; Text = $line.Substring(0, $at) + $replace + $line.Substring($at + $find.Length) }
}

function Get-SliceChanges {
    $paths = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in @(Invoke-Git log --no-merges --format= --name-only --grep '^SL-068[ :]' HEAD)) {
        if ($path) { [void]$paths.Add($path) }
    }
    if ((Invoke-Git rev-parse --abbrev-ref HEAD) -ceq 'slice/SL-068') {
        foreach ($path in @(Invoke-Git diff --name-only HEAD) + @(Invoke-Git ls-files --others --exclude-standard)) {
            if ($path) { [void]$paths.Add($path) }
        }
    }
    , $paths
}

# ---- C-001: no write path, so the edit is reported not applied and nothing stands in for the record ----

function Invoke-C001 {
    Write-Output 'C-001  the engine-record edit is reported not applied, with no in-repo stand-in'
    $text = Read-Tree $Decision
    $submitted = Get-Submitted
    Check ($null -ne $submitted) "$Decision has the section '$SubmittedHeading' with a find and a replace block"
    if ($null -eq $submitted) { return }
    $section = Flat $submitted.Section
    Check ($section.Contains("Status: **not applied** by SL-068's build (2026-09-28).")) 'the section says the edit is not applied by this build'
    Check ($section.Contains('has no write path to the engine''s architecture record')) 'it says why: no write path to the record'
    Check ($section.Contains('the text submitted for the edit, not a copy of the record')) 'it labels its wording submitted text, not a copy of the record'
    Check (-not ($text -match '(?im)^\s*Status:\s*\**applied')) 'no line of the record says the edit was applied'
    Check ($submitted.Replace.StartsWith("and the rolled-up totals per folder. $NewSentence", [StringComparison]::Ordinal)) 'the replace block carries the new sentence'

    $carriers = @(Find-Files $NewSentence @('.', ':(exclude).taad/slices'))
    Write-Output "        files carrying '$NewSentence': $(if ($carriers.Count) { $carriers -join ', ' } else { 'none' })"
    Check ($carriers -contains $Decision) "$Decision carries it"
    $standIns = @($carriers | Where-Object { $_ -notin @($Decision, $ThisScript) })
    Check ($standIns.Count -eq 0) "no other file carries it as a stand-in for the record (found: $(if ($standIns.Count) { $standIns -join ', ' } else { 'none' }))"
    $records = @(Get-SliceChanges | Where-Object { $_ -match '(?i)architect' -and -not $_.StartsWith($AllowedUnder, [StringComparison]::Ordinal) })
    Check ($records.Count -eq 0) "the slice added no architecture-record file (found: $(if ($records.Count) { $records -join ', ' } else { 'none' }))"
}

# ---- C-002: the submitted edit applies once to the line as handed, and never to a line without the clause ----

function Invoke-C002 {
    Write-Output 'C-002  the submitted edit applies to the old clause only, exactly once'
    $submitted = Get-Submitted
    Check ($null -ne $submitted) "$Decision has a find and a replace block"
    if ($null -eq $submitted) { return }
    $find = $submitted.Find
    $replace = $submitted.Replace
    CheckEqual 'the rolled-up totals per folder, and the children of each folder sorted largest first.' $find 'the find text is the old clause and the item before it'

    # The Scan Store line's sentence as the engine handed it to SL-068's build on 2026-09-28. Test input only, not
    # the record: the record is the engine's.
    $handed = 'Also holds the hardlink dedup table (the first name seen charges the bytes, later names count as files but add 0 bytes), the rolled-up totals per folder, and the children of each folder sorted largest first. Readers can read while the scan is still writing.'
    $expected = 'Also holds the hardlink dedup table (the first name seen charges the bytes, later names count as files but add 0 bytes), and the rolled-up totals per folder. No child order is stored: a folder''s children are ordered largest first on each read (ChildrenLargestFirst sorts a temporary index per call; Core read of 100,000 children measured 9-44 ms in Release against a 250 ms budget). Readers can read while the scan is still writing.'

    $first = Invoke-SubmittedEdit $handed $find $replace
    Check $first.Applied 'the line as handed holds the clause once, so the edit applies'
    CheckEqual $expected $first.Text 'the edited sentence is the one the design fixed'
    Check (-not $replace.Contains($find)) 'the replace text does not contain the find text'

    $again = Invoke-SubmittedEdit $first.Text $find $replace
    Check (-not $again.Applied) 'on a line already edited the edit applies nothing'
    CheckEqual $first.Text $again.Text 'and reports the text it found, unchanged'
    CheckEqual 0 $again.Occurrences 'the edited line no longer holds the clause'

    $reworded = $handed.Replace('the children of each folder sorted largest first', 'each folder''s children kept sorted largest first')
    $result = Invoke-SubmittedEdit $reworded $find $replace
    Check (-not $result.Applied) 'on a reworded line the edit applies nothing'
    CheckEqual $reworded $result.Text 'and reports the reworded text'

    $doubled = "$handed $handed"
    $result = Invoke-SubmittedEdit $doubled $find $replace
    Check (-not $result.Applied) 'on a line holding the clause twice the edit applies nothing'
    CheckEqual 2 $result.Occurrences 'it counted both'

    $section = Flat $submitted.Section
    Check ($section.Contains('Find this text, exactly once:')) 'the record asks for the find text exactly once'
    Check ($section.Contains('apply nothing and report the text found')) 'the record says to apply nothing otherwise and report the text found'
    Check ($section.Contains('Everything else in the line stays, the budget figures included.')) 'the record says the rest of the line stays'
}

# ---- C-003: the pointer is appended after the paragraph ending at line 126, and nothing else moved ----

function Invoke-C003 {
    Write-Output "C-003  $Sl042Doc gains the pointer after line 126 and nothing else"
    CheckEqual "2`t0`t$Sl042Doc" ((Invoke-Git diff --numstat $Base -- $Sl042Doc) -join "`n") "git diff --numstat $($Base.Substring(0, 7)) reads 2 0"

    $raw = [System.IO.File]::ReadAllText((Join-Path $Repo $Sl042Doc))
    $crlf = [regex]::Matches($raw, "`r`n").Count
    $lf = [regex]::Matches($raw, "(?<!`r)`n").Count
    Check ($crlf -eq 0 -or $lf -eq 0) "$Sl042Doc uses one line-ending style (CRLF $crlf, bare LF $lf)"

    $old = @(Invoke-Git show "${Base}:$Sl042Doc")
    $new = @((Read-Tree $Sl042Doc) -split "`n")
    if ($new.Count -gt 0 -and $new[-1] -eq '') { $new = $new[0..($new.Count - 2)] }
    Check ($old[125].EndsWith('not a decision on the person''s behalf.', [StringComparison]::Ordinal)) "line 126 at $($Base.Substring(0, 7)) is the m9 paragraph's last line"
    CheckEqual '' $old[126] 'line 127 there is blank, so line 126 ends its paragraph'
    Check ($old[127].StartsWith('## ', [StringComparison]::Ordinal)) "line 128 there is the next heading ($(Show $old[127]))"

    CheckEqual ($old.Count + 2) $new.Count 'the file is two lines longer'
    $before = @(0..126 | Where-Object { $old[$_] -cne $new[$_] })
    Check ($before.Count -eq 0) "lines 1-127 are byte-identical (differ at: $(if ($before.Count) { ($before | ForEach-Object { $_ + 1 }) -join ', ' } else { 'none' }))"
    CheckEqual $Pointer $new[127] 'line 128 is the pointer'
    CheckEqual '' $new[128] 'line 129 is blank'
    $after = @(127..($old.Count - 1) | Where-Object { $old[$_] -cne $new[$_ + 2] })
    Check ($after.Count -eq 0) "everything after is the old text two lines down (differ at old: $(if ($after.Count) { ($after | ForEach-Object { $_ + 1 }) -join ', ' } else { 'none' }))"
    $heading = @($new[0..127] | Where-Object { $_.StartsWith('## ', [StringComparison]::Ordinal) })[-1]
    CheckEqual '## The architecture line this slice revises (review m9)' $heading 'the pointer sits in the m9 section'
    Check (Test-Path -LiteralPath (Join-Path $Repo $Decision)) "the file it points at, $Decision, exists"
}

# ---- C-004: the figures quoted are the reads listed, and harnesses are kept apart ----

function Invoke-C004 {
    Write-Output 'C-004  the measurements quoted are the reads listed, harness by harness'
    $text = Read-Tree $Decision
    $section = Get-Section $text 'What was measured'
    Check ($null -ne $section) "$Decision has a 'What was measured' section"
    if ($null -eq $section) { return }

    $runs = @($section -split "`n" | Where-Object { $_ -match '^- Run \d+ \(' })
    CheckEqual 3 $runs.Count 'three Release runs are listed'
    $reads = [System.Collections.Generic.List[double]]::new()
    foreach ($run in $runs) {
        $values = @(($run -split ': ', 2)[1] -split ',\s*' | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
        CheckEqual 10 $values.Count "$(($run -split ':')[0]) lists 10 reads"
        foreach ($value in $values) { $reads.Add($value) }
    }
    if ($reads.Count -eq 0) { return }
    $low = ($reads | Measure-Object -Minimum).Minimum
    $high = ($reads | Measure-Object -Maximum).Maximum
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $range = "**$($low.ToString('F2', $inv)) to $($high.ToString('F2', $inv)) ms**"
    Check ($section.Contains($range)) "the stated Release range is the listed reads' minimum and maximum, $range"
    Check ($high -lt 250) "every listed read is under the 250 ms budget (slowest $high)"
    $margin = (250 / $high).ToString('F1', $inv)
    Check ((Flat $section).Contains("$margin times margin")) "the margin stated is 250 over the slowest read, $margin"

    $submitted = Get-Submitted
    $rounded = "$([Math]::Round($low))-$([Math]::Round($high)) ms"
    Check ($null -ne $submitted -and $submitted.Replace.Contains("measured $rounded in Release against a 250 ms budget")) "the submitted engine line quotes the same range rounded, $rounded"

    # Whole numbers only: SL-042's '35.9 to 40.2' is not the brief's figure.
    $brief = @($text -split "`n" | Where-Object { $_ -match '(?<![\d.])9(?:-| to )40(?![\d.])' })
    Check ($brief.Count -ge 1 -and @($brief | Where-Object { $_ -notmatch 'brief' }).Count -eq 0) "9-40 ms appears only as the brief's figure ($($brief.Count) line(s))"

    $flatSection = Flat $section
    $sl044 = Flat (((Read-Tree $Sl044Doc) -split "`n")[126..129] -join ' ')
    foreach ($pair in @(@('97.9 ms', '**97.9 ms**'), @('47.5 ms', 'Core''s sort 47.5 ms'), @('141.9 ms', '**141.9 ms**'), @('44.0 ms', 'sort 44.0'))) {
        Check ($flatSection.Contains($pair[0]) -and $sl044.Contains($pair[1])) "SL-044's $($pair[0]) is quoted and is in $Sl044Doc`:127-130"
    }
    $windowLine = @($section -split "`n- " | Where-Object { $_.Contains('97.9 ms') })
    Check ($windowLine.Count -eq 1 -and (Flat $windowLine[0]).Contains('window harness') -and (Flat $windowLine[0]).Contains('published exe')) 'the SL-044 figures sit in their own item naming the window harness'
    $sl042Line88 = ((Read-Tree $Sl042Doc) -split "`n")[87]
    Check ($flatSection.Contains('35.9 to 40.2 ms') -and $sl042Line88.Contains('35.9 to 40.2 ms')) "SL-042's 35.9 to 40.2 ms is quoted and is at $Sl042Doc`:88"

    $merged = @([regex]::Matches($text, '(\d+(?:\.\d+)?)\s*(?:-|to)\s*(\d+(?:\.\d+)?) ms') | Where-Object {
        [double]::Parse($_.Groups[1].Value, $inv) -lt 20 -and [double]::Parse($_.Groups[2].Value, $inv) -gt [Math]::Ceiling($high)
    } | ForEach-Object Value)
    Check ($merged.Count -eq 0) "no range runs from the Core reads into the window figures (found: $(if ($merged.Count) { $merged -join ', ' } else { 'none' }))"
}

# ---- C-005: rule and result, one commit or two, and the record says which ----

function Invoke-C005 {
    Write-Output 'C-005  the record says whether the rule arrived before its result'
    $ruleCommit = @(Invoke-Git log --format=%H --reverse -S 'Children are sorted on read, never stored' -- $Sl042Doc)[0]
    $figureCommit = @(Invoke-Git log --format=%H --reverse -S '35.9 to 40.2 ms' -- $Sl042Doc)[0]
    $designRecord = '.taad/slices/SL-042/design-slice-29.json'
    $designCommit = @(Invoke-Git log --format=%H --reverse --diff-filter=A -- $designRecord)[0]
    Write-Output "        line 45 arrived in $ruleCommit, the figures at line 88 in $figureCommit, $designRecord in $designCommit"
    $text = Flat (Get-Section (Read-Tree $Decision) 'The rule, and who wrote it')
    if ($ruleCommit -ceq $figureCommit) {
        Check ($text.Contains("all arrived in the same commit, $($ruleCommit.Substring(0, 7))")) 'they arrived together, and the record says so with the commit'
        Check ($designCommit -ceq $ruleCommit -and $text.Contains('Git cannot tell them apart: the design record')) 'the design record arrived in that commit too, and the record says so'
        Check (-not $text.Contains('predates the figures in git')) 'the record does not claim git shows the rule first'
    }
    else {
        Check (-not $text.Contains('same commit')) 'they arrived in different commits, and the record does not say the same commit'
    }

    $ledger = [System.IO.File]::ReadAllText((Join-Path $Repo '.taad/slices/SL-042/index.json')) | ConvertFrom-Json -DateKind String
    $design = @($ledger.stages | Where-Object stage -eq 'design-slice' | ForEach-Object attempts | Where-Object details -eq 'design-slice-29.json')
    $build = @($ledger.stages | Where-Object stage -eq 'build-slice' | ForEach-Object attempts | Where-Object n -eq 1)
    CheckEqual 1 $design.Count 'the ledger has design attempt 29'
    CheckEqual 1 $build.Count 'the ledger has build attempt 1'
    if ($design.Count -ne 1 -or $build.Count -ne 1) { return }
    $designAt = ([string]$design[0].at).Substring(0, 19) + 'Z'
    $buildAt = ([string]$build[0].at).Substring(0, 19) + 'Z'
    Check ([string]::CompareOrdinal($designAt, $buildAt) -lt 0) "the ledger dates the design ($designAt) before the build ($buildAt)"
    Check ($text.Contains("design attempt 29 at $designAt") -and $text.Contains("measured it at $buildAt")) 'the record quotes both ledger times'

    $designText = [System.IO.File]::ReadAllText((Join-Path $Repo $designRecord))
    foreach ($quote in 'Sort children on every read (OnDisk descending, then Entry ascending) and store no per-folder order',
        'If the test fails, a cached order can sit behind the same call', 'asserts every read is under 250 ms') {
        Check ($designText.Contains($quote) -and $text.Contains($quote)) "the record quotes SL-042's design: '$quote'"
    }
    Check ($text.Contains('The rule is SL-042''s design, not the person''s.')) 'the record names SL-042''s design as the author'
    Check ($text.Contains('The person did not write the rule and can override this closure.')) 'the record leaves the closure to the person to override'
}

# ---- C-006: the trip wire runs in the default suite, and the record says in which configuration ----

function Invoke-C006 {
    Write-Output 'C-006  the timing test runs in a default run, and the record says how'
    $source = Read-Tree $TimingTest
    $project = Read-Tree $TestProject
    $filter = if ($project -match '<FsaDefaultTestFilter>([^<]*)</FsaDefaultTestFilter>') { $Matches[1].Replace('&amp;', '&') } else { $null }
    Check ($null -ne $filter) "$TestProject has a FsaDefaultTestFilter"
    if ($null -eq $filter) { return }
    Check ($project.Contains('<VSTestTestCaseFilter Condition="''$(VSTestTestCaseFilter)'' == ''''">$(FsaDefaultTestFilter)</VSTestTestCaseFilter>')) 'a run that names no filter gets that default'
    $clauses = @($filter -split '&')
    $traits = @([regex]::Matches($source, '\[Trait\("([^"]+)",\s*"([^"]+)"\)\]') | ForEach-Object { "$($_.Groups[1].Value)!=$($_.Groups[2].Value)" })
    $excluded = @($traits | Where-Object { $_ -in $clauses })
    Write-Output "        traits on the timing test: $(if ($traits.Count) { $traits -join ', ' } else { 'none' }); default filter: $filter"
    Check ($excluded.Count -eq 0) "no Trait of the timing test is left out by the default filter (left out: $(if ($excluded.Count) { $excluded -join ', ' } else { 'none' }))"
    Check (-not ($source -match 'Skip\s*=')) 'the timing test is not skipped'
    Check ($source.Contains('private const double BudgetMs = 250;')) 'its budget is 250 ms'
    Check ($source.Contains('AssemblyConfigurationAttribute')) 'it logs the build configuration'
    Check ($source.Contains('[Collection(nameof(MemoryBudget))]')) 'it is in the MemoryBudget collection'
    Check ((Read-Tree 'tests/FsAnalyser.Core.Tests/ScanStoreTests.cs').Contains('[CollectionDefinition(nameof(MemoryBudget), DisableParallelization = true)]')) 'which runs alone'

    $setters = @(foreach ($path in 'Directory.Build.props', $TestProject, 'eng/dotnet.ps1') {
        if ((Read-Tree $path) -match '<Configuration\b|--configuration|(^|\s)-c\s') { $path }
    })
    $defaultIsDebug = $setters.Count -eq 0
    Write-Output "        files setting a default configuration: $(if ($setters.Count) { $setters -join ', ' } else { 'none' })"
    $text = Flat (Get-Section (Read-Tree $Decision) 'What reopens it')
    Check ($text.Contains("the test carries no Trait, and Core.Tests' default filter (``FsaDefaultTestFilter``, ``$filter``)")) 'the record says the test has no Trait, quoting the default filter'
    Check ($text.Contains('names no filter runs it')) 'the record says a run naming no filter runs it'
    if ($defaultIsDebug) {
        Check ($text.Contains('so that default run is Debug')) 'no default configuration is set, and the record says a default run is Debug'
    }
    else {
        Check (-not $text.Contains('so that default run is Debug')) 'a default configuration is set, and the record does not say Debug'
    }
    Check ($text.Contains('only a Release run counts toward 1')) 'the record says only a Release run can reopen the question'

    # Condition 2 is window case T-003's, which dotnet test never runs.
    $windowCases = Read-Tree 'tests/window/WindowCases.ps1'
    $allList = @($windowCases -split "`n" | Where-Object { $_ -match '^\$cases = if \(\$Case -eq ''All''\)' })
    Check ($windowCases.Contains('Check ($open.firstChild -and [double]$open.ms -lt 500)')) 'window case T-003 fails an open of 500 ms or over'
    Check ($allList.Count -eq 1 -and $allList[0].Contains("'T-003'")) 'T-003 is in the window cases'' All'
    Check ($text.Contains('Window case T-003 checks it') -and $text.Contains('never in `dotnet test`, so no default run watches it')) 'the record says T-003 checks the 500 ms open, outside dotnet test'
}

# ---- C-007: live refresh is outside the closure ----

function Invoke-C007 {
    Write-Output 'C-007  the closure leaves live-refresh re-sorting out'
    $text = Read-Tree $Decision
    $order = @('## Decision: closed', '## Not covered', '## What reopens it') | ForEach-Object { $text.IndexOf("`n$_`n", [StringComparison]::Ordinal) }
    Check ($order[0] -ge 0 -and $order[1] -gt $order[0] -and $order[2] -gt $order[1]) "the record has Decision, Not covered and What reopens it, in that order (at $($order -join ', '))"
    $section = Flat (Get-Section $text 'Not covered')
    Check ($section.Contains('**Re-sorting during a live refresh.**')) 'Not covered names re-sorting during a live refresh'
    Check ($section.Contains('median 84 ms against a 50 ms bar')) 'it gives SL-044''s 84 ms against 50'
    Check ($section.Contains('(docs/design/SL-044-folder-tree.md:17-19)')) 'it cites SL-044''s doc'
    Check ($section.Contains('maintained under concurrent scan writes')) 'it says a stored order there must be maintained under scan writes'
    $sl044 = Flat (((Read-Tree $Sl044Doc) -split "`n")[16..18] -join ' ')
    Check ($sl044.Contains('median 84 ms against 50') -and $sl044.Contains('a UI gap of 866 ms against 250')) "$Sl044Doc`:17-19 says so"
    Check ((Flat (Get-Section $text 'Decision: closed')).Contains('This covers reading the children of a folder in a completed store')) 'the decision limits itself to a completed store'
    $submitted = Get-Submitted
    Check ($null -ne $submitted -and $submitted.Replace -notmatch '(?i)refresh|live|mid-scan|while the scan') 'the submitted engine line claims nothing about refresh'
}

# ---- C-008: two documents and this script, nothing else ----

function ConvertTo-GlobRegex([string] $glob) {
    $pattern = [regex]::Escape($glob).Replace('\*\*/', '(?:.*/)?').Replace('\*\*', '.*').Replace('\*', '[^/]*').Replace('\?', '[^/]')
    "^$pattern$"
}

function Invoke-C008 {
    Write-Output 'C-008  this slice changed two documents and its check, and nothing reads the documents'
    $changes = Get-SliceChanges
    Write-Output "        touched: $(if ($changes.Count) { @($changes) -join ', ' } else { 'nothing' })"
    Check ($changes.Contains($Decision)) "the slice added $Decision"
    Check ($changes.Contains($Sl042Doc)) "the slice changed $Sl042Doc"
    $outside = @($changes | Where-Object { $_ -notin $Allowed -and -not $_.StartsWith($AllowedUnder, [StringComparison]::Ordinal) })
    Check ($outside.Count -eq 0) "every path the slice touched is on the allowed list (outside it: $(if ($outside.Count) { $outside -join ', ' } else { 'none' }))"
    Check (@($changes | Where-Object { $_.StartsWith('src/', [StringComparison]::Ordinal) }).Count -eq 0) 'nothing under src/ changed'
    Check (@($changes | Where-Object { $_.StartsWith('tests/', [StringComparison]::Ordinal) -and $_ -cne $ThisScript }).Count -eq 0) 'nothing under tests/ changed but this script'
    Check (-not $changes.Contains('.taad/fitness.yaml')) '.taad/fitness.yaml is untouched'

    $globs = @((Read-Tree '.taad/fitness.yaml') -split "`n" | Where-Object { $_ -match '^\s*files:\s*(.+?)\s*$' } | ForEach-Object {
        ($_ -replace '^\s*files:\s*', '').Trim().Trim("'").Trim('"')
    } | Sort-Object -Unique)
    Check ($globs.Count -gt 0) "the fitness rules' files globs were read ($($globs.Count) distinct)"
    $reaching = @(foreach ($glob in $globs) {
        foreach ($doc in $Decision, $Sl042Doc) { if ($doc -match (ConvertTo-GlobRegex $glob)) { "$glob -> $doc" } }
    })
    Check ($reaching.Count -eq 0) "no fitness rule's files glob reaches either document (reaching: $(if ($reaching.Count) { $reaching -join ', ' } else { 'none' }))"

    foreach ($name in 'SL-068-sort-on-read', 'SL-042-folder-totals') {
        $readers = @(Find-Files $name @('src', 'tests', 'eng') | Where-Object { $_ -cne $ThisScript })
        Check ($readers.Count -eq 0) "nothing in src, tests or eng names $name but this script (found: $(if ($readers.Count) { $readers -join ', ' } else { 'none' }))"
    }
}

$cases = if ($Case -eq 'All') { 'C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-006', 'C-007', 'C-008' } else { @($Case) }
foreach ($name in $cases) {
    & "Invoke-$($name.Replace('-', ''))"
}

if ($script:Failures.Count -gt 0) {
    Write-Output "$($script:Failures.Count) check(s) failed"
    exit 1
}
Write-Output 'every check held'
exit 0
