<#
SL-105: the committed driver that shows the new Fact BITES. The slice's acceptance test (FileRowRevisedGuardTests) reads sources, so it
passes when the Fact is written, whether or not the Fact catches anything. This script is the check that it does. It applies one
one-line mutant of TopLevelTracker's Revised latch to a COPY of the tree, builds the test project, runs the TopLevel tests of
tests/FsAnalyser.Core.Tests, and compares the tests that went red with the one that must.

  A   TopLevelTracker.Build's latch gated by row kind (`r.Kind != EntryKind.File &&`): a File row is never marked Revised
      -> exactly TopLevelTests.AnInPlaceCorrectionOfARootLevelFileNameMarksThatFileRowRevised goes red, and its message says
         "row a-stale.bin: Revised is False" (the watcher's own clause, not a setup or a flag failure).

How a run keeps the real tree out of it (the discipline of SL-102's Mutants.ps1, lesson 428):
  - the copy is the tracked files of this worktree as they stand now (committed or not) plus untracked ones, made by
    `git ls-files`, without the untracked .taad/ and .taad-scratch/ (this slice's records and scratch), into a new folder under
    %LOCALAPPDATA%\Temp named for this process (ScratchRoots.ps1). The files are copied, never linked, and no SDK is copied;
  - the mutant's file is addressed inside that copy by absolute path, and the run refuses a target that resolves at or under
    the worktree (or is a link) before it writes anything;
  - the old text must match exactly once, the build is --no-incremental, and the test build's FsAnalyser.Core.dll must be written
    after the edit, or the run is void;
  - a mutant run that executes a different number of tests from the unmutated run is void;
  - the worktree's src is hashed before and after, and `git diff --exit-code src` must return 0 at the end;
  - it starts by removing the copies earlier runs left when they were killed (ScratchRoots.ps1, ScratchRootCorpus.json), after
    shutting down the build servers that may still hold their files.

The suite is run under the engine's temp folder (%SystemRoot%\TEMP) and under the ordinary one (%LOCALAPPDATA%\Temp), set in this
process for the test run only and put back (SL-096, lesson 371). Needs a .NET 10 SDK: the machine's, or the repo-local .dotnet/
that eng/dotnet.ps1 installs (it is used through DOTNET_ROOT and PATH, so the copy downloads nothing).

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-105-FileRowRevisedGuard/Mutants.ps1
  ... -Temp Ordinary      (one temp folder instead of both)

Cost: two builds of the test project and four runs of the TopLevel tests (25 tests, about 5 s each). Writes nothing but the copy
(removed at the end), TRX files inside it, and NuGet's usual cache. Starts no service, takes no lock and changes no setting.
Exit 0 when every expectation held, 1 when any did not.
#>

param(
    [ValidateSet('Both', 'Engine', 'Ordinary')] [string] $Temp = 'Both',
    [switch] $KeepCopy
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ScratchRoots.ps1')

$Pwsh = (Get-Process -Id $PID).Path
$Worktree = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim().Replace('/', '\')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $Worktree 'FsAnalyser.slnx'))) {
    throw "setup: $PSScriptRoot is not inside a worktree holding FsAnalyser.slnx"
}

$TestProject = 'tests\FsAnalyser.Core.Tests\FsAnalyser.Core.Tests.csproj'
$Filter = 'FullyQualifiedName~TopLevel'

# The mutant as data: where, the old text (found exactly once), the new text, the exact test that must go red and what its message says.
$Mutant = @{
    Id = 'A'; File = 'src\FsAnalyser.Core\TopLevelTracker.cs'
    Old = 'if (state == TopLevelState.Done && (r.Bytes, r.OnDisk) != (r.SettledBytes, r.SettledOnDisk))'
    New = 'if (r.Kind != EntryKind.File && state == TopLevelState.Done && (r.Bytes, r.OnDisk) != (r.SettledBytes, r.SettledOnDisk))'
    Red = 'FsAnalyser.Core.Tests.TopLevelTests.AnInPlaceCorrectionOfARootLevelFileNameMarksThatFileRowRevised'
    Says = 'row a-stale.bin: Revised is False'
}

$Temps = @()
if ($Temp -ne 'Ordinary') { $Temps += @{ Name = 'Engine'; Path = (Join-Path $env:SystemRoot 'TEMP') } }
if ($Temp -ne 'Engine') { $Temps += @{ Name = 'Ordinary'; Path = (Join-Path $env:LOCALAPPDATA 'Temp') } }

$Failures = New-Object System.Collections.Generic.List[string]
function Verify([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $what" }
    else { Write-Output "  FAIL  $what"; $Failures.Add($what) }
}

function Under([string] $path, [string] $folder) {
    $full = [IO.Path]::GetFullPath($path).TrimEnd('\')
    $root = [IO.Path]::GetFullPath($folder).TrimEnd('\')
    return $full.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)
}

# A digest of everything under the worktree's src, build output left out: taken before and after, it shows nothing in the
# worktree changed even when src was already dirty at the start.
function Get-SrcDigest {
    $lines = Get-ChildItem -LiteralPath (Join-Path $Worktree 'src') -Recurse -File -Force |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
        Sort-Object FullName |
        ForEach-Object { '{0} {1}' -f $_.FullName.Substring($Worktree.Length), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    return [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-', '')
}

# The .NET 10 SDK to use: the machine's, else the repo-local one eng/dotnet.ps1 installs (bootstrapped from the worktree).
function Set-Sdk {
    function Has10([string] $exe) { if (-not (Test-Path -LiteralPath $exe)) { return $false }; return [bool]((& $exe --list-sdks 2>$null) | Where-Object { $_ -match '^10\.0\.\d+' }) }
    $machine = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($machine -and (Has10 $machine.Source)) { return }
    $local = Join-Path $Worktree '.dotnet'
    if (-not (Has10 (Join-Path $local 'dotnet.exe'))) {
        & $Pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Worktree 'eng\dotnet.ps1') --version | Out-Null
    }
    if (-not (Has10 (Join-Path $local 'dotnet.exe'))) { throw "setup: no .NET 10 SDK on the machine or in $local" }
    $env:DOTNET_ROOT = $local
    $env:PATH = "$local;$env:PATH"
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
}

function Invoke-Dn([string] $root, [string[]] $dnArgs) {
    Push-Location $root
    try { & $Pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'eng\dotnet.ps1') @dnArgs 2>&1 }
    finally { Pop-Location }
}

# The tests that ran and the ones that failed, with their messages, from a trx file.
function Read-Trx([string] $dir) {
    $trx = Get-ChildItem -LiteralPath $dir -Filter *.trx | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $trx) { throw "no trx file in ${dir}: the tests did not run" }
    [xml] $x = Get-Content -LiteralPath $trx.FullName -Raw
    $counters = $x.TestRun.ResultSummary.Counters
    $failed = @($x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' })
    return @{
        Executed = [int] $counters.executed
        Failed = @($failed | ForEach-Object { [string] $_.testName } | Sort-Object)
        Messages = @($failed | ForEach-Object { [string] $_.Output.ErrorInfo.Message })
    }
}

function Run-Tests([string] $copy, [hashtable] $tempChoice, [string] $tag) {
    $results = Join-Path $copy "results\$tag"
    $saved = @{ TEMP = $env:TEMP; TMP = $env:TMP }
    $env:TEMP = $tempChoice.Path
    $env:TMP = $tempChoice.Path
    try {
        $out = Invoke-Dn $copy @('test', $TestProject, '--no-build', '--filter', $Filter, '--logger', 'trx', '--results-directory', $results)
    }
    finally {
        $env:TEMP = $saved.TEMP
        $env:TMP = $saved.TMP
    }
    $summary = ($out | Select-String 'Passed!|Failed!' | Select-Object -Last 1)
    Write-Host "    $tag : $summary"
    return Read-Trx $results
}

$Digest0 = Get-SrcDigest
Set-Sdk
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

# Earlier runs that were killed left their copies behind (and perhaps a build server holding their files).
$TempBase = Join-Path $env:LOCALAPPDATA 'Temp'
Write-Output 'earlier copies:'
$swept = @(Remove-StaleScratchRoots $TempBase { Invoke-Dn $Worktree @('build-server', 'shutdown') })
foreach ($s in $swept) { Write-Output "  $($s.Outcome): $($s.Name)" }
if ($swept.Count -eq 0) { Write-Output '  none' }

$Copy = Join-Path $TempBase (New-ScratchRootName $PID)
if (Under $Copy $Worktree) { throw "setup: the copy $Copy would be under the worktree $Worktree" }

try {
    # The copy: tracked files as they stand, plus untracked ones, without this slice's records and scratch.
    $files = @(& git -C $Worktree -c core.quotepath=off ls-files --cached) +
        @(& git -C $Worktree -c core.quotepath=off ls-files --others --exclude-standard |
            Where-Object { $_ -notmatch '^(\.taad-scratch|\.taad)/' })
    $count = 0
    foreach ($relative in ($files | Sort-Object -Unique)) {
        $from = Join-Path $Worktree $relative
        if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { continue } # tracked, deleted in the worktree
        $to = Join-Path $Copy $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
        Copy-Item -LiteralPath $from -Destination $to
        $count++
    }
    Write-Output "copy: $count files into $Copy"

    $build = @('build', $TestProject, '--no-incremental', '-nologo')
    $coreDll = Join-Path $Copy 'tests\FsAnalyser.Core.Tests\bin\Debug\net10.0-windows\FsAnalyser.Core.dll'

    Write-Output 'unmutated:'
    $b = Invoke-Dn $Copy $build
    Verify ($LASTEXITCODE -eq 0) "the copy builds ($(($b | Select-String 'Warning\(s\)|Error\(s\)') -join ' '))"
    if ($LASTEXITCODE -ne 0) { $b | Select-String ' error ' | Select-Object -First 10 | ForEach-Object { Write-Output "    $_" }; throw 'the unmutated copy does not build' }
    $baseline = @{}
    foreach ($t in $Temps) {
        $r = Run-Tests $Copy $t "base-$($t.Name)"
        $baseline[$t.Name] = $r
        Verify ($r.Executed -gt 0 -and $r.Failed.Count -eq 0) "unmutated, $($t.Name) temp: $($r.Executed) tests executed, $($r.Failed.Count) failed"
        if ($r.Failed.Count -gt 0) { $r.Failed | ForEach-Object { Write-Output "    red: $_" } }
    }

    Write-Output "mutant $($Mutant.Id):"
    $target = [IO.Path]::GetFullPath((Join-Path $Copy $Mutant.File))
    if (-not (Under $target $Copy)) { throw "refused: $target is not inside the copy $Copy" }
    if (Under $target $Worktree) { throw "refused: $target is at or under the worktree $Worktree" }
    if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "refused: $target is a link" }

    $original = [IO.File]::ReadAllBytes($target)
    $bom = $original.Length -ge 3 -and $original[0] -eq 0xEF -and $original[1] -eq 0xBB -and $original[2] -eq 0xBF
    $text = [Text.UTF8Encoding]::new($bom).GetString($original)
    if ($bom) { $text = $text.TrimStart([char] 0xFEFF) }
    $found = ([regex]::Matches($text, [regex]::Escape($Mutant.Old))).Count
    Verify ($found -eq 1) "the old text is found exactly once in $($Mutant.File) (found $found)"
    if ($found -eq 1) {
        try {
            $edited = Get-Date
            [IO.File]::WriteAllText($target, $text.Replace($Mutant.Old, $Mutant.New), [Text.UTF8Encoding]::new($bom))
            $b = Invoke-Dn $Copy $build
            $built = ($LASTEXITCODE -eq 0)
            Verify $built "mutant $($Mutant.Id) builds"
            if (-not $built) { $b | Select-String ' error ' | Select-Object -First 5 | ForEach-Object { Write-Output "    $_" } }
            $stamp = (Get-Item -LiteralPath $coreDll).LastWriteTime
            Verify ($stamp -gt $edited) "the test build's FsAnalyser.Core.dll was written after the edit ($($stamp.ToString('HH:mm:ss.fff')) > $($edited.ToString('HH:mm:ss.fff')))"
            if ($built -and $stamp -gt $edited) {
                foreach ($t in $Temps) {
                    $r = Run-Tests $Copy $t "$($Mutant.Id)-$($t.Name)"
                    Verify ($r.Executed -eq $baseline[$t.Name].Executed) "mutant $($Mutant.Id), $($t.Name) temp: $($r.Executed) tests executed, as many as unmutated ($($baseline[$t.Name].Executed))"
                    $same = (($r.Failed -join '|') -ceq $Mutant.Red)
                    Verify $same "mutant $($Mutant.Id), $($t.Name) temp: exactly [$($Mutant.Red)] went red"
                    if (-not $same) { $r.Failed | ForEach-Object { Write-Output "    red: $_" } }
                    Verify (@($r.Messages | Where-Object { $_ -like "*$($Mutant.Says)*" }).Count -eq 1) "mutant $($Mutant.Id), $($t.Name) temp: its message says '$($Mutant.Says)'"
                }
            }
        }
        finally {
            [IO.File]::WriteAllBytes($target, $original)
            $back = ([IO.File]::ReadAllBytes($target)).Length -eq $original.Length
            Verify $back "mutant $($Mutant.Id) taken out of the copy again"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $Copy) { Invoke-Dn $Copy @('build-server', 'shutdown') | Out-Null }
    if (-not $KeepCopy -and (Test-Path -LiteralPath $Copy)) {
        try { Clear-ScratchReadOnly $Copy; [IO.Directory]::Delete($Copy, $true) }
        catch { Write-Output "  note: the copy $Copy was not removed: $($_.Exception.Message) (the next run removes it)" }
    }
}

Write-Output 'the worktree:'
& git -C $Worktree diff --exit-code --stat -- src | Out-Null
Verify ($LASTEXITCODE -eq 0) 'git diff --exit-code src returned 0'
Verify ((Get-SrcDigest) -eq $Digest0) 'the worktree src hashes as it did when the run began'

if ($Failures.Count -gt 0) {
    Write-Output "$($Failures.Count) check(s) failed:"
    $Failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output 'the mutant was killed by exactly the test it should be, and the worktree was not touched'
exit 0
