<#
SL-098: the two helpers tests/rules/RuleCases.ps1 uses so that it works under a TEMP its user may create folders in but
not list (the engine's C:\WINDOWS\TEMP), fed the shapes they branch on. The acceptance test of the bug,
SL-098-RuleCasesUnlistableTemp.Tests.ps1, runs the whole script under such a TEMP and sees none of these: its scratch holds no
read-only file and no link. RuleCases.ps1 runs its cases when it is loaded, so each function is taken from its source through
the PowerShell parser (the route SL-076's test uses for Invoke-HarnessRaw) and defined here, with a stub Check that records
what the real one would have counted.

Remove-ScratchDir, in a parent this user cannot list (a Deny ListDirectory ACE on that folder alone, as in the acceptance test):
  - a tree with a read-only file is removed (a plain recursive delete stops on it: the same tree is shown to survive one);
  - a tree with a junction to a folder outside it, holding a read-only file and an ordinary one, is removed while the target
    keeps both files and the read-only attribute (the junction is unlinked, never followed: the read-only file in the tree is what
    sends the helper through its attribute walk, so the walk meets the junction);
  - a folder that is not there is a no-op that records nothing;
  - a tree with a file held open with FileShare.None records exactly one 'cleanup:' failure and does not throw, and the same
    call removes it once the handle is released.
Get-ScratchParent, as TempTree.ChooseTestsRoot is fed in TempTreeRootTests: a temp folder at or under the Windows folder (any case,
either slash, the folder itself) goes to LocalAppData\Temp; one that only shares the name does not; a LocalAppData that is blank,
not fully qualified or itself under the Windows folder is refused by name.
Both run under Windows PowerShell 5.1 as well as pwsh 7, so a run under pwsh repeats itself under powershell.exe.

Worst case on this machine: one folder with a few dozen small items under the user's temp folder (or LocalAppData\Temp when the
temp folder is in the Windows folder), removed again at the end with the deny ACE taken off first. About 10 s.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/bugs/SL-098-TempRoots.Tests.ps1

Exit 0 when every check held, 1 when any failed. Nothing is skipped: a shape this machine cannot build is a failure.
#>

param([switch] $NoRecurse)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$CasesRel = 'tests\rules\RuleCases.ps1'
$script:Failures = [System.Collections.Generic.List[string]]::new()
$script:Counted = [System.Collections.Generic.List[string]]::new()
$script:Junctions = [System.Collections.Generic.List[string]]::new()

# This script's own verdicts.
function Verify([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Output "  ok    $(($what -split "`n")[0])"
    }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

# What the functions under test call: the failure RuleCases would have counted.
function Check([bool] $condition, [string] $what) {
    if (-not $condition) { $script:Counted.Add($what) }
}

$parsed = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $Repo $CasesRel), [ref]$null, [ref]$null)
foreach ($name in 'Remove-ScratchDir', 'Get-ScratchParent') {
    $found = $parsed.Find({
            param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true)
    if (-not $found) { throw "test setup: no function $name in $CasesRel" }
    . ([scriptblock]::Create($found.Extent.Text))
}

# The attribute walk Remove-ScratchDir falls back on, nested in it, so that its reparse-point rule is fed on its own: the recursive
# delete that runs first unlinks a junction before it stops on a read-only file, so through Remove-ScratchDir the walk never meets
# one (review m1). It reads $state, $reparse and $readOnly from the function it is nested in.
$removeAst = $parsed.Find({
        param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Remove-ScratchDir' }, $true)
foreach ($name in 'Clear-ReadOnly', 'Open-Tree') {
    $found = $removeAst.Body.Find({
            param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true)
    if (-not $found) { throw "test setup: no function $name nested in Remove-ScratchDir in $CasesRel" }
    . ([scriptblock]::Create($found.Extent.Text))
}
$state = @{ Last = $null }
$reparse = [System.IO.FileAttributes]::ReparsePoint
$readOnly = [System.IO.FileAttributes]::ReadOnly

function Get-Counted { ($script:Counted -join ' | ') }

# ---- Get-ScratchParent ----

function Test-ScratchParent {
    Write-Output 'Get-ScratchParent: the temp folder, or LocalAppData\Temp when that is in the Windows folder'
    $win = 'C:\WINDOWS'
    $local = 'C:\Users\u\AppData\Local'
    $rows = @(
        @('the engine''s temp folder', 'C:\WINDOWS\TEMP\', $win, $local, 'C:\Users\u\AppData\Local\Temp'),
        @('the Windows folder itself', 'C:\WINDOWS', $win, $local, 'C:\Users\u\AppData\Local\Temp'),
        @('another case and forward slashes', 'c:/windows/temp', $win, $local, 'C:\Users\u\AppData\Local\Temp'),
        @('a folder deep under the Windows folder', 'C:\Windows\System32\config\systemprofile\AppData\Local\Temp\', $win, $local, 'C:\Users\u\AppData\Local\Temp'),
        @('the user''s own temp folder', 'C:\Users\u\AppData\Local\Temp\', $win, $local, 'C:\Users\u\AppData\Local\Temp\'),
        @('a name that only starts like the Windows folder', 'C:\WindowsX\Temp\', $win, $local, 'C:\WindowsX\Temp\'),
        @('a sibling folder named Windows.old', 'C:\Windows.old\Temp\', $win, $local, 'C:\Windows.old\Temp\'),
        @('Windows on another drive', 'D:\Temp\', $win, $local, 'D:\Temp\'),
        @('a blank Windows folder', 'C:\WINDOWS\TEMP\', '', $local, 'C:\WINDOWS\TEMP\'),
        @('a Windows folder that is not fully qualified', 'C:\WINDOWS\TEMP\', 'Windows', $local, 'C:\WINDOWS\TEMP\')
    )
    foreach ($row in $rows) {
        $what, $temp, $windows, $appData, $expected = $row
        $got = try { Get-ScratchParent $temp $windows $appData } catch { "threw: $($_.Exception.Message)" }
        Verify ($got -ceq $expected) "$what -> $expected (got $got)"
    }
    $refusals = @(
        @('a blank LocalAppData', ''),
        @('a LocalAppData that is not fully qualified', 'AppData\Local'),
        @('a LocalAppData with a drive and no folder separator', 'C:AppData'),
        @('a LocalAppData under the Windows folder (a service profile)', 'C:\Windows\ServiceProfiles\LocalService\AppData\Local')
    )
    foreach ($row in $refusals) {
        $what, $appData = $row
        $got = try { Get-ScratchParent 'C:\WINDOWS\TEMP\' $win $appData } catch { "threw: $($_.Exception.Message)" }
        Verify ($got.StartsWith('threw: RuleCases needs a temp folder outside the Windows folder')) "$what is refused by name (got $got)"
    }
    # With no arguments it reads this process: the folder is outside the Windows folder, and nothing is made.
    $parent = Get-ScratchParent
    $windows = [Environment]::GetFolderPath('Windows').TrimEnd('\')
    Verify (-not ($parent.TrimEnd('\') + '\').StartsWith($windows + '\', [StringComparison]::OrdinalIgnoreCase)) "this process's scratch parent $parent is outside the Windows folder $windows"
}

# ---- Remove-ScratchDir ----

# A listable folder to work in: the TEMP this process has, unless that is at or under the Windows folder, when it is LocalAppData\Temp.
$Base = Join-Path (Get-ScratchParent) ('fsa-sl098-roots-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
$NoList = Join-Path $Base 'no-list'
$Outside = Join-Path $Base 'outside'

$deny = [System.Security.AccessControl.FileSystemAccessRule]::new(
    [System.Security.Principal.WindowsIdentity]::GetCurrent().User,
    [System.Security.AccessControl.FileSystemRights]::ListDirectory,
    [System.Security.AccessControl.InheritanceFlags]::None,
    [System.Security.AccessControl.PropagationFlags]::None,
    [System.Security.AccessControl.AccessControlType]::Deny)
$denied = $false

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

function New-File([string] $path, [string] $text = 'scratch', [switch] $ReadOnly) {
    [System.IO.File]::WriteAllText($path, $text)
    if ($ReadOnly) { [System.IO.File]::SetAttributes($path, [System.IO.FileAttributes]::ReadOnly) }
}

function New-Junction([string] $link, [string] $target) {
    $out = & cmd.exe /c mklink /J "$link" "$target" 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or -not [System.IO.Directory]::Exists($link)) { throw "mklink /J $link $target failed: $out" }
    $script:Junctions.Add($link)
}

# A tree in the unlistable parent: nested folders, a few files, and the read-only file the shape asks for.
function New-Tree([string] $name, [switch] $ReadOnlyFile) {
    $root = Join-Path $NoList $name
    [void][System.IO.Directory]::CreateDirectory((Join-Path $root 'a\b'))
    New-File (Join-Path $root 'top.txt')
    New-File (Join-Path $root 'a\mid.txt')
    New-File (Join-Path $root 'a\b\leaf.txt')
    if ($ReadOnlyFile) { New-File (Join-Path $root 'a\b\locked.txt') -ReadOnly }
    $root
}

function Test-RemoveScratchDir {
    Write-Output 'Remove-ScratchDir in a parent this user can create in but not list'
    [void][System.IO.Directory]::CreateDirectory($NoList)
    [void][System.IO.Directory]::CreateDirectory($Outside)
    Set-NoList
    $listable = $true
    try { [void][System.IO.Directory]::GetDirectories($NoList) } catch { $listable = $false }
    Verify (-not $listable) "a deny ACE makes $NoList unlistable for this user, so the engine's TEMP is reproduced"
    if ($listable) { return }

    # A read-only file.
    $control = New-Tree 'control' -ReadOnlyFile
    $plain = try { [System.IO.Directory]::Delete($control, $true); 'removed' } catch { 'refused' }
    Verify ($plain -eq 'refused' -and [System.IO.Directory]::Exists($control)) "a plain recursive delete stops on the read-only file and leaves the tree (it $plain it), so the shape is fed"
    $script:Counted.Clear()
    $tree = $control
    Remove-ScratchDir $tree
    Verify (-not [System.IO.Directory]::Exists($tree)) 'a tree with a read-only file is removed under a parent that cannot be listed'
    Verify ($script:Counted.Count -eq 0) "and counts nothing ($(Get-Counted))"
    $script:Counted.Clear()
    $tree = New-Tree 'plain'
    Remove-ScratchDir $tree
    Verify (-not [System.IO.Directory]::Exists($tree) -and $script:Counted.Count -eq 0) "a tree with no read-only file is removed and counts nothing ($(Get-Counted))"

    # A junction to a folder outside.
    $target = Join-Path $Outside 'target'
    [void][System.IO.Directory]::CreateDirectory($target)
    New-File (Join-Path $target 'locked.txt') -ReadOnly
    New-File (Join-Path $target 'plain.txt')
    $tree = New-Tree 'with-junction' -ReadOnlyFile
    New-Junction (Join-Path $tree 'a\link') $target
    $script:Counted.Clear()
    Remove-ScratchDir $tree
    Verify (-not [System.IO.Directory]::Exists($tree)) 'a junction inside scratch is unlinked, and the tree that held it is removed'
    Verify ([System.IO.Directory]::Exists($target)) 'its target folder is still there'
    Verify ([System.IO.File]::Exists((Join-Path $target 'plain.txt'))) 'its ordinary file is still there'
    Verify ([System.IO.File]::Exists((Join-Path $target 'locked.txt')) -and
        ([System.IO.File]::GetAttributes((Join-Path $target 'locked.txt')) -band [System.IO.FileAttributes]::ReadOnly) -ne 0) 'its read-only file is still there and still read-only (the walk did not follow the junction)'
    Verify ($script:Counted.Count -eq 0) "and counts nothing ($(Get-Counted))"
    $script:Counted.Clear()
    $tree = New-Tree 'junction-only'
    New-Junction (Join-Path $tree 'link') $target
    Remove-ScratchDir $tree
    Verify (-not [System.IO.Directory]::Exists($tree) -and [System.IO.File]::Exists((Join-Path $target 'plain.txt')) -and $script:Counted.Count -eq 0) "a tree whose only unusual entry is a junction is removed without touching the target ($(Get-Counted))"

    # The walk on its own: a read-only file in the tree is cleared, a junction is unlinked and not followed.
    $tree = New-Tree 'walk' -ReadOnlyFile
    New-Junction (Join-Path $tree 'a\link') $target
    $state.Last = $null
    Open-Tree ([System.IO.DirectoryInfo]::new($tree))
    Verify ($null -eq $state.Last) "the attribute walk meets no error on a tree with a junction ($($state.Last.Message))"
    Verify (([System.IO.File]::GetAttributes((Join-Path $tree 'a\b\locked.txt')) -band [System.IO.FileAttributes]::ReadOnly) -eq 0) 'the walk clears the read-only file in the tree'
    Verify (-not [System.IO.Directory]::Exists((Join-Path $tree 'a\link'))) 'the walk unlinks the junction'
    Verify ([System.IO.File]::Exists((Join-Path $target 'plain.txt')) -and
        ([System.IO.File]::GetAttributes((Join-Path $target 'locked.txt')) -band [System.IO.FileAttributes]::ReadOnly) -ne 0) 'the target of the junction is untouched: its ordinary file is there and its read-only file is still read-only'
    $script:Counted.Clear()
    Remove-ScratchDir $tree
    Verify (-not [System.IO.Directory]::Exists($tree) -and $script:Counted.Count -eq 0) "and the tree is then removed ($(Get-Counted))"

    # A folder that is not there.
    $script:Counted.Clear()
    $gone = Join-Path $NoList 'never-made'
    $threw = $false
    try { Remove-ScratchDir $gone } catch { $threw = $true }
    Verify (-not $threw -and $script:Counted.Count -eq 0) "a missing folder is a no-op: it throws nothing and counts nothing ($(Get-Counted))"
    $again = New-Tree 'twice'
    Remove-ScratchDir $again
    Remove-ScratchDir $again
    Verify (-not [System.IO.Directory]::Exists($again) -and $script:Counted.Count -eq 0) 'a folder removed twice is still a no-op the second time'

    # A file held open by another handle.
    $script:Counted.Clear()
    $held = New-Tree 'held'
    $handle = [System.IO.File]::Open((Join-Path $held 'a\b\leaf.txt'), [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $threw = $false
    try {
        try { Remove-ScratchDir $held } catch { $threw = $true }
        Verify (-not $threw) 'a held file does not make the helper throw'
        Verify ($script:Counted.Count -eq 1 -and $script:Counted[0].StartsWith("cleanup: ${held}: ")) "a held file is counted once, naming the folder (counted: $(Get-Counted))"
        Verify ([System.IO.Directory]::Exists($held)) 'and the folder is still there while the handle is held'
    }
    finally {
        $handle.Dispose()
    }
    $script:Counted.Clear()
    Remove-ScratchDir $held
    Verify (-not [System.IO.Directory]::Exists($held) -and $script:Counted.Count -eq 0) "the same call removes it once the handle is released ($(Get-Counted))"
}

# ---- Main ----

try {
    [void][System.IO.Directory]::CreateDirectory($Base)
    try { Test-ScratchParent } catch { Verify $false "Get-ScratchParent stopped: $($_.Exception.Message)" }
    try { Test-RemoveScratchDir } catch { Verify $false "Remove-ScratchDir stopped: $($_.Exception.Message)`n$($_.ScriptStackTrace)" }
}
finally {
    $ErrorActionPreference = 'Continue'
    try { Clear-NoList } catch { Write-Output "  note  could not take the deny ACE off ${NoList}: $($_.Exception.Message)" }
    foreach ($link in $script:Junctions) {
        try { if ([System.IO.Directory]::Exists($link)) { [System.IO.Directory]::Delete($link) } } catch { Write-Output "  note  could not unlink ${link}: $($_.Exception.Message)" }
    }
    & attrib.exe -r "$Base\*" /s /d 2>&1 | Out-Null
    try { if ([System.IO.Directory]::Exists($Base)) { [System.IO.Directory]::Delete($Base, $true) } } catch { Write-Output "  note  could not remove ${Base}: $($_.Exception.Message)" }
    $ErrorActionPreference = 'Stop'
}
Verify (-not [System.IO.Directory]::Exists($Base)) "this test left nothing behind ($Base)"

# The functions run under Windows PowerShell 5.1 too (SettledCountProbe and the SL-069 test run RuleCases.ps1 under both).
if (-not $NoRecurse -and $PSVersionTable.PSEdition -eq 'Core') {
    Write-Output 'the same checks under Windows PowerShell 5.1'
    $ErrorActionPreference = 'Continue'
    $inner = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -NoRecurse 2>&1 | Out-String
    $innerExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Verify ($innerExit -eq 0 -and $inner -match '(?m)^All checks held\.\s*$') "powershell.exe runs every check above and they hold (exit $innerExit)`n$inner"
}

Write-Output ''
if ($script:Failures.Count) {
    Write-Output "$($script:Failures.Count) check(s) failed."
    exit 1
}
Write-Output 'All checks held.'
exit 0
