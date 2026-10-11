<#
SL-099: the reorder in PoolWatch.ps1's Get-Missing, the Ordinal table in Get-Net and the 14 new tags in the watched list must not
move a single line that a fixture shape SL-095 already pinned prints. This runs each of those cases (C-053 to C-056, C-027, P-001 to
P-003, C-011) twice, once from a copy of tests/window as it was at a base commit (default 0feac0e, taken with `git archive` into a
scratch folder, never a worktree) and once from the tree, in child processes whose TEMP and TMP are set here to LocalAppData\Temp,
so that the baseline copy is not run under the engine's TEMP. Both runs use the tree's PoolWatchCases.ps1, copied over the base's
(the base's has no Get-ScratchParent, and prints no -Report text), so the fixtures are the same and what differs is the
PoolWatch.ps1 that reads them. Output is compared after dropping
what legitimately differs: the 'scratch:' lines SL-099 added, the random case-<12 hex> folder and the scratch parent in a path,
PoolWatch.ps1's own line numbers (the -Protocol throw prints one) and a boot's age in seconds.

  - every case but P-003 and C-011: the exit codes are equal and the normalised output is equal, line for line;
  - P-003 has new fixtures (guard-stopped arms) after its old one, which the base's PoolWatch.ps1 reports as it always did and
    the tree's reports differently, on purpose: only its first fixture (X3, F2, F4: the header line to the next one) is
    compared, line for line, and the tree's exit code must be 0;
  - C-011 prints the boot's age and a source line number, so only its exit code and its count of ok and FAIL lines are compared.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-099-ReportTextUnchanged.Tests.ps1
  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-099-ReportTextUnchanged.Tests.ps1 -Case C-053

About two minutes for All. A base commit that is not in this clone is a failure, never a skip. Exit 0 when every check held, 1 when
any failed.
#>

param(
    [ValidateSet('All', 'C-053', 'C-054', 'C-055', 'C-056', 'C-027', 'P-001', 'P-002', 'P-003', 'C-011')]
    [string[]] $Case = @('All'),
    [string] $BaseRef = '0feac0e'
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $(($what -split "`n")[0])" }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

$scratchParent = [System.IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'Temp')
$cases = if ($Case -contains 'All') { @('C-053', 'C-054', 'C-055', 'C-056', 'C-027', 'P-001', 'P-002', 'P-003', 'C-011') } else { @($Case) }
$work = Join-Path $scratchParent ('fsa-sl099-base-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
Write-Output "scratch: $work (TEMP $scratchParent)"

# The case's output with what may differ taken out (see the header).
function Get-Normalised([string[]] $lines) {
    @($lines |
            Where-Object { $_ -notmatch '^  scratch: ' } |
            ForEach-Object {
                $l = $_ -replace '[A-Za-z]:\\[^\s|*?"<>]*?case-[0-9a-f]{12}', '<run>'
                $l = $l -replace 'case-[0-9a-f]{12}', 'case-<id>'
                $l = $l -replace 'PoolWatch\.ps1:\d+', 'PoolWatch.ps1:<n>'
                $l -replace '\b\d+ s old\b', '<n> s old'
            })
}

# A case's first fixture: from its first header line (four spaces, then text) to the next header line.
function Get-FirstSection([string[]] $lines) {
    $at = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -match '^    \S' })
    if ($at.Count -eq 0) { return @() }
    $end = if ($at.Count -gt 1) { $at[1] } else { $lines.Count }
    @($lines[$at[0]..($end - 1)])
}

function Invoke-Cases([string] $tree, [string] $name) {
    $shell = (Get-Process -Id $PID).Path
    $script = Join-Path $tree 'tests\window\PoolWatchCases.ps1'
    $saved = @{ TEMP = $env:TEMP; TMP = $env:TMP }
    $result = @{}
    $ErrorActionPreference = 'Continue'
    try {
        $env:TEMP = $scratchParent; $env:TMP = $scratchParent
        foreach ($c in $cases) {
            $out = @(& $shell -NoProfile -ExecutionPolicy Bypass -File $script -Case $c 2>&1 | ForEach-Object { "$_" })
            $result[$c] = [pscustomobject]@{ Exit = $LASTEXITCODE; Lines = $out }
        }
    }
    finally {
        $ErrorActionPreference = 'Stop'
        $env:TEMP = $saved.TEMP; $env:TMP = $saved.TMP
    }
    $result
}

try {
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $zip = Join-Path $work 'base.zip'
    & git -C $Repo archive --format=zip -o $zip $BaseRef tests/window 2>&1 | ForEach-Object { Write-Output "        | $_" }
    Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $zip)) "git archive $BaseRef tests/window (exit $LASTEXITCODE)"
    if (Test-Path -LiteralPath $zip) {
        $base = Join-Path $work 'base'
        Expand-Archive -LiteralPath $zip -DestinationPath $base
        Check (Test-Path -LiteralPath (Join-Path $base 'tests\window\PoolWatchCases.ps1')) 'the base copy holds PoolWatchCases.ps1'
        Check (Test-Path -LiteralPath (Join-Path $base 'tests\window\PoolWatch.ps1')) 'the base copy holds PoolWatch.ps1'
        Copy-Item -LiteralPath (Join-Path $Repo 'tests\window\PoolWatchCases.ps1') -Destination (Join-Path $base 'tests\window\PoolWatchCases.ps1') -Force
        $was = Invoke-Cases $base 'base'
        $now = Invoke-Cases $Repo 'tree'
        foreach ($c in $cases) {
            Write-Output $c
            $w = $was[$c]; $n = $now[$c]
            if ($c -cne 'P-003') { Check ($w.Exit -eq $n.Exit) "$c exits $($w.Exit) at $BaseRef and $($n.Exit) in the tree" }
            else { Check ($n.Exit -eq 0) "$c exits 0 in the tree (exit $($n.Exit))" }
            $wl = Get-Normalised $w.Lines; $nl = Get-Normalised $n.Lines
            if ($c -ceq 'C-011') {
                $wOk = @($wl | Where-Object { $_ -match '^  ok ' }).Count; $nOk = @($nl | Where-Object { $_ -match '^  ok ' }).Count
                $wFail = @($wl | Where-Object { $_ -match '^  FAIL ' }).Count; $nFail = @($nl | Where-Object { $_ -match '^  FAIL ' }).Count
                Check ($wOk -eq $nOk -and $wFail -eq $nFail) "C-011 prints $wOk ok and $wFail FAIL at $BaseRef, $nOk ok and $nFail FAIL in the tree"
            }
            elseif ($c -ceq 'P-003') {
                $wl = Get-FirstSection $wl; $nl = Get-FirstSection $nl
                $diff = @(0..([Math]::Max($wl.Count, $nl.Count) - 1) | Where-Object { $wl[$_] -cne $nl[$_] })
                Check ($wl.Count -ge 10 -and $diff.Count -eq 0) "P-003's first fixture prints the same $($wl.Count) lines at $BaseRef and in the tree $(if ($diff.Count) { "(line $($diff[0]) base: '$($wl[$diff[0]])' tree: '$($nl[$diff[0]])')" })"
            }
            else {
                $same = $wl.Count -eq $nl.Count -and @(0..([Math]::Max($wl.Count, $nl.Count) - 1) | Where-Object { $wl[$_] -cne $nl[$_] }).Count -eq 0
                $firstDiff = if ($same) { '' } else { $at = (0..([Math]::Max($wl.Count, $nl.Count) - 1) | Where-Object { $wl[$_] -cne $nl[$_] })[0]; "line $at base: '$($wl[$at])' tree: '$($nl[$at])'" }
                Check $same "$c prints the same $($wl.Count) lines at $BaseRef and in the tree $firstDiff"
            }
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
