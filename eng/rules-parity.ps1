# Checks that the two copies of the architecture rules still hold the same patterns (SL-031): the xUnit
# twin, tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs, and the twinned entries of .taad/fitness.yaml
# that the engine's Fitness check runs. It reads the two files as UTF-8 text and writes nothing.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File eng/rules-parity.ps1 [-Root <repo>] [-ExpectFacts <n>]
#
# Exit 0 AGREE. Exit 1 DRIFT, followed by up to 10 lines, each naming an entry or Fact that pairs with
# nothing, where it is, the nearest one on the other side, and the patterns that differ. Exit 2
# CANNOT-READ: a file is missing, a shape either reader cannot classify, zero Facts or patterns, a Fact
# count that differs from -ExpectFacts, or a self-test that did not see drift. Not being able to check
# never reads as agreement. The first line of output is the verdict, and all output is ASCII.
#
# PATTERNS ONLY. Scopes and exemptions are written in different notations in the two copies, and the two
# engines walk, decode and match files differently. Both are the job of eng/rules-harness/harness.py,
# which needs the TAAD engine. RuleCopiesAgreeTests runs this script on every test run.
#
# What the readers accept, and they refuse everything else:
#   C#   Each [Fact] or [Theory] of ArchitectureRulesTests makes one AssertNoMatch( call. Its first
#        argument (the files) ends a line, and every line after it is one verbatim @"..." literal
#        ending ',' or ');'.
#   yaml Entries at two spaces ('  - name:'), read up to the first '# settled by SL-' line. Entries below
#        it have no xUnit twin and are only counted. Each must_not_match item is one single-quoted scalar.
# The normaliser is the yaml header's authoring rules and nothing else. A twinned yaml pattern must start
# with (?m), which is stripped: xUnit applies RegexOptions.Multiline to every pattern, so a yaml pattern
# without it is drift. '^\ufeff?' (the escape in either case, or a literal U+FEFF) reads as '^'. '' in
# yaml and "" in C# read as one quote. Entries pair with Facts by the multiset of their normalised
# patterns, so a Fact split across two scopes pairs with both of its entries. A scope entry deleted from
# one of those two is invisible here; the harness's per-scope plants are what catch it.

param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    # A string, so a bad value is CANNOT-READ (exit 2) and not a binding error, whose exit 1 reads as DRIFT.
    [string] $ExpectFacts = ''
)

$ErrorActionPreference = 'Stop'

$CsRel = 'tests/FsAnalyser.Core.Tests/ArchitectureRulesTests.cs'
$YamlRel = '.taad/fitness.yaml'
$Bom = [string][char]0xFEFF
$BomGuard = '\^(?:\\u[fF][eE][fF][fF]|' + $Bom + ')\?'
$NoMultiline = [string][char]1
$MaxShown = 10

# Non-ASCII shown as \uXXXX, so the output reads the same whatever the console's code page.
function Show([string] $text) {
    $builder = New-Object System.Text.StringBuilder
    foreach ($c in $text.ToCharArray()) {
        if ([int]$c -lt 32 -or [int]$c -gt 126) { [void]$builder.AppendFormat('\u{0:X4}', [int]$c) } else { [void]$builder.Append($c) }
    }
    $builder.ToString()
}

function Show-Name([string] $name) { "'$(Show $name)'" }

function Stop-Reading([string] $where, [string] $why) {
    throw [System.IO.InvalidDataException]::new("${where}: $why")
}

function Read-Lines([string] $rel) {
    $path = Join-Path $Root $rel
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Stop-Reading $rel "file not found under $Root" }
    $text = [System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))
    if ($text.StartsWith($Bom, [StringComparison]::Ordinal)) { $text = $text.Substring(1) }
    return , ($text -split "`r?`n")
}

# ---- The C# copy ----

# Where the files argument of the AssertNoMatch( call starting at ($line, $col) ends: the line holding
# its comma. Strings and char literals are skipped, so a paren or comma inside one does not count.
function Find-FilesArgumentEnd([string[]] $lines, [int] $line, [int] $col) {
    $depth = 0
    for ($i = $line; $i -lt $lines.Count; $i++) {
        $s = $lines[$i]
        $c = if ($i -eq $line) { $col } else { 0 }
        while ($c -lt $s.Length) {
            $ch = $s[$c]
            if ($ch -ceq '@' -and $c + 1 -lt $s.Length -and $s[$c + 1] -ceq '"') {
                $c += 2
                while ($true) {
                    if ($c -ge $s.Length) { Stop-Reading "${CsRel}:$($i + 1)" 'a verbatim string runs onto the next line' }
                    if ($s[$c] -ceq '"') {
                        if ($c + 1 -lt $s.Length -and $s[$c + 1] -ceq '"') { $c += 2; continue }
                        $c++
                        break
                    }
                    $c++
                }
                continue
            }
            if ($ch -ceq '"') {
                if ($s.Substring($c).StartsWith('"""', [StringComparison]::Ordinal)) { Stop-Reading "${CsRel}:$($i + 1)" 'a raw string literal' }
                $c++
                while ($true) {
                    if ($c -ge $s.Length) { Stop-Reading "${CsRel}:$($i + 1)" 'a string runs onto the next line' }
                    if ($s[$c] -ceq '\') { $c += 2; continue }
                    if ($s[$c] -ceq '"') { $c++; break }
                    $c++
                }
                continue
            }
            if ($ch -ceq "'") {
                $close = $s.IndexOf("'", $c + $(if ($c + 1 -lt $s.Length -and $s[$c + 1] -ceq '\') { 3 } else { 2 }))
                if ($close -lt 0) { Stop-Reading "${CsRel}:$($i + 1)" 'a char literal the reader cannot close' }
                $c = $close + 1
                continue
            }
            if ($ch -ceq '(' -or $ch -ceq '[') { $depth++ }
            elseif ($ch -ceq ')' -or $ch -ceq ']') {
                $depth--
                if ($depth -lt 0) { Stop-Reading "${CsRel}:$($i + 1)" 'AssertNoMatch( closes before any pattern' }
            }
            elseif ($ch -ceq ',' -and $depth -eq 0) {
                if ($s.Substring($c + 1).Trim().Length -ne 0) {
                    Stop-Reading "${CsRel}:$($i + 1)" 'the files argument of AssertNoMatch( must end its line, with each pattern on a line of its own'
                }
                return $i
            }
            $c++
        }
    }
    Stop-Reading "${CsRel}:$($line + 1)" 'AssertNoMatch( never reaches its patterns'
}

function Read-Facts {
    $lines = Read-Lines $CsRel
    $attribute = '^\s*\[(Fact|Theory)(\(.*\))?\]\s*$'
    $signature = '^\s*public\s+(?:async\s+)?(?:void|Task)\s+(\w+)\s*\('
    $callStart = '^\s*AssertNoMatch\('
    $literal = '^\s*@"((?:[^"]|"")*)"\s*(,|\);)\s*$'

    $attributeLines = @(for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -cmatch $attribute) { $i } })
    $facts = New-Object System.Collections.Generic.List[object]
    for ($k = 0; $k -lt $attributeLines.Count; $k++) {
        $start = $attributeLines[$k]
        $end = if ($k + 1 -lt $attributeLines.Count) { $attributeLines[$k + 1] } else { $lines.Count }
        $j = $start + 1
        while ($j -lt $end -and $lines[$j] -cmatch '^\s*\[') { $j++ }
        if ($j -ge $end -or $lines[$j] -cnotmatch $signature) {
            Stop-Reading "${CsRel}:$($start + 1)" 'a [Fact] that is not followed by a public method the reader can name'
        }
        $name = $Matches[1]
        $calls = @(for ($i = $j + 1; $i -lt $end; $i++) { if ($lines[$i] -cmatch $callStart) { $i } })
        if ($calls.Count -eq 0) { Stop-Reading "${CsRel}:$($j + 1)" "Fact $name makes no AssertNoMatch( call" }
        if ($calls.Count -gt 1) { Stop-Reading "${CsRel}:$($calls[1] + 1)" "Fact $name makes more than one AssertNoMatch( call" }

        $call = $calls[0]
        $afterFiles = (Find-FilesArgumentEnd $lines $call ($lines[$call].IndexOf('AssertNoMatch(') + 'AssertNoMatch('.Length)) + 1
        $patterns = New-Object System.Collections.Generic.List[string]
        $closed = $false
        for ($i = $afterFiles; $i -lt $end -and -not $closed; $i++) {
            if ($lines[$i] -cnotmatch $literal) {
                Stop-Reading "${CsRel}:$($i + 1)" "a line in Fact $name's AssertNoMatch( call that is not one verbatim @`"...`" literal ending ',' or ');': $(Show $lines[$i].Trim())"
            }
            $patterns.Add($Matches[1].Replace('""', '"'))
            $closed = $Matches[2] -ceq ');'
        }
        if (-not $closed) { Stop-Reading "${CsRel}:$($call + 1)" "Fact $name's AssertNoMatch( call never ends with ');'" }
        if ($patterns.Count -eq 0) { Stop-Reading "${CsRel}:$($call + 1)" "Fact $name has zero patterns" }
        $facts.Add([pscustomobject]@{ Name = $name; Where = "${CsRel}:$($j + 1)"; Patterns = $patterns.ToArray() })
    }
    return , $facts.ToArray()
}

# ---- The yaml copy ----

function Read-SingleQuoted([string] $value, [string] $where, [string] $what) {
    if ($value -cmatch "^'((?:[^']|'')*)'\s*(#.*)?$") { return $Matches[1].Replace("''", "'") }
    $kind = if ($value.StartsWith('"')) { 'a double-quoted scalar' }
        elseif ($value -cmatch '^[|>]') { 'a block scalar' }
        elseif ($value.StartsWith("'")) { 'a single-quoted scalar that does not end on its line' }
        else { 'a plain scalar' }
    Stop-Reading $where "$what is $kind; only a single-quoted scalar on one line is read: $(Show $value)"
}

function Read-Entries {
    $lines = Read-Lines $YamlRel
    $rulesAt = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -cmatch '^rules:\s*$') { $rulesAt = $i; break } }
    if ($rulesAt -lt 0) { Stop-Reading $YamlRel "no top-level 'rules:' line" }
    $markerAt = $lines.Count
    for ($i = $rulesAt + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -cmatch '^\s*#\s*settled by SL-') { $markerAt = $i; break } }

    $starts = New-Object System.Collections.Generic.List[int]
    $settled = 0
    for ($i = $rulesAt + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch '^(\s*)-\s+name\s*:') {
            if ($Matches[1] -cne '  ') { Stop-Reading "${YamlRel}:$($i + 1)" 'an entry that is not indented two spaces' }
            if ($i -lt $markerAt) { $starts.Add($i) } else { $settled++ }
        }
        # Any other dash at the entries' depth or shallower, including a bare '  -' with name: on the next
        # line, is an entry YAML reads and this does not: refuse it. List items sit deeper and never match.
        elseif ($lines[$i] -cmatch '^ {0,2}-(\s|$)') { Stop-Reading "${YamlRel}:$($i + 1)" "an entry that does not start with 'name:' on its dash line" }
    }

    $entries = New-Object System.Collections.Generic.List[object]
    for ($k = 0; $k -lt $starts.Count; $k++) {
        $start = $starts[$k]
        $end = if ($k + 1 -lt $starts.Count) { $starts[$k + 1] } else { $markerAt }
        [void]($lines[$start] -cmatch '^  -\s+name\s*:\s*(.*)$')
        $rawName = $Matches[1].Trim()
        $name = if ($rawName.StartsWith("'")) { Read-SingleQuoted $rawName "${YamlRel}:$($start + 1)" 'the name' } else { $rawName }
        $keys = @(for ($i = $start + 1; $i -lt $end; $i++) { if ($lines[$i] -cmatch '^\s+must_not_match\s*:') { $i } })
        if ($keys.Count -eq 0) { Stop-Reading "${YamlRel}:$($start + 1)" "twinned entry $(Show-Name $name) has no must_not_match" }
        if ($keys.Count -gt 1) { Stop-Reading "${YamlRel}:$($keys[1] + 1)" "twinned entry $(Show-Name $name) has two must_not_match keys" }
        $key = $keys[0]
        [void]($lines[$key] -cmatch '^(\s+)must_not_match\s*:\s*(.*)$')
        $indent = $Matches[1].Length
        if ($Matches[2].Trim().Length -ne 0) {
            Stop-Reading "${YamlRel}:$($key + 1)" "must_not_match of $(Show-Name $name) is not a block list with one item per line"
        }
        # YAML carries a list on past blank and comment lines, so this does too: stopping at one would leave
        # the items after it unread here while Fitness still enforces them. The list ends only at a line
        # that is neither, indented no deeper than the key; the loop never passes the next entry ($end).
        $patterns = New-Object System.Collections.Generic.List[string]
        for ($i = $key + 1; $i -lt $end; $i++) {
            $line = $lines[$i]
            if ($line -cmatch '^(\s*)-\s+(.*)$' -and $Matches[1].Length -ge $indent) {
                $patterns.Add((Read-SingleQuoted $Matches[2].Trim() "${YamlRel}:$($i + 1)" "a pattern of $(Show-Name $name)"))
                continue
            }
            if ($line.Trim().Length -eq 0 -or $line -cmatch '^\s*#') { continue }
            if (($line.Length - $line.TrimStart().Length) -le $indent) { break }
            Stop-Reading "${YamlRel}:$($i + 1)" "a pattern of $(Show-Name $name) runs onto the next line"
        }
        if ($patterns.Count -eq 0) { Stop-Reading "${YamlRel}:$($key + 1)" "twinned entry $(Show-Name $name) has zero patterns" }
        $entries.Add([pscustomobject]@{ Name = $name; Where = "${YamlRel}:$($start + 1)"; Patterns = $patterns.ToArray() })
    }
    return [pscustomobject]@{ Entries = $entries.ToArray(); Settled = $settled; MarkerFound = $markerAt -lt $lines.Count }
}

# ---- Normalising and pairing ----

function Get-Normalised([string] $pattern, [bool] $fromYaml) {
    if ($fromYaml) {
        if (-not $pattern.StartsWith('(?m)', [StringComparison]::Ordinal)) { return $NoMultiline + $pattern }
        $pattern = $pattern.Substring(4)
    }
    return [regex]::Replace($pattern, $BomGuard, '^')
}

function Show-Pattern([string] $normalised) {
    if ($normalised.StartsWith($NoMultiline, [StringComparison]::Ordinal)) { return "'$(Show $normalised.Substring(1))' (lacks (?m))" }
    "'$(Show $normalised)'"
}

function Get-Key([string[]] $normalised) {
    $sorted = [string[]]$normalised.Clone()
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    return [string]::Join("`n", $sorted)
}

# The patterns of $a that $b lacks, counting repeats. Ordinal: pwsh 7's -ceq compares by culture, and
# under ICU that ignores characters such as U+0001 and U+FEFF.
function Get-Missing([string[]] $a, [string[]] $b) {
    $rest = New-Object System.Collections.Generic.List[string]
    foreach ($p in $b) { $rest.Add($p) }
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($p in $a) {
        $at = -1
        for ($i = 0; $i -lt $rest.Count; $i++) { if ([string]::Equals($rest[$i], $p, [StringComparison]::Ordinal)) { $at = $i; break } }
        if ($at -ge 0) { $rest.RemoveAt($at) } else { $out.Add($p) }
    }
    return , $out.ToArray()
}

function Get-CommonPrefix([string] $a, [string] $b) {
    $n = [Math]::Min($a.Length, $b.Length)
    $i = 0
    while ($i -lt $n -and [int]$a[$i] -eq [int]$b[$i]) { $i++ }
    $i
}

# Only for naming the other side in a drift line: most patterns shared, then fewest differing, then the
# unshared patterns closest to one of the other's by common prefix (so a one-character typo finds its twin).
function Get-Nearest($item, [object[]] $others) {
    $best = $null
    $bestScore = $null
    foreach ($o in $others) {
        $missing = Get-Missing $item.Normalised $o.Normalised
        $extra = Get-Missing $o.Normalised $item.Normalised
        $prefix = 0
        foreach ($p in $missing) {
            $top = 0
            foreach ($q in $o.Normalised) { $top = [Math]::Max($top, (Get-CommonPrefix $p $q)) }
            $prefix += $top
        }
        $score = @(($item.Normalised.Count - $missing.Count), (-($missing.Count + $extra.Count)), $prefix)
        $better = $null -eq $bestScore
        for ($i = 0; -not $better -and $i -lt 3; $i++) {
            if ($score[$i] -ne $bestScore[$i]) { $better = $score[$i] -gt $bestScore[$i]; break }
        }
        if ($better) { $best = $o; $bestScore = $score }
    }
    return $best
}

function Get-Difference($yamlSide, $csSide) {
    $missing = Get-Missing $yamlSide.Normalised $csSide.Normalised
    $onlyYaml = @(foreach ($p in $missing) { Show-Pattern $p })
    $missing = Get-Missing $csSide.Normalised $yamlSide.Normalised
    $onlyCs = @(foreach ($p in $missing) { Show-Pattern $p })
    $parts = @()
    if ($onlyYaml.Count) { $parts += "only in yaml $($onlyYaml -join ', ')" }
    if ($onlyCs.Count) { $parts += "only in C# $($onlyCs -join ', ')" }
    if (-not $parts.Count) { return 'the same patterns' }
    $parts -join '; '
}

function Add-Normalised([object[]] $items, [bool] $fromYaml) {
    foreach ($item in $items) {
        $normalised = [string[]]@($item.Patterns | ForEach-Object { Get-Normalised $_ $fromYaml })
        $item | Add-Member -NotePropertyName Normalised -NotePropertyValue $normalised -Force
        $item | Add-Member -NotePropertyName Key -NotePropertyValue (Get-Key $normalised) -Force
    }
}

function Group-ByKey([object[]] $items) {
    $groups = New-Object 'System.Collections.Generic.Dictionary[string,System.Collections.Generic.List[object]]' ([StringComparer]::Ordinal)
    foreach ($item in $items) {
        if (-not $groups.ContainsKey($item.Key)) { $groups[$item.Key] = New-Object System.Collections.Generic.List[object] }
        $groups[$item.Key].Add($item)
    }
    return , $groups
}

# One line per entry or Fact that pairs with nothing. Empty when the copies agree.
function Compare-Copies([object[]] $facts, [object[]] $entries) {
    $drift = New-Object System.Collections.Generic.List[string]
    $factGroups = Group-ByKey $facts
    $entryGroups = Group-ByKey $entries
    foreach ($key in $factGroups.Keys) {
        $group = $factGroups[$key]
        $paired = if ($entryGroups.ContainsKey($key)) { $entryGroups[$key].Count } else { 0 }
        if ($paired -eq 0) {
            foreach ($fact in $group) {
                $near = Get-Nearest $fact $entries
                $drift.Add("Fact $($fact.Name) ($($fact.Where)) pairs with no twinned entry; nearest entry $(Show-Name $near.Name) ($($near.Where)): $(Get-Difference $near $fact)")
            }
        }
        elseif ($paired -lt $group.Count) {
            $names = ($group | ForEach-Object { "$($_.Name) ($($_.Where))" }) -join ', '
            $drift.Add("$($group.Count) Facts share one pattern set but only $paired twinned entries carry it: $names")
        }
    }
    foreach ($key in $entryGroups.Keys) {
        if ($factGroups.ContainsKey($key)) { continue }
        foreach ($entry in $entryGroups[$key]) {
            $near = Get-Nearest $entry $facts
            $drift.Add("yaml entry $(Show-Name $entry.Name) ($($entry.Where)) pairs with no Fact; nearest Fact $($near.Name) ($($near.Where)): $(Get-Difference $entry $near)")
        }
    }
    return , $drift.ToArray()
}

# ---- Main ----

try {
    $expected = $null
    if ($ExpectFacts.Length -gt 0) {
        $parsed = 0
        if (-not [int]::TryParse($ExpectFacts, [ref]$parsed)) { Stop-Reading '-ExpectFacts' "not a whole number: $(Show $ExpectFacts)" }
        $expected = $parsed
    }

    $facts = Read-Facts
    $yaml = Read-Entries
    $entries = $yaml.Entries
    if ($facts.Count -eq 0) { Stop-Reading $CsRel 'zero [Fact]s found' }
    if ($entries.Count -eq 0) { Stop-Reading $YamlRel 'zero twinned entries found' }
    if ($null -ne $expected -and $facts.Count -ne $expected) {
        Stop-Reading $CsRel "the reader found $($facts.Count) Facts where the test class has $expected; a Fact the reader cannot see is a Fact nobody compares"
    }
    Add-Normalised $facts $false
    Add-Normalised $entries $true

    # Self-test: the comparator must see one pattern dropped from the first twinned entry.
    $first = $entries[0]
    $mutant = [pscustomobject]@{ Name = $first.Name; Where = $first.Where; Patterns = @($first.Patterns | Select-Object -Skip 1) }
    Add-Normalised @($mutant) $true
    $mutated = @($mutant) + @($entries | Select-Object -Skip 1)
    if ((Compare-Copies $facts $mutated).Count -eq 0) {
        Stop-Reading 'self-test' "dropping one pattern from $(Show-Name $first.Name) ($($first.Where)) did not register as drift"
    }

    $patternCount = ($facts | ForEach-Object { $_.Patterns.Count } | Measure-Object -Sum).Sum
    $drift = Compare-Copies $facts $entries
    if ($drift.Count -eq 0) {
        Write-Output ("rules-parity: AGREE $($facts.Count) facts <- $($entries.Count) twinned entries, $patternCount patterns; " +
            "$($yaml.Settled) settled entries not compared (patterns only; engine behaviour is rules-harness)")
        exit 0
    }
    Write-Output "rules-parity: DRIFT $($drift.Count) unpaired ($($facts.Count) facts, $($entries.Count) twinned entries, $patternCount patterns)"
    $drift | Select-Object -First $MaxShown | ForEach-Object { Write-Output "  $_" }
    if ($drift.Count -gt $MaxShown) { Write-Output "  ... and $($drift.Count - $MaxShown) more" }
    exit 1
}
catch {
    Write-Output "rules-parity: CANNOT-READ $(Show $_.Exception.Message)"
    exit 2
}
