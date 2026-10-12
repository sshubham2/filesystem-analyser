<#
SL-105: how Mutants.ps1 keeps the copies it makes from piling up. Dot-sourced by Mutants.ps1 and by
ScratchRootCorpus.Tests.ps1; it does nothing when run alone.

A mutant run copies the tree into a folder of its own under %LOCALAPPDATA%\Temp and removes it in a `finally`. A run that is
killed (the engine's case runner ends one at 120 s, a tool call has its own limit, a person closes the window) never reaches
that `finally`, so the copy stays, and a build server that was started from it can keep its files open as well. Every
copy is therefore named for the process that made it, fsa-sl105-mutant-<pid>-<8 hex digits>, and the next run starts by
removing the ones whose process is gone: ScratchRootCorpus.json lists the states they are found in, and
ScratchRootCorpus.Tests.ps1 feeds each one in.

What is ours and what is not: a DIRECTORY whose name starts with fsa-sl105-mutant- is ours, whatever follows (the name an earlier
draft gave its copies had no process id). It is left alone when its process id names a process that is still running and is
PowerShell (a run in progress, or this one), and removed otherwise. A file, and a folder of any other name, is never touched.
A copy that cannot be removed (something still holds a file open) is reported and left, and the sweep goes on to the next.
#>

$ScratchPrefix = 'fsa-sl105-mutant-'

function New-ScratchRootName([int] $OwnerPid) {
    return $ScratchPrefix + $OwnerPid + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
}

# The process id a scratch folder's name carries, or -1 when it carries none.
function Get-ScratchOwnerPid([string] $Name) {
    if ($Name -match ('^' + [regex]::Escape($ScratchPrefix) + '(\d+)-[0-9a-f]{8}$')) { return [int] $Matches[1] }
    return -1
}

# A run in progress is a live PowerShell. A process id that has been handed to something else since is not one.
function Test-ScratchOwnerAlive([int] $OwnerPid) {
    if ($OwnerPid -lt 0) { return $false }
    $process = Get-Process -Id $OwnerPid -ErrorAction SilentlyContinue
    return [bool] ($process -and $process.ProcessName -in @('pwsh', 'powershell'))
}

# A copy's read-only files (Copy-Item keeps the attribute) refuse to be deleted. Links are not entered and not changed.
function Clear-ScratchReadOnly([string] $Folder) {
    foreach ($entry in [IO.DirectoryInfo]::new($Folder).EnumerateFileSystemInfos()) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        if ($entry.Attributes -band [IO.FileAttributes]::ReadOnly) { $entry.Attributes = $entry.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) }
        if ($entry.Attributes -band [IO.FileAttributes]::Directory) { Clear-ScratchReadOnly $entry.FullName }
    }
}

<#
Removes the scratch copies under $Base that no run owns any more, and returns one object per entry it looked at:
Name, and Outcome = 'swept' (removed), 'kept' (not ours to remove, or its run is alive) or 'stuck' (ours, but it could not
be removed and is still there). $Release is called once, with no argument, before the first removal: the build servers a killed
run started are shared by every build of its SDK and hold files of the copy it built, so they are shut down first.
#>
function Remove-StaleScratchRoots([string] $Base, [scriptblock] $Release = {}) {
    $report = New-Object System.Collections.Generic.List[object]
    if (-not (Test-Path -LiteralPath $Base -PathType Container)) { return @() }

    $stale = New-Object System.Collections.Generic.List[IO.DirectoryInfo]
    foreach ($entry in (Get-ChildItem -LiteralPath $Base -Force | Where-Object { $_.Name.StartsWith($ScratchPrefix, [StringComparison]::OrdinalIgnoreCase) } | Sort-Object Name)) {
        if (-not $entry.PSIsContainer -or (Test-ScratchOwnerAlive (Get-ScratchOwnerPid $entry.Name))) {
            $report.Add([pscustomobject] @{ Name = $entry.Name; Outcome = 'kept' })
        }
        else { $stale.Add($entry) }
    }
    if ($stale.Count -eq 0) { return $report.ToArray() }

    try { & $Release | Out-Null } catch { Write-Host "  note: the build servers were not shut down: $($_.Exception.Message)" }
    foreach ($folder in $stale) {
        try {
            Clear-ScratchReadOnly $folder.FullName
            [IO.Directory]::Delete($folder.FullName, $true)
        }
        catch { Write-Host "  note: $($folder.FullName) was not removed: $($_.Exception.Message)" }
        $outcome = if (Test-Path -LiteralPath $folder.FullName) { 'stuck' } else { 'swept' }
        $report.Add([pscustomobject] @{ Name = $folder.Name; Outcome = $outcome })
    }
    return $report.ToArray()
}
