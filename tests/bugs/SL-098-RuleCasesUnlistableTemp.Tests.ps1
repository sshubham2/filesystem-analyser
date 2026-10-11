<#
Bug (SL-098): tests/rules/RuleCases.ps1 cannot clean up after itself when TEMP is a folder the user may create in
but not list, which is the engine's own TEMP (C:\WINDOWS\TEMP: BUILTIN\Users get only (CI)(S,WD,AD,X) there).

RuleCases.ps1 places its scratch folders (New-TempDir, and the fsa-rules-* copy a failed harness run keeps) directly
in GetTempPath() and removes them with Remove-Item -LiteralPath <dir> -Recurse -Force -ErrorAction SilentlyContinue
(line 182 in Invoke-HarnessRaw, line 859 in the main finally). PowerShell's Remove-Item first calls
InternalSymbolicLinkLinkCodeMethods.IsReparsePointLikeSymlink, which runs FindFirstFileEx on the folder's own path
and so lists its PARENT. When the parent cannot be listed that throws System.ComponentModel.Win32Exception (native 5,
'Access is denied.'), a terminating error that -ErrorAction SilentlyContinue does not suppress.

Expected: a RuleCases run under a TEMP it cannot list stops no case, prints no 'Access is denied', runs to its summary
line, and leaves none of its folders in that TEMP (it removes them another way, e.g. [IO.Directory]::Delete inside a
try/catch, or it keeps its scratch in a TEMP it can list).
Actual: the first expected-failure harness run ends in 'C-006 stopped: Access is denied.' (the case is abandoned at
line 182), the finally at line 859 throws the same error, the script ends there with no summary line, and every
folder it made is left in the unlistable TEMP.

How it is measured: this runs the real tests/rules/RuleCases.ps1 -Case C-006 in a child shell whose TEMP and TMP are a
folder carrying a Deny ListDirectory ACE for the current user on that folder alone (create and traverse still work, as
under C:\WINDOWS\TEMP). The engine's python is a stand-in .cmd that does what harness.py does on a failed run: makes
fsa-rules-* in %TEMP%, says 'harness: scratch kept at <it>' and exits 2. Because it is not the engine, some of C-006's
own checks fail, so the exit code of RuleCases is not asserted; only the bug's symptoms are.

Worst case on this machine: one folder with a few dozen empty folders under the user's temp folder (or the one TEMP
names when it is outside the Windows folder), removed again at the end; the deny ACE is taken off first. About 3 s.
It SKIPS (exit 0, with the reason) when this is not Windows or the deny ACE does not make the folder unlistable.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-098-RuleCasesUnlistableTemp.Tests.ps1

Exit 0 when every check held (or it skipped), 1 when any failed.
#>

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    Write-Output 'SKIP: this reproduction needs Windows (an NTFS folder with a deny ACE).'
    exit 0
}

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Cases = Join-Path $Repo 'tests\rules\RuleCases.ps1'
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Check([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Output "  ok    $(($what -split "`n")[0])"
    }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

# A listable folder to work in: the TEMP this shell has, unless that is at or under the Windows folder (the engine's
# shells carry C:\WINDOWS\TEMP), when it is the user's own %LOCALAPPDATA%\Temp.
$temp = [System.IO.Path]::GetTempPath()
$windows = [Environment]::GetFolderPath('Windows')
if ($windows -and $temp.StartsWith($windows.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -and $env:LOCALAPPDATA) {
    $temp = Join-Path $env:LOCALAPPDATA 'Temp'
}
$Base = Join-Path $temp ('fsa-sl098-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
$NoList = Join-Path $Base 'no-list'
$StandIn = Join-Path $Base 'stand-in\python.cmd'
$EngineRoot = Join-Path $Base 'no-such-engine'

# Deny ListDirectory (ReadData) for the current user on the folder itself only: the user can still create children
# in it and open them by name, as in C:\WINDOWS\TEMP, but cannot enumerate it.
$deny = [System.Security.AccessControl.FileSystemAccessRule]::new(
    [System.Security.Principal.WindowsIdentity]::GetCurrent().User,
    [System.Security.AccessControl.FileSystemRights]::ListDirectory,
    [System.Security.AccessControl.InheritanceFlags]::None,
    [System.Security.AccessControl.PropagationFlags]::None,
    [System.Security.AccessControl.AccessControlType]::Deny)
$denied = $false

# Only the access list is read and written, so the owner and group are never touched.
function Get-NoListAcl {
    $info = [System.IO.DirectoryInfo]::new($NoList)
    $access = [System.Security.AccessControl.AccessControlSections]::Access
    if ($PSVersionTable.PSEdition -eq 'Core') { [System.IO.FileSystemAclExtensions]::GetAccessControl($info, $access) }
    else { $info.GetAccessControl($access) }
}

function Save-NoListAcl($acl) {
    $info = [System.IO.DirectoryInfo]::new($NoList)
    if ($PSVersionTable.PSEdition -eq 'Core') { [System.IO.FileSystemAclExtensions]::SetAccessControl($info, $acl) }
    else { $info.SetAccessControl($acl) }
}

function Set-NoList {
    $acl = Get-NoListAcl
    $acl.AddAccessRule($deny)
    Save-NoListAcl $acl
    $script:denied = $true
}

function Clear-NoList {
    if (-not $script:denied) { return }
    $acl = Get-NoListAcl
    [void]$acl.RemoveAccessRuleSpecific($deny)
    Save-NoListAcl $acl
    $script:denied = $false
}

try {
    [void][System.IO.Directory]::CreateDirectory($NoList)
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $StandIn))
    [void][System.IO.Directory]::CreateDirectory($EngineRoot)

    # What harness.py does on a failed run, in a form that starts in milliseconds: the scratch folder in the TEMP it
    # was started with (Python's tempfile reads TEMP), the line that names it, and exit 2.
    [System.IO.File]::WriteAllText($StandIn, (@(
                '@echo off',
                'set "work=%TEMP%\fsa-rules-sl098%RANDOM%%RANDOM%"',
                'mkdir "%work%" >nul 2>&1',
                'echo harness: CANNOT MEASURE: a stand-in for harness.py',
                'echo harness: scratch kept at %work%',
                'echo harness: exit 2',
                'exit /b 2'
            ) -join "`r`n") + "`r`n", [System.Text.Encoding]::ASCII)

    Set-NoList
    $listable = $true
    try { [void][System.IO.Directory]::GetDirectories($NoList) } catch { $listable = $false }
    if ($listable) {
        Write-Output "SKIP: a deny ACE on $NoList does not stop this user listing it here, so the engine's TEMP cannot be reproduced."
        exit 0
    }

    Write-Output 'RuleCases.ps1 -Case C-006 with TEMP and TMP a folder this user can create in but not list'
    $shell = (Get-Process -Id $PID).Path
    $saved = @{ TEMP = $env:TEMP; TMP = $env:TMP }
    $ErrorActionPreference = 'Continue'
    try {
        $env:TEMP = $NoList
        $env:TMP = $NoList
        $output = & $shell -NoProfile -ExecutionPolicy Bypass -File $Cases -Case C-006 -EnginePython $StandIn -EngineRoot $EngineRoot 2>&1 | Out-String
    }
    finally {
        $ErrorActionPreference = 'Stop'
        $env:TEMP = $saved.TEMP
        $env:TMP = $saved.TMP
    }

    Clear-NoList
    $left = @([System.IO.Directory]::GetFileSystemEntries($NoList) | ForEach-Object { Split-Path -Leaf $_ })
    $symptoms = ((($output -split "`r?`n") | Where-Object { $_ -match 'stopped:|Access is denied|Win32Exception' } | Select-Object -First 6) -join "`n")

    Check ($output -notmatch '(?m)\bC-\d{3} stopped:') "no case stops (a case stopped:`n$symptoms)"
    Check (-not $output.Contains('Access is denied')) "nothing is denied access while cleaning up`n$symptoms"
    Check ($output -match '(?m)^(All checks held\.|\d+ check\(s\) failed\.)\s*$') "the script runs to its summary line, past the cleanup in its finally`n$($output.Trim())"
    Check ($left.Count -eq 0) "no folder of the run is left in the unlistable TEMP (left: $($left -join ', '))"
}
finally {
    $ErrorActionPreference = 'Continue'
    try { Clear-NoList } catch { Write-Output "  note  could not take the deny ACE off ${NoList}: $($_.Exception.Message)" }
    try { [System.IO.Directory]::Delete($Base, $true) } catch { Write-Output "  note  could not remove ${Base}: $($_.Exception.Message)" }
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
