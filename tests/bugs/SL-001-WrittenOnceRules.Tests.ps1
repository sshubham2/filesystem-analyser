<#
SL-001: the three fitness entries this slice settles (.taad/fitness.yaml, under '# settled by SL-001'):

  'in the App, only UnreadableRows words the container layer store'   (SC-021, SC-046)
  'no count in the App is written with a fixed plural'                (SC-044)
  'the report never reads the coverage panel's screen lines'          (Q-016)

Each is a tripwire for named spellings, not a closed class (its 'because' says which gaps it leaves). This reads each entry's
must_not_match patterns from .taad/fitness.yaml, never a copy, and applies them as .NET regexes:

  - MUST-FIRE rows are matched by at least one pattern of their entry: today's five hand-typed store wordings, today's four fixed
    plurals (each a line of the pre-slice tree at 21632dc, so this is the code before the fix failing the rows), the spellings
    the design lists ($"...Windows Container Layers...", @"wcifs.sys"), and the report reading the panel's lines.
  - MUST-STAY-QUIET rows are matched by none: the designed correct call, the 'wcifs-layer-store' and 'container-layer-store' ids,
    the comment lines that name the store, the counts written through Count, and the report's panel keys.
  - every src/FsAnalyser.App file is blanked of // and /* */ comments as Fitness blanks them, then matched, and none matches
    (UnreadableRows.cs is excepted from the first entry, as the entry says).

-Head applies the same patterns to the pre-slice sources (git show 21632dc:<path>): the first two entries must match there, which
is the evidence that the entries catch what the slice removed. -Python <python.exe> also applies every pattern with that
interpreter's re module, the engine's regex dialect, and requires its hit counts to equal .NET's on every row.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-001-WrittenOnceRules.Tests.ps1
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-001-WrittenOnceRules.Tests.ps1 -Head
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-001-WrittenOnceRules.Tests.ps1 -Python "$env:APPDATA\taad3\venv\Scripts\python.exe"

Exit 0 when every check held, 1 when any failed. Reads files only.
#>

param(
    [string] $Fitness = '',
    [string] $AppRoot = '',
    [string] $Python = '',
    [switch] $Head
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Fitness) { $Fitness = Join-Path $Repo '.taad\fitness.yaml' }
if (-not $AppRoot) { $AppRoot = Join-Path $Repo 'src\FsAnalyser.App' }
$script:Failures = New-Object System.Collections.Generic.List[string]
$PreSliceCommit = '21632dc'

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $(($what -split "`n")[0])" }
    else {
        Write-Output "  FAIL  $(($what -split "`n")[0])"
        $script:Failures.Add($what)
    }
}

$StoreRule = 'in the App, only UnreadableRows words the container layer store'
$PluralRule = 'no count in the App is written with a fixed plural'
$ReportRule = "the report never reads the coverage panel's screen lines"

# The must_not_match patterns of the entry called $name: the '    - ' lines after its '    must_not_match:' line, as YAML reads the
# scalars: plain ones as they are, single-quoted ones unquoted. A double-quoted one is refused.
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

# The files an entry names and the ones it excepts: the '    files:' line and the '    except_files:' list.
function Read-Scalar([string[]] $lines, [string] $name, [string] $key) {
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -ceq "  - name: $name") { $start = $i; break } }
    for ($i = $start + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^  - name:' -or $lines[$i] -match '^\S') { break }
        if ($lines[$i].StartsWith("    ${key}: ")) { return $lines[$i].Substring(("    ${key}: ").Length).Trim("'") }
    }
    return $null
}
function Read-List([string[]] $lines, [string] $name, [string] $key) {
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -ceq "  - name: $name") { $start = $i; break } }
    $items = New-Object System.Collections.Generic.List[string]
    $inList = $false
    for ($i = $start + 1; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        if ($l -match '^  - name:' -or $l -match '^\S') { break }
        if ($l -ceq "    ${key}:") { $inList = $true; continue }
        if ($inList -and $l.StartsWith('    - ')) { $items.Add($l.Substring(6).Trim("'")) }
        elseif ($inList) { break }
    }
    , $items.ToArray()
}

# Blanks // and /* */ comments, keeping strings (regular, verbatim, interpolated) and every newline, as Fitness blanks them.
function Remove-Comments([string] $text) {
    $sb = New-Object System.Text.StringBuilder
    $i = 0
    $n = $text.Length
    while ($i -lt $n) {
        $c = $text[$i]
        $next = if ($i + 1 -lt $n) { $text[$i + 1] } else { [char]0 }
        if ($c -eq '/' -and $next -eq '/') {
            while ($i -lt $n -and $text[$i] -ne "`n") { [void]$sb.Append(' '); $i++ }
        }
        elseif ($c -eq '/' -and $next -eq '*') {
            while ($i -lt $n -and -not ($text[$i] -eq '*' -and $i + 1 -lt $n -and $text[$i + 1] -eq '/')) {
                [void]$sb.Append($(if ($text[$i] -eq "`n" -or $text[$i] -eq "`r") { $text[$i] } else { ' ' })); $i++
            }
            if ($i -lt $n) { [void]$sb.Append('  '); $i += 2 }
        }
        elseif ($c -eq '@' -and $next -eq '"' -or ($c -eq '$' -and $next -eq '@' -and $i + 2 -lt $n -and $text[$i + 2] -eq '"') -or ($c -eq '@' -and $next -eq '$' -and $i + 2 -lt $n -and $text[$i + 2] -eq '"')) {
            while ($text[$i] -ne '"') { [void]$sb.Append($text[$i]); $i++ }
            [void]$sb.Append('"'); $i++
            while ($i -lt $n) {
                if ($text[$i] -eq '"' -and $i + 1 -lt $n -and $text[$i + 1] -eq '"') { [void]$sb.Append('""'); $i += 2; continue }
                if ($text[$i] -eq '"') { [void]$sb.Append('"'); $i++; break }
                [void]$sb.Append($text[$i]); $i++
            }
        }
        elseif ($c -eq '"') {
            [void]$sb.Append('"'); $i++
            while ($i -lt $n -and $text[$i] -ne '"' -and $text[$i] -ne "`n") {
                if ($text[$i] -eq '\' -and $i + 1 -lt $n) { [void]$sb.Append($text[$i]); $i++ }
                [void]$sb.Append($text[$i]); $i++
            }
            if ($i -lt $n -and $text[$i] -eq '"') { [void]$sb.Append('"'); $i++ }
        }
        elseif ($c -eq "'" -and $i + 2 -lt $n -and ($text[$i + 2] -eq "'" -or ($text[$i + 1] -eq '\' -and $i + 3 -lt $n))) {
            $close = $text.IndexOf("'", $i + 2)
            if ($close -lt 0) { $close = $i }
            [void]$sb.Append($text.Substring($i, $close - $i + 1)); $i = $close + 1
        }
        else { [void]$sb.Append($c); $i++ }
    }
    $sb.ToString()
}

$yaml = [IO.File]::ReadAllLines($Fitness)
$store = Read-Patterns $yaml $StoreRule
$plural = Read-Patterns $yaml $PluralRule
$report = Read-Patterns $yaml $ReportRule
Check ($store.Count -ge 1 -and $plural.Count -ge 1 -and $report.Count -ge 2) "the three entries hold $($store.Count), $($plural.Count) and $($report.Count) patterns"
Check ((Read-Scalar $yaml $StoreRule 'files') -ceq 'src/FsAnalyser.App/**/*.cs') 'the store entry covers the whole App'
Check ((Read-List $yaml $StoreRule 'except_files') -contains 'src/FsAnalyser.App/UnreadableRows.cs') 'the store entry excepts UnreadableRows.cs, the one source of the words'
Check ((Read-Scalar $yaml $PluralRule 'files') -ceq 'src/FsAnalyser.App/**/*.cs') 'the plural entry covers the whole App'
Check ((Read-Scalar $yaml $ReportRule 'files') -ceq 'src/FsAnalyser.App/WindowReport.cs') 'the report entry covers WindowReport.cs'
$topLevelKeys = @($yaml | Where-Object { $_ -match '^[A-Za-z_]+:' })
Check ($topLevelKeys.Count -ge 1 -and $topLevelKeys[-1].TrimEnd() -ceq 'rules:') 'rules: stays the last top-level key'

$storeRx = @($store | ForEach-Object { [regex]::new($_) })
$pluralRx = @($plural | ForEach-Object { [regex]::new($_) })
$reportRx = @($report | ForEach-Object { [regex]::new($_) })
function Get-Hits($set, [string] $text) { @($set | Where-Object { $_.IsMatch((Remove-Comments $text)) }).Count }

# id, entry, expectation, the line as it is (or was, at 21632dc), why
$rows = @(
    @('S1', 'store', 'fire', '                "Windows container layers can''t be scanned safely: the wcifs filter leaks kernel memory and a System handle " +', 'MainWindow.xaml.cs:614 before the slice'),
    @('S2', 'store', 'fire', '                "for each entry read, until a full restart. This folder is in the Windows container layer store, so nothing " +', 'MainWindow.xaml.cs:615 before the slice'),
    @('S3', 'store', 'fire', '            notes.Add($"Not scanned, size not counted: {row.Path} (Windows container layers)." + (why is null ? "" :', 'MainWindow.xaml.cs:648 before the slice'),
    @('S4', 'store', 'fire', '        : (store.Flags(_entry) & EntryFlags.NotScanned) != 0 ? "size not counted: Windows container layers, skipped to avoid a leak in Windows'' wcifs.sys driver; see Settings"', 'FolderTreeNode.cs:110 before the slice'),
    @('S5', 'store', 'fire', '        TopLevelState.NotScanned => $"{row.Name}  (size not counted: Windows container layers)",', 'TopLevelRowText.cs:54 before the slice'),
    @('S6', 'store', 'fire', '        string label = $"{n} Windows Container Layers skipped";', 'an interpolated string, any case'),
    @('S7', 'store', 'fire', '        string driver = @"wcifs.sys";', 'a verbatim string naming the driver'),
    @('S8', 'store', 'fire', '        string one = "the Windows container layer";', 'the singular'),
    @('S9', 'store', 'fire', '        Show("Skipped to avoid a leak in wcifs");', 'the driver at a string''s end'),
    @('Q1', 'store', 'quiet', '        : (store.Flags(_entry) & EntryFlags.NotScanned) != 0 ? UnreadableRows.NotScannedText(UnreadableRows.FlaggedReason)', 'the designed correct call'),
    @('Q2', 'store', 'quiet', '        TopLevelState.NotScanned => $"{row.Name}  ({UnreadableRows.NotScannedShortText(UnreadableRows.FlaggedReason)})",', 'the designed correct call, short'),
    @('Q3', 'store', 'quiet', '    internal static string WorkaroundId => KnownWorkarounds.WcifsLayerStore.Id;  // "wcifs-layer-store"', 'a trailing comment, blanked'),
    @('Q4', 'store', 'quiet', '        Id: "wcifs-layer-store",', 'the precaution''s id'),
    @('Q5', 'store', 'quiet', '        return "container-layer-store";', 'the refusal''s id'),
    @('Q6', 'store', 'quiet', '    // A row with no path here has its ''Show in Explorer'' disabled: a NotScannedRow, the Windows container layer store,', 'MainWindow.xaml.cs:906, a comment'),
    @('Q7', 'store', 'quiet', '    /// <summary>Whether Core left this folder out on purpose (SL-095): the Windows container layer store, never opened.</summary>', 'FolderTreeNode.cs:96, a doc comment'),
    @('Q8', 'store', 'quiet', '    ScanRefusal.ContainerLayerStore => UnreadableRows.RefusedText(result.Refusal, why),', 'an enum member, no string'),
    @('P1', 'plural', 'fire', '        string unreadable = p.Unreadable > 0 ? $", {p.Unreadable:N0} folders unreadable" : "";', 'MainWindow.xaml.cs:342 before the slice'),
    @('P2', 'plural', 'fire', '        CountersText.Text = $"{p.Files:N0} files, {p.Folders:N0} folders, {FormatSize(p.Bytes)} ({FormatSize(p.BytesOnDisk)} on disk){unreadable}";', 'MainWindow.xaml.cs:343 before the slice'),
    @('P3', 'plural', 'fire', '            notes.Add($"{result.ProbeFailures:N0} files could not be opened to read their true size on disk; their listed size was used.");', 'MainWindow.xaml.cs:640 before the slice'),
    @('P4', 'plural', 'fire', '            notes.Add($"{result.VanishedBeforeProbe:N0} files were deleted before their true size could be read; their listed size was used.");', 'MainWindow.xaml.cs:642 before the slice'),
    @('P5', 'plural', 'fire', '        string s = $"{links:N0} links not followed";', 'links with a fixed plural'),
    @('P6', 'plural', 'fire', '        string s = $"{count:N0}   files";', 'spaces before the noun'),
    @('Q9', 'plural', 'quiet', '            $"{Count(t.Files, "file")} and {Count(t.Folders, "folder")}; {Count(t.Links, "link")} not followed\n" +', 'a count through Count'),
    @('Q10', 'plural', 'quiet', '            ? $"Reading each file''s own size: {p.FilesSized:N0} of {p.FilesToSize:N0}"', 'a figure of something else'),
    @('Q11', 'plural', 'quiet', '    internal static string Count(long n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";', 'FigureText''s own formatter'),
    @('Q12', 'plural', 'quiet', '            UnreadableTab.Header = unreadable.Count == 0 ? $"Not scanned ({notScanned.Count:N0})"', 'a number in brackets'),
    @('Q13', 'plural', 'quiet', '            : $"Top {top.Count:N0} of {Count(result.Totals.Files, "file")} scanned{part}.";', 'a count then a word of its own'),
    @('Q14', 'plural', 'quiet', '        return unit == 0 ? $"{bytes:N0} bytes" : $"{value:N1} {units[unit]}";', 'a size'),
    @('R1', 'report', 'fire', '                lines = window.CoverageModel.ScreenLines,', 'the panel''s on-screen lines'),
    @('R2', 'report', 'fire', '                text = window.CoverageLines.Text,', 'the panel''s TextBlock'),
    @('R3', 'report', 'fire', '        var shown = cover.ScreenLines.Where(l => l.Length > 0);', 'a copy of the lines'),
    @('Q15', 'report', 'quiet', '                CoveragePanel = new { visible = window.CoveragePanel.IsVisible },', 'the panel''s layout, no text'),
    @('Q16', 'report', 'quiet', '                    folders = cover.Folders.Select(f => new { kind = f.Kind, size = f.Size }).ToArray(),', 'the folders as kind and size'),
    @('Q17', 'report', 'quiet', '            ["coverage"] = window.CoverageModel is { } cover && window.CoveragePanel.IsVisible', 'the model, never its lines'),
    @('Q18', 'report', 'quiet', '                    remainder = new { bytes = cover.Remainder.Bytes, includesStore = cover.Remainder.IncludesStore, text = cover.Remainder.Text },', 'a path-free text')
)
$sets = @{ store = $storeRx; plural = $pluralRx; report = $reportRx }
foreach ($kind in 'fire', 'quiet') {
    Write-Output "$kind rows"
    foreach ($row in @($rows | Where-Object { $_[2] -ceq $kind })) {
        $hits = Get-Hits $sets[$row[1]] $row[3]
        if ($kind -ceq 'fire') { Check ($hits -ge 1) "$($row[0]) fires ($($row[1])): $($row[3].Trim())   [$($row[4])]" }
        else { Check ($hits -eq 0) "$($row[0]) stays quiet ($($row[1])): $($row[3].Trim())   [$($row[4])]" }
    }
}
Check (@($rows | Where-Object { $_[2] -ceq 'fire' }).Count -ge 15) 'the corpus holds at least 15 must-fire rows'
Check (@($rows | Where-Object { $_[2] -ceq 'quiet' }).Count -ge 15) 'the corpus holds at least 15 must-stay-quiet rows'

# The real tree, blanked of comments: no match anywhere the entries look.
Write-Output 'the real tree'
$except = Read-List $yaml $StoreRule 'except_files'
$checked = 0
foreach ($file in Get-ChildItem -LiteralPath $AppRoot -Filter '*.cs' -Recurse | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }) {
    $rel = 'src/FsAnalyser.App/' + $file.FullName.Substring($AppRoot.Length + 1).Replace('\', '/')
    $text = if ($Head) {
        $shown = & git -C $Repo show "${PreSliceCommit}:$rel" 2>$null
        if ($LASTEXITCODE -ne 0) { continue }
        $shown -join "`n"
    } else { [IO.File]::ReadAllText($file.FullName) }
    $blank = Remove-Comments $text
    $checked++
    if ($Head) {
        if ($rel -notin $except) {
            $h = @($storeRx | Where-Object { $_.IsMatch($blank) }).Count
            if ($h -gt 0) { Write-Output "        $rel matches the store entry in 21632dc" }
            if ($h -gt 0) { $script:HeadStore = $true }
        }
        if (@($pluralRx | Where-Object { $_.IsMatch($blank) }).Count -gt 0) { Write-Output "        $rel matches the plural entry in 21632dc"; $script:HeadPlural = $true }
        continue
    }
    if ($rel -notin $except) {
        $hits = @($storeRx | ForEach-Object { $_.Matches($blank) } | ForEach-Object { $_.Value.Trim() })
        Check ($hits.Count -eq 0) "$rel holds no word of the store outside UnreadableRows$(if ($hits.Count) { ': ' + ($hits -join ' | ') })"
    }
    $hits = @($pluralRx | ForEach-Object { $_.Matches($blank) } | ForEach-Object { $_.Value.Trim() })
    Check ($hits.Count -eq 0) "$rel writes no count with a fixed plural$(if ($hits.Count) { ': ' + ($hits -join ' | ') })"
    if ($rel -ceq 'src/FsAnalyser.App/WindowReport.cs') {
        $hits = @($reportRx | ForEach-Object { $_.Matches($blank) } | ForEach-Object { $_.Value.Trim() })
        Check ($hits.Count -eq 0) "WindowReport.cs reads neither CoverageLines nor ScreenLines$(if ($hits.Count) { ': ' + ($hits -join ' | ') })"
    }
}
Check ($checked -ge 10) "$checked App sources were read"
if ($Head) {
    Check ([bool] $script:HeadStore) "the store entry matches the pre-slice tree at $PreSliceCommit"
    Check ([bool] $script:HeadPlural) "the plural entry matches the pre-slice tree at $PreSliceCommit"
}

# The engine's regex dialect: every pattern, on every row and every App source, hits as often under Python's re as under .NET.
if ($Python) {
    Write-Output "python re parity ($Python)"
    # The rows are matched as they are given to the entries (comments blanked first), one object each, so the order survives JSON.
    $payload = @{
        patterns = @{ store = @($store); plural = @($plural); report = @($report) }
        rows     = @($rows | ForEach-Object { [pscustomobject]@{ kind = $_[1]; line = (Remove-Comments $_[3]) } })
    } | ConvertTo-Json -Depth 5 -Compress
    $script = @'
import json, re, sys
data = json.load(sys.stdin)
out = []
for row in data["rows"]:
    out.append(sum(1 for p in data["patterns"][row["kind"]] if re.search(p, row["line"], re.M)))
print(json.dumps(out))
'@
    $result = ($payload | & $Python -I -c $script) -join ''
    $pyHits = @(($result | ConvertFrom-Json) | ForEach-Object { $_ }) # not $python: variable names are case-insensitive, and -Python is a [string]
    Check ($pyHits.Count -eq $rows.Count) "python gave $($pyHits.Count) hit counts for $($rows.Count) rows"
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $net = Get-Hits $sets[$rows[$i][1]] $rows[$i][3]
        Check ($pyHits[$i] -eq $net) "$($rows[$i][0]): python re hits $($pyHits[$i]), .NET hits $net"
    }
}

if ($script:Failures.Count -gt 0) {
    Write-Output "FAILED: $($script:Failures.Count) check(s)"
    exit 1
}
Write-Output 'ALL CHECKS HELD'
exit 0
