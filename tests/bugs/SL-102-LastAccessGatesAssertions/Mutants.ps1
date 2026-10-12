<#
SL-102: the committed driver that shows the new assertions BITE. The slice's own acceptance test
(LastAccessGatesAssertionsTests) reads sources, so it passes when the assertions are written, whether or not they catch
anything. This script is the check that they do. It applies one-line mutants of M6's gates to a COPY of the tree, builds each,
runs the default suite of tests/FsAnalyser.Core.Tests, and compares the set of tests that went red with the set each mutant
must turn red. A mutant that no test kills, or that turns red a test it should not, fails the run.

  A      Scanner.KeepsLastAccess without its cloud-attribute clause           -> the three AFolderWithACloudAttribute rows
  B      Scanner.DecideLastAccess with remote hard-wired to false             -> NothingOnThisPcsAdminShare... and
                                                                                 AScanThroughThisPcsAdminShare...
  Offline / Pinned / Unpinned  one bit dropped from CloudFolderAttributes     -> only that bit's row

The two recall bits cannot be set on a folder from user mode (NTFS drops them), so dropping either from the mask survives the
suite by design; it is not a mutant here (SL-102 design, filed for the CF-SYNC-ROOT rig).

How a run keeps the real tree out of it:
  - the copy is the tracked files of this worktree as they stand now (committed or not) plus untracked ones, made by
    `git ls-files`, into a new folder under %LOCALAPPDATA%\Temp. The files are copied, never linked;
  - every file a mutant edits is addressed inside that copy by absolute path, and the run refuses a target that resolves at
    or under the worktree (or the copy's own root) before it writes anything;
  - each mutant's old text must match exactly once, the build is --no-incremental, and the test build's FsAnalyser.Core.dll must
    be written after the edit, or the run is void;
  - a mutant run that executes fewer or more tests than the unmutated run is void;
  - the worktree's src is hashed before and after, and `git diff --exit-code src` must return 0 at the end.

The suite is run under the engine's temp folder (%SystemRoot%\TEMP) and under the ordinary one (%LOCALAPPDATA%\Temp), set in
this process for the test run only and put back (SL-096, lesson 371). Needs a .NET 10 SDK: the machine's, or the repo-local
.dotnet/ that eng/dotnet.ps1 installs (it is used through DOTNET_ROOT and PATH, so the copy downloads nothing).

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-102-LastAccessGatesAssertions/Mutants.ps1
  ... -Mutant B -Temp Ordinary      (one mutant, one temp; the unmutated baseline always runs first)

Cost: one build (about 30 s) and one suite run (about 30 s) per mutant per temp folder; the whole default is about 10 minutes.
Writes nothing but the copy (a few tens of MB, removed at the end), TRX files inside it, and NuGet's usual cache. Starts no
service, takes no lock and changes no setting. Exit 0 when every expectation held, 1 when any did not.
#>

param(
    [ValidateSet('All', 'A', 'B', 'Offline', 'Pinned', 'Unpinned')] [string] $Mutant = 'All',
    [ValidateSet('Both', 'Engine', 'Ordinary')] [string] $Temp = 'Both',
    [switch] $KeepCopy
)

$ErrorActionPreference = 'Stop'

$Pwsh = (Get-Process -Id $PID).Path
$Worktree = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim().Replace('/', '\')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $Worktree 'FsAnalyser.slnx'))) {
    throw "setup: $PSScriptRoot is not inside a worktree holding FsAnalyser.slnx"
}

$Scanner = 'src\FsAnalyser.Core\Scanning\Scanner.cs'
$Class = 'FsAnalyser.Core.Tests.LastAccessTests'
$Rows = "$Class.AFolderWithACloudAttributeIsNotAskedToKeepItsLastAccessTimeButIsStillEntered"
$Share = "$Class.NothingOnThisPcsAdminShareIsAskedToKeepItsLastAccessTime"
$LayerShare = 'FsAnalyser.Core.Tests.ContainerLayerStoreTests.AScanThroughThisPcsAdminShareRefusesInsideTheStoreAndSkipsItFromAbove'
$MaskTail = '| Kernel32.FILE_ATTRIBUTE_OFFLINE | Kernel32.FILE_ATTRIBUTE_PINNED | Kernel32.FILE_ATTRIBUTE_UNPINNED;'

# The mutants as data: where, the old text (found exactly once), the new text, and the exact tests that must go red.
$Mutants = @(
    @{ Id = 'A'; File = $Scanner
       Old = 'if (!_keepLastAccess || (attributes & CloudFolderAttributes) != 0)'
       New = 'if (!_keepLastAccess)'
       Red = @("$Rows(attribute: Offline)", "$Rows(attribute: 524288)", "$Rows(attribute: 1048576)") },
    @{ Id = 'B'; File = $Scanner
       Old = 'remote = io.Query(root, Kernel32.FileRemoteProtocolInfo, info, RemoteProtocolInfoSize) == 0;'
       New = '_ = io.Query(root, Kernel32.FileRemoteProtocolInfo, info, RemoteProtocolInfoSize); remote = false;'
       Red = @($Share, $LayerShare) },
    @{ Id = 'Offline'; File = $Scanner
       Old = $MaskTail
       New = '| Kernel32.FILE_ATTRIBUTE_PINNED | Kernel32.FILE_ATTRIBUTE_UNPINNED;'
       Red = @("$Rows(attribute: Offline)") },
    @{ Id = 'Pinned'; File = $Scanner
       Old = $MaskTail
       New = '| Kernel32.FILE_ATTRIBUTE_OFFLINE | Kernel32.FILE_ATTRIBUTE_UNPINNED;'
       Red = @("$Rows(attribute: 524288)") },
    @{ Id = 'Unpinned'; File = $Scanner
       Old = $MaskTail
       New = '| Kernel32.FILE_ATTRIBUTE_OFFLINE | Kernel32.FILE_ATTRIBUTE_PINNED;'
       Red = @("$Rows(attribute: 1048576)") }
)
if ($Mutant -ne 'All') { $Mutants = @($Mutants | Where-Object { $_.Id -eq $Mutant }) }

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

function Invoke-Dn([string] $copy, [string[]] $dnArgs) {
    Push-Location $copy
    try { & $Pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $copy 'eng\dotnet.ps1') @dnArgs 2>&1 }
    finally { Pop-Location }
}

# The tests that ran and the ones that failed, from a trx file.
function Read-Trx([string] $dir) {
    $trx = Get-ChildItem -LiteralPath $dir -Filter *.trx | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $trx) { throw "no trx file in ${dir}: the suite did not run" }
    [xml] $x = Get-Content -LiteralPath $trx.FullName -Raw
    $counters = $x.TestRun.ResultSummary.Counters
    $failed = @($x.TestRun.Results.UnitTestResult | Where-Object { $_.outcome -eq 'Failed' } | ForEach-Object { [string] $_.testName } | Sort-Object)
    return @{ Executed = [int] $counters.executed; Failed = $failed }
}

function Run-Suite([string] $copy, [hashtable] $tempChoice, [string] $tag) {
    $results = Join-Path $copy "results\$tag"
    $saved = @{ TEMP = $env:TEMP; TMP = $env:TMP }
    $env:TEMP = $tempChoice.Path
    $env:TMP = $tempChoice.Path
    try {
        $out = Invoke-Dn $copy @('test', 'tests\FsAnalyser.Core.Tests\FsAnalyser.Core.Tests.csproj', '--no-build', '--logger', 'trx', '--results-directory', $results)
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

$Copy = Join-Path (Join-Path $env:LOCALAPPDATA 'Temp') ('fsa-sl102-mutants-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
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

    $build = @('build', 'FsAnalyser.slnx', '--no-incremental', '-nologo')
    $coreDll = Join-Path $Copy 'tests\FsAnalyser.Core.Tests\bin\Debug\net10.0-windows\FsAnalyser.Core.dll'

    Write-Output 'unmutated:'
    $b = Invoke-Dn $Copy $build
    Verify ($LASTEXITCODE -eq 0) "the copy builds ($(($b | Select-String 'Warning\(s\)|Error\(s\)') -join ' '))"
    if ($LASTEXITCODE -ne 0) { $b | Select-String ' error ' | Select-Object -First 10 | ForEach-Object { Write-Output "    $_" }; throw 'the unmutated copy does not build' }
    $baseline = @{}
    foreach ($t in $Temps) {
        $r = Run-Suite $Copy $t "base-$($t.Name)"
        $baseline[$t.Name] = $r
        Verify ($r.Failed.Count -eq 0) "unmutated, $($t.Name) temp: $($r.Executed) tests executed, $($r.Failed.Count) failed"
        if ($r.Failed.Count -gt 0) { $r.Failed | ForEach-Object { Write-Output "    red: $_" } }
    }

    foreach ($m in $Mutants) {
        Write-Output "mutant $($m.Id):"
        $target = [IO.Path]::GetFullPath((Join-Path $Copy $m.File))
        if (-not (Under $target $Copy)) { throw "refused: $target is not inside the copy $Copy" }
        if (Under $target $Worktree) { throw "refused: $target is at or under the worktree $Worktree" }
        if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "refused: $target is a link" }

        $original = [IO.File]::ReadAllBytes($target)
        $bom = $original.Length -ge 3 -and $original[0] -eq 0xEF -and $original[1] -eq 0xBB -and $original[2] -eq 0xBF
        $text = [Text.UTF8Encoding]::new($bom).GetString($original)
        if ($bom) { $text = $text.TrimStart([char] 0xFEFF) }
        $found = ([regex]::Matches($text, [regex]::Escape($m.Old))).Count
        Verify ($found -eq 1) "the old text is found exactly once in $($m.File) (found $found)"
        if ($found -ne 1) { continue }

        try {
            $edited = Get-Date
            [IO.File]::WriteAllText($target, $text.Replace($m.Old, $m.New), [Text.UTF8Encoding]::new($bom))
            $b = Invoke-Dn $Copy $build
            $built = ($LASTEXITCODE -eq 0)
            Verify $built "mutant $($m.Id) builds"
            if (-not $built) { $b | Select-String ' error ' | Select-Object -First 5 | ForEach-Object { Write-Output "    $_" }; continue }
            $stamp = (Get-Item -LiteralPath $coreDll).LastWriteTime
            Verify ($stamp -gt $edited) "the test build's FsAnalyser.Core.dll was written after the edit ($($stamp.ToString('HH:mm:ss.fff')) > $($edited.ToString('HH:mm:ss.fff')))"
            if ($stamp -le $edited) { continue }

            foreach ($t in $Temps) {
                $r = Run-Suite $Copy $t "$($m.Id)-$($t.Name)"
                Verify ($r.Executed -eq $baseline[$t.Name].Executed) "mutant $($m.Id), $($t.Name) temp: $($r.Executed) tests executed, as many as unmutated ($($baseline[$t.Name].Executed))"
                $expected = @($m.Red | Sort-Object)
                $same = (($r.Failed -join "|") -ceq ($expected -join "|"))
                Verify $same "mutant $($m.Id), $($t.Name) temp: exactly [$($expected -join '; ')] went red"
                if (-not $same) { $r.Failed | ForEach-Object { Write-Output "    red: $_" } }
            }
        }
        finally {
            [IO.File]::WriteAllBytes($target, $original)
            $back = ([IO.File]::ReadAllBytes($target)).Length -eq $original.Length
            Verify $back "mutant $($m.Id) taken out of the copy again"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $Copy) { Invoke-Dn $Copy @('build-server', 'shutdown') | Out-Null }
    if (-not $KeepCopy -and (Test-Path -LiteralPath $Copy)) {
        try { [IO.Directory]::Delete($Copy, $true) }
        catch { Write-Output "  note: the copy $Copy was not removed: $($_.Exception.Message)" }
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
Write-Output 'every mutant was killed by exactly the tests it should be, and the worktree was not touched'
exit 0
