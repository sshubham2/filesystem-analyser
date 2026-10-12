<#
SL-104 (O-031, and review M1 and M2): the class the widened settled rules close, as a table.

The settled rules for timestamp and attribute writers, for directory listings and for the Attributes setter are regexes over
source text. They were found short twice, one spelling at a time: R2-m4 (File.SetAttributes, instance GetFiles and
GetDirectories) and then review M1 (the same property setters with a compound operator: fi.LastWriteTimeUtc += span,
fi.Attributes -= flag, fi.IsReadOnly |= flag) and M2 (FileInfo.Encrypt() beside File.Encrypt). tests/bugs/SL-104-SettledRuleClassCorpus.json
lists the whole class: every property setter with every operator its type defines, every writer and listing as a File or
Directory static, a FileInfo or DirectoryInfo instance, a method group, a using static call and a fully qualified name, the Win32 and NT
names with their Zw alias, the reads and look-alikes that must stay quiet, the spellings a text rule cannot see (residual), and a
census of every public member of File, Directory, FileInfo, DirectoryInfo and FileSystemInfo that writes a time or an attribute or
lists a directory.

This reads the patterns from .taad/fitness.yaml (never a copy of them) and matches each row's lines, joined by a line feed, as one
.NET Regex with RegexOptions.Multiline, which is how the engine reads a whole file. -Fitness points it at another copy of the rules
file, for the rules as they stood before the fix:

  git show 824622e:.taad/fitness.yaml > $env:TEMP\fitness-before.yaml
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-104-SettledRuleClassCorpus.Tests.ps1 -Fitness $env:TEMP\fitness-before.yaml

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-104-SettledRuleClassCorpus.Tests.ps1

Exit 0 when every check held, 1 when any failed. -Measure prints each row's hit count and judges nothing.
#>
param(
    [string] $Fitness,
    [string] $Corpus,
    [switch] $Measure
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Fitness) { $Fitness = Join-Path $Repo '.taad\fitness.yaml' }
if (-not $Corpus) { $Corpus = Join-Path $PSScriptRoot 'SL-104-SettledRuleClassCorpus.json' }
$Multiline = [System.Text.RegularExpressions.RegexOptions]::Multiline
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) { if (-not $Measure) { Write-Output "  ok    $what" } }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

# One entry of the rules file, by its whole '  - name: ...' header line: its must_not_match patterns (single-quoted or plain
# scalars on lines of their own, '' read as one quote) and its except_files. $null when the entry is absent.
function Read-Entry([string[]] $lines, [string] $name) {
    $header = "  - name: $name"
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].TrimEnd() -ceq $header) { $start = $i; break } }
    if ($start -lt 0) { return $null }
    $end = $lines.Count
    for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^  - name: ') { $end = $i; break } }
    $patterns = New-Object System.Collections.Generic.List[regex]
    $except = New-Object System.Collections.Generic.List[string]
    for ($i = $start + 1; $i -lt $end; $i++) {
        $key = $lines[$i].TrimEnd()
        if ($key -ceq '    must_not_match:' -or $key -ceq '    except_files:') {
            for ($n = $i + 1; $n -lt $end; $n++) {
                $m = [regex]::Match($lines[$n], '^\s+- (.*?)\s*$')
                if (-not $m.Success) { break }
                $value = $m.Groups[1].Value
                $quoted = [regex]::Match($value, "^'(.*)'$")
                if ($quoted.Success) { $value = $quoted.Groups[1].Value.Replace("''", "'") }
                elseif ($value.StartsWith('"')) { throw "the entry '$name' has a double-quoted scalar this reader does not read: $value" }
                if ($key -ceq '    must_not_match:') { $patterns.Add([regex]::new($value, $Multiline)) } else { $except.Add($value) }
            }
        }
    }
    [pscustomobject]@{ Name = $name; Patterns = $patterns.ToArray(); Except = $except.ToArray() }
}

function Hit-Count($entry, [string[]] $lines) {
    if (-not $entry) { return 0 }
    $text = $lines -join "`n"
    @($entry.Patterns | Where-Object { $_.IsMatch($text) }).Count
}

$yaml = [IO.File]::ReadAllLines($Fitness)
$table = [IO.File]::ReadAllText($Corpus) | ConvertFrom-Json
$entries = @{}
foreach ($k in 'timestamp', 'listing', 'attributes') {
    $name = $table.entries.$k
    $entries[$k] = Read-Entry $yaml $name
    if ($entries[$k]) {
        if (-not $Measure) { Write-Output "$($entries[$k].Patterns.Count) patterns read for the $k entry '$name'" }
    }
    elseif ($k -ne 'attributes') { throw "${Fitness} has no entry '$name'" }
    elseif (-not $Measure) { Write-Output "no entry '$name' in ${Fitness}: its rows have no pattern to fire" }
}

if ($Measure) {
    foreach ($row in $table.rows) { "{0}`t{1}`t{2}" -f $row.id, $row.rule, (Hit-Count $entries[$row.rule] $row.lines) }
    foreach ($c in $table.census) { "{0}`t{1}`t{2}" -f ('CE:' + $c.type + '.' + $c.member), $c.rule, (Hit-Count $entries[$c.rule] @($c.sample)) }
    exit 0
}

$ids = @($table.rows | ForEach-Object { $_.id }) + @($table.census | ForEach-Object { 'CE:' + $_.type + '.' + $_.member })
Check (@($ids | Sort-Object -Unique).Count -eq $ids.Count) 'no row id or census member appears twice'
Check (@($table.rows | Where-Object { $_.expect -cnotin 'fire', 'quiet', 'residual' }).Count -eq 0) 'every row expects fire, quiet or residual'

$tally = @{}
foreach ($row in $table.rows) {
    $hits = Hit-Count $entries[$row.rule] $row.lines
    $shown = ($row.lines | ForEach-Object { $_.Trim() }) -join ' / '
    $label = "$($row.id) [$($row.family)] $($row.rule) $($row.expect), $hits hit(s): $shown"
    switch -CaseSensitive ($row.expect) {
        'fire' { Check ($hits -ge 1) $label }
        'quiet' { Check ($hits -eq 0) $label }
        'residual' { Check ($hits -eq 0) "$label  <- a residual that now matches: reclassify it to fire and strike it from the rule's because-text" }
    }
    if ($row.exempt) {
        Check ($entries[$row.rule] -and $entries[$row.rule].Except -contains $row.exempt) "$($row.id) is kept quiet only by except_files naming $($row.exempt)"
    }
    $tally["$($row.expect)"] = 1 + $(if ($tally.ContainsKey("$($row.expect)")) { $tally["$($row.expect)"] } else { 0 })
}

# The Attributes entry excepts exactly the three files the other two entries' exceptions imply.
$expectedExcept = 'src/FsAnalyser.Core/Native/Kernel32.cs', 'src/FsAnalyser.Core/Scanning/DirectoryReader.cs', 'src/FsAnalyser.Core/Scanning/SynchronousIoCancel.cs'
$actualExcept = if ($entries['attributes']) { @($entries['attributes'].Except | Sort-Object) } else { @() }
Check ((($actualExcept -join '|') -ceq ($expectedExcept -join '|'))) "the Attributes entry excepts exactly Kernel32.cs, DirectoryReader.cs and SynchronousIoCancel.cs"

# The census: one sample per member, judged by its disposition.
foreach ($c in $table.census) {
    $hits = Hit-Count $entries[$c.rule] @($c.sample)
    $label = "$($c.type).$($c.member) is $($c.disposition) in the $($c.rule) entry, $hits hit(s): $($c.sample)"
    if ($c.disposition -ceq 'fenced') { Check ($hits -ge 1) $label } else { Check ($hits -eq 0) $label }
}

# Every member of the five types that writes or lists is in the census: a new BCL member is a finding, not a silent gap. The
# runtime must be .NET 10 (pwsh 7.6), the one the product runs on; Windows PowerShell 5.1 enumerates .NET Framework.
if ($PSVersionTable.PSEdition -ceq 'Core' -and [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription -like '.NET 10.*') {
    $known = @{}
    foreach ($c in $table.census) { $known[$c.type + '.' + $c.member] = $true }
    $flags = [System.Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly'
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($type in [System.IO.File], [System.IO.Directory], [System.IO.FileInfo], [System.IO.DirectoryInfo], [System.IO.FileSystemInfo]) {
        foreach ($name in ($type.GetMethods($flags) | ForEach-Object { $_.Name } | Sort-Object -Unique)) {
            $family = $name -cmatch '^(Set|set_)' -or $name -cmatch '^(Encrypt|Decrypt)$' -or $name -cmatch '^(Get|Enumerate)(Files|Directories|FileSystemEntries|FileSystemInfos)$'
            if ($family -and -not $known.ContainsKey($type.Name + '.' + $name)) { $missing.Add($type.Name + '.' + $name) }
        }
    }
    Check ($missing.Count -eq 0) "the census names every Set*, set_*, Encrypt, Decrypt and listing member of the five types ($($missing -join ', ') missing)"
}
else {
    Write-Output "  skip  the live census check needs pwsh on .NET 10 (this is $($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion))"
}

Write-Output ''
Write-Output ("{0} rows: {1} fire, {2} quiet, {3} residual; {4} census members" -f $table.rows.Count, $tally['fire'], $tally['quiet'], $tally['residual'], $table.census.Count)
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
