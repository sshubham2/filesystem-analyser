<#
SL-104 (O-031, SL-056 code review R2-m4): the settled rules for timestamp/attribute writers and for directory listings let
through the .NET spellings their own because-text says are fenced.

Bug: two settled entries of .taad/fitness.yaml, both over src/FsAnalyser.Core/**/*.cs, match source text, and each names some
spellings of a writer or a listing but not others (neither has an xUnit twin, so only the Fitness check enforces them):
  - 'only the directory-listing open path may write a file-system timestamp' (rule 2) says "every way to write a time or an
    attribute is fenced", but has no pattern for File.SetAttributes; \bSetFileAttributes\w* needs the Win32 name.
  - 'only DirectoryReader lists a directory' (rule 4) has an instance pattern
    \.(EnumerateFiles|EnumerateDirectories|EnumerateFileSystemInfos|GetFileSystemInfos)\s*\( that leaves out GetFiles and
    GetDirectories, so DirectoryInfo.GetFiles() / GetDirectories() pass (Directory.GetFiles is the only static form named).

Expected: File.SetAttributes(...) fires rule 2, and DirectoryInfo.GetFiles(...) / GetDirectories(...) fire rule 4, so a second,
unfenced writer or listing in Core outside the one sanctioned call site breaks a settled rule. Reads (File.GetAttributes,
Path.GetFileName) stay quiet, so the widening is not a blanket.

Actual: every File.SetAttributes line and every GetFiles / GetDirectories instance call below gets 0 hits from all patterns of
its rule, so such a line anywhere in Core outside Scanning (Store/, the Core root) reports no violation from any rule. Only the
control lines (the spellings the patterns already name, and the quiet reads) behave as the rules' because-text says.

This reads the patterns from .taad/fitness.yaml (never a copy of them) and applies them to each line alone, as .NET regexes
with RegexOptions.Multiline (the engine uses Python re; these patterns mean the same in both). It reads one file and runs in
well under a second.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-104-SettledRuleSpellings.Tests.ps1

Exit 0 when every check held, 1 when any failed.
#>

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Fitness = Join-Path $Repo '.taad\fitness.yaml'
$Multiline = [System.Text.RegularExpressions.RegexOptions]::Multiline
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $what" }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

# The must_not_match patterns of one entry of .taad/fitness.yaml, by its whole '  - name: ...' header line: the single-quoted
# scalars on lines of their own after its '    must_not_match:' line, '' read as one quote.
function Read-YamlPatterns([string[]] $lines, [string] $header) {
    $entry = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].TrimEnd() -ceq $header) { $entry = $i; break } }
    if ($entry -lt 0) { throw "$Fitness has no entry '$header'" }
    $key = -1
    for ($i = $entry + 1; $i -lt $lines.Count; $i++) { if ($lines[$i].TrimEnd() -ceq '    must_not_match:') { $key = $i; break } }
    if ($key -lt 0) { throw "the entry '$header' has no must_not_match" }
    $found = New-Object System.Collections.Generic.List[regex]
    for ($n = $key + 1; $n -lt $lines.Count; $n++) {
        $m = [regex]::Match($lines[$n], '^\s+- ''(.*)''$')
        if (-not $m.Success) { break }
        $found.Add([regex]::new($m.Groups[1].Value.Replace("''", "'"), $Multiline))
    }
    if ($found.Count -eq 0) { throw "the entry '$header' has no single-quoted patterns under must_not_match" }
    , $found.ToArray()
}

$yaml = [IO.File]::ReadAllLines($Fitness)
$rules = @{
    timestamp = Read-YamlPatterns $yaml '  - name: only the directory-listing open path may write a file-system timestamp'
    listing   = Read-YamlPatterns $yaml '  - name: only DirectoryReader lists a directory'
}
foreach ($k in 'timestamp', 'listing') { Write-Output "$($rules[$k].Count) patterns read for the $k rule" }

# id, rule, what the rule must do, the line
$rows = @(
    # controls the rules already settle: they prove the patterns were read and applied as the because-text says
    , @('W10', 'timestamp', 'fire', '        File.SetLastAccessTimeUtc(path, DateTime.UtcNow);')
    , @('W11', 'timestamp', 'fire', '        if (!SetFileAttributesW(path, 0x80)) throw new IOException();')
    , @('W12', 'timestamp', 'fire', '        info.LastWriteTimeUtc = stamp;')
    , @('L09', 'listing', 'fire', '        var names = Directory.GetFiles(path);')
    , @('L10', 'listing', 'fire', '        var all = dir.GetFileSystemInfos();')
    , @('L11', 'listing', 'fire', '        foreach (var f in dir.EnumerateFiles()) { }')

    # the reported spellings: the .NET attribute writer, and the DirectoryInfo listings
    , @('W01', 'timestamp', 'fire', '        File.SetAttributes(path, FileAttributes.Normal);')
    , @('W02', 'timestamp', 'fire', '        System.IO.File.SetAttributes(path, FileAttributes.ReadOnly);')
    , @('W03', 'timestamp', 'fire', '        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);')
    , @('L01', 'listing', 'fire', '        var files = new DirectoryInfo(p).GetFiles();')
    , @('L02', 'listing', 'fire', '        var subs = dir.GetDirectories();')
    , @('L03', 'listing', 'fire', '        var txt = dir.GetFiles("*.txt", SearchOption.AllDirectories);')
    , @('L04', 'listing', 'fire', '        var top = info.GetDirectories("*", SearchOption.TopDirectoryOnly);')

    # reads and unrelated calls stay quiet, so the widening is not a blanket
    , @('W20', 'timestamp', 'quiet', '        var a = File.GetAttributes(path);')
    , @('W21', 'timestamp', 'quiet', '        var t = File.GetLastWriteTimeUtc(path);')
    , @('L20', 'listing', 'quiet', '        var name = Path.GetFileName(p);')
    , @('L21', 'listing', 'quiet', '        var exists = Directory.Exists(p);')
)

Check (@($rows | ForEach-Object { $_[0] } | Sort-Object -Unique).Count -eq $rows.Count) 'no row id appears twice'

foreach ($row in $rows) {
    $id = $row[0]; $rule = $row[1]; $expect = $row[2]; $line = $row[3]
    $hits = @($rules[$rule] | Where-Object { $_.IsMatch($line) })
    if ($expect -ceq 'fire') {
        Check ($hits.Count -ge 1) "$id fires the $rule rule, $($hits.Count) hit(s): $($line.Trim())"
    }
    else {
        Check ($hits.Count -eq 0) "$id stays quiet in the $rule rule, $($hits.Count) hit(s): $($line.Trim())"
    }
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
