<#
SL-103 m1 (SL-091 review n2, deferred as O-048): the write guards declared closed as a class (ExplorerSettingsWriteCorpus.json)
let through an ordinary writable open of Explorer\Advanced.

Bug: the OpenSubKey pattern of each guard treats the key as opened for writing only if `true` or `ReadWriteSubTree` follows
on the line (the marker `\b(true|ReadWriteSubTree)\b`). The documented .NET overloads OpenSubKey(string, RegistryRights) and
OpenSubKey(string, RegistryKeyPermissionCheck, RegistryRights) open the key with SetValue / WriteKey / FullControl rights and
match neither alternative. The marker is in three copies:
  - .taad/fitness.yaml, 'TheAppNeverWritesExplorersSettings (*.cs)' over src/**/*.cs,
  - its xUnit twin, ArchitectureRulesTests.TheAppNeverWritesExplorersSettings (kept equal by eng/rules-parity.ps1),
  - .taad/fitness.yaml, "no window case or hook changes Explorer's view settings" over tests/window/** (rule C), which
    covers the PowerShell spelling [System.Security.AccessControl.RegistryRights]::SetValue.

Expected: every one of those lines is caught by the guard of its scope (in every copy of it), and the read-only rights
(RegistryRights.ReadKey, QueryValues) and the one-argument read stay quiet, so the widening does not turn the guard into a
blanket on OpenSubKey of that key.

Actual: every rights-argument line below gets 0 hits from all six src patterns (both copies) and all nine window patterns, so
a src or window-script write of Hidden through such a key passes dotnet test, Fitness and the corpus test alike. Only the
control lines (an open with `true`, `$true`, the read-only rights, the one-argument read) behave as the corpus says.

This reads the patterns from the repository files (never a copy of them) and applies them to each line alone, as .NET
regexes with RegexOptions.Multiline, the way ExplorerSettingsWriteRuleTests feeds the corpus to them. It reads files only
and runs in well under a second.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-103-ExplorerAdvancedRightsOpen.Tests.ps1

Exit 0 when every check held, 1 when any failed. The corpus the class is enumerated into is
tests/FsAnalyser.Core.Tests/ExplorerSettingsWriteCorpus.json.
#>

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Fitness = Join-Path $Repo '.taad\fitness.yaml'
$Rules = Join-Path $Repo 'tests\FsAnalyser.Core.Tests\ArchitectureRulesTests.cs'
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

# The verbatim C# string arguments (@"...") of the Fact called $name in ArchitectureRulesTests.cs, "" read as one quote.
function Read-FactPatterns([string[]] $lines, [string] $name) {
    $fact = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Contains("public void $name()")) { $fact = $i; break } }
    if ($fact -lt 0) { throw "$Rules has no Fact '$name'" }
    $found = New-Object System.Collections.Generic.List[regex]
    for ($n = $fact + 1; $n -lt $lines.Count; $n++) {
        $m = [regex]::Match($lines[$n], '^\s*@"(.*)"(,|\);)\s*$')
        if ($m.Success) {
            $found.Add([regex]::new($m.Groups[1].Value.Replace('""', '"'), $Multiline))
            if ($m.Groups[2].Value -eq ');') { break }
        }
        elseif ($lines[$n].TrimStart().StartsWith('}')) { break }
    }
    if ($found.Count -eq 0) { throw "the Fact '$name' has no verbatim string patterns" }
    , $found.ToArray()
}

$yaml = [IO.File]::ReadAllLines($Fitness)
$cs = [IO.File]::ReadAllLines($Rules)
$srcYaml = Read-YamlPatterns $yaml "  - name: 'TheAppNeverWritesExplorersSettings (*.cs)'"
$srcFact = Read-FactPatterns $cs 'TheAppNeverWritesExplorersSettings'
$windowYaml = Read-YamlPatterns $yaml "  - name: no window case or hook changes Explorer's view settings"
$guards = @{
    src    = @(
        [pscustomobject]@{ Copy = 'fitness.yaml src entry'; Patterns = $srcYaml },
        [pscustomobject]@{ Copy = 'ArchitectureRulesTests Fact'; Patterns = $srcFact }
    )
    window = @(
        [pscustomobject]@{ Copy = 'fitness.yaml window entry'; Patterns = $windowYaml }
    )
}
foreach ($scope in 'src', 'window') {
    foreach ($g in $guards[$scope]) { Write-Output "$($g.Patterns.Count) $scope patterns from $($g.Copy)" }
}

$adv = 'Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$advTwice = 'Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced'
$srcOpen = '        using var key = Registry.CurrentUser.OpenSubKey(@"{0}", {1});'
$winOpen = '$adv = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(''{0}'', {1})'
$rights = '[System.Security.AccessControl.RegistryRights]'
$rightsSetValue = $rights + '::SetValue'
$rightsQueryValues = $rights + '::QueryValues'
$rightsFullControl = $rights + '::FullControl'
$rightsReadKey = $rights + '::ReadKey'

$srcTrue = '        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"' + $adv + '", true);'
$srcRead = '        using var key = Registry.CurrentUser.OpenSubKey(@"' + $adv + '");'
$srcBase = '        using var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default).OpenSubKey(@"' + $adv + '", RegistryRights.WriteKey);'
$srcTwice = '        using var key = Registry.CurrentUser.OpenSubKey("' + $advTwice + '", RegistryRights.SetValue);'

# id, scope, what the guard must do, the line
$rows = @(
    # controls the guards already settle: they prove the patterns were read and applied as the corpus does
    , @('B07', 'src', 'fire', $srcTrue)
    , @('B21', 'src', 'quiet', $srcRead)
    , @('W16', 'window', 'fire', ($winOpen -f $adv, '$true'))

    # the reported lines: the key opened with SetValue / WriteKey / FullControl rights
    , @('B30', 'src', 'fire', ($srcOpen -f $adv, 'RegistryRights.SetValue'))
    , @('B31', 'src', 'fire', ($srcOpen -f $adv, 'RegistryKeyPermissionCheck.Default, RegistryRights.FullControl'))
    , @('B33', 'src', 'fire', $srcBase)
    , @('W35', 'window', 'fire', ($winOpen -f $adv, $rightsSetValue))

    # siblings of the same open: another rights spelling, the same call
    , @('B34', 'src', 'fire', ($srcOpen -f $adv, 'RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.SetValue'))
    , @('B35', 'src', 'fire', ($srcOpen -f $adv, 'RegistryRights.QueryValues | RegistryRights.SetValue'))
    , @('B36', 'src', 'fire', ($srcOpen -f $adv, 'System.Security.AccessControl.RegistryRights.SetValue'))
    , @('B37', 'src', 'fire', ($srcOpen -f $adv, 'rights: RegistryRights.SetValue'))
    , @('B40', 'src', 'fire', $srcTwice)
    , @('W41', 'window', 'fire', ($winOpen -f $adv, ($rightsQueryValues + ' -bor ' + $rightsSetValue)))
    , @('W42', 'window', 'fire', ($winOpen -f $adv, ('[Microsoft.Win32.RegistryKeyPermissionCheck]::Default, ' + $rightsFullControl)))
    , @('W43', 'window', 'fire', ($srcOpen -f $adv, 'RegistryRights.SetValue'))

    # a rights open that cannot write stays quiet, so the widening is not a blanket on OpenSubKey of this key
    , @('B32', 'src', 'quiet', ($srcOpen -f $adv, 'RegistryRights.ReadKey'))
    , @('B41', 'src', 'quiet', ($srcOpen -f $adv, 'RegistryRights.QueryValues'))
    , @('W44', 'window', 'quiet', ($winOpen -f $adv, $rightsReadKey))
    , @('W47', 'window', 'quiet', ($winOpen -f $adv, '$false'))
)

Check (@($rows | ForEach-Object { $_[0] } | Sort-Object -Unique).Count -eq $rows.Count) 'no row id appears twice'

foreach ($row in $rows) {
    $id = $row[0]; $scope = $row[1]; $expect = $row[2]; $line = $row[3]
    foreach ($g in $guards[$scope]) {
        $hits = @($g.Patterns | Where-Object { $_.IsMatch($line) })
        if ($expect -ceq 'fire') {
            Check ($hits.Count -ge 1) "$id fires in the $scope guard ($($g.Copy)), $($hits.Count) hit(s): $line"
        }
        else {
            Check ($hits.Count -eq 0) "$id stays quiet in the $scope guard ($($g.Copy)), $($hits.Count) hit(s): $line"
        }
    }
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
