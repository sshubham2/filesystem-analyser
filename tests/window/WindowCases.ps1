# The App's window cases (SL-023). Each case builds a tree under %TEMP%, runs the published app on it
# in report mode and compares what the window showed with what the case built. The run goes through
# eng/read-window.ps1, as validate does, except where the case is about the raw arguments. There is no
# App test project: this reads the shipped exe's own window, which an in-process test cannot.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/WindowCases.ps1 -Case C-001
#
# -Case All runs every case. Exit 0 when every check held, 1 when any failed. The app is published
# first (eng/dotnet.ps1, into artifacts/publish) when the exe is missing or older than any source.
#
# The oracle is the tree each case builds, never the report's own "core" section. Every non-empty file
# is a multiple of 64 KiB and every other file is empty, so the size on disk equals the size on any
# NTFS volume with clusters of 64 KiB or less, without asking the volume.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('All', 'C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-007', 'C-008', 'C-009', 'C-010', 'C-011', 'C-013', 'C-014')]
    [string] $Case
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Exe = Join-Path $Repo 'artifacts\publish\FsAnalyser.App.exe'
$Reader = Join-Path $Repo 'eng\read-window.ps1'
$KiB64 = 64KB
$script:Failures = [System.Collections.Generic.List[string]]::new()

# ---- Checks ----

# Non-ASCII shown as \uXXXX, so a failure prints the same whatever the console's code page.
function Show($value) {
    if ($null -eq $value) { return '<null>' }
    $text = [string]$value
    $builder = [System.Text.StringBuilder]::new()
    foreach ($c in $text.ToCharArray()) {
        if ([int]$c -lt 32 -or [int]$c -gt 126) { [void]$builder.AppendFormat('\u{0:X4}', [int]$c) } else { [void]$builder.Append($c) }
    }
    "'$builder'"
}

function Check([bool] $condition, [string] $what) {
    if ($condition) {
        Write-Output "  ok    $(($what -split "`n")[0])"
    }
    else {
        Write-Output "  FAIL  $what"
        $script:Failures.Add($what)
    }
}

function CheckEqual($expected, $actual, [string] $what) {
    $same = ($null -eq $expected -and $null -eq $actual) -or
        ($null -ne $expected -and $null -ne $actual -and [string]$expected -ceq [string]$actual)
    Check $same "$what`n          expected $(Show $expected)`n          actual   $(Show $actual)"
}

# ---- The tree's own figures, in the window's words ----

function Format-Count([long] $n, [string] $noun) {
    if ($n -eq 1) { "1 $noun" } else { '{0:N0} {1}s' -f $n, $noun }
}

function Format-Size([long] $bytes) {
    $units = 'bytes', 'KB', 'MB', 'GB', 'TB', 'PB'
    [double] $value = $bytes
    $unit = 0
    while ($value -ge 1024 -and $unit -lt $units.Length - 1) {
        $value /= 1024
        $unit++
    }
    if ($unit -eq 0) { '{0:N0} bytes' -f $bytes } else { '{0:N1} {1}' -f $value, $units[$unit] }
}

# The first two lines of OutcomeDetail for a tree: counts, then bytes (the rest vary per run).
function Expected-Lines($manifest) {
    "$(Format-Count $manifest.Files 'file') and $(Format-Count $manifest.Folders 'folder'); $(Format-Count $manifest.Links 'link') not followed"
    "$(Format-Size $manifest.Bytes) in files, taking $(Format-Size $manifest.Bytes) on disk"
}

function CheckFinished($read, [string] $root, [string] $shownRoot, $manifest) {
    CheckEqual 'EXIT 0' $read.Exit 'exit code'
    Check ($null -ne $read.Report) 'a report was written'
    if ($null -eq $read.Report) { return }
    $window = $read.Report.window
    CheckEqual 1 $read.Report.schema 'schema'
    CheckEqual $root $read.Report.root 'root is the path passed'
    CheckEqual $true $window.OutcomePanel.visible 'OutcomePanel is on screen'
    Check ($window.OutcomePanel.actualHeight -gt 0) "OutcomePanel has been laid out (actualHeight $($window.OutcomePanel.actualHeight))"
    CheckEqual "Finished: $shownRoot" $window.OutcomeTitle 'OutcomeTitle'
    $lines = @(Expected-Lines $manifest)
    CheckEqual $lines[0] $window.OutcomeDetail[0] 'OutcomeDetail line 1 against the tree'
    CheckEqual $lines[1] $window.OutcomeDetail[1] 'OutcomeDetail line 2 against the tree'
}

# ---- Trees ----

function New-CaseFolder {
    $folder = Join-Path ([System.IO.Path]::GetTempPath()) ("fsa-window-cases\{0}" -f [guid]::NewGuid().ToString('N'))
    [void][System.IO.Directory]::CreateDirectory($folder)
    $folder
}

function New-File([string] $path, [long] $bytes) {
    [void][System.IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [System.IO.File]::WriteAllBytes($path, [byte[]]::new($bytes))
}

# About 20 files in 4 folders: scanned well inside one 250 ms timer tick.
function New-SmallTree([string] $root) {
    $bytes = 0
    for ($d = 1; $d -le 4; $d++) {
        for ($f = 1; $f -le 5; $f++) {
            New-File (Join-Path $root "folder$d\file$f.bin") ($f * $KiB64)
            $bytes += $f * $KiB64
        }
    }
    [pscustomobject]@{ Files = 20; Folders = 4; Links = 0; Bytes = $bytes }
}

# Enough for many timer ticks before the ending. Thousands of files alone scan in well under one tick
# here (30,000 empty files: 54 ms), so the tree also holds 30,150 nested folders, which is what takes
# the scan past a second (20,100 folders: 1.0 to 1.3 s).
function New-LargeTree([string] $root) {
    $tops = 150
    $files = 0
    $bytes = 0
    for ($i = 0; $i -lt $tops; $i++) {
        $top = Join-Path $root "n$i"
        for ($j = 0; $j -lt 200; $j++) {
            [void][System.IO.Directory]::CreateDirectory((Join-Path $top "m$j"))
        }
        New-File (Join-Path $top 'data.bin') $KiB64
        $bytes += $KiB64
        for ($f = 0; $f -lt 29; $f++) {
            New-File (Join-Path $top "empty$f.txt") 0
        }
        $files += 30
    }
    [pscustomobject]@{ Files = $files; Folders = $tops + $tops * 200; Links = 0; Bytes = $bytes }
}

function Remove-CaseFolder([string] $folder) {
    if (Test-Path -LiteralPath $folder) {
        Remove-Item -LiteralPath $folder -Recurse -Force
    }
}

# ---- Running the app ----

# Publishes when the exe is missing or older than any source. The mutex keeps two cases run side by
# side from publishing over each other.
function Update-App {
    $mutex = [System.Threading.Mutex]::new($false, 'Local\FsAnalyser-WindowCases-Publish')
    [void]$mutex.WaitOne()
    try {
        $sources = @(Get-ChildItem -LiteralPath (Join-Path $Repo 'src') -Recurse -File |
                Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }) +
            @(Get-Item -LiteralPath (Join-Path $Repo 'Directory.Build.props'), (Join-Path $Repo 'global.json'))
        $newest = ($sources | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc
        if ((Test-Path -LiteralPath $Exe) -and (Get-Item -LiteralPath $Exe).LastWriteTimeUtc -ge $newest) {
            return
        }
        Write-Output 'publishing the app (eng/dotnet.ps1 publish)'
        & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'eng\dotnet.ps1') publish `
            (Join-Path $Repo 'src\FsAnalyser.App\FsAnalyser.App.csproj') -c Release -o (Join-Path $Repo 'artifacts\publish') |
            ForEach-Object { "  $_" }
        if ($LASTEXITCODE -ne 0) {
            throw "publish failed with exit $LASTEXITCODE"
        }
    }
    finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
}

# Through eng/read-window.ps1, the way validate reads the window.
function Read-Window([string] $root, [int] $timeoutSec = 120) {
    $lines = @(& pwsh -NoProfile -ExecutionPolicy Bypass -File $Reader -Root $root -Exe $Exe -TimeoutSec $timeoutSec)
    if ($LASTEXITCODE -ne 0) {
        throw "eng/read-window.ps1 failed with exit $LASTEXITCODE"
    }
    $text = ($lines | Select-Object -Skip 1) -join "`n"
    [pscustomobject]@{
        Exit   = $lines[0]
        Text   = $text
        Report = if ($text -eq 'NO REPORT') { $null } else { $text | ConvertFrom-Json }
    }
}

# Straight through Start-Process, for the cases about raw arguments. -ArgumentList joins an array with
# spaces and quotes nothing.
function Start-App($arguments, [string] $workingDirectory = [System.IO.Path]::GetTempPath()) {
    $process = Start-Process -FilePath $Exe -ArgumentList $arguments -WorkingDirectory $workingDirectory -PassThru
    $null = $process.Handle # without it ExitCode reads empty after the wait
    $process
}

# The exit code, or TIMEOUT after killing the process: a run that waits on a person never ends.
function Wait-App($process, [int] $timeoutSec = 60) {
    if ($process.WaitForExit($timeoutSec * 1000)) {
        return [string]$process.ExitCode
    }
    $process.Kill()
    $process.WaitForExit()
    'TIMEOUT'
}

function Invoke-App($arguments, [int] $timeoutSec = 60) {
    Wait-App (Start-App $arguments) $timeoutSec
}

# ---- Cases ----

# C-001: a tree that finishes inside one timer tick.
function Test-C001 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'small'
        $manifest = New-SmallTree $root
        CheckFinished (Read-Window $root) $root $root $manifest
    }
    finally { Remove-CaseFolder $case }
}

# C-002: a tree large enough for many timer ticks before the ending (New-LargeTree).
function Test-C002 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'large'
        $manifest = New-LargeTree $root

        $read = Read-Window $root
        CheckFinished $read $root $root $manifest
        if ($null -ne $read.Report -and $null -ne $read.Report.core) {
            # Not an oracle for the window: only proof that this tree fed the shape (several ticks).
            $elapsed = $read.Report.core.elapsedMs
            Check ($elapsed -ge 750) "the scan ran for at least three 250 ms ticks ($elapsed ms)"
            Write-Output "        CountersText after the last tick: $(Show $read.Report.window.CountersText)"
        }
    }
    finally { Remove-CaseFolder $case }
}

# C-003: a junction and a hardlinked pair. The junction's target holds 1 MiB that must not be counted,
# and the pair's 128 KiB must be counted once.
function Test-C003 {
    $case = New-CaseFolder
    $junction = $null
    try {
        $outside = Join-Path $case 'outside'
        New-File (Join-Path $outside 'big.bin') 1MB
        $root = Join-Path $case 'links'
        $first = Join-Path $root 'one\a.bin'
        New-File $first (2 * $KiB64)
        New-File (Join-Path $root 'one\c.bin') $KiB64
        [void][System.IO.Directory]::CreateDirectory((Join-Path $root 'two'))
        New-Item -ItemType HardLink -Path (Join-Path $root 'two\b.bin') -Value $first | Out-Null
        $junction = Join-Path $root 'j'
        New-Item -ItemType Junction -Path $junction -Value $outside | Out-Null
        $manifest = [pscustomobject]@{ Files = 3; Folders = 2; Links = 1; Bytes = 3 * $KiB64 }

        CheckFinished (Read-Window $root) $root $root $manifest
    }
    finally {
        if ($junction -and (Test-Path -LiteralPath $junction)) {
            [System.IO.Directory]::Delete($junction) # the link only, never its target
        }
        Remove-CaseFolder $case
    }
}

# C-004: the root does not exist. The ending is in the file, not in the exit code.
function Test-C004 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'missing'
        $read = Read-Window $root
        CheckEqual 'EXIT 0' $read.Exit 'exit code'
        Check ($null -ne $read.Report) 'a report was written'
        if ($null -eq $read.Report) { return }
        CheckEqual $root $read.Report.root 'root is the path passed'
        $title = $read.Report.window.OutcomeTitle
        Check (-not $title.StartsWith('Finished')) "OutcomeTitle is not Finished ($(Show $title))"
        CheckEqual "Could not read $root" $title 'OutcomeTitle'
        CheckEqual $true $read.Report.window.OutcomePanel.visible 'OutcomePanel is on screen'
        Check ($null -ne $read.Report.core) 'core holds the result'
        if ($null -ne $read.Report.core) {
            CheckEqual 'RootUnreadable' $read.Report.core.status 'core.status'
            Check ($null -ne $read.Report.core.rootError) "core.rootError is set ($($read.Report.core.rootError))"
        }
    }
    finally { Remove-CaseFolder $case }
}

# C-005: ShowProblem before any ScanResult. A relative root is refused by LongPath.NormalizeRoot before
# the scan makes a result, so the ending comes with no result and core is null.
function Test-C005 {
    $root = 'fsa-not-an-absolute-path'
    $read = Read-Window $root
    CheckEqual 'EXIT 0' $read.Exit 'exit code (70 would mean the null result crashed the report)'
    Check ($null -ne $read.Report) 'a report was written'
    if ($null -eq $read.Report) { return }
    CheckEqual $root $read.Report.root 'root is the argument passed'
    CheckEqual "Could not read $root" $read.Report.window.OutcomeTitle 'OutcomeTitle'
    Check ($read.Report.window.OutcomeDetail[0] -like '*is not an absolute path*') "OutcomeDetail says why ($(Show $read.Report.window.OutcomeDetail[0]))"
    Check ($read.Text -match '"core":\s*null') 'core is null'
}

# C-007: a root with a space, passed the way Start-Process passes an array: unquoted, so it splits.
# Report mode must refuse it (exit 2, no file); the reader must quote it and get the whole path back.
function Test-C007 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'tree with space'
        $manifest = New-SmallTree $root
        $report = Join-Path $case 'report.json'

        CheckEqual '2' (Invoke-App @($root, '--report', $report)) 'unquoted root: exit code'
        Check (-not (Test-Path -LiteralPath $report)) 'unquoted root: no report'
        Check (-not (Test-Path -LiteralPath "$report.tmp")) 'unquoted root: no .tmp'

        CheckFinished (Read-Window $root) $root $root $manifest
    }
    finally { Remove-CaseFolder $case }
}

# C-008: a report from an earlier run sits at the target path.
function Test-C008 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'tree'
        [void](New-SmallTree $root)
        $report = Join-Path $case 'report.json'
        $stale = '{"schema":1,"stale":true}'

        Set-Content -LiteralPath $report -Value $stale
        Set-Content -LiteralPath "$report.tmp" -Value $stale
        CheckEqual '2' (Invoke-App @('--report', $report)) 'no root: exit code'
        Check (-not (Test-Path -LiteralPath $report)) 'no root: the stale report is gone'
        Check (-not (Test-Path -LiteralPath "$report.tmp")) 'no root: the stale .tmp is gone'

        Set-Content -LiteralPath $report -Value $stale
        CheckEqual '0' (Invoke-App @($root, '--report', $report)) 'with a root: exit code'
        $written = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw } else { '' }
        Check ($written -notmatch 'stale') 'with a root: the stale report was replaced'
        CheckEqual $root ($written | ConvertFrom-Json).root 'with a root: the new report is this run''s'
        Check (-not (Test-Path -LiteralPath "$report.tmp")) 'with a root: no .tmp left'
    }
    finally { Remove-CaseFolder $case }
}

# C-009: the report cannot be written. Exit 3 and nothing left behind, whether it is refused before any
# scan or the final write fails because the report's folder went away during the scan.
function Test-C009 {
    $case = New-CaseFolder
    $process = $null
    try {
        $root = Join-Path $case 'tree'
        [void](New-SmallTree $root)

        $report = Join-Path $case 'no-such-folder\report.json'
        CheckEqual '3' (Invoke-App @($root, '--report', $report)) 'missing folder: exit code'
        Check (-not (Test-Path -LiteralPath (Split-Path -Parent $report))) 'missing folder: nothing was created'

        $folder = Join-Path $case 'a-folder'
        [void][System.IO.Directory]::CreateDirectory($folder)
        CheckEqual '3' (Invoke-App @($root, '--report', $folder)) 'a folder as the report path: exit code'
        Check (Test-Path -LiteralPath $folder -PathType Container) 'a folder as the report path: the folder is untouched'
        Check (-not (Test-Path -LiteralPath "$folder.tmp")) 'a folder as the report path: no .tmp'

        # Past the startup check: the folder is deleted once the window is up, while a large tree is
        # still scanning, so only the final write can fail.
        $large = Join-Path $case 'large'
        [void](New-LargeTree $large)
        $out = Join-Path $case 'out'
        [void][System.IO.Directory]::CreateDirectory($out)
        $report = Join-Path $out 'r.json'
        $process = Start-App "`"$large`" --report `"$report`""
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not $process.HasExited -and $process.MainWindowHandle -eq [IntPtr]::Zero -and $clock.Elapsed.TotalSeconds -lt 30) {
            Start-Sleep -Milliseconds 10
            $process.Refresh()
        }
        Check ($process.MainWindowHandle -ne [IntPtr]::Zero -and -not $process.HasExited) "folder gone mid-scan: the window was up and the app still running after $($clock.ElapsedMilliseconds) ms"
        Remove-Item -LiteralPath $out -Recurse -Force
        CheckEqual '3' (Wait-App $process) 'folder gone mid-scan: exit code'
        Check (-not (Test-Path -LiteralPath $report)) 'folder gone mid-scan: no report'
        Check (-not (Test-Path -LiteralPath "$report.tmp")) 'folder gone mid-scan: no .tmp'
    }
    finally {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        Remove-CaseFolder $case
    }
}

# C-010: non-ASCII in the root, and a no-break space and a dash in the window text (the title shows the
# root). The file must be pure ASCII, read the same by Windows PowerShell 5.1 and pwsh.
function Test-C010 {
    $case = New-CaseFolder
    try {
        $name = "fsa $([char]0x00E9) $([char]0x65E5)$([char]0x672C)$([char]0x00A0)$([char]0x2013) end"
        $root = Join-Path $case $name
        $manifest = New-SmallTree $root

        $read = Read-Window $root
        CheckFinished $read $root $root $manifest
        Check ($read.Text -cmatch '^[\x00-\x7F]*$') 'the report text is pure ASCII'
        foreach ($code in 0x00E9, 0x65E5, 0x672C, 0x00A0, 0x2013) {
            $escape = '{0}u{1:X4}' -f [char]92, $code # a backslash, u and four hex digits
            Check ($read.Text.Contains($escape, [StringComparison]::OrdinalIgnoreCase)) "the report holds $escape"
        }

        # The file itself, read by both shells.
        $report = Join-Path $case 'report.json'
        CheckEqual '0' (Invoke-App "`"$root`" --report `"$report`"") 'direct run: exit code'
        [byte[]] $bytes = [System.IO.File]::ReadAllBytes($report)
        CheckEqual '123' $bytes[0] 'the file starts with { (no byte-order mark)'
        Check (@($bytes | Where-Object { $_ -ge 0x80 }).Count -eq 0) 'every byte of the file is ASCII'

        $codes = { param($s) ([int[]][char[]]$s) -join ',' }
        $probe = Join-Path $case 'read51.ps1'
        Set-Content -LiteralPath $probe -Encoding ascii -Value @(
            'param([string] $Path)'
            '$j = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json'
            '([int[]][char[]]$j.root) -join '','''
            '([int[]][char[]]$j.window.OutcomeTitle) -join '','''
        )
        $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $read51 = @(& $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $probe -Path $report)
        $json = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        CheckEqual (& $codes $root) $read51[0] 'Windows PowerShell 5.1 reads root as passed'
        CheckEqual (& $codes "Finished: $root") $read51[1] 'Windows PowerShell 5.1 reads OutcomeTitle'
        CheckEqual (& $codes $json.window.OutcomeTitle) $read51[1] 'pwsh and 5.1 read the same OutcomeTitle'
    }
    finally { Remove-CaseFolder $case }
}

# C-011: no --report. The window stays open after its scan has ended and nothing is written.
function Test-C011 {
    $case = New-CaseFolder
    $process = $null
    try {
        $root = Join-Path $case 'tree'
        $manifest = New-SmallTree $root
        $work = Join-Path $case 'work'
        [void][System.IO.Directory]::CreateDirectory($work)
        $before = @(Get-ChildItem -LiteralPath $case -Recurse -Force | ForEach-Object FullName)

        # How long the same tree takes to reach its ending, measured in report mode.
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        CheckFinished (Read-Window $root) $root $root $manifest
        $toEnding = $clock.Elapsed.TotalSeconds

        $process = Start-App @($root) $work
        $wait = [Math]::Max(5, 3 * $toEnding)
        Start-Sleep -Milliseconds ([int]($wait * 1000))
        Check (-not $process.HasExited) ("still open {0:N1} s after launch; its ending came within {1:N1} s in report mode" -f $wait, $toEnding)
        $after = @(Get-ChildItem -LiteralPath $case -Recurse -Force | ForEach-Object FullName)
        $new = @($after | Where-Object { $_ -notin $before })
        Check ($new.Count -eq 0 -and $after.Count -eq $before.Count) "no file was written in the tree or the working folder ($(Show ($new -join ', ')))"
    }
    finally {
        if ($process -and -not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        Remove-CaseFolder $case
    }
}

# C-013: a root ending in a backslash, which would escape the closing quote if the reader did not
# double it.
function Test-C013 {
    $case = New-CaseFolder
    try {
        $tree = Join-Path $case 'tree'
        $manifest = New-SmallTree $tree
        $root = "$tree\"
        CheckFinished (Read-Window $root) $root $tree $manifest
    }
    finally { Remove-CaseFolder $case }
}

# C-014: nothing to scan or nowhere to write. Exit 2 at once; a window waiting on a person would run
# into the timeout instead.
function Test-C014 {
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'tree'
        [void](New-SmallTree $root)
        $report = Join-Path $case 'report.json'

        CheckEqual '2' (Invoke-App @('--report') 30) '--report alone: exit code'
        CheckEqual '2' (Invoke-App @($root, '--report') 30) 'a root and --report with no value: exit code'
        CheckEqual '2' (Invoke-App @('--report', $report) 30) 'a report path and no root: exit code'
        Check (-not (Test-Path -LiteralPath $report)) 'a report path and no root: no report'
    }
    finally { Remove-CaseFolder $case }
}

# ---- Run ----

$cases = if ($Case -eq 'All') { 'C-001', 'C-002', 'C-003', 'C-004', 'C-005', 'C-007', 'C-008', 'C-009', 'C-010', 'C-011', 'C-013', 'C-014' } else { @($Case) }
Update-App
$failed = @()
foreach ($key in $cases) {
    Write-Output $key
    $before = $script:Failures.Count
    try {
        & "Test-$($key -replace '-', '')"
    }
    catch {
        Write-Output "  FAIL  $($_.Exception.Message)"
        $script:Failures.Add("$key threw: $($_.Exception.Message)")
    }
    if ($script:Failures.Count -gt $before) {
        $failed += $key
    }
}

if ($failed.Count -gt 0) {
    Write-Output "FAILED: $($failed -join ', ')"
    exit 1
}
Write-Output "PASSED: $($cases -join ', ')"
exit 0
