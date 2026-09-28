<#
Bug (SL-069): tests/rules/RuleCases.ps1 hard-codes the parity counts instead of deriving them.

The script pins the twinned count in $Twinned and spells the facts, twinned and pattern counts of the AGREE
line (and the -ExpectFacts value) into C-001, C-003, C-004, C-005 and C-008. SL-039, SL-042 and SL-043 each raised the
twinned or settled counts and left those strings stale or red, and the next slice that declares a twinned
rule breaks them again.

Expected: the counts come from what eng/rules-parity.ps1 reports about the tree it is run on, so a slice that
declares one more twinned rule (one more [Fact] in ArchitectureRulesTests.cs with one more twinned entry in
.taad/fitness.yaml, both copies agreeing) leaves the rule-copy cases green. C-001 on such a tree exits 0.

Actual: on such a tree the parity tool AGREEs on one more Fact, twinned entry and pattern than the tree the copy
was made from, but C-001 still expects that tree's counts and passes its Fact count to -ExpectFacts, so it exits 1
(FAIL on the pinned AGREE line, and the -ExpectFacts run is CANNOT-READ).
#>

$ErrorActionPreference = 'Stop'

function Find-RepoRoot {
    param([string]$Start)
    $dir = Get-Item -LiteralPath $Start
    while ($null -ne $dir) {
        if (Test-Path (Join-Path $dir.FullName 'FsAnalyser.slnx')) {
            return $dir.FullName
        }
        $dir = $dir.Parent
    }
    throw "No FsAnalyser.slnx found above $Start."
}

$repoRoot = Find-RepoRoot -Start $PSScriptRoot
$Utf8 = [System.Text.UTF8Encoding]::new($false)

$casesRel = 'tests\rules\RuleCases.ps1'
$parityRel = 'eng\rules-parity.ps1'
$yamlRel = '.taad\fitness.yaml'
$csRel = 'tests\FsAnalyser.Core.Tests\ArchitectureRulesTests.cs'

# Insert $text into $rel of the scratch tree just before the one occurrence of $anchor, in that file's line endings.
function Add-Before {
    param([string]$Root, [string]$Rel, [string]$Anchor, [string]$Text)
    $path = Join-Path $Root $Rel
    $content = [System.IO.File]::ReadAllText($path, $Utf8)
    $eol = if ($content.Contains("`r`n")) { "`r`n" } else { "`n" }
    $anchor = $Anchor.Replace("`n", $eol)
    $at = $content.IndexOf($anchor, [StringComparison]::Ordinal)
    if ($at -lt 0) { throw "test setup: '$Anchor' is not in $Rel" }
    if ($content.IndexOf($anchor, $at + 1, [StringComparison]::Ordinal) -ge 0) { throw "test setup: '$Anchor' is in $Rel more than once" }
    $insert = $Text.Replace("`n", $eol)
    [System.IO.File]::WriteAllText($path, $content.Substring(0, $at) + $insert + $content.Substring($at), $Utf8)
}

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('fsa-sl069-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    foreach ($rel in @($casesRel, $parityRel, $yamlRel, $csRel)) {
        $target = Join-Path $scratch $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot $rel) -Destination $target
    }

    # The copy's own counts before the change, so the setup guard below expects them plus one and pins none.
    $before = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scratch $parityRel) -Root $scratch 2>&1 | Out-String
    $counts = [regex]::Match($before, 'rules-parity: AGREE (\d+) facts <- (\d+) twinned entries, (\d+) patterns;')
    if ($LASTEXITCODE -ne 0 -or -not $counts.Success) {
        throw "test setup: the parity tool does not AGREE on the unchanged scratch copy (exit $LASTEXITCODE): $before"
    }
    $facts, $twinned, $patterns = 1..3 | ForEach-Object { [int]$counts.Groups[$_].Value + 1 }

    # A slice declares one more twinned rule: a new Fact, and its twinned yaml entry above the settled marker.
    Add-Before $scratch $csRel '    private static IEnumerable<string> Files(' @'
    [Fact]
    public void TheVolumeReadNamesNoBrandNewThing()
    {
        AssertNoMatch(Files(@"src\FsAnalyser.Core\Scanning", "VolumeSpace.cs"),
            @"\bBrandNewThing\b");
    }


'@
    Add-Before $scratch $yamlRel '  # settled by SL-023' @'
  - name: 'TheVolumeReadNamesNoBrandNewThing (*.cs)'
    files: 'src/FsAnalyser.Core/Scanning/VolumeSpace.cs'
    must_not_match:
      - '(?m)\bBrandNewThing\b'
    including_comments: true
    because: 'A probe rule, twinned in both copies, as a later slice would declare one.'


'@

    $parity = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scratch $parityRel) -Root $scratch 2>&1 | Out-String
    $parityExit = $LASTEXITCODE
    if ($parityExit -ne 0 -or -not $parity.Contains("rules-parity: AGREE $facts facts <- $twinned twinned entries, $patterns patterns;")) {
        throw "test setup: the parity tool does not AGREE on the scratch tree with one more twinned rule (exit $parityExit): $parity"
    }

    # The rule-copy case that pins the AGREE line on the clean tree, run from the scratch tree's own copy.
    $ErrorActionPreference = 'Continue'
    $output = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scratch $casesRel) -Case C-001 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'

    if ($exit -ne 0) {
        throw "RuleCases.ps1 -Case C-001 exited $exit on a tree where rules-parity says '$($parity.Trim())'. The counts in RuleCases.ps1 are hard-coded, not derived from the parity output.`n$output"
    }
    if (-not $output.Contains('All checks held.')) {
        throw "RuleCases.ps1 -Case C-001 exited 0 without 'All checks held.':`n$output"
    }
    Write-Host 'OK: RuleCases.ps1 C-001 holds on a tree with one more twinned rule; its counts follow the parity output.'
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
exit 0
