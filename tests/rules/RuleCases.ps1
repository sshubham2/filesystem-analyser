# The rule-copy cases (SL-031). C-001 to C-005 run eng/rules-parity.ps1 against copies of the two rule
# files with one shape fed in each, under both Windows PowerShell 5.1 (the shell RuleCopiesAgreeTests
# launches) and pwsh 7. C-006 to C-014 run eng/rules-harness/harness.py, which needs the TAAD engine:
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/rules/RuleCases.ps1 -Case C-002
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/rules/RuleCases.ps1 -Case C-010 `
#       -EnginePython <the engine's python.exe> -EngineRoot <taad_v3 checkout>\backend
#
# -Case All runs every case. Exit 0 when every check held, 1 when any failed. Each guard is also shown to
# bite: the defect it exists for is put back in a copy, and the check must fail on it. A harness case
# that needs a changed repo runs it on a copy of this repo's files in %TEMP% (with its own throwaway
# `git init`, because the harness copies what git lists), built with this repo's SDK (--dotnet-from).

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('All', 'C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-006', 'C-007', 'C-008', 'C-009', 'C-010',
        'C-011', 'C-012', 'C-013', 'C-014')]
    [string] $Case,
    [string] $EnginePython = '',
    [string] $EngineRoot = ''
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Parity = Join-Path $Repo 'eng\rules-parity.ps1'
$Harness = Join-Path $Repo 'eng\rules-harness\harness.py'
$CsRel = 'tests\FsAnalyser.Core.Tests\ArchitectureRulesTests.cs'
$YamlRel = '.taad\fitness.yaml'
$Utf8 = [System.Text.UTF8Encoding]::new($false)
$Shells = @('powershell.exe', 'pwsh')
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:Temps = [System.Collections.Generic.List[string]]::new()
# The folder every New-TempDir folder is made in, made on first use (Get-ScratchParent) and removed last.
$script:ScratchRoot = $null
# The counts the AGREE line carries, worked out from this tree's own two files on first use (Get-RuleCounts,
# Get-ExpectedSettled) and kept for the run. No count is written in this file.
$script:Counts = $null
$script:ExpectedSettled = $null

# ---- Checks ----

# Non-ASCII shown as \uXXXX, so a failure prints the same whatever the console's code page.
function Show($value) {
    if ($null -eq $value) { return '<null>' }
    $builder = [System.Text.StringBuilder]::new()
    foreach ($c in ([string]$value).ToCharArray()) {
        if ([int]$c -gt 126 -or ([int]$c -lt 32 -and $c -ne "`n")) { [void]$builder.AppendFormat('\u{0:X4}', [int]$c) } else { [void]$builder.Append($c) }
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

function CheckRun($run, [int] $exit, [string] $what, [string[]] $contains = @()) {
    Check ($run.Exit -eq $exit) "$what exits $exit`n          got exit $($run.Exit):`n$($run.Output)"
    foreach ($text in $contains) {
        Check ($run.Output.Contains($text)) "$what says $(Show $text)`n$($run.Output)"
    }
}

# ---- Temporary trees ----

# Where this run's scratch root is made, by the rule of TempTree.ChooseTestsRoot (tests/FsAnalyser.Core.Tests/TempTree.cs):
# the temp folder itself, unless that is at or under the Windows folder (compared without case, at a separator), when it is
# the user's own LocalAppData\Temp. The engine's shells carry TEMP=C:\WINDOWS\TEMP, where an ordinary user may create folders
# but not list them (BUILTIN\Users get only (CI)(S,WD,AD,X)); a killed run's leftovers there cannot be found by name, so the
# root goes where they can. Refused by name when LocalAppData\Temp is not outside the Windows folder either. It lists and
# creates nothing. It runs under Windows PowerShell 5.1 as well as pwsh 7 (SettledCountProbe and the SL-069 test run copies
# of this file under both), so it uses no API that only .NET Core has: a pattern stands in for IsPathFullyQualified.
function Get-ScratchParent(
    [string] $TempPath = [System.IO.Path]::GetTempPath(),
    [string] $WindowsDir = [Environment]::GetFolderPath('Windows'),
    [string] $LocalAppData = [Environment]::GetFolderPath('LocalApplicationData')) {
    $qualified = { param([string] $p) $p -match '^([A-Za-z]:[\\/]|[\\/]{2}[^\\/])' }
    $under = {
        param([string] $path, [string] $folder)
        $a = [System.IO.Path]::GetFullPath($path).TrimEnd('\', '/')
        $b = [System.IO.Path]::GetFullPath($folder).TrimEnd('\', '/')
        $a.Equals($b, [StringComparison]::OrdinalIgnoreCase) -or $a.StartsWith($b + '\', [StringComparison]::OrdinalIgnoreCase)
    }
    if (-not $WindowsDir.Trim() -or -not (& $qualified $WindowsDir) -or -not (& $under $TempPath $WindowsDir)) { return $TempPath }
    $target = if ($LocalAppData.Trim()) { [System.IO.Path]::Combine($LocalAppData, 'Temp') } else { '' }
    if (-not $target -or -not (& $qualified $target) -or (& $under $target $WindowsDir)) {
        throw "RuleCases needs a temp folder outside the Windows folder: TEMP is '$TempPath', LOCALAPPDATA\Temp is '$target', the Windows folder is '$WindowsDir'"
    }
    $target
}

# Every folder a case makes is made inside one root of the run, fsa-rulecases-<10 hex>, directly in the parent that
# Get-ScratchParent names (no shared base folder, which an unlistable TEMP would keep). The root is made on first use, so a
# run that makes no scratch (C-001) makes none, and its path is printed once. Write-Host, not Write-Output: this function's
# output is the folder it returns. The harness child keeps the TEMP it inherited, so what it makes (fsa-rules-*) is in that.
function New-TempDir([string] $prefix) {
    if (-not $script:ScratchRoot) {
        $root = [System.IO.Path]::Combine((Get-ScratchParent), 'fsa-rulecases-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
        [void][System.IO.Directory]::CreateDirectory($root)
        $script:ScratchRoot = $root
        Write-Host "scratch: $root (TEMP $([System.IO.Path]::GetTempPath()))"
    }
    $dir = Join-Path $script:ScratchRoot ($prefix + [guid]::NewGuid().ToString('N').Substring(0, 10))
    New-Item -ItemType Directory -Path $dir | Out-Null
    $script:Temps.Add($dir)
    $dir
}

# The only way this script removes a folder (SL-098). PowerShell's own cmdlet for it opens a folder by first listing its
# PARENT (the file system provider's IsReparsePointLikeSymlink runs FindFirstFileEx on the folder's own path), and when the
# parent cannot be listed, as C:\WINDOWS\TEMP cannot be by an ordinary user, it throws a terminating Win32Exception(5)
# that -ErrorAction does not suppress: the case stops, and a failure in a finally ends the script before its summary. That
# is a defect of PowerShell's provider, outside this project (github.com/PowerShell/PowerShell/issues/16804), with no
# upstream fix for access denied. [System.IO.Directory]::Delete lists only the folder's own contents, so it works there.
# It stops on a read-only file, which the cmdlet with -Force did not: the fallback clears ReadOnly on every entry, by
# explicit path (attrib /s lists the parent and finds nothing there), walking by hand and removing each link as a link,
# never following it (Delete's own handling of a junction is not relied on: dotnet/runtime#86249), pauses for a scanner
# or indexer holding a file, and deletes once more. What is still there is a counted FAIL; this never throws. No-op when
# the folder is gone. Windows PowerShell 5.1 and pwsh 7: attributes only, no LinkTarget or EnumerationOptions.
function Remove-ScratchDir([string] $Path) {
    $state = @{ Last = $null }
    $reparse = [System.IO.FileAttributes]::ReparsePoint
    $readOnly = [System.IO.FileAttributes]::ReadOnly
    function Clear-ReadOnly([string] $item, [System.IO.FileAttributes] $attributes) {
        if (($attributes -band $readOnly) -ne 0) { [System.IO.File]::SetAttributes($item, $attributes -band (-bnot $readOnly)) }
    }
    function Open-Tree([System.IO.DirectoryInfo] $folder) {
        foreach ($entry in $folder.GetFileSystemInfos()) {
            try {
                if (($entry.Attributes -band $reparse) -ne 0) {
                    if ($entry -is [System.IO.DirectoryInfo]) { [System.IO.Directory]::Delete($entry.FullName) } else { [System.IO.File]::Delete($entry.FullName) }
                }
                elseif ($entry -is [System.IO.DirectoryInfo]) {
                    Open-Tree $entry
                    Clear-ReadOnly $entry.FullName $entry.Attributes
                }
                else { Clear-ReadOnly $entry.FullName $entry.Attributes }
            }
            catch { $state.Last = $_.Exception }
        }
    }
    try {
        if (-not [System.IO.Directory]::Exists($Path)) { return }
        try { [System.IO.Directory]::Delete($Path, $true) } catch { $state.Last = $_.Exception }
        if ([System.IO.Directory]::Exists($Path)) {
            $top = [System.IO.DirectoryInfo]::new($Path)
            $recurse = ($top.Attributes -band $reparse) -eq 0
            try {
                if ($recurse) {
                    Open-Tree $top
                    Clear-ReadOnly $Path $top.Attributes
                }
            }
            catch { $state.Last = $_.Exception }
            Start-Sleep -Milliseconds 250
            try { [System.IO.Directory]::Delete($Path, $recurse) } catch { $state.Last = $_.Exception }
        }
        if ([System.IO.Directory]::Exists($Path)) {
            $why = if ($state.Last) { if ($state.Last.InnerException) { $state.Last.InnerException.Message } else { $state.Last.Message } } else { 'it is still there' }
            Check $false "cleanup: ${Path}: $why"
        }
    }
    catch {
        Check $false "cleanup: ${Path}: $($_.Exception.Message)"
    }
}

function Copy-RepoFile([string] $root, [string] $rel) {
    $target = Join-Path $root $rel
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath (Join-Path $Repo $rel) -Destination $target
}

# The two files the parity check reads, and nothing else.
function New-ParityRoot {
    $root = New-TempDir 'fsa-parity-case-'
    Copy-RepoFile $root $CsRel
    Copy-RepoFile $root $YamlRel
    $root
}

# Every file git lists in this repo (tracked, and untracked but not ignored), under a throwaway git init.
function New-RepoCopy {
    $root = New-TempDir 'fsa-rules-case-'
    $listed = (& git -C $Repo ls-files -co --exclude-standard -z | Out-String) -split "`0"
    foreach ($rel in $listed) {
        if ($rel.Trim().Length -and (Test-Path -LiteralPath (Join-Path $Repo $rel) -PathType Leaf)) { Copy-RepoFile $root $rel }
    }
    & git -C $root init -q
    if ($LASTEXITCODE) { throw "git init in $root failed" }
    $root
}

function Edit-File([string] $root, [string] $rel, [string] $old, [string] $new, [switch] $Pattern, [switch] $All) {
    $path = Join-Path $root $rel
    $text = [System.IO.File]::ReadAllText($path, $Utf8)
    $eol = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    if ($Pattern) {
        $regex = [regex]::new($old)
        if (-not $regex.IsMatch($text)) { throw "case setup: no match for $(Show $old) in $rel" }
        $text = if ($All) { $regex.Replace($text, $new) } else { $regex.Replace($text, $new, 1) }
    }
    else {
        $old = $old.Replace("`n", $eol)
        $new = $new.Replace("`n", $eol)
        $at = $text.IndexOf($old, [StringComparison]::Ordinal)
        if ($at -lt 0) { throw "case setup: $(Show $old) is not in $rel" }
        $text = $text.Substring(0, $at) + $new + $text.Substring($at + $old.Length)
    }
    [System.IO.File]::WriteAllText($path, $text, $Utf8)
}

function Get-LineOf([string] $root, [string] $rel, [string] $needle, [switch] $Exact, [switch] $Last) {
    $lines = [System.IO.File]::ReadAllLines((Join-Path $root $rel), $Utf8)
    $found = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if (($Exact -and $lines[$i] -ceq $needle) -or (-not $Exact -and $lines[$i].Contains($needle))) {
            $found = $i + 1
            if (-not $Last) { return $found }
        }
    }
    if ($found) { return $found }
    throw "case setup: $(Show $needle) is not in $rel"
}

# A copy of one of the harness's row files with one row changed.
function New-RowsFile([string] $name, [string] $id, [scriptblock] $change) {
    $data = [System.IO.File]::ReadAllText((Join-Path $Repo "eng\rules-harness\$name"), $Utf8) | ConvertFrom-Json
    $row = @($data.rows | Where-Object { $_.id -eq $id })[0]
    & $change $row | Out-Null
    $path = Join-Path (New-TempDir 'fsa-rows-case-') $name
    [System.IO.File]::WriteAllText($path, ($data | ConvertTo-Json -Depth 10), $Utf8)
    $path
}

# ---- Running the two tools ----

function Invoke-Parity([string] $shell, [string] $root, [string[]] $more = @()) {
    $ErrorActionPreference = 'Continue'
    $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $Parity -Root $root @more 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Output = $output.Trim() }
}

# One shape, fed to a fresh copy under each shell.
function Test-Parity([string] $what, [scriptblock] $mutate, [int] $exit, [string[]] $contains = @(), [string[]] $more = @()) {
    foreach ($shell in $Shells) {
        $root = New-ParityRoot
        $expected = @(& $mutate $root)
        $run = Invoke-Parity $shell $root $more
        CheckRun $run $exit "$what [$shell]" (@($contains) + $expected)
    }
}

function Invoke-HarnessRaw([string[]] $arguments) {
    if (-not $EnginePython -or -not $EngineRoot) {
        # Out-Host: the FAIL line is printed and counted here, and the caller gets the one object, not an array of both.
        Check $false 'harness cases need -EnginePython and -EngineRoot' | Out-Host
        return [pscustomobject]@{ Exit = -1; Output = '' }
    }
    $ErrorActionPreference = 'Continue'
    $env:PYTHONDONTWRITEBYTECODE = '1'
    $output = & $EnginePython $Harness @arguments 2>&1 | Out-String
    $exit = $LASTEXITCODE
    # A run that did not exit 0 keeps its scratch copy for a person to read; a case that expected that
    # removes it, and only a folder the harness itself made directly in %TEMP%. Only the leaf is read from the
    # line: the harness prints the path as TEMP spells it (a space, an 8.3 short name) and escaped to ASCII by
    # say() (a non-ASCII account name), while mkdtemp's leaf is always ASCII (HF-026). Python's tempfile reads
    # TEMP before TMP and .NET's GetTempPath reads TMP first, so the leaf is looked for directly in each.
    $kept = [regex]::Match($output, '(?m)^harness: scratch kept at .*[\\/](fsa-rules-[A-Za-z0-9_]+)\s*$')
    if ($kept.Success) {
        foreach ($folder in @([System.IO.Path]::GetTempPath(), $env:TEMP) | Where-Object { $_ }) {
            # Out-Host: a cleanup FAIL is printed and counted here, and this function still returns the one object.
            Remove-ScratchDir ([System.IO.Path]::Combine($folder, $kept.Groups[1].Value)) | Out-Host
        }
    }
    [pscustomobject]@{ Exit = $exit; Output = $output.Trim() }
}

function Invoke-Harness([string[]] $arguments) {
    Invoke-HarnessRaw (@('--engine-root', $EngineRoot) + $arguments)
}

# A row of the harness's matrix: id, scenario, batch, xunit recorded/seen, fitness recorded/seen, verdict.
function Test-Rows($run, [string[]] $ids, [string] $verdict, [string] $xunit = '', [string] $fitness = '') {
    foreach ($id in $ids) {
        $rows = @($run.Output -split "`r?`n" | ForEach-Object {
                $m = [regex]::Match($_, "^\s+$([regex]::Escape($id))\s+\S+\s+\d+\s+(\S+)\s+(\S+)\s+(.+?)\s*$")
                if ($m.Success) { $m }
            })
        $held = $rows.Count -eq 1 -and $rows[0].Groups[3].Value -ceq $verdict -and
            (-not $xunit -or $rows[0].Groups[1].Value -ceq $xunit) -and (-not $fitness -or $rows[0].Groups[2].Value -ceq $fitness)
        $what = "row $id reads $verdict"
        if ($xunit) { $what += ", xunit $xunit, fitness $fitness (recorded/seen)" }
        Check $held "$what`n$($run.Output)"
    }
}

# ---- The counts ----

# The Facts, twinned entries and patterns rules-parity must report for this tree, recounted here from the
# tree's own two files by a route of their own (SL-069). Never read from the tool's output, run or
# dot-sourced from it: an expectation read back from the tool agrees with whatever it prints, and
# tests/rules/SettledCountProbe.ps1 -Shape Misclassified fails on that. A Fact is a line that opens with a
# [Fact] or [Theory] attribute in any spelling, so one the tool's reader cannot see is still counted and
# -ExpectFacts catches it; a pattern is a line that is one verbatim @"..." literal ending ',' or ');'; a
# twinned entry is a '  - name:' line above the FIRST '# settled by SL-' marker, however many follow it.
# Worked out on first use and kept for the run, so a tree it cannot count stops only the cases that ask,
# by name. A missing file, no marker or a count of 0 is refused, never replaced by a number.
function Get-RuleCounts {
    if ($null -eq $script:Counts) {
        foreach ($rel in @($CsRel, $YamlRel)) {
            if (-not (Test-Path -LiteralPath (Join-Path $Repo $rel) -PathType Leaf)) { throw "RuleCounts: no $rel under $Repo" }
        }
        $cs = [System.IO.File]::ReadAllLines((Join-Path $Repo $CsRel), $Utf8)
        [int] $facts = @($cs -cmatch '^\s*\[\s*(Fact|Theory)\b').Count
        [int] $patterns = @($cs -cmatch '^\s*@".*"\s*(,|\);)\s*$').Count
        $yaml = [System.IO.File]::ReadAllLines((Join-Path $Repo $YamlRel), $Utf8)
        [int] $twinned = 0
        [int] $marker = -1
        for ($i = 0; $i -lt $yaml.Length -and $marker -lt 0; $i++) {
            if ($yaml[$i] -cmatch '^\s*#\s*settled by SL-') { $marker = $i }
            elseif ($yaml[$i] -cmatch '^  - name:') { $twinned++ }
        }
        if ($marker -lt 0) { throw "RuleCounts: no '# settled by SL-' marker in $YamlRel under $Repo, so nothing parts the twinned entries from the settled ones" }
        if ($facts -eq 0) { throw "RuleCounts: no [Fact] or [Theory] line in $CsRel under $Repo" }
        if ($patterns -eq 0) { throw "RuleCounts: no verbatim @`"...`" pattern line in $CsRel under $Repo" }
        if ($twinned -eq 0) { throw "RuleCounts: no '  - name:' entry above the first settled marker in $YamlRel under $Repo" }
        $script:Counts = [pscustomobject]@{ Facts = $facts; Twinned = $twinned; Patterns = $patterns }
    }
    $script:Counts
}

# Every item of the yaml's rules: list, counted here line by line, less the twinned entries: what C-001 and
# C-003 expect rules-parity to print as settled. Never read from its output, and never per '# settled by'
# marker, since one slice can append several entries under one. Worked out on first use and kept for the
# run, so a yaml it cannot count fails only the cases that ask, never the script or a lone -Case run.
# tests/rules/SettledCountProbe.ps1 feeds C-001 and C-003 grown yamls and broken summary lines.
function Get-ExpectedSettled {
    if ($null -eq $script:ExpectedSettled) {
        $path = Join-Path $Repo $YamlRel
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "ExpectedSettled: fitness rule count unavailable: no $YamlRel under $Repo" }
        $lines = [System.IO.File]::ReadAllLines($path, $Utf8)
        [int] $rules = -1
        [int] $indent = -1
        [int] $count = 0
        for ($i = 0; $i -lt $lines.Length; $i++) {
            if ($rules -lt 0) {
                if ($lines[$i] -cmatch '^rules:') { $rules = $i }
                continue
            }
            $item = [regex]::Match($lines[$i], '^( *)-( |$)')
            if (-not $item.Success) { continue }
            if ($indent -lt 0) { $indent = $item.Groups[1].Length }
            if ($item.Groups[1].Length -eq $indent) { $count++ }
        }
        if ($rules -lt 0 -or $count -eq 0) { throw "ExpectedSettled: fitness rule count unavailable: no rules: list in $path" }
        [int] $twinned = (Get-RuleCounts).Twinned
        [int] $settled = $count - $twinned
        if ($settled -lt 1) { throw "ExpectedSettled: derived count $settled < 1 ($count rules, $twinned twinned)" }
        $script:ExpectedSettled = $settled
    }
    [int] $script:ExpectedSettled
}

# The one spelling of the line rules-parity prints on AGREE (eng/rules-parity.ps1, Main): this tree's counts,
# moved by what a case did to its copy. It takes only those moves, so no case can write a count; each is
# measured against the tree the copy was made from, never recounted from the changed copy.
function Format-AgreeLine([int] $DFacts = 0, [int] $DTwinned = 0, [int] $DPatterns = 0, [int] $DSettled = 0) {
    # Settled first, so a tree with no yaml stops the way ExpectedSettled has always said it does.
    $settled = (Get-ExpectedSettled) + $DSettled
    $counts = Get-RuleCounts
    "rules-parity: AGREE $($counts.Facts + $DFacts) facts <- $($counts.Twinned + $DTwinned) twinned entries, " +
    "$($counts.Patterns + $DPatterns) patterns; $settled settled entries not compared"
}

# The value passed to -ExpectFacts: this tree's Fact count, moved by $DFacts.
function Get-ExpectFacts([int] $DFacts = 0) {
    [string]((Get-RuleCounts).Facts + $DFacts)
}

# ---- The cases ----

function Case-C001 {
    Write-Output 'C-001 the clean tree: its Facts, twinned entries and patterns, the settled entries below the marker'
    # Every count moves with the slices that declare rules, so each comes from this tree's own files.
    $agree = Format-AgreeLine
    $facts = Get-ExpectFacts
    foreach ($shell in $Shells) {
        CheckRun (Invoke-Parity $shell $Repo) 0 "the repo [$shell]" @($agree)
        CheckRun (Invoke-Parity $shell $Repo @('-ExpectFacts', $facts)) 0 "the repo with -ExpectFacts $facts [$shell]" @($agree)
    }
}

function Case-C002 {
    Write-Output 'C-002 a pattern edited or dropped in only one copy'
    Test-Parity 'a typo in one scope entry of a split Fact' {
        param($root)
        Edit-File $root $YamlRel "'(?m)(Library|Dll)Import'" "'(?m)(Library|Dll)Imprt'"
        "yaml entry 'InteropLivesOnlyInNative (src)' (.taad/fitness.yaml:$(Get-LineOf $root $YamlRel "name: 'InteropLivesOnlyInNative (src)'"))"
        "nearest Fact InteropLivesOnlyInNative (tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs:$(Get-LineOf $root $CsRel 'public void InteropLivesOnlyInNative'))"
    } 1 @('rules-parity: DRIFT 1 unpaired', "only in yaml '(Library|Dll)Imprt'; only in C# '(Library|Dll)Import'")

    Test-Parity 'a typo in the twin of a one-scope Fact' {
        param($root)
        Edit-File $root $YamlRel "'(?m)\b(File|Directory|DirectoryInfo|FileInfo|DriveInfo)\.'" "'(?m)\b(File|Directory|DirectoryInfo|FileInfo)\.'"
        "Fact TheScannerMakesNoBclFileSystemCalls (tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs:$(Get-LineOf $root $CsRel 'public void TheScannerMakesNoBclFileSystemCalls')) pairs with no twinned entry"
        "yaml entry 'TheScannerMakesNoBclFileSystemCalls (*.cs)' (.taad/fitness.yaml:$(Get-LineOf $root $YamlRel "name: 'TheScannerMakesNoBclFileSystemCalls (*.cs)'")) pairs with no Fact"
    } 1 @('rules-parity: DRIFT 2 unpaired')

    Test-Parity 'one of a Fact''s five patterns dropped from C#' {
        param($root)
        Edit-File $root $CsRel "            @`"FileSystemEnumerable`",`n" ''
        "Fact TheScannerListsDirectoriesOnlyThroughDirectoryReader (tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs:$(Get-LineOf $root $CsRel 'public void TheScannerListsDirectoriesOnlyThroughDirectoryReader'))"
    } 1 @('rules-parity: DRIFT 2 unpaired', "only in yaml 'FileSystemEnumerable'")

    Test-Parity 'one of a Fact''s five patterns dropped from yaml' {
        param($root)
        Edit-File $root $YamlRel "      - '(?m)AttributesToSkip'`n" ''
        "yaml entry 'TheScannerListsDirectoriesOnlyThroughDirectoryReader (*.cs)'"
    } 1 @('rules-parity: DRIFT 2 unpaired', "only in C# 'AttributesToSkip'")
}

function Case-C003 {
    Write-Output 'C-003 a Fact with no yaml entry, or a yaml entry above the settled marker with no Fact'
    Test-Parity 'the twin of a one-scope Fact deleted' {
        param($root)
        Edit-File $root $YamlRel "(?ms)^  - name: 'TheScannerMakesNoBclFileSystemCalls \(\*\.cs\)'.*?(?=^  - name:|^  # settled by SL-)" '' -Pattern
        "Fact TheScannerMakesNoBclFileSystemCalls (tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs:$(Get-LineOf $root $CsRel 'public void TheScannerMakesNoBclFileSystemCalls')) pairs with no twinned entry"
    } 1 @('rules-parity: DRIFT 1 unpaired')

    Test-Parity 'a split Fact changed, so both of its entries are stale' {
        param($root)
        Edit-File $root $CsRel '@"IsReparsePoint",' '@"IsReparsePointOrLink",'
        'Fact OnlyTheClassifierReadsTheReparseAttribute ('
        "yaml entry 'OnlyTheClassifierReadsTheReparseAttribute (*.cs)'"
        "yaml entry 'OnlyTheClassifierReadsTheReparseAttribute (*.xaml)'"
    } 1 @('rules-parity: DRIFT 3 unpaired')

    $newEntry = "  - name: 'ANewRuleInTheWrongPlace (*.cs)'`n    files: 'src/**/*.cs'`n    must_not_match:`n      - '(?m)NewThing'`n    including_comments: true`n    because: 'probe'`n`n"
    Test-Parity 'a new entry authored above the settled marker' {
        param($root)
        Edit-File $root $YamlRel '  # settled by SL-023' ($newEntry + '  # settled by SL-023')
        "yaml entry 'ANewRuleInTheWrongPlace (*.cs)' (.taad/fitness.yaml:$(Get-LineOf $root $YamlRel "name: 'ANewRuleInTheWrongPlace (*.cs)'")) pairs with no Fact"
    } 1 @('rules-parity: DRIFT 1 unpaired')

    # One settled entry more than this repo's own yaml, counted before the copy is appended to.
    $agree = Format-AgreeLine -DSettled 1
    Test-Parity 'the same entry appended below the marker, as the engine appends' {
        param($root)
        $path = Join-Path $root $YamlRel
        [System.IO.File]::AppendAllText($path, "`r`n  # settled by SL-999`r`n" + $newEntry.Replace("`n", "`r`n"), $Utf8)
    } 0 @($agree)
}

function Case-C004 {
    Write-Output 'C-004 a twinned pattern without (?m), and the BOM guard spelled three ways'
    Test-Parity 'a twinned pattern without (?m)' {
        param($root)
        Edit-File $root $YamlRel "'(?m)IsReparsePoint'" "'IsReparsePoint'"
    } 1 @('rules-parity: DRIFT 1 unpaired', "only in yaml 'IsReparsePoint' (lacks (?m)); only in C# 'IsReparsePoint'")

    # The escape spelled out from [char]92, so no tool along the way turns it into the character. The
    # replacement text of Regex.Replace takes a backslash literally; only $ is special in it.
    $bs = [string][char]92
    $guard = [regex]::Escape("^${bs}ufeff?")
    # A spelling of the guard moves no count.
    $agree = Format-AgreeLine
    foreach ($spelling in @("${bs}ufeff", "${bs}uFEFF", "${bs}uFeFf", [string][char]0xFEFF)) {
        $label = if ($spelling.Length -eq 1) { 'a literal U+FEFF' } else { Show $spelling }
        Test-Parity "the BOM guard written as $label" {
            param($root)
            Edit-File $root $YamlRel $guard ('^' + $spelling + '?') -Pattern -All
            $text = [System.IO.File]::ReadAllText((Join-Path $root $YamlRel), $Utf8)
            if (-not $text.Contains('^' + $spelling + '?')) { throw "case setup: the copy does not carry ^$(Show $spelling)?" }
        } 0 @($agree)
    }

    Test-Parity 'a BOM without the ? is not the guard' {
        param($root)
        Edit-File $root $YamlRel $guard "^${bs}ufeff" -Pattern
    } 1 @('rules-parity: DRIFT 1 unpaired')
}

function Case-C005 {
    Write-Output 'C-005 shapes the readers cannot classify or count exit 2 with file:line, a yaml item after a blank or comment line is read, never AGREE'
    $wpf = '            @"<UseWPF>\s*true",'
    $cs = @(
        @('two literals on one line', ($wpf + "`n" + '            @"<UseWindowsForms>\s*true",'), ($wpf + ' @"<UseWindowsForms>\s*true",'), '@"<UseWPF>\s*true", @"<UseWindowsForms>'),
        @('a concatenation', $wpf, '            @"<UseWPF>\s*" + @"true",', '@"<UseWPF>\s*" + @"true"'),
        @('a raw string', $wpf, '            """<UseWPF>\s*true""",', '"""<UseWPF>'),
        @('a const', $wpf, '            UseWpfPattern,', 'UseWpfPattern,'),
        @('nameof', $wpf, '            nameof(UseWpfPattern),', 'nameof(UseWpfPattern)'),
        @('an interpolated string', $wpf, '            $@"<UseWPF>\s*{true}",', '$@"<UseWPF>')
    )
    foreach ($shape in $cs) {
        $name, $old, $new, $needle = $shape
        Test-Parity "C# $name" {
            param($root)
            Edit-File $root $CsRel $old $new
            "rules-parity: CANNOT-READ tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs:$(Get-LineOf $root $CsRel $needle):"
        } 2
    }

    $noBcl = '        AssertNoMatch(Files(@"src\FsAnalyser.Core\Scanning", "*.cs"),' + "`n" + '            @"\b(File|Directory|DirectoryInfo|FileInfo|DriveInfo)\.");'
    Test-Parity 'a Fact with no AssertNoMatch( call' {
        param($root)
        Edit-File $root $CsRel $noBcl '        Assert.True(true);'
    } 2 @('rules-parity: CANNOT-READ', 'Fact TheScannerMakesNoBclFileSystemCalls makes no AssertNoMatch( call')
    Test-Parity 'a Fact with zero patterns' {
        param($root)
        Edit-File $root $CsRel $noBcl '        AssertNoMatch(Files(@"src\FsAnalyser.Core\Scanning", "*.cs"));'
        "rules-parity: CANNOT-READ tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs:$(Get-LineOf $root $CsRel 'AssertNoMatch(Files(@"src\FsAnalyser.Core\Scanning", "*.cs"));'): AssertNoMatch( closes before any pattern"
    } 2
    Test-Parity 'zero Facts' {
        param($root)
        Edit-File $root $CsRel '\[Fact\]' '[Fect]' -Pattern -All
    } 2 @('rules-parity: CANNOT-READ tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs: zero [Fact]s found')
    # One Fact more than this tree holds, so the reader disagrees whatever the tree's count has grown to.
    $more = Get-ExpectFacts -DFacts 1
    Test-Parity 'a Fact count that differs from the reflected count' { param($root) } 2 `
        @("rules-parity: CANNOT-READ tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs: the reader found $(Get-ExpectFacts) Facts where the test class has $more") @('-ExpectFacts', $more)

    $isReparse = "      - '(?m)IsReparsePoint'"
    $yaml = @(
        @('a double-quoted pattern', '      - "(?m)IsReparsePoint"', 'is a double-quoted scalar'),
        @('a block pattern', ("      - >-`n        (?m)IsReparsePoint"), 'is a block scalar'),
        @('a plain pattern', '      - (?m)IsReparsePoint', 'is a plain scalar')
    )
    foreach ($shape in $yaml) {
        $name, $new, $says = $shape
        Test-Parity "yaml $name" {
            param($root)
            Edit-File $root $YamlRel $isReparse $new
            "rules-parity: CANNOT-READ .taad/fitness.yaml:$(Get-LineOf $root $YamlRel ($new -split "`n")[0]): a pattern of 'OnlyTheClassifierReadsTheReparseAttribute (*.cs)' $says"
        } 2
    }

    # A yaml-only pattern after a blank or comment line: YAML (and so Fitness) still reads it as part of the
    # list, so the reader must too. Reading it gives DRIFT; stopping before it would give AGREE (O-019).
    $last = "      - '(?m)AttributesToSkip'`n"
    $onlyInYaml = "      - '(?m)OnlyInYaml'`n"
    $hidden = @(
        @('after a blank line', "`n"),
        @('after a comment at the key''s indent', "    # a note`n"),
        @('after a comment deeper than the key', "        # a deeper note`n"),
        @('after a blank line and a comment', "`n    # a note`n")
    )
    foreach ($shape in $hidden) {
        $name, $gap = $shape
        Test-Parity "a yaml-only pattern $name" {
            param($root)
            Edit-File $root $YamlRel $last ($last + $gap + $onlyInYaml)
        } 1 @('rules-parity: DRIFT 2 unpaired', "yaml entry 'TheScannerListsDirectoriesOnlyThroughDirectoryReader (*.cs)'", "only in yaml 'OnlyInYaml'")
    }
    # ... and a blank line and a comment between two twinned items hide neither: still AGREE on every count.
    $agree = Format-AgreeLine
    Test-Parity 'a blank line and a comment between two items' {
        param($root)
        Edit-File $root $YamlRel "      - '(?m)EnumerationOptions'`n" ("      - '(?m)EnumerationOptions'`n`n    # a note`n")
    } 0 @($agree)

    # An entry opened by a bare dash, with name: on the next line: legal YAML that Fitness reads as a rule,
    # so the reader must refuse it wherever it sits, never skip it or fold it into its neighbour (O-020).
    $bareBody = "    name: 'AYamlOnlyRule (*.cs)'`n    files: 'src/**/*.cs'`n    must_not_match:`n      - '(?m)OnlyInYaml'`n    including_comments: true`n`n"
    $bare = @(
        @('first after rules:', "rules:`n", ("rules:`n  -`n" + $bareBody), '  -'),
        @('just above the settled marker', '  # settled by SL-023', ("  -`n" + $bareBody + '  # settled by SL-023'), '  -'),
        @('at column 0, first after rules:', "rules:`n", ("rules:`n-`n" + $bareBody), '-')
    )
    foreach ($shape in $bare) {
        $name, $old, $new, $dash = $shape
        Test-Parity "a bare-dash entry $name" {
            param($root)
            Edit-File $root $YamlRel $old $new
            "rules-parity: CANNOT-READ .taad/fitness.yaml:$(Get-LineOf $root $YamlRel $dash -Exact): an entry that does not start with 'name:' on its dash line"
        } 2
    }

    # A column-0 line after 'rules:' changes which list YAML, and so Fitness, reads as the rules: a second
    # 'rules:' replaces the first, and another key takes every entry after it (O-021).
    $oneRule = "  - name: 'OnlyRule (*.cs)'`n    files: 'src/**/*.cs'`n    must_not_match:`n      - '(?m)OnlyInYaml'`n"
    $noBclEntry = "  - name: 'TheScannerMakesNoBclFileSystemCalls (*.cs)'"
    $topLevel = @(
        @('a second rules: block at the end', 'rules:', { param($root) [System.IO.File]::AppendAllText((Join-Path $root $YamlRel), ("`nrules:`n" + $oneRule).Replace("`n", "`r`n"), $Utf8) }),
        @('a column-0 key between twinned entries', 'parked:', { param($root) Edit-File $root $YamlRel $noBclEntry ("parked:`n" + $noBclEntry) }),
        @('a document marker above the settled marker', '---', { param($root) Edit-File $root $YamlRel '  # settled by SL-023' "---`n  # settled by SL-023" })
    )
    foreach ($shape in $topLevel) {
        $name, $line, $insert = $shape
        Test-Parity $name {
            param($root)
            & $insert $root
            "rules-parity: CANNOT-READ .taad/fitness.yaml:$(Get-LineOf $root $YamlRel $line -Exact -Last): a column-0 line after 'rules:'"
        } 2
    }

    # A whole entry hidden inside one CRLF line by a lone CR, U+0085 or U+2028, which Python's universal
    # newlines and YAML break on and this reader would not (O-022). The separator is built from [char].
    $noBclLast = "      - '(?m)\b(File|Directory|DirectoryInfo|FileInfo|DriveInfo)\.'`n    including_comments: true"
    foreach ($code in @(13, 0x85, 0x2028)) {
        $sep = [string][char]$code
        Test-Parity ('an entry hidden behind U+{0:X4}' -f $code) {
            param($root)
            $hidden = $sep + "  - name: 'Hidden (*.cs)'" + $sep + "    files: 'src/**/*.cs'" + $sep + '    must_not_match:' + $sep + "      - '(?m)OnlyInYaml'"
            Edit-File $root $YamlRel $noBclLast ($noBclLast + $hidden)
            'rules-parity: CANNOT-READ .taad/fitness.yaml:{0}: a line break that Python and YAML read and this reader does not (U+{1:X4})' -f `
                ((Get-LineOf $root $YamlRel "'(?m)\b(File|Directory|DirectoryInfo|FileInfo|DriveInfo)\.'") + 1), $code
        } 2
    }

    # The yaml reader is a closed grammar (O-023). Each shape below is legal YAML that PyYAML, and so
    # Fitness, reads otherwise than a line-by-line reading would, and each must be refused at its line.
    $noBclPattern = "'(?m)\b(File|Directory|DirectoryInfo|FileInfo|DriveInfo)\.'"
    $noBclItem = "      - $noBclPattern`n"
    $yamlOnly = "      - '(?m)OnlyInYaml'`n"
    $noBclRule = "'TheScannerMakesNoBclFileSystemCalls (*.cs)'"
    $notAKey = "a line in twinned entry $noBclRule that is not a key at indent 4"
    $dupKeys = @(
        @('bare', '    must_not_match:', "the key must_not_match appears twice in $noBclRule"),
        @('single-quoted', "    'must_not_match':", $notAKey),
        @('double-quoted', '    "must_not_match":', $notAKey),
        @('tagged', '    !!str must_not_match:', $notAKey),
        @('explicit', "    ? must_not_match`n    :", $notAKey)
    )
    foreach ($shape in $dupKeys) {
        $spelling, $key, $says = $shape
        Test-Parity "a second must_not_match key, $spelling, after the real list" {
            param($root)
            Edit-File $root $YamlRel $noBclItem ($noBclItem + $key + "`n" + $yamlOnly)
            "rules-parity: CANNOT-READ .taad/fitness.yaml:$((Get-LineOf $root $YamlRel $noBclPattern) + 1): $says"
        } 2
    }
    Test-Parity 'a must_not_import key in a twinned entry' {
        param($root)
        Edit-File $root $YamlRel $noBclItem ($noBclItem + "    must_not_import:`n      - 'FsAnalyser.App*'`n")
        "rules-parity: CANNOT-READ .taad/fitness.yaml:$((Get-LineOf $root $YamlRel $noBclPattern) + 1): a key must_not_import in twinned entry $noBclRule"
    } 2
    Test-Parity 'a deeper line after a scalar key, which YAML folds into the value' {
        param($root)
        Edit-File $root $YamlRel ($noBclItem + "    including_comments: true`n") ($noBclItem + "    including_comments: true`n" + $yamlOnly)
        "rules-parity: CANNOT-READ .taad/fitness.yaml:$((Get-LineOf $root $YamlRel $noBclPattern) + 2): $notAKey"
    } 2
    Test-Parity 'a plain scalar value in a twinned entry' {
        param($root)
        Edit-File $root $YamlRel "    files: 'src/FsAnalyser.Core/Scanning/**/*.cs'" '    files: src/FsAnalyser.Core/Scanning/**/*.cs'
        "rules-parity: CANNOT-READ .taad/fitness.yaml:$(Get-LineOf $root $YamlRel '    files: src/FsAnalyser.Core/Scanning/**/*.cs' -Exact): files of 'TheScannerListsDirectoriesOnlyThroughDirectoryReader (*.cs)' is a plain scalar"
    } 2
    Test-Parity 'a settled marker inside the last twinned entry''s list' {
        param($root)
        Edit-File $root $YamlRel $noBclItem ($noBclItem + "      # settled by SL-099`n" + $yamlOnly)
        "rules-parity: CANNOT-READ .taad/fitness.yaml:$(Get-LineOf $root $YamlRel '      # settled by SL-099' -Exact): the settled marker sits inside an entry"
    } 2
    Test-Parity 'a key before rules:' {
        param($root)
        Edit-File $root $YamlRel "rules:`n" "parked: 1`nrules:`n"
        "rules-parity: CANNOT-READ .taad/fitness.yaml:$(Get-LineOf $root $YamlRel 'parked: 1' -Exact): a line before 'rules:' that is not blank or a comment"
    } 2
    foreach ($code in @(0xA0, 0x3000, 9)) {
        $lead = [string][char]$code
        Test-Parity ('a column-0 key behind U+{0:X4}' -f $code) {
            param($root)
            Edit-File $root $YamlRel $noBclEntry ($lead + "parked:`n" + $noBclEntry)
            'rules-parity: CANNOT-READ .taad/fitness.yaml:{0}: leading whitespace that is not an ASCII space (U+{1:X4})' -f (Get-LineOf $root $YamlRel ($lead + 'parked:') -Exact), $code
        } 2
    }

    # The C# reader is a closed grammar too (O-024): a Fact's body is '{', one AssertNoMatch( call and
    # '}', and nothing in the file can hide a Fact from it or change what AssertNoMatch does with a pattern.
    $bclFact = 'TheScannerMakesNoBclFileSystemCalls'
    $bclCall = $noBcl + "`n"
    $bclHead = "    [Fact]`n    public void $bclFact()"
    $second = 'ArchitectureRulesTests.AssertNoMatch(Files(@"src\FsAnalyser.Core\Scanning", "*.cs"), @"OnlyInCs");'
    $filesHelper = '    private static IEnumerable<string> Files('
    $declaration = '    private static void AssertNoMatch(IEnumerable<string> files, params string[] patterns)'
    $csShapes = @(
        @('a second, qualified AssertNoMatch call after the call', $bclCall, ($bclCall + "        $second`n"), "        $second",
            "Fact $bclFact names AssertNoMatch more than once"),
        @('a second call behind if (true)', $bclCall, ($bclCall + "        if (true) $second`n"), 'if (true) ArchitectureRulesTests',
            "Fact $bclFact names AssertNoMatch more than once"),
        @('a return before the call', "$bclHead`n    {`n", ("$bclHead`n    {`n        return;`n"), '        return;',
            "a line in Fact $bclFact before its AssertNoMatch( call"),
        @('another statement after the call', $bclCall, ($bclCall + "        Assert.True(true);`n"), '        Assert.True(true);',
            "a line in Fact $bclFact after its AssertNoMatch( call"),
        @('a files argument that reads the files', $noBcl,
            $noBcl.Replace('"*.cs"),', '"*.cs").Where(f => !File.ReadAllText(f).Contains("OnlyInCs")),'), '.Where(f => !File.ReadAllText',
            "Fact $bclFact's files argument uses File"),
        @('an interpolated files argument', $noBcl, $noBcl.Replace('@"src\FsAnalyser.Core\Scanning"', '$"{Environment.CurrentDirectory}"'),
            'Environment.CurrentDirectory', ("Fact $bclFact's files argument holds " + '''$''')),
        @('a Fact in a block comment', $bclHead, ("    /*`n    [Fact]`n    public void Decoy() { }`n    */`n" + $bclHead), '    /*', 'a block comment'),
        @('a Fact behind a preprocessor directive', $bclHead, ("#if NEVER`n" + $bclHead), '#if NEVER', 'a preprocessor directive'),
        @('a Fact written on one line', $filesHelper, ("    [Fact] public void OneLine() => AssertNoMatch(Files(`"src`", `"*.cs`"), @`"OnlyInCs`");`n`n" + $filesHelper),
            '[Fact] public void OneLine()', "an attribute, or a Fact or Theory name, that is not '[Fact]' or '[Theory]' alone on its line"),
        @('a nested class with a Fact', $filesHelper,
            ("    public class Nested`n    {`n        [Fact]`n        public void Decoy()`n        {`n            AssertNoMatch(Files(`"src`", `"*.cs`"),`n                @`"OnlyInCs`");`n        }`n    }`n`n" + $filesHelper),
            '    public class Nested', "a type declaration other than 'public class ArchitectureRulesTests'"),
        @('a second AssertNoMatch declaration', $declaration,
            ("    private static void AssertNoMatch(IEnumerable<string> files, string pattern) => AssertNoMatch(files, pattern, @`"OnlyInCs`");`n`n" + $declaration),
            'string pattern) => AssertNoMatch', "AssertNoMatch named outside a Fact's one call and its one declaration"),
        @('an AssertNoMatch that changes what a pattern means', 'new Regex(p, RegexOptions.Multiline)', 'new Regex(p, RegexOptions.Multiline | RegexOptions.IgnoreCase)',
            $declaration, 'the body of AssertNoMatch is not the one the normaliser rests on'),
        @('another using before the class', "using System.Text.RegularExpressions;`n", "using System.Text.RegularExpressions;`nusing static System.Math;`n",
            'using static System.Math;', 'a line before the class other than')
    )
    foreach ($shape in $csShapes) {
        $label, $anchor, $replacement, $needle, $says = $shape
        Test-Parity "C# $label" {
            param($root)
            Edit-File $root $CsRel $anchor $replacement
            "rules-parity: CANNOT-READ tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs:$(Get-LineOf $root $CsRel $needle): $says"
        } 2
    }
}

function Case-C006 {
    Write-Output 'C-006 the harness cannot reach the engine, or the engine result lacks a field: exit 2 naming the path'
    $missing = Join-Path (New-TempDir 'fsa-engine-case-') 'no-such-engine'
    CheckRun (Invoke-HarnessRaw @('--engine-root', $missing)) 2 'an engine root that does not exist' @("engine root $missing", 'no taad3/fitness.py', 'harness: exit 2')
    CheckRun (Invoke-HarnessRaw @('--engine-root', $Repo)) 2 'an engine root that holds no engine' @("engine root $Repo", 'no taad3/fitness.py')
    CheckRun (Invoke-HarnessRaw @()) 2 'no --engine-root at all (required, no default)' @('--engine-root')

    # A stand-in engine: its check() leaves out 'unran', and its import refuses a TAAD3_HOME other than the
    # run's own scratch (%TEMP%\fsa-rules-*\home), so passing also proves the harness set it before importing.
    $fake = New-TempDir 'fsa-engine-case-'
    New-Item -ItemType Directory -Path (Join-Path $fake 'taad3') | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $fake 'taad3\__init__.py'), '', $Utf8)
    [System.IO.File]::WriteAllText((Join-Path $fake 'taad3\fitness.py'), @'
import os, tempfile, types
_home = os.path.abspath(os.environ.get("TAAD3_HOME") or "/")
_work = os.path.dirname(_home)
if not (os.path.basename(_home) == "home" and os.path.basename(_work).startswith("fsa-rules-")
        and os.path.normcase(os.path.dirname(_work)) == os.path.normcase(os.path.abspath(tempfile.gettempdir()))):
    raise RuntimeError("TAAD3_HOME was not pointed at scratch before the import: " + _home)

def check(tree, build=False):
    return types.SimpleNamespace(verdict="pass", rules=[], violations=[], unmatched=[], content_files=0)
'@, $Utf8)
    $run = Invoke-HarnessRaw @('--engine-root', $fake)
    CheckRun $run 2 'an engine whose result lacks a field' @("engine root $fake", 'the result of taad3.fitness.check lacks unran')
    Check (-not $run.Output.Contains('TAAD3_HOME was not pointed')) "TAAD3_HOME pointed at scratch before the import`n$($run.Output)"
    Check (-not $run.Output.Contains('MISSED') -and -not $run.Output.Contains('harness: exit 1')) 'never reported as the engine disagreeing'

    $broken = New-TempDir 'fsa-engine-case-'
    New-Item -ItemType Directory -Path (Join-Path $broken 'taad3') | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $broken 'taad3\__init__.py'), '', $Utf8)
    [System.IO.File]::WriteAllText((Join-Path $broken 'taad3\fitness.py'), "raise ImportError('this engine is broken')`n", $Utf8)
    CheckRun (Invoke-HarnessRaw @('--engine-root', $broken)) 2 'an engine that fails to import' @("engine root $broken", 'importing taad3.fitness failed')

    # Only an import rule, which needs a code graph the scratch copy has not got, is named NOT RUN and let
    # through (SL-069). A rule the engine did not run that is not an import rule still stops the clean baseline.
    $unran = New-TempDir 'fsa-engine-case-'
    New-Item -ItemType Directory -Path (Join-Path $unran 'taad3') | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $unran 'taad3\__init__.py'), '', $Utf8)
    [System.IO.File]::WriteAllText((Join-Path $unran 'taad3\fitness.py'), @'
import types

def check(tree, build=False):
    rule = types.SimpleNamespace(name="a content rule the engine did not run", must_not_import=[])
    return types.SimpleNamespace(verdict="unable", rules=[rule], violations=[], unmatched=[], unran=[rule], content_files=0)
'@, $Utf8)
    $run = Invoke-HarnessRaw @('--engine-root', $unran, '--scenario', 'clean')
    CheckRun $run 2 'an engine that did not run a rule that is not an import rule' @(
        'the clean baseline is not clean', "fitness rules that did not run: ['a content rule the engine did not run']", 'harness: exit 2')
    Check (-not $run.Output.Contains('NOT RUN')) "that rule is never named NOT RUN and let through`n$($run.Output)"

    # A second taad3 importable from elsewhere on the path, the way a shared venv would hold one (SL-075): the
    # engine root still wins, and the header names its fitness.py. The decoy fails if it is ever imported.
    if (-not $EngineRoot) { Check $false 'the decoy-engine checks need -EngineRoot'; return }
    $engineFile = Join-Path ([System.IO.Path]::GetFullPath($EngineRoot)) 'taad3\fitness.py'
    $decoy = New-TempDir 'fsa-engine-case-'
    New-Item -ItemType Directory -Path (Join-Path $decoy 'taad3') | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $decoy 'taad3\__init__.py'), '', $Utf8)
    [System.IO.File]::WriteAllText((Join-Path $decoy 'taad3\fitness.py'), "raise ImportError('the decoy taad3.fitness was imported')`n", $Utf8)
    $pythonPath = $env:PYTHONPATH
    try {
        $env:PYTHONPATH = $decoy
        $run = Invoke-Harness @('--scenario', 'clean')
    }
    finally { $env:PYTHONPATH = $pythonPath }
    CheckRun $run 0 'a decoy taad3 on PYTHONPATH' @('| CLEAN')
    Check ($run.Output.IndexOf("(taad3.fitness from $engineFile)", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        -not $run.Output.Contains('the decoy taad3.fitness was imported')) "the header names $engineFile, not the decoy`n$($run.Output)"

    # Bites: a decoy already imported before the harness runs (a sitecustomize on the path) is the module the
    # harness gets, and it refuses to measure with it rather than report what the decoy said.
    $preloaded = New-TempDir 'fsa-engine-case-'
    New-Item -ItemType Directory -Path (Join-Path $preloaded 'taad3') | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $preloaded 'taad3\__init__.py'), '', $Utf8)
    [System.IO.File]::WriteAllText((Join-Path $preloaded 'taad3\fitness.py'), "def check(tree, build=False):`n    raise RuntimeError('the decoy was asked to check')`n", $Utf8)
    [System.IO.File]::WriteAllText((Join-Path $preloaded 'sitecustomize.py'), "import taad3.fitness`n", $Utf8)
    try {
        $env:PYTHONPATH = $preloaded
        $run = Invoke-Harness @('--scenario', 'clean')
    }
    finally { $env:PYTHONPATH = $pythonPath }
    CheckRun $run 2 'a decoy taad3.fitness imported before the harness' @('taad3.fitness was imported from', "$preloaded\taad3\fitness.py, not from this root", 'harness: exit 2')
    Check (-not $run.Output.Contains('the decoy was asked to check')) "the decoy's check() is never called`n$($run.Output)"
}

# The names of this tree's import rules (a must_not_import that is not empty), read by the yaml parser of the
# engine's own interpreter, so a name reads as the engine reads it however yaml lets it be spelt: quoted,
# escaped, wrapped onto a second line, folded, or followed by a comment. Each is then spelt as the harness's
# say() prints it, a character past ASCII as its backslash escape (an e-acute as \xe9), and JSON keeps the
# pipe ASCII.
function Get-ImportRuleNames {
    if (-not $EnginePython) { throw 'the import rules are read with -EnginePython, which was not given' }
    $ErrorActionPreference = 'Continue'
    $code = 'import sys, json, yaml; rules = yaml.safe_load(open(sys.argv[1], encoding=''utf-8-sig''))[''rules'']; ' +
        'print(json.dumps([r[''name''].encode(''ascii'', ''backslashreplace'').decode(''ascii'') for r in rules if r.get(''must_not_import'')]))'
    $lines = @(& $EnginePython -c $code (Join-Path $Repo $YamlRel) 2>&1)
    $exit = $LASTEXITCODE
    $json = @($lines | Where-Object { $_ -is [string] })
    if ($exit -or $json.Count -ne 1) { throw "the engine's python did not list the import rules of $YamlRel (exit $exit): $($lines -join "`n")" }
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($name in ($json[0] | ConvertFrom-Json)) { $names.Add([string]$name) }
    , $names
}

function Case-C007 {
    Write-Output 'C-007 the clean scenario: no plants, zero hits in both engines'
    $run = Invoke-Harness @('--scenario', 'clean')
    CheckRun $run 0 'the clean scenario on this repo' @('| CLEAN', "xunit $(Get-ExpectFacts) Facts, 0 hits", 'none unmatched')
    $parity = [regex]::Match($run.Output, '(\d+) twinned entries, \d+ patterns; (\d+) settled')
    $rules = [regex]::Match($run.Output, 'fitness (pass|unable), (\d+) rules')
    Check ($parity.Success -and $rules.Success -and [int]$rules.Groups[2].Value -eq [int]$parity.Groups[1].Value + [int]$parity.Groups[2].Value) `
        "the Fitness rule count equals the yaml entries (twinned + settled)`n$($run.Output)"

    # An import rule (must_not_import) needs a code graph the harness's scratch copy has not got: each is
    # named NOT RUN and is never a pass, and every other rule runs. The names come from this tree's yaml.
    $imports = Get-ImportRuleNames
    if ($imports.Count) {
        # A name yaml ends or breaks with a newline (name: > or name: |) reaches the output as CRLF, the way a
        # console on Windows writes it, and comes from the parser as LF.
        $output = $run.Output.Replace("`r`n", "`n")
        $said = @($imports | Where-Object { -not $output.Contains("'$_'") })
        Check ($rules.Groups[1].Value -ceq 'unable' -and $run.Output.Contains("NOT RUN, not passed: $($imports.Count) import rule(s)") -and $said.Count -eq 0) `
            "the $($imports.Count) import rule(s) are named NOT RUN, and the verdict is not pass (unnamed: $($said -join ', '))`n$($run.Output)"
    }
    else {
        Check ($rules.Groups[1].Value -ceq 'pass' -and $run.Output.Contains('none unran')) "with no import rule, every rule ran and passed`n$($run.Output)"
    }

    # Bites: a hit nobody planted stops the run before any row is judged.
    $copy = New-RepoCopy
    Edit-File $copy 'src\FsAnalyser.Core\Scanning\Scanner.cs' '(?s)\r?\n$' "`r`n// stray: DllImport`r`n" -Pattern
    $run = Invoke-Harness @('--root', $copy, '--dotnet-from', $Repo, '--scenario', 'clean')
    CheckRun $run 2 'a stray violation already in the tree' @('the clean baseline is not clean', 'InteropLivesOnlyInNative', 'src/FsAnalyser.Core/Scanning/Scanner.cs')
}

$MustFire = 1..16 | ForEach-Object { 'F{0:D2}' -f $_ }

function Case-C008 {
    Write-Output 'C-008 the 16 must-fire plants, one per scope of each split Fact, inserted into files that have lines'
    $run = Invoke-Harness @('--scenario', 'plants', '--only', ($MustFire -join ','))
    CheckRun $run 0 'the 16 must-fire plants' @('harness: 16 rows, 16 AS-RECORDED, 0 not, 0 stray hits')
    Test-Rows $run $MustFire 'AS-RECORDED'

    # Bites: a scope entry deleted from yaml is parity's blind spot, and the per-scope plant catches it. The
    # entry is one of this tree's twinned entries, and the Fact keeps its other scope, so only that count moves.
    $copy = New-RepoCopy
    Edit-File $copy $YamlRel "(?ms)^  - name: 'InteropLivesOnlyInNative \(tests\)'.*?(?=^  - name: )" '' -Pattern
    $run = Invoke-Harness @('--root', $copy, '--dotnet-from', $Repo, '--scenario', 'plants', '--only', 'F02,F12')
    CheckRun $run 1 'the tests scope of InteropLivesOnlyInNative deleted from yaml' @((Format-AgreeLine -DTwinned -1))
    Test-Rows $run @('F02') 'AS-RECORDED'
    Test-Rows $run @('F12') 'MISSED-BY-FITNESS'

    # ... and the same scope dropped from the xUnit copy, which parity cannot see either.
    $copy = New-RepoCopy
    Edit-File $copy $CsRel 'Files("src", "*.cs").Concat(Files("tests", "*.cs"))' 'Files("src", "*.cs")'
    $run = Invoke-Harness @('--root', $copy, '--dotnet-from', $Repo, '--scenario', 'plants', '--only', 'F02,F12')
    CheckRun $run 1 'the tests scope of InteropLivesOnlyInNative dropped from xUnit' @((Format-AgreeLine))
    Test-Rows $run @('F02') 'AS-RECORDED'
    Test-Rows $run @('F12') 'MISSED-BY-XUNIT'
}

function Case-C009 {
    Write-Output 'C-009 plants in the exempted files (P07c-P07e) stay quiet in both engines'
    $run = Invoke-Harness @('--scenario', 'plants', '--only', 'P07c,P07d,P07e')
    CheckRun $run 0 'the three exempt-file plants' @('harness: 3 rows, 3 AS-RECORDED')
    Test-Rows $run @('P07c', 'P07d', 'P07e') 'AS-RECORDED'

    # Bites: the same plant in a file that is not exempt reads FIRED-WHEN-EXEMPT ...
    $rows = New-RowsFile 'plants.json' 'P07d' { param($row) $row.file = 'src/FsAnalyser.Core/Scanning/DirectoryReader.cs' }
    $run = Invoke-Harness @('--plants', $rows, '--scenario', 'plants', '--only', 'P07d')
    CheckRun $run 1 'the classifier plant moved out of the exempted file' @('FIRED-WHEN-EXEMPT')
    Test-Rows $run @('P07d') 'FIRED-WHEN-EXEMPT'

    # ... and an exemption dropped from yaml fails the clean baseline, because the exempted file really
    # holds what the rule forbids.
    $copy = New-RepoCopy
    Edit-File $copy $YamlRel "      - 'src/FsAnalyser.Core/Scanning/EntryClassifier.cs'`n" ''
    $run = Invoke-Harness @('--root', $copy, '--dotnet-from', $Repo, '--scenario', 'plants', '--only', 'P07d')
    CheckRun $run 2 'the classifier exemption dropped from yaml' @('rules-parity: AGREE', 'the clean baseline is not clean', 'src/FsAnalyser.Core/Scanning/EntryClassifier.cs')
}

function Case-C010 {
    Write-Output 'C-010 a violating file under packages/ or dist/ (D1, D2): xUnit fires, Fitness does not walk there'
    $run = Invoke-Harness @('--scenario', 'divergences', '--only', 'D1,D2')
    CheckRun $run 0 'D1 and D2' @('harness: 2 rows, 2 AS-RECORDED')
    Test-Rows $run @('D1', 'D2') 'AS-RECORDED' 'fire/fire' 'quiet/quiet'
}

function Case-C011 {
    Write-Output 'C-011 UTF-16LE, cp1252 and over-2-MB files, each carrying a violation'
    $run = Invoke-Harness @('--scenario', 'divergences', '--only', 'D6,D7,D10,D11,D13')
    CheckRun $run 0 'D6, D7, D10, D11 and D13' @('harness: 5 rows, 5 AS-RECORDED')
    Test-Rows $run @('D6', 'D7', 'D10', 'D11') 'AS-RECORDED' 'fire/fire' 'quiet/quiet'
    Test-Rows $run @('D13') 'AS-RECORDED' 'fire/fire' 'fire/fire'
}

function Case-C012 {
    Write-Output 'C-012 a case-only rename of an exempted file, in two steps through a temporary name'
    $run = Invoke-Harness @('--scenario', 'divergences', '--only', 'D3,D4,D9')
    CheckRun $run 0 'D3, D4 and D9' @('harness: 3 rows, 3 AS-RECORDED')
    Test-Rows $run @('D3', 'D4', 'D9') 'AS-RECORDED' 'fire/fire' 'quiet/quiet'

    # Bites: a rename that is not a case-only change is PLANT-BROKEN, never a silent no-op.
    $rows = New-RowsFile 'divergences.json' 'D3' { param($row) $row.to = 'EntryClassifier.cs' }
    $run = Invoke-Harness @('--divergences', $rows, '--scenario', 'divergences', '--only', 'D3')
    CheckRun $run 1 'a rename to the same name' @('is not a case-only change')
    Test-Rows $run @('D3') 'PLANT-BROKEN'
}

function Case-C013 {
    Write-Output 'C-013 a using directive split across two lines (D5): Fitness fires, xUnit does not'
    $run = Invoke-Harness @('--scenario', 'divergences', '--only', 'D5')
    CheckRun $run 0 'D5' @('harness: 1 rows, 1 AS-RECORDED')
    Test-Rows $run @('D5') 'AS-RECORDED' 'quiet/quiet' 'fire/fire'
}

function Case-C014 {
    Write-Output 'C-014 a recorded divergence both engines now agree on fails as DIVERGENCE-CLOSED'
    # D13 is an agreement both engines give; recording it as a divergence is what an engine fix looks like.
    $rows = New-RowsFile 'divergences.json' 'D13' { param($row) $row.expect.fitness = 'quiet' }
    $run = Invoke-Harness @('--divergences', $rows, '--scenario', 'divergences', '--only', 'D13')
    CheckRun $run 1 'a row recorded as a divergence that both engines agree on' @('DIVERGENCE-CLOSED', 'delete row D13 from divergences.json', 'harness: exit 1')
    Test-Rows $run @('D13') 'DIVERGENCE-CLOSED'
}

# ---- Main ----

$cases = if ($Case -eq 'All') { 1..14 | ForEach-Object { 'C-{0:D3}' -f $_ } } else { @($Case) }
try {
    foreach ($c in $cases) {
        try { & "Case-$($c.Replace('-', ''))" }
        catch { Check $false "$c stopped: $($_.Exception.Message)" }
    }
}
finally {
    # None of these can throw, so the summary below always runs; the root goes last.
    foreach ($dir in $script:Temps) { Remove-ScratchDir $dir }
    if ($script:ScratchRoot) { Remove-ScratchDir $script:ScratchRoot }
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
