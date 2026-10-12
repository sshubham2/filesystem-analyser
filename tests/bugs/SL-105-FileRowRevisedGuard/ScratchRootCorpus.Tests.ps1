<#
SL-105: feeds every row of ScratchRootCorpus.json to the sweep Mutants.ps1 starts with (ScratchRoots.ps1 ::
Remove-StaleScratchRoots), against real folders, and checks what is left on disk and what the sweep reported. The class is
"a scratch copy left behind by a run that was killed": each row is one state such a copy is found in, or one entry that looks like
one and must be left alone.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-105-FileRowRevisedGuard/ScratchRootCorpus.Tests.ps1
  ... -Before     runs the same rows against the shape the code had before the sweep: a script that removes its copy
                  at the end of its own run and nothing else, so that it sweeps nothing. The rows for killed runs fail.

Everything happens in a folder of its own under %LOCALAPPDATA%\Temp, named like a scratch copy of this run's process, which is
removed at the end and which a later Mutants.ps1 would remove if this one were killed. The sweep is pointed at that folder, never at
%LOCALAPPDATA%\Temp itself, so no real copy is touched. It starts two throwaway processes (one that exits at once, one that
sleeps), holds one file open, and stops what it started. Cost: a few seconds. Writes under 1 MB. Exit 0 when every row held, 1 when
any did not.
#>

param([switch] $Before)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ScratchRoots.ps1')

if ($Before) {
    # The code before the fix: nothing looks at the folders earlier runs left, so every one of them stays.
    function Remove-StaleScratchRoots([string] $Base, [scriptblock] $Release = {}) {
        foreach ($entry in (Get-ChildItem -LiteralPath $Base -Force | Where-Object { $_.Name.StartsWith($ScratchPrefix, [StringComparison]::OrdinalIgnoreCase) })) {
            [pscustomobject] @{ Name = $entry.Name; Outcome = 'kept' }
        }
    }
}

$Corpus = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ScratchRootCorpus.json') -Raw | ConvertFrom-Json
$Failures = New-Object System.Collections.Generic.List[string]
function Verify([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $what" }
    else { Write-Output "  FAIL  $what"; $Failures.Add($what) }
}

$Temp = Join-Path $env:LOCALAPPDATA 'Temp'
$Base = Join-Path $Temp (New-ScratchRootName $PID)
$Held = New-Object System.Collections.Generic.List[IO.FileStream]
$Sleeper = $null

function Add-Content-File([string] $root, [string] $relative, [string] $text = 'x') {
    $path = Join-Path $root $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    Set-Content -LiteralPath $path -Value $text
    return $path
}

try {
    New-Item -ItemType Directory -Path $Base | Out-Null

    # A process id that has gone, and one that is alive and is not PowerShell.
    $gone = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\cmd.exe') -ArgumentList '/c', 'exit' -WindowStyle Hidden -PassThru -Wait
    $DeadPid = $gone.Id
    $Sleeper = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\cmd.exe') -ArgumentList '/c', 'ping -n 120 127.0.0.1 >nul' -WindowStyle Hidden -PassThru
    Verify (-not (Get-Process -Id $DeadPid -ErrorAction SilentlyContinue)) "setup: process $DeadPid has exited"
    Verify ((Get-Process -Id $Sleeper.Id).ProcessName -eq 'cmd') "setup: process $($Sleeper.Id) is alive and is not PowerShell"

    $names = @{}
    $index = 0
    foreach ($row in $Corpus.rows) {
        $hex = '{0:x8}' -f $index
        $ownerPid = switch ($row.owner) { 'dead' { $DeadPid } 'self' { $PID } 'liveOther' { $Sleeper.Id } default { -1 } }
        $name = switch ($row.name) {
            'pid' { "$ScratchPrefix$ownerPid-$hex" }
            'legacy' { "$ScratchPrefix$hex" }
            'other' { "fsa-sl102-mutants-$hex" }
        }
        $names[$row.id] = $name
        $path = Join-Path $Base $name
        if ($row.kind -eq 'file') { Set-Content -LiteralPath $path -Value 'x'; $index++; continue }

        New-Item -ItemType Directory -Path $path | Out-Null
        foreach ($token in $row.contents) {
            switch ($token) {
                'copied' { Add-Content-File $path 'src\FsAnalyser.Core\Scanner.cs' | Out-Null; Add-Content-File $path 'eng\dotnet.ps1' | Out-Null }
                'built' {
                    Add-Content-File $path 'tests\FsAnalyser.Core.Tests\bin\Debug\net10.0-windows\FsAnalyser.Core.dll' | Out-Null
                    Add-Content-File $path 'tests\FsAnalyser.Core.Tests\obj\Debug\net10.0-windows\project.assets.cache' | Out-Null
                }
                'results' { Add-Content-File $path 'results\base-Ordinary\run.trx' | Out-Null }
                'readOnly' {
                    $file = Add-Content-File $path 'src\FsAnalyser.Core\Native\Kernel32.cs'
                    (Get-Item -LiteralPath $file).Attributes = [IO.FileAttributes]::ReadOnly
                }
                'heldOpen' {
                    $file = Add-Content-File $path 'tests\HoldRig\bin\Debug\net10.0-windows\HoldRig.dll'
                    $Held.Add([IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None))
                }
                'empty' { }
                default { throw "setup: row $($row.id) has the contents token '$token', which this runner does not know" }
            }
        }
        $index++
    }

    # ---- the first sweep: every state at once, with a file held open in the first stale one ----
    $State = @{ Released = 0 }
    $release = { $State.Released++ }.GetNewClosure()
    Write-Output 'the sweep:'
    $report = $null
    try { $report = @(Remove-StaleScratchRoots $Base $release) }
    catch { Verify $false "the sweep did not throw ($($_.Exception.Message))" }
    if ($null -ne $report) { Verify $true 'the sweep did not throw, whatever it found' }

    foreach ($row in $Corpus.rows) {
        $path = Join-Path $Base $names[$row.id]
        $there = Test-Path -LiteralPath $path
        $outcome = ($report | Where-Object { $_.Name -eq $names[$row.id] } | Select-Object -First 1).Outcome
        $label = "$($row.id) ($($row.name) name, $($row.kind), owner $($row.owner), [$($row.contents -join ', ')]): $($row.expected)"
        $onDisk = if ($row.expected -eq 'swept') { -not $there } else { $there }
        $reported = if ($row.expected -eq 'ignored') { $null -eq $outcome } else { $outcome -eq $row.expected }
        Verify ($onDisk -and $reported) "$label  [on disk: $(if ($there) { 'there' } else { 'gone' }), reported: $outcome]"
    }
    Verify ($State.Released -eq 1) "the build servers were shut down once, before anything was removed ($($State.Released) times)"

    # ---- once the holder lets go, the next run removes what it could not ----
    foreach ($stream in $Held) { $stream.Dispose() }
    $Held.Clear()
    $State.Released = 0
    $again = @(Remove-StaleScratchRoots $Base $release)
    $stuckRow = $Corpus.rows | Where-Object { $_.expected -eq 'stuck' } | Select-Object -First 1
    Write-Output 'the next sweep, the file no longer held:'
    Verify ($stuckRow -and -not (Test-Path -LiteralPath (Join-Path $Base $names[$stuckRow.id]))) "$($stuckRow.id): removed now"

    # ---- and with nothing left to remove it shuts nothing down ----
    $State.Released = 0
    $none = @(Remove-StaleScratchRoots $Base $release)
    Verify ($State.Released -eq 0) "nothing stale: the build servers were not shut down ($($State.Released) times)"
    Verify (@($none | Where-Object { $_.Outcome -ne 'kept' }).Count -eq 0) 'nothing stale: everything reported is kept'
}
finally {
    foreach ($stream in $Held) { $stream.Dispose() }
    if ($Sleeper -and -not $Sleeper.HasExited) { Stop-Process -Id $Sleeper.Id -Force -ErrorAction SilentlyContinue }
    if ((Test-Path -LiteralPath $Base) -and $Base.StartsWith($Temp + '\' + $ScratchPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        try { Clear-ScratchReadOnly $Base; [IO.Directory]::Delete($Base, $true) }
        catch { Write-Output "  note: $Base was not removed: $($_.Exception.Message)" }
    }
}

if ($Failures.Count -gt 0) {
    Write-Output "$($Failures.Count) check(s) failed:"
    $Failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "every row of the corpus ($(@($Corpus.rows).Count)) held"
exit 0
