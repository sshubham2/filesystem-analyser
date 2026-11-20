<#
SL-008: 'Rescan as administrator' on the published app, in report mode, from an ordinary (not elevated) shell. The one unattended check
that the offer is READ FROM A VISIBLE BUTTON on a real whole local drive: every other check of the offer is a negative, and a report that
always said offered=false would pass them all.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/SL-008-RescanOfferOnALocalDrive.Tests.ps1 [-Publish] [-Exe <path>] [-DriveTimeoutSec <n>]

It runs, in this order, and says each one's verdict:

  privileged  before anything is launched: a shell that is administrator, or SYSTEM, ends the script INCONCLUSIVE (exit 2). The app makes no
              offer in an elevated copy, so offered=false is the right answer there and the checks below would call it a defect; and a
              start that asks for elevation is no longer seen as one (there is nothing to prompt).
  no-path     the app started with no path (CreateProcess, never the shell's start, which would raise a prompt where CreateProcess
  path-window with 740): still running after a few seconds is held; an exit, 740 at the launch, or a start that fails is FAILED. The same
              with a folder as its path: the window scans it and stays.
  folder      report mode on a small folder, no trailing backslash, then with one: exit 0 within 60 s (a tiny folder ends in seconds,
              so a run still going is a start that is waiting on a prompt: FAILED, with 740 at the launch and with no report), and the
              report's rescanAsAdministrator.offered is the boolean false. A report with the key missing is FAILED.
  root-unreadable  report mode on a folder that is not there: the same, and the ending is root-unreadable.
  drive-pick  the ready Fixed drive with the least used space that opens through \\?\GLOBALROOT\GLOBAL??\X:\ from this script, the same
              open the app's probe makes. Not a SUBST letter, and not any letter this logon session alone holds (QueryDosDevice cannot tell
              a session's own \Device\ letter from a global one; only the open does). None: INCONCLUSIVE.
  drive       report mode on that drive, bounded by -DriveTimeoutSec (3600). A completed ending that left folders unread: offered=true read
              from a button that is visible and in view, on the pickers' line (PickerPanel's height is the folder run's) is held;
              offered=false is FAILED. Unreadable 0 with offered=false, a timeout, no report and an outcome other than completed are
              INCONCLUSIVE; unreadable 0 with offered=true is FAILED.

The verdict of the script: any FAILED is FAILED (exit 1); else any INCONCLUSIVE is INCONCLUSIVE (exit 2); else held (exit 0). A
FAILED in a short run is never lost behind a drive scan that did not end. System Volume Information is refused to an unelevated token on
every NTFS volume (measured on C:\), so a whole drive that finishes is expected to leave at least one folder unread.

-Stub <file> replaces every launch, the report read, the privilege read and the drive list with what a JSON file says, so the table
above is run for every ending a real run can have without a prompt, a window or a drive scan. The file is one scenario, or { "rows": [ ... ] }:
each row is { id, privileged, drives: [ { letter, type, ready, usedBytes, opensGlobal } ], runs: { noPath, pathWindow, folder,
folderSlash, rootUnreadable, drive } }, a run being { exit, report }. With rows, one line is written per row,
`ROW <id> exit=<n> <step>=<verdict> ...`, and the script's own exit code is 0; with one scenario the exit code is the verdict's. The
rows and what each must give are tests/window/SL-008-RescanOfferEndingsCorpus.json, run by RescanOfferEndingsTests.

Needs the published app (artifacts/publish), built here by eng/dotnet.ps1 when it is missing or older than src. Scratch is under
%LOCALAPPDATA%\Temp (never the engine shell's %TEMP%, which is C:\WINDOWS\TEMP there, where nothing can be deleted) and is removed at the
end. Runs under pwsh and under Windows PowerShell 5.1.
#>

param(
    [string] $Exe = '',
    [switch] $Publish,
    [int] $DriveTimeoutSec = 3600,
    [int] $ShortTimeoutSec = 60,
    [int] $WindowSeconds = 6,
    [string] $Stub = ''
)

$ErrorActionPreference = 'Stop'
$script:Stubbed = $false
$script:Quiet = $false
$script:Row = $null
$script:Results = New-Object System.Collections.Generic.List[object]

# Written to the console and not the pipeline, so that a function that says something still returns only its value.
function Say([string] $text) { if (-not $script:Quiet) { [Console]::Out.WriteLine($text) } }

function Add-Result([string] $id, [string] $verdict, [string] $why) {
    $script:Results.Add([pscustomobject]@{ Id = $id; Verdict = $verdict; Why = $why })
    Say ('  {0,-12} {1}: {2}' -f $verdict, $id, $why)
}

# ---- what the shell is ----

# Administrator or SYSTEM, read the way that works under both PowerShells: a UAC-limited administrator's token holds the
# Administrators group as deny-only, so IsInRole is false for it, as it is for the app that does not offer in an ordinary copy.
function Get-Privileged {
    if ($script:Stubbed) { return [bool]$script:Row.privileged }
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    [bool]($principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator) -or $identity.IsSystem)
}

# ---- the drives ----

function Initialize-Native {
    if ('FsaSl008.Native' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace FsaSl008
{
    public static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern uint SetErrorMode(uint mode);

        // Whether the letter's root opens through the global DOS-device namespace: the open the app's probe makes, with the same
        // flags (FILE_READ_ATTRIBUTES, share all, open existing, backup semantics). No "insert a disk" box for a drive with no media.
        public static bool OpensThroughGlobal(char letter)
        {
            uint previous = SetErrorMode(1);
            try
            {
                IntPtr handle = CreateFileW(@"\\?\GLOBALROOT\GLOBAL??\" + char.ToUpperInvariant(letter) + @":\", 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (handle == new IntPtr(-1))
                    return false;
                CloseHandle(handle);
                return true;
            }
            finally
            {
                SetErrorMode(previous);
            }
        }
    }
}
'@
}

# One row per drive: its letter, type, whether it is ready, its used bytes, and whether its letter opens through the global namespace.
# The namespace is opened only for a Fixed drive that is ready, so a network letter is never touched from here.
function Get-Drives {
    if ($script:Stubbed) { return @($script:Row.drives) }
    Initialize-Native
    $rows = @()
    foreach ($drive in [System.IO.DriveInfo]::GetDrives()) {
        $ready = $false
        $used = [long]0
        $global = $false
        if ($drive.DriveType -eq [System.IO.DriveType]::Fixed) {
            try { $ready = [bool]$drive.IsReady } catch { $ready = $false }
            if ($ready) {
                try { $used = [long]($drive.TotalSize - $drive.TotalFreeSpace) } catch { $ready = $false }
                if ($ready) { $global = [FsaSl008.Native]::OpensThroughGlobal($drive.Name[0]) }
            }
        }
        $rows += [pscustomobject]@{ letter = [string]$drive.Name[0]; type = [string]$drive.DriveType; ready = $ready; usedBytes = $used; opensGlobal = $global }
    }
    $rows
}

# The drive the scan is run on: the ready Fixed one with the least used space whose letter the global namespace holds.
function Select-Drive($drives) {
    $qualifying = @($drives | Where-Object { $_.ready -and $_.type -eq 'Fixed' -and $_.opensGlobal }) # MUTATE:global
    if ($qualifying.Count -eq 0) { return $null }
    ($qualifying | Sort-Object { [long]$_.usedBytes }, { [string]$_.letter } | Select-Object -First 1)
}

# ---- one launch ----

# The root as one argument that Windows' own splitting hands back as it was: in quotes, a trailing run of backslashes doubled so that it
# does not escape the closing quote (SL-007, SL-089). The same rule eng/read-window.ps1 has.
function Format-Argument([string] $Value) {
    '"' + ($Value -replace '(\\+)$', '$1$1') + '"'
}

function Get-Win32Code($exception) {
    for ($e = $exception; $null -ne $e; $e = $e.InnerException) {
        if ($e -is [System.ComponentModel.Win32Exception]) { return [int]$e.NativeErrorCode }
    }
    return 0
}

# The app by CreateProcess, never the shell's start (which would raise a prompt for a program that asks to be elevated, and wait for a person).
# Returns Exit: 'EXIT <n>', 'TIMEOUT' (killed after the bound), 'ALIVE' (a window run still going after the bound, then killed), 'ELEVATION'
# (Windows refused the start with 740: the program asks for elevation) or 'NOSTART <code>'; and Report: the report read, or $null.
function Invoke-Launch([string] $Kind, [string] $Root, [int] $TimeoutSec) {
    if ($script:Stubbed) {
        $run = $script:Row.runs.$Kind
        # A row with more than one drive gives each its own run: drive<Letter>, the drive run's when there is none.
        if ($Kind -ceq 'drive') {
            $own = $script:Row.runs.PSObject.Properties['drive' + $Root.Substring(0, 1)]
            if ($null -ne $own -and $null -ne $own.Value) { $run = $own.Value }
        }
        if ($null -eq $run) { return [pscustomobject]@{ Exit = 'NOTHING'; Report = $null } }
        return [pscustomobject]@{ Exit = [string]$run.exit; Report = $run.report }
    }
    $folder = Join-Path $env:LOCALAPPDATA 'Temp\fsa-sl008'
    [void][System.IO.Directory]::CreateDirectory($folder)
    $report = Join-Path $folder ('{0}.json' -f [guid]::NewGuid().ToString('N'))
    $window = $Kind -in 'noPath', 'pathWindow'
    $arguments = if ($Kind -ceq 'noPath') { '' } elseif ($window) { Format-Argument $Root } else { '{0} --report {1}' -f (Format-Argument $Root), (Format-Argument $report) }

    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $script:Exe
    $info.Arguments = $arguments
    $info.UseShellExecute = $false
    try { $process = [System.Diagnostics.Process]::Start($info) }
    catch {
        $code = Get-Win32Code $_.Exception
        return [pscustomobject]@{ Exit = $(if ($code -eq 740) { 'ELEVATION' } else { "NOSTART $code" }); Report = $null }
    }
    $null = $process.Handle
    $bound = if ($window) { $WindowSeconds } else { $TimeoutSec }
    $exit = $null
    if ($process.WaitForExit($bound * 1000)) { $exit = "EXIT $($process.ExitCode)" }
    else {
        try { $process.Kill() } catch { }
        $process.WaitForExit()
        $exit = if ($window) { 'ALIVE' } else { 'TIMEOUT' }
    }
    $read = $null
    if (Test-Path -LiteralPath $report -PathType Leaf) {
        $read = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        Remove-Item -LiteralPath $report -Force
    }
    [pscustomobject]@{ Exit = $exit; Report = $read }
}

# ---- the verdicts: one row for every ending a run can have ----
# held / FAILED / INCONCLUSIVE. A short run that does not behave is a defect; a drive scan that does not end says nothing about the code.

function New-Verdict([string] $Verdict, [string] $Why) { [pscustomobject]@{ Verdict = $Verdict; Why = $Why } }

# The app started with no path, or with a folder as its path: a window app, so a run still going after the bound is right.
function Get-WindowVerdict($read) {
    if ($read.Exit -ceq 'ELEVATION') { return New-Verdict 'FAILED' 'the app asked for elevation at its start (Windows refused the start with 740)' }
    if ($read.Exit -ceq 'ALIVE') { return New-Verdict 'held' 'the window stayed up, with no prompt and no exit' }
    if ($read.Exit -like 'NOSTART*') { return New-Verdict 'FAILED' "the app did not start ($($read.Exit))" }
    New-Verdict 'FAILED' "the app ended ($($read.Exit)) where its window should have stayed up"
}

# A report-mode run of a root that is no drive: the app ends by itself with exit 0 and a report, and the report says the ending offers nothing.
function Get-ReportVerdict($read, [string] $EndingWanted) {
    if ($read.Exit -ceq 'ELEVATION') { return New-Verdict 'FAILED' 'the app asked for elevation at its start (Windows refused the start with 740)' }
    if ($read.Exit -ceq 'TIMEOUT') { return New-Verdict 'FAILED' 'the app was still running after the short bound: a start that waits on a prompt, or a window that never ended' }
    if ($read.Exit -like 'NOSTART*' -or $read.Exit -ceq 'NOTHING') { return New-Verdict 'FAILED' "the app did not start ($($read.Exit))" }
    if ($null -eq $read.Report) { return New-Verdict 'FAILED' "no report ($($read.Exit))" }
    if ($read.Exit -cne 'EXIT 0') { return New-Verdict 'FAILED' "the app exited with '$($read.Exit)', not EXIT 0" }
    $key = $read.Report.PSObject.Properties['rescanAsAdministrator']
    if ($null -eq $key -or $null -eq $key.Value -or $null -eq $key.Value.PSObject.Properties['offered']) { return New-Verdict 'FAILED' 'the report has no rescanAsAdministrator.offered' }
    if ($key.Value.offered -isnot [bool]) { return New-Verdict 'FAILED' 'rescanAsAdministrator.offered is not true or false' }
    if ($key.Value.offered) { return New-Verdict 'FAILED' "the ending of a root that is no drive offers 'Rescan as administrator'" }
    if ($EndingWanted -and $read.Report.outcome -cne $EndingWanted) { return New-Verdict 'INCONCLUSIVE' "the ending was '$($read.Report.outcome)', not '$EndingWanted': the offer is false, as it is, but this is not the run the case needs" }
    New-Verdict 'held' 'the key is there and false'
}

# The whole drive. $folderHeight is PickerPanel's height in the folder run: the pickers' line must keep it with the button on it.
function Get-DriveVerdict($read, $folderHeight) {
    if ($read.Exit -ceq 'ELEVATION') { return New-Verdict 'FAILED' 'the app asked for elevation at its start (Windows refused the start with 740)' }
    if ($read.Exit -ceq 'TIMEOUT') { return New-Verdict 'INCONCLUSIVE' 'the drive scan did not end within its bound' }
    if ($null -eq $read.Report) { return New-Verdict 'INCONCLUSIVE' "no report ($($read.Exit))" }
    if ($read.Exit -cne 'EXIT 0') { return New-Verdict 'FAILED' "the app exited with '$($read.Exit)' and wrote a report" }
    $report = $read.Report
    if ($null -eq $report.core -or $report.outcome -cne 'completed') { return New-Verdict 'INCONCLUSIVE' "the scan did not complete (outcome '$($report.outcome)'): a drive that was stopped or refused has nothing to offer" }
    $unreadable = [int]$report.core.unreadable
    $key = $report.PSObject.Properties['rescanAsAdministrator']
    if ($null -eq $key -or $null -eq $key.Value -or $key.Value.offered -isnot [bool]) { return New-Verdict 'FAILED' 'the report has no rescanAsAdministrator.offered, true or false' }
    $offered = [bool]$key.Value.offered
    if ($unreadable -eq 0) {
        if ($offered) { return New-Verdict 'FAILED' "the ending offers 'Rescan as administrator' with 0 folders unread" }
        return New-Verdict 'INCONCLUSIVE' 'no folder was left unread on this drive, so there is nothing to offer'
    }
    if (-not $offered) { return New-Verdict 'FAILED' "the scan finished with $unreadable folders unread, in a copy that is not elevated, and the ending does not offer 'Rescan as administrator'" }
    $button = $report.window.RescanAsAdministratorButton
    if ($null -eq $button -or $button.visible -ne $true) { return New-Verdict 'FAILED' 'offered is true, and the button is not visible' }
    if ($button.inView -ne $true) { return New-Verdict 'FAILED' 'the button is visible and not in view' }
    $height = $report.window.PickerPanel.actualHeight
    if ($null -ne $folderHeight -and [Math]::Abs([double]$height - [double]$folderHeight) -gt 0.5) {
        return New-Verdict 'FAILED' "the pickers' line took $height px with the button, and $folderHeight px in the folder run"
    }
    New-Verdict 'held' "offered is true with $unreadable folders unread, read from a visible button that is in view, and the pickers' line kept its height"
}

# ---- the scratch tree ----

function New-Scratch {
    if ($script:Stubbed) { return [pscustomobject]@{ Base = 'stub'; Tree = 'stub\tree'; Missing = 'stub\missing' } }
    $base = Join-Path $env:LOCALAPPDATA ('Temp\fsa-sl008\{0}' -f [guid]::NewGuid().ToString('N'))
    $tree = Join-Path $base 'tree'
    for ($i = 0; $i -lt 3; $i++) {
        [void][System.IO.Directory]::CreateDirectory((Join-Path $tree "d$i\inner"))
        [System.IO.File]::WriteAllBytes((Join-Path $tree "d$i\f.bin"), (New-Object byte[] 2048))
    }
    [pscustomobject]@{ Base = $base; Tree = $tree; Missing = (Join-Path $base 'missing') }
}

function Remove-Scratch($scratch) {
    if ($script:Stubbed -or -not $scratch -or -not $scratch.Base) { return }
    try { if (Test-Path -LiteralPath $scratch.Base) { [System.IO.Directory]::Delete($scratch.Base, $true) } } catch { Say "  (could not remove $($scratch.Base): $($_.Exception.Message))" }
    $parent = Join-Path $env:LOCALAPPDATA 'Temp\fsa-sl008'
    try { if ((Test-Path -LiteralPath $parent) -and -not (Get-ChildItem -LiteralPath $parent -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $parent -Force } } catch { }
}

function Update-Published {
    if ($script:Stubbed) { return }
    $repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $given = [bool]$script:Exe
    if (-not $script:Exe) { $script:Exe = Join-Path $repo 'artifacts\publish\FsAnalyser.App.exe' }
    $newest = (Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Recurse -File -Include *.cs, *.xaml, *.csproj |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
    $stale = $Publish -or -not (Test-Path -LiteralPath $script:Exe -PathType Leaf) -or (Get-Item -LiteralPath $script:Exe).LastWriteTimeUtc -lt $newest
    if ($stale -and -not $given) {
        Say '  (publishing the app)'
        $result = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'eng\dotnet.ps1') publish (Join-Path $repo 'src\FsAnalyser.App\FsAnalyser.App.csproj') -c Release -o (Join-Path $repo 'artifacts\publish') 2>&1)
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $script:Exe -PathType Leaf)) {
            $result | Select-Object -Last 15 | ForEach-Object { Say "  $_" }
            throw "the app did not publish (exit $LASTEXITCODE)"
        }
    }
    if (-not (Test-Path -LiteralPath $script:Exe -PathType Leaf)) { throw "no app at $($script:Exe)" }
}

# ---- the whole run: one scenario, real or stubbed ----

function Get-Overall {
    if (@($script:Results | Where-Object { $_.Verdict -ceq 'FAILED' }).Count -gt 0) { return 1 }
    if (@($script:Results | Where-Object { $_.Verdict -like 'INCONCLUSIVE*' }).Count -gt 0) { return 2 }
    0
}

function Invoke-Scenario {
    $script:Results.Clear()
    $privileged = Get-Privileged # MUTATE:privileged
    if ($privileged) {
        Add-Result 'privileged' 'INCONCLUSIVE' 'this shell is administrator or SYSTEM: the app makes no offer in an elevated copy, and a start that asks for elevation raises no prompt here'
        return (Get-Overall)
    }
    Add-Result 'privileged' 'held' 'an ordinary shell'

    $scratch = New-Scratch
    try {
        $window = Invoke-Launch 'noPath' '' $WindowSeconds
        $v = Get-WindowVerdict $window
        Add-Result 'no-path' $v.Verdict $v.Why

        $window = Invoke-Launch 'pathWindow' $scratch.Tree $WindowSeconds
        $v = Get-WindowVerdict $window
        Add-Result 'path-window' $v.Verdict $v.Why

        $folder = Invoke-Launch 'folder' $scratch.Tree $ShortTimeoutSec
        $v = Get-ReportVerdict $folder 'completed'
        Add-Result 'folder' $v.Verdict $v.Why
        $folderHeight = if ($null -ne $folder.Report -and $null -ne $folder.Report.window) { $folder.Report.window.PickerPanel.actualHeight } else { $null }

        $slash = Invoke-Launch 'folderSlash' ($scratch.Tree + '\') $ShortTimeoutSec
        $v = Get-ReportVerdict $slash 'completed'
        Add-Result 'folder-slash' $v.Verdict $v.Why

        $missing = Invoke-Launch 'rootUnreadable' $scratch.Missing $ShortTimeoutSec
        $v = Get-ReportVerdict $missing 'root-unreadable'
        Add-Result 'root-unreadable' $v.Verdict $v.Why

        $pick = Select-Drive (Get-Drives)
        if ($null -eq $pick) {
            Add-Result 'drive-pick' 'INCONCLUSIVE' 'no ready Fixed drive opens through the global DOS-device namespace: a SUBST letter or a session-local one is no drive an elevated copy can scan'
            return (Get-Overall)
        }
        Add-Result 'drive-pick' 'held' "the drive with the least used space whose letter the global namespace holds ($($pick.usedBytes) bytes used)"
        $drive = Invoke-Launch 'drive' ("{0}:\" -f $pick.letter) $DriveTimeoutSec
        $v = Get-DriveVerdict $drive $folderHeight
        Add-Result 'drive' $v.Verdict $v.Why
    }
    finally { Remove-Scratch $scratch }
    Get-Overall
}

function Write-Summary([int] $code) {
    Say ''
    foreach ($r in $script:Results | Where-Object { $_.Verdict -like 'INCONCLUSIVE*' }) { Say "INCONCLUSIVE: $($r.Id): $($r.Why)" }
    foreach ($r in $script:Results | Where-Object { $_.Verdict -ceq 'FAILED' }) { Say "FAILED: $($r.Id): $($r.Why)" }
    Say $(switch ($code) { 0 { 'ALL CHECKS HELD' } 1 { 'FAILED' } default { 'INCONCLUSIVE' } })
}

# ---- go ----

if ($Stub) {
    $script:Stubbed = $true
    $data = Get-Content -LiteralPath $Stub -Raw | ConvertFrom-Json
    if ($null -ne $data.PSObject.Properties['rows']) {
        $script:Quiet = $true
        foreach ($row in $data.rows) {
            $script:Row = $row
            $code = Invoke-Scenario
            $steps = ($script:Results | ForEach-Object { '{0}={1}' -f $_.Id, $_.Verdict }) -join ' '
            Write-Output ("ROW {0} exit={1} {2}" -f $row.id, $code, $steps)
        }
        exit 0
    }
    $script:Row = $data
    $code = Invoke-Scenario
    Write-Summary $code
    exit $code
}

Update-Published
$code = Invoke-Scenario
Write-Summary $code
exit $code
