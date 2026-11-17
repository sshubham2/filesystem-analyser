<#
SL-001: the whole-drive coverage panel, read from the published exe's own window in report mode (eng/read-window.ps1), the
way validate reads it. One case per shape the design names:

  A  a finished scan of a whole local drive (a SUBST letter over a %TEMP% tree) whose volume figure lands: the panel is on screen
     and the report carries 'coverage' with its figures; the $MFT is the one measured part, within 64 MiB of FileInfo of the
     volume's own $MFT; measured + remainder = unseen; the remainder is 'space this account cannot read', never measured, with no
     container layer store in it; OutcomeDetail keeps its four lines and the finished coverage line word for word.
  B  the same tree by its folder path: no panel, 'coverage' null.
  C  a drive letter with nothing behind it: no panel, root-unreadable.
  D  two folders this account may not list (deny RD): named, 'unreadable', no size, as many as core.unreadable.
  E  the container layer store left out unopened (a SUBST letter over %ProgramData%\Microsoft\Windows\Containers, the precaution
     on): named 'not-scanned' with no size, the remainder says it includes the store, nothing opened under it.
  F  two 64 KiB files whose size probe is refused (deny S,RA): reported apart, never subtracted from the unseen space.
  G  one such file, in the singular, in the note beside it.
  H  a whole-drive scan stopped part-way by the hook's stop mode, whose volume figure still lands: no panel, and the partial
     line word for word.

The panel sits inside the outcome texts, which are capped at 76 px while a section of the ending is shown, so it is a scroll away:
every case that shows it records 'top' less OutcomeTexts' verticalOffset against OutcomeTexts' actualHeight, and says 'a scroll away'.

  pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/SL-001-CoveragePanel.Tests.ps1 [-Case A,B,...] [-NoStore]

Needs the published app (artifacts/publish) and, for case H, the built hook (artifacts/window-hook); both are built by
eng/dotnet.ps1 (see eng/read-window.ps1's header and tests/window/WindowCases.ps1's Update-Hook). Exit 0 when every check held,
1 when any failed. A case whose shape could not be made on this machine says INCONCLUSIVE and is not a failure.
#>

param(
    [string[]] $Case = @('A', 'B', 'C', 'D', 'E', 'F', 'G', 'H'),
    [switch] $NoStore,
    [switch] $Publish,
    [string] $Exe = '',
    [string] $HookDll = ''
)

$ErrorActionPreference = 'Stop'
$Case = @($Case | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) # -File passes 'A,B' as one string
$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Reader = Join-Path $Repo 'eng\read-window.ps1'
if (-not $Exe) { $Exe = Join-Path $Repo 'artifacts\publish\FsAnalyser.App.exe' }
if (-not $HookDll) { $HookDll = Join-Path $Repo 'artifacts\window-hook\FsaWindowHook.dll' }
$KiB64 = 64 * 1024
$MiB64 = 64 * 1024 * 1024
$script:Failures = New-Object System.Collections.Generic.List[string]
$script:Inconclusive = New-Object System.Collections.Generic.List[string]
$Sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value

function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Output "  ok    $what" }
    else { Write-Output "  FAIL  $what"; $script:Failures.Add($what) }
}
function CheckEqual($expected, $actual, [string] $what) {
    Check ($expected -ceq $actual -or ($null -eq $expected -and $null -eq $actual) -or "$expected" -ceq "$actual") "$what (expected $expected, got $actual)"
}

function New-Case {
    $folder = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-sl001\{0}" -f [guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($folder)
    $folder
}
function New-File([string] $path, [long] $bytes) {
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    $content = [byte[]]::new($bytes)
    [System.Random]::new(20).NextBytes($content) # does not compress, so the file takes its own size on disk
    [System.IO.File]::WriteAllBytes($path, $content)
}
function Get-FreeDriveLetter {
    $used = @([System.Environment]::GetLogicalDrives() | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() })
    foreach ($letter in 'ZYXWVUTSRQPONMLKJIHGFED'.ToCharArray()) { if ($used -notcontains [string]$letter) { return [string]$letter } }
    throw 'every drive letter from D to Z is in use'
}
function Add-Subst([string] $target) {
    $letter = Get-FreeDriveLetter
    $out = & subst.exe "${letter}:" $target 2>&1
    if ($LASTEXITCODE -ne 0) { throw "subst ${letter}: $target exited ${LASTEXITCODE}: $out" }
    $letter
}
function Remove-Subst([string] $letter) { if ($letter) { & subst.exe "${letter}:" /D 2>&1 | Out-Null } }
function Deny([string] $path, [string] $rights) {
    $out = & icacls.exe $path /deny "*${Sid}:($rights)" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "icacls /deny on $path exited ${LASTEXITCODE}: $out" }
}
function Undeny([string] $path) {
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return }
    & icacls.exe $path /remove:d "*$Sid" 2>&1 | Out-Null
}
function Remove-Case([string] $folder) {
    if (Test-Path -LiteralPath $folder) { try { [System.IO.Directory]::Delete('\\?\' + $folder, $true) } catch { Write-Output "  (could not remove $folder : $($_.Exception.Message))" } }
}

# One report-mode launch through eng/read-window.ps1; $variables are set for it alone and removed however it ends.
function Read-Window([string] $root, [hashtable] $variables = @{}, [int] $timeoutSec = 120) {
    try {
        foreach ($name in $variables.Keys) { Set-Item -LiteralPath "Env:$name" -Value $variables[$name] }
        $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $Reader -Root $root -Exe $Exe -TimeoutSec $timeoutSec)
    }
    finally {
        foreach ($name in $variables.Keys) { if (Test-Path -LiteralPath "Env:$name") { Remove-Item -LiteralPath "Env:$name" } }
    }
    $text = ($lines | Select-Object -Skip 1) -join "`n"
    [pscustomobject]@{ Exit = $lines[0]; Text = $text; Report = if ($text -eq 'NO REPORT') { $null } else { $text | ConvertFrom-Json } }
}

# The published app and the hook are built from the source as it is, never taken from an earlier run: the default exe is published
# again when it is missing, older than the newest source file, or -Publish says so (the same for the hook, which case H needs).
function Get-NewestSource([string] $folder) {
    (Get-ChildItem -LiteralPath $folder -Recurse -File -Include *.cs, *.xaml, *.csproj |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
}
function Update-Built([string] $output, [string] $project, [string[]] $extraArguments, [string] $artifact, [string] $sources) {
    $stale = $Publish -or -not (Test-Path -LiteralPath $artifact -PathType Leaf) -or (Get-Item -LiteralPath $artifact).LastWriteTimeUtc -lt (Get-NewestSource $sources)
    if (-not $stale) { return }
    Write-Output "building $project into $output"
    $result = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'eng\dotnet.ps1') @extraArguments (Join-Path $Repo $project) -c Release -o $output 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        $result | Select-Object -Last 15 | ForEach-Object { "  $_" }
        throw "$project did not build (exit $LASTEXITCODE)"
    }
}
if (-not $PSBoundParameters.ContainsKey('Exe')) {
    Update-Built (Join-Path $Repo 'artifacts\publish') 'src\FsAnalyser.App\FsAnalyser.App.csproj' @('publish') $Exe (Join-Path $Repo 'src')
}
if ($Case -contains 'H' -and -not $PSBoundParameters.ContainsKey('HookDll')) {
    Update-Built (Join-Path $Repo 'artifacts\window-hook') 'tests\window\FsaWindowHook\FsaWindowHook.csproj' @('build') $HookDll (Join-Path $Repo 'tests\window\FsaWindowHook')
}

$FinishedLine = '^The volume reports .+ in use, so .+ of it was not seen by this scan \(file-system metadata, folders it could not read, and anything else an ordinary account cannot list\)\.$'
$PartialLine = '^The volume reports .+ in use; .+ of it is not in this partial scan\.$'
$UnnegatedMeasured = '(?i)(?<!not )(?<!never )\bmeasured\b'
$VolumeRoot = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetTempPath())

function CheckEnding($read, [string] $what) {
    Check ($read.Exit -eq 'EXIT 0') "$what`: the app exited 0 ($($read.Exit))"
    Check ($null -ne $read.Report) "$what`: a report was written"
    Check (-not [regex]::IsMatch($read.Text, '[^\x00-\x7F]')) "$what`: the report is pure ASCII"
}

# Where the panel is, for a case that shows it: a scroll away, as the design says, and read by the report as such.
function CheckAScrollAway($report) {
    $window = $report.window
    CheckEqual $true $window.CoveragePanel.visible 'CoveragePanel is shown'
    Check ($null -ne $window.CoveragePanel.top -and $window.CoveragePanel.actualHeight -gt 0) "CoveragePanel has a height ($($window.CoveragePanel.actualHeight)) and a top ($($window.CoveragePanel.top))"
    $below = $window.CoveragePanel.top - $window.OutcomeTexts.verticalOffset
    if ($below -ge $window.OutcomeTexts.actualHeight) {
        Write-Output "  note  the panel is a scroll away: it starts $([int]$below) px into the texts, which show $([int]$window.OutcomeTexts.actualHeight) px (offset $($window.OutcomeTexts.verticalOffset))"
    }
    else {
        Write-Output "  note  the panel starts $([int]$below) px into the texts, which show $([int]$window.OutcomeTexts.actualHeight) px: in view"
    }
}

function CheckParts($cov, [string] $what) {
    $measured = @($cov.measured)
    $sum = 0L
    foreach ($m in $measured) { $sum += [long]$m.bytes }
    CheckEqual ([long]$cov.unseen) ($sum + [long]$cov.remainder.bytes) "$what`: measured + remainder = unseen"
    CheckEqual ([long]$cov.unseen) ([long]$cov.volumeInUse - [long]$cov.seenOnDisk) "$what`: unseen = volumeInUse - seenOnDisk"
    Check ([long]$cov.remainder.bytes -ge 0) "$what`: the remainder is not negative"
    Check ($cov.remainder.text -match '(?i)cannot read') "$what`: the remainder says it is space this account cannot read"
    Check ($cov.remainder.text -notmatch $UnnegatedMeasured) "$what`: the remainder is never worded as measured"
    foreach ($m in $measured) { Check ($m.text -match '(?i)\bmeasured\b' -and $m.text -match 'valid data length') "$what`: the $($m.id) part says what was measured" }
}

function Run-Case([string] $name, [scriptblock] $body) {
    if ($Case -notcontains $name) { return }
    Write-Output "case $name"
    $folder = New-Case
    $letter = $null
    $denied = New-Object System.Collections.Generic.List[string]
    try { & $body $folder ([ref]$letter) $denied }
    finally {
        foreach ($d in $denied) { Undeny $d }
        Remove-Subst $letter
        Remove-Case $folder
    }
}

# ---- A: a finished whole-drive scan ----
Run-Case 'A' {
    param($folder, $letterRef, $denied)
    $tree = Join-Path $folder 'small'
    $bytes = 0
    for ($d = 1; $d -le 4; $d++) { for ($f = 1; $f -le 5; $f++) { New-File (Join-Path $tree "folder$d\file$f.bin") ($f * $KiB64); $bytes += $f * $KiB64 } }
    $letter = Add-Subst $tree
    $letterRef.Value = $letter
    $drive = "${letter}:\"
    $read = Read-Window $drive
    CheckEnding $read 'A'
    if ($null -eq $read.Report) { return }
    $report = $read.Report
    CheckEqual 'completed' $report.outcome 'the scan finished'
    $cov = $report.coverage
    Check ($null -ne $cov) 'the report carries coverage'
    if ($null -eq $cov) { return }
    CheckEqual ([long]$report.core.bytesOnDisk) ([long]$cov.seenOnDisk) 'seenOnDisk is core.bytesOnDisk'
    Check ([long]$cov.unseen -ge 0) "unseen is not negative ($($cov.unseen))"
    CheckParts $cov 'A'
    $oracle = ([System.IO.FileInfo]::new($VolumeRoot + '$MFT')).Length
    $mft = @($cov.measured | Where-Object id -ceq 'mft')
    Check ($mft.Count -eq 1) 'the $MFT is the one measured part'
    if ($mft.Count -eq 1) { Check ([math]::Abs([long]$mft[0].bytes - $oracle) -le $MiB64) "the measured `$MFT ($($mft[0].bytes)) is within 64 MiB of FileInfo of $($VolumeRoot)`$MFT ($oracle)" }
    CheckEqual $false $cov.remainder.includesStore 'no store was left out, so the remainder does not include it'
    Check ($cov.remainder.text -notmatch 'container layer') 'the remainder does not mention a container layer'
    CheckEqual 0 @($cov.folders).Count 'no folder is named'
    CheckEqual $null $cov.sizeNotRead 'no file whose size was not read'
    $lines = @($report.window.OutcomeDetail)
    CheckEqual 4 $lines.Count "OutcomeDetail keeps its four lines ($($lines -join ' | '))"
    if ($lines.Count -eq 4) {
        Check ($lines[2] -match $FinishedLine) "the finished coverage line is word for word ($($lines[2]))"
        Check ($lines[3].StartsWith('Read in ')) 'line 4 is the time'
    }
    CheckAScrollAway $report
    Check ($report.window.CoveragePanel.top -ne $null) 'the report holds CoveragePanel.top'
    $json = $read.Text
    Check ($json -notmatch [regex]::Escape($tree)) 'the report names no path of the tree (Q-016)'
    $texts = @($cov.remainder.text) + @($cov.measured | ForEach-Object { $_.text })
    Check (@($texts | Where-Object { $_.Contains('\') -or $_.Contains('/') }).Count -eq 0) 'no text of coverage holds a path separator'
}

# ---- B: a folder root ----
Run-Case 'B' {
    param($folder, $letterRef, $denied)
    $tree = Join-Path $folder 'small'
    New-File (Join-Path $tree 'a\file.bin') $KiB64
    $read = Read-Window $tree
    CheckEnding $read 'B'
    if ($null -eq $read.Report) { return }
    CheckEqual 'completed' $read.Report.outcome 'the scan finished'
    CheckEqual $null $read.Report.coverage 'coverage is null for a folder'
    CheckEqual $false $read.Report.window.CoveragePanel.visible 'CoveragePanel is not shown'
    CheckEqual $null $read.Report.window.CoveragePanel.top 'CoveragePanel.top is null while it is not shown'
    Check (@(@($read.Report.window.OutcomeDetail) | Where-Object { $_.StartsWith('The volume reports') }).Count -eq 0) 'no volume line'
}

# ---- C: a drive letter nothing is behind ----
Run-Case 'C' {
    param($folder, $letterRef, $denied)
    $read = Read-Window ((Get-FreeDriveLetter) + ':\')
    CheckEnding $read 'C'
    if ($null -eq $read.Report) { return }
    CheckEqual 'root-unreadable' $read.Report.outcome 'the root could not be read'
    CheckEqual $null $read.Report.coverage 'coverage is null'
    CheckEqual $false $read.Report.window.CoveragePanel.visible 'CoveragePanel is not shown'
}

# ---- D: two folders this account may not list ----
Run-Case 'D' {
    param($folder, $letterRef, $denied)
    $tree = Join-Path $folder 'tree'
    New-File (Join-Path $tree 'readable\file.bin') $KiB64
    foreach ($name in 'no-list-1', 'no-list-2') {
        $path = Join-Path $tree $name
        New-File (Join-Path $path 'hidden.bin') $KiB64
        Deny $path 'RD'
        $denied.Add($path)
    }
    $letter = Add-Subst $tree
    $letterRef.Value = $letter
    $read = Read-Window "${letter}:\"
    CheckEnding $read 'D'
    if ($null -eq $read.Report -or $null -eq $read.Report.coverage) { Check $false 'D: coverage is in the report'; return }
    $cov = $read.Report.coverage
    CheckEqual 2 $read.Report.core.unreadable 'core.unreadable is 2'
    $folders = @($cov.folders)
    CheckEqual 2 @($folders | Where-Object kind -ceq 'unreadable').Count 'two unreadable folders are named, as many as core.unreadable'
    CheckEqual 0 @($folders | Where-Object kind -ceq 'not-scanned').Count 'no folder left out on purpose'
    Check (@($folders | Where-Object { $null -ne $_.size }).Count -eq 0) 'no folder carries a size'
    Check ($cov.remainder.text -match 'It includes 2 folders this scan could not read\.') "the remainder counts them ($($cov.remainder.text))"
    CheckParts $cov 'D'
    CheckEqual $true $read.Report.window.UnreadableExpander.visible 'the Unreadable folders tab is shown'
    Check ($read.Text -notmatch 'no-list-') 'the report names no folder (Q-016)'
}

# ---- E: the container layer store left out ----
Run-Case 'E' {
    param($folder, $letterRef, $denied)
    $containers = Join-Path $env:ProgramData 'Microsoft\Windows\Containers'
    if ($NoStore -or -not (Test-Path -LiteralPath (Join-Path $containers 'Layers'))) {
        Write-Output '  INCONCLUSIVE: this PC has no container layer store under ProgramData (or -NoStore): the store case could not be made'
        $script:Inconclusive.Add('E')
        return
    }
    $letter = Add-Subst $containers
    $letterRef.Value = $letter
    $read = Read-Window "${letter}:\"
    CheckEnding $read 'E'
    if ($null -eq $read.Report) { return }
    $report = $read.Report
    CheckEqual 1 $report.core.skippedStores 'the store was left out (core.skippedStores)'
    CheckEqual 0 $report.core.opensUnderExcludedStore 'nothing was opened under it (core.opensUnderExcludedStore)'
    $cov = $report.coverage
    Check ($null -ne $cov) 'the report carries coverage'
    if ($null -eq $cov) { return }
    $store = @(@($cov.folders) | Where-Object kind -ceq 'not-scanned')
    CheckEqual 1 $store.Count 'the store is named as not-scanned, once'
    Check (@($store | Where-Object { $null -ne $_.size }).Count -eq 0) 'the store carries no size'
    CheckEqual $true $cov.remainder.includesStore 'the remainder includes the store'
    Check ($cov.remainder.text -match 'container layer' -and $cov.remainder.text -match 'left out unopened') "the remainder says so ($($cov.remainder.text))"
    Check ($cov.remainder.text -match '(?i)cannot read' -and $cov.remainder.text -notmatch $UnnegatedMeasured) 'the remainder is still space this account cannot read, not a measured figure'
    CheckEqual ([long]$cov.unseen) ([long]$cov.volumeInUse - [long]$cov.seenOnDisk) 'unseen = volumeInUse - seenOnDisk (the store adds nothing to either)'
    CheckParts $cov 'E'
    Check (@($report.window.OutcomeNotes.lines) -join ' ' -match 'Not scanned, size not counted:') 'the ending note still names the store'
}

# ---- F and G: files whose own size could not be read ----
function Run-SizeNotRead([string] $name, [int] $files) {
    Run-Case $name {
        param($folder, $letterRef, $denied)
        $tree = Join-Path $folder 'tree'
        New-File (Join-Path $tree 'read.bin') $KiB64
        for ($i = 1; $i -le $files; $i++) {
            $path = Join-Path $tree "refused$i.bin"
            New-File $path $KiB64
            Deny $path 'S,RA'
            $denied.Add($path)
        }
        $letter = Add-Subst $tree
        $letterRef.Value = $letter
        $read = Read-Window "${letter}:\"
        CheckEnding $read $name
        if ($null -eq $read.Report -or $null -eq $read.Report.coverage) { Check $false "${name}: coverage is in the report"; return }
        $report = $read.Report
        $cov = $report.coverage
        $apart = $cov.sizeNotRead
        Check ($null -ne $apart) 'sizeNotRead is reported'
        if ($null -eq $apart) { return }
        CheckEqual $files $apart.files 'the files whose size was not read'
        CheckEqual ($files * $KiB64) ([long]$apart.bytes) "their bytes, at the size the listing gave ($($files * $KiB64))"
        Check ([long]$report.core.bytesOnDisk -ge [long]$apart.bytes) 'they are inside what the scan counted (core.bytesOnDisk)'
        CheckEqual ([long]$cov.volumeInUse - [long]$report.core.bytesOnDisk) ([long]$cov.unseen) 'unseen is the volume less what the scan counted, with them in the counted side'
        Check ($apart.text -match 'not part of the unseen space') 'the words say they are not part of the unseen space'
        CheckParts $cov $name
        $notes = @($report.window.OutcomeNotes.lines) -join "`n"
        if ($files -eq 1) {
            Check ($notes -match '(?m)^1 file could not be opened to read its true size on disk; its listed size was used\.$') "the note is in the singular ($notes)"
            Check ($apart.text -match '^1 file whose own size could not be read is not part of') "the panel is in the singular ($($apart.text))"
        }
        else {
            Check ($notes -match "(?m)^$files files could not be opened to read their true size on disk; their listed size was used\.$") "the note keeps its words ($notes)"
        }
    }
}
Run-SizeNotRead 'F' 2
Run-SizeNotRead 'G' 1

# ---- H: a whole-drive scan stopped part-way ----
Run-Case 'H' {
    param($folder, $letterRef, $denied)
    if (-not (Test-Path -LiteralPath $HookDll -PathType Leaf)) {
        Write-Output "  INCONCLUSIVE: no hook at $HookDll (build tests/window/FsaWindowHook with eng/dotnet.ps1 into artifacts/window-hook)"
        $script:Inconclusive.Add('H')
        return
    }
    # 24,000 folders of one small file each: a scan of about two seconds, which the progress timer (250 ms) and the hook's poll
    # reach well inside, where a small tree is over before the first tick.
    $tree = Join-Path $folder 'big'
    1..40 | ForEach-Object -ThrottleLimit 8 -Parallel {
        $top = Join-Path $using:tree ("g{0:D2}" -f $_)
        for ($d = 1; $d -le 600; $d++) {
            $dir = Join-Path $top ("d{0:D3}" -f $d)
            [void][System.IO.Directory]::CreateDirectory($dir)
            [System.IO.File]::WriteAllBytes((Join-Path $dir 'a.bin'), [byte[]]::new(2048))
        }
    }
    $letter = Add-Subst $tree
    $letterRef.Value = $letter
    $log = Join-Path $folder 'hook.log'
    $stopped = $null
    for ($attempt = 1; $attempt -le 3 -and $null -eq $stopped; $attempt++) {
        $read = Read-Window "${letter}:\" @{ DOTNET_STARTUP_HOOKS = $HookDll; FSA_HOOK_MODE = 'stop'; FSA_HOOK_AT_FOLDERS = '15'; FSA_HOOK_LOG = $log }
        if ($null -ne $read.Report -and $read.Report.outcome -ceq 'cancelled') { $stopped = $read }
        else { Write-Output "  (attempt ${attempt}: the scan was not stopped: $($read.Exit), $($read.Report.outcome))" }
    }
    if ($null -eq $stopped) {
        Write-Output '  INCONCLUSIVE: the hook did not stop a scan of this tree in three attempts'
        $script:Inconclusive.Add('H')
        return
    }
    CheckEnding $stopped 'H'
    $report = $stopped.Report
    CheckEqual 'cancelled' $report.outcome 'the scan was stopped'
    CheckEqual $null $report.coverage 'coverage is null for a stopped scan'
    CheckEqual $false $report.window.CoveragePanel.visible 'CoveragePanel is not shown'
    CheckEqual $null $report.window.CoveragePanel.top 'CoveragePanel.top is null'
    $lines = @($report.window.OutcomeDetail)
    Check (@($lines | Where-Object { $_ -match $PartialLine }).Count -eq 1) "the partial line is word for word ($($lines -join ' | '))"
}

Write-Output ''
if ($script:Inconclusive.Count -gt 0) { Write-Output "INCONCLUSIVE cases: $($script:Inconclusive -join ', ')" }
if ($script:Failures.Count -gt 0) {
    Write-Output "FAILED: $($script:Failures.Count) check(s)"
    exit 1
}
Write-Output 'ALL CHECKS HELD'
exit 0
