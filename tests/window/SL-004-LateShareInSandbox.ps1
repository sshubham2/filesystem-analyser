<#
SL-004 (SC-059): does a mapped share that answers only after "Drives" is shown get its button, in letter order, in the published app?
Run in a Windows Sandbox, as the administrator it is, never on a person's PC: it stops this machine's file-server services, maps
a drive letter to a share of its own and lists a second letter beside it.

  powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\taad\tree\tests\window\SL-004-LateShareInSandbox.ps1 [-Wake 20,120]
  (-Wake 20,20,120,120,120 runs a delay more than once, as w20, w20-2, w120, w120-2 and w120-3)

The Sandbox maps the working tree read-only at C:\taad\tree, which must hold artifacts\publish (the published app) and
artifacts\window-hook (the drives hook, tests/window/FsaWindowHook/DrivesMode.cs). Everything it makes is under C:\work, and what it
reports is written to C:\taad\out: late-share.json (a verdict per arm), a hook log, the report and a thread log per arm.

The set-up, once: a share of C:\work\share mapped as Y:, and the folder C:\work\neighbour as the SUBST letter Z:, so that a late Y: has a
neighbour on each side of it (C: and Z:) and appending at the end differs from inserting in letter order. Then the arms:
  control   the share answers: the picker shows C:, Y:, Z: (proves the hook reads the share's button when there is one to read)
  w<N>      the server is silenced (LanmanServer, srv2 and srvnet stopped, a listener on 445 that accepts and never answers), the app
            is launched, and the server is woken N s after the launch (the listener killed, the services started again)
  silent    a negative control, last: the server is silenced and not woken within the hook's 20 s wait. The check must read it as FAILED,
            with the cause "still blocked" (the share's read thread alive), and the app must still end through its report
Each arm's verdict is SL-004-LateShareVerdict.ps1's. Exit 0 when every arm held, 1 when any failed, 3 when the set-up could not be made.
#>

param(
    [string[]] $Wake = @('20', '120'),
    [string] $Tree = 'C:\taad\tree',
    [string] $Out = 'C:\taad\out'
)

$ErrorActionPreference = 'Continue'
# Stops this machine's file server and maps a drive letter to a share of its own: only ever in a disposable Windows Sandbox, whose
# user is WDAGUtilityAccount. On any other machine it refuses before it touches anything.
if (-not (Test-Path -LiteralPath 'C:\Users\WDAGUtilityAccount')) {
    Write-Output 'REFUSED: this is not a Windows Sandbox (no C:\Users\WDAGUtilityAccount). The script stops the file-server services and maps a drive letter; run it through the Sandbox tool, never on a PC.'
    exit 3
}
[int[]] $WakeDelays = @($Wake | ForEach-Object { "$_" -split ',' } | Where-Object { $_ } | ForEach-Object { [int]$_ }) # -File hands 20,120 over as one string
. (Join-Path $PSScriptRoot 'SL-004-LateShareVerdict.ps1')
New-Item -ItemType Directory -Force $Out | Out-Null
New-Item -ItemType Directory -Force C:\work | Out-Null
$log = Join-Path $Out 'run-log.txt'
$t0 = Get-Date
function L([string] $message) {
    $line = '{0,8:N1} {1}' -f ((Get-Date) - $t0).TotalSeconds, $message
    Add-Content -Path $log -Value $line
    Write-Output $line
}

$ShareLetter = 'Y:'
$NeighbourLetter = 'Z:'
$HookVariables = 'DOTNET_STARTUP_HOOKS', 'FSA_HOOK_MODE', 'FSA_HOOK_LOG', 'FSA_HOOK_WANT', 'FSA_HOOK_WANT_DEADLINE_S'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class FsaThreadNames
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    // The description Windows holds for a thread: the name a .NET thread was given. Null when it has none or cannot be read.
    public static string Of(uint threadId)
    {
        IntPtr thread = OpenThread(0x0800, false, threadId); // THREAD_QUERY_LIMITED_INFORMATION
        if (thread == IntPtr.Zero) return null;
        try
        {
            IntPtr text;
            if (GetThreadDescription(thread, out text) < 0 || text == IntPtr.Zero) return null;
            string name = Marshal.PtrToStringUni(text);
            LocalFree(text);
            return name;
        }
        finally { CloseHandle(thread); }
    }
}
'@

# The indexes N of the app's "FsAnalyser drive read N" threads alive now, and how many threads there are.
function Get-DriveReadThreads([int] $processId) {
    $alive = New-Object System.Collections.Generic.List[int]
    $named = 0
    try {
        foreach ($thread in (Get-Process -Id $processId -ErrorAction Stop).Threads) {
            $name = [FsaThreadNames]::Of([uint32]$thread.Id)
            if ($name) { $named++ }
            if ($name -cmatch '^FsAnalyser drive read (\d+)$') { $alive.Add([int]$Matches[1]) }
        }
    }
    catch { return $null }
    [pscustomobject]@{ Alive = $alive.ToArray(); Named = $named }
}

function Get-Listen445 { (Get-NetTCPConnection -LocalPort 445 -State Listen -ErrorAction SilentlyContinue | ForEach-Object { "$($_.LocalAddress) pid=$($_.OwningProcess)" }) -join ',' }
function Get-ServiceStates {
    (@('LanmanServer', 'srv2', 'srvnet') | ForEach-Object {
            $state = sc.exe query $_ | Select-String 'STATE'
            "$_=" + $(if ($state) { ($state.ToString() -split '\s+')[-1] } else { '?' })
        }) -join ' '
}

@'
$l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, 445)
$l.Start()
Add-Content C:\taad\out\listener.txt ("{0:o} listening pid={1}" -f (Get-Date), $PID)
$held = New-Object System.Collections.ArrayList
while ($true) { $c = $l.AcceptTcpClient(); [void]$held.Add($c); Add-Content C:\taad\out\listener.txt ("{0:o} accept {1}" -f (Get-Date), $c.Client.RemoteEndPoint) }
'@ | Set-Content C:\work\listener.ps1

$script:Listener = $null
function Silence {
    Stop-Service LanmanServer -Force -ErrorAction SilentlyContinue
    $null = sc.exe stop srv2
    $null = sc.exe stop srvnet
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while ((Get-Listen445) -and $wait.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 500 }
    $script:Listener = Start-Process powershell -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File C:\work\listener.ps1' -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2
    L ("silenced: $(Get-ServiceStates) listen445=$(Get-Listen445)")
}
function Wake {
    if ($script:Listener) { Stop-Process -Id $script:Listener.Id -Force -ErrorAction SilentlyContinue }
    $null = sc.exe start srvnet
    $null = sc.exe start srv2
    Start-Service LanmanServer -ErrorAction SilentlyContinue
    L ("woken: $(Get-ServiceStates) listen445=$(Get-Listen445) share=$((Get-SmbShare -Name fsa -ErrorAction SilentlyContinue).Name)")
}

# ---- Set-up ----

$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
L "build $($cv.CurrentBuild).$($cv.UBR) user=$([Environment]::UserName)"
$publish = Join-Path $Tree 'artifacts\publish'
$hookFolder = Join-Path $Tree 'artifacts\window-hook'
if (-not (Test-Path (Join-Path $publish 'FsAnalyser.App.exe')) -or -not (Test-Path (Join-Path $hookFolder 'FsaWindowHook.dll'))) {
    L "SETUP FAILED: no published app or no hook under $Tree\artifacts"
    exit 3
}
New-Item -ItemType Directory -Force C:\work\app, C:\work\hook, C:\work\scanroot, C:\work\share, C:\work\neighbour | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination C:\work\app -Recurse -Force
Copy-Item -Path (Join-Path $hookFolder '*') -Destination C:\work\hook -Recurse -Force
Set-Content C:\work\scanroot\a.txt 'x'
Set-Content C:\work\share\a.txt 'x'
Set-Content C:\work\neighbour\a.txt 'x'

try { New-SmbShare -Name fsa -Path C:\work\share -FullAccess Everyone -ErrorAction Stop | Out-Null; L 'share fsa made' } catch { L "New-SmbShare failed: $_" }
$mapped = cmd /c "net use $ShareLetter \\127.0.0.1\fsa 2>&1"
L ("net use $ShareLetter exit=$LASTEXITCODE " + ($mapped -join ' '))
$substituted = cmd /c "subst $NeighbourLetter C:\work\neighbour 2>&1"
L ("subst $NeighbourLetter exit=$LASTEXITCODE " + ($substituted -join ' '))

# The picker's own list: fixed, removable and network drives, in letter order. A read's index is its place here.
$drives = @([System.IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -in 'Fixed', 'Removable', 'Network' })
L ('drives the picker lists: ' + (($drives | ForEach-Object { "$($_.Name.TrimEnd('\')) $($_.DriveType)" }) -join ', '))
$shareIndex = -1
for ($i = 0; $i -lt $drives.Count; $i++) { if ($drives[$i].Name.TrimEnd('\') -ceq $ShareLetter) { $shareIndex = $i } }
$neighbourListed = @($drives | Where-Object { $_.Name.TrimEnd('\') -ceq $NeighbourLetter }).Count -eq 1
if ($shareIndex -lt 0 -or -not $neighbourListed -or $shareIndex -eq 0 -or $shareIndex -eq ($drives.Count - 1)) {
    L "SETUP FAILED: $ShareLetter and $NeighbourLetter must both be listed, the share with a neighbour on each side (share index $shareIndex of $($drives.Count))"
    exit 3
}
L "the share's read index is $shareIndex of $($drives.Count)"

$Exe = 'C:\work\app\FsAnalyser.App.exe'
$HookDll = 'C:\work\hook\FsaWindowHook.dll'
$ScanRoot = 'C:\work\scanroot'

# One report-mode launch of the app under the drives hook. Returns the process and when it was launched. The root and the report path
# are quoted, and a trailing backslash doubled, as the window cases do (Start-Process quotes nothing).
function Start-DrivesRun([string] $tag, [int] $deadlineSeconds) {
    $hookLog = Join-Path $Out "hook-$tag.log"
    $report = "C:\work\report-$tag.json"
    foreach ($file in $hookLog, $report) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
    $rootArgument = '"' + ($ScanRoot -replace '(\\+)$', '$1$1') + '"'
    $reportArgument = '"' + ($report -replace '(\\+)$', '$1$1') + '"'
    try {
        $env:DOTNET_STARTUP_HOOKS = $HookDll
        $env:FSA_HOOK_MODE = 'drives'
        $env:FSA_HOOK_LOG = $hookLog
        $env:FSA_HOOK_WANT = "$ShareLetter,$NeighbourLetter"
        $env:FSA_HOOK_WANT_DEADLINE_S = "$deadlineSeconds"
        $launchUtc = [DateTime]::UtcNow
        $process = Start-Process -FilePath $Exe -ArgumentList "$rootArgument --report $reportArgument" -WorkingDirectory C:\work -PassThru
        $null = $process.Handle
    }
    finally {
        foreach ($name in $HookVariables) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
    }
    [pscustomobject]@{ Process = $process; LaunchUtc = $launchUtc; HookLog = $hookLog; Report = $report }
}

# Samples the app's drive-read threads about once a second until $until (seconds after the launch), or until it exits.
function Watch-Run($run, [double] $until, $samples) {
    while (((Get-Date).ToUniversalTime() - $run.LaunchUtc).TotalSeconds -lt $until -and -not $run.Process.HasExited) {
        $seen = Get-DriveReadThreads $run.Process.Id
        if ($seen) { $samples.Add([pscustomobject]@{ Utc = [DateTime]::UtcNow; Alive = @($seen.Alive); Named = $seen.Named }) }
        Start-Sleep -Milliseconds 1000
    }
}

function Complete-Run($run, [double] $timeoutSeconds, $samples) {
    Watch-Run $run $timeoutSeconds $samples
    $exit = 'TIMEOUT'
    if ($run.Process.HasExited) { $exit = [string]$run.Process.ExitCode }
    else { $run.Process.Kill(); $run.Process.WaitForExit() }
    $text = if (Test-Path -LiteralPath $run.Report) { Get-Content -LiteralPath $run.Report -Raw } else { $null }
    [pscustomobject]@{
        Exit   = $exit
        Report = if ($text) { $text | ConvertFrom-Json } else { $null }
        HookLog = @(if (Test-Path -LiteralPath $run.HookLog) { Get-Content -LiteralPath $run.HookLog | ForEach-Object { [string]$_ } }) # plain strings: Get-Content's carry provider properties into the JSON
    }
}

# What a verdict was made from, as late-share.json keeps it: the first sample, each one whose set of live read threads differs
# from the one before, and the last.
function Compress-Samples($samples) {
    $kept = New-Object System.Collections.Generic.List[object]
    $before = $null
    $count = $samples.Count
    for ($i = 0; $i -lt $count; $i++) {
        $sample = $samples[$i]
        $key = ($sample.Alive | ForEach-Object { "$_" }) -join ','
        if ($i -eq 0 -or $i -eq ($count - 1) -or $key -cne $before) {
            $kept.Add([pscustomobject]@{ Utc = $sample.Utc.ToString('o'); Alive = @($sample.Alive); Named = $sample.Named })
        }
        $before = $key
    }
    $kept.ToArray()
}

# One arm as late-share.json holds it: the verdict, and the inputs the verdict was made from, so that the verdict can be made again
# (tests/window/SL-004-LateShareCorpus.json is made of these).
function New-ArmRecord([string] $tag, $run, [datetime] $wakeUtc, [int] $wakeSeconds, $result, $verdict, $samples, [int] $listenerAccepts) {
    [pscustomobject]@{
        Arm = $tag; Held = ($verdict.Verdict -ceq 'held'); Verdict = $verdict.Verdict; Buttons = $verdict.Buttons; Exit = $result.Exit; LeftAfterMs = $verdict.LeftAfterMs
        ShareSeenAfterLaunchS = $verdict.ShareSeenAfterLaunchS; Cause = $verdict.Cause; ListenerAccepts = $listenerAccepts
        Checks = @($verdict.Checks | ForEach-Object { [pscustomobject]@{ Key = $_.Key; Name = $_.Name; Ok = $_.Ok; Detail = $_.Detail } })
        Input = [pscustomobject]@{
            HookLog = @($result.HookLog); ReportButtons = $result.Report.window.DrivePanel.buttons; Exit = $result.Exit
            LaunchUtc = $run.LaunchUtc.ToString('o'); WakeUtc = $wakeUtc.ToString('o'); WakeSeconds = $wakeSeconds
            Share = $ShareLetter; ShareReadIndex = $shareIndex; Observations = @(Compress-Samples $samples)
        }
    }
}

function Write-Arm([string] $tag, $result, $verdict, $samples) {
    $lines = @($result.HookLog)
    $lines | Set-Content (Join-Path $Out "hook-$tag.txt")
    ($samples | ForEach-Object { '{0:o} alive={1}' -f $_.Utc, ($_.Alive -join ',') }) | Set-Content (Join-Path $Out "threads-$tag.txt")
    foreach ($line in $lines) { L "  hook: $line" }
    foreach ($check in $verdict.Checks) { L ('  {0,-5} {1} ({2})' -f $(if ($check.Ok) { 'ok' } else { 'FAIL' }), $check.Name, $check.Detail) }
    if ($verdict.Cause) { L "  cause: $($verdict.Cause)" }
    L "arm $tag verdict: $($verdict.Verdict); buttons=$($verdict.Buttons -join ',')"
}

$arms = New-Object System.Collections.Generic.List[object]

# ---- Control: the share answers ----
L '=== arm control (the share answers) ==='
$samples = New-Object System.Collections.Generic.List[object]
$run = Start-DrivesRun 'control' 60
$result = Complete-Run $run 90 $samples
$controlRead = Read-DrivesHookLog $result.HookLog
$controlButtons = if ($controlRead.Done) { @($controlRead.Done.Buttons) } else { @() }
$controlOk = ($result.Exit -ceq '0' -and ($controlButtons -join ',') -ceq 'C:,Y:,Z:' -and $null -ne $result.Report -and $result.Report.window.DrivePanel.buttons -eq 3)
foreach ($line in $result.HookLog) { L "  hook: $line" }
L ("arm control: exit=$($result.Exit) buttons=$($controlButtons -join ',') reportButtons=$($result.Report.window.DrivePanel.buttons) -> " + $(if ($controlOk) { 'held' } else { 'FAILED' }))
$arms.Add([pscustomobject]@{ Arm = 'control'; Held = $controlOk; Buttons = $controlButtons; Exit = $result.Exit })

# ---- The silenced arms ----
$occurrences = @{}
foreach ($seconds in $WakeDelays) {
    # The same delay twice (-Wake 120,120) is two arms, w120 and w120-2: a wake is a race with the SMB client's own retry, and one run says little.
    $occurrences[$seconds] = 1 + [int]$occurrences[$seconds]
    $tag = if ($occurrences[$seconds] -gt 1) { "w$seconds-$($occurrences[$seconds])" } else { "w$seconds" }
    L "=== arm $tag (the server wakes $seconds s after the launch) ==="
    Silence
    $samples = New-Object System.Collections.Generic.List[object]
    $deadline = $seconds + 60
    $run = Start-DrivesRun $tag $deadline
    Watch-Run $run $seconds $samples
    $listenerAccepts = if (Test-Path (Join-Path $Out 'listener.txt')) { @(Get-Content (Join-Path $Out 'listener.txt') | Select-String 'accept').Count } else { 0 }
    L ("before the wake at {0:N1} s: app exited={1}, listener accepts so far={2}" -f ([DateTime]::UtcNow - $run.LaunchUtc).TotalSeconds, $run.Process.HasExited, $listenerAccepts)
    $wakeUtc = [DateTime]::UtcNow
    Wake
    $result = Complete-Run $run ($seconds + $deadline + 30) $samples
    $verdict = Get-LateShareVerdict -HookLog $result.HookLog -Report $result.Report -Exit $result.Exit -LaunchUtc $run.LaunchUtc -WakeUtc $wakeUtc `
        -WakeSeconds $seconds -Share $ShareLetter -Observations $samples.ToArray() -ShareReadIndex $shareIndex
    Write-Arm $tag $result $verdict $samples
    $arms.Add((New-ArmRecord $tag $run $wakeUtc $seconds $result $verdict $samples $listenerAccepts))
}

# ---- Negative control: the server never wakes while the hook waits ----
# The check has to be able to fail: a share that does not answer within the hook's wait must read as FAILED, with the cause "still
# blocked", and the app must still end through its report (a read stuck on a dead server does not hold report-mode Shutdown).
# This arm holds when it is read that way, and is woken only afterwards.
L '=== arm silent (negative control: the server does not wake within the hook''s 20 s wait) ==='
Silence
$samples = New-Object System.Collections.Generic.List[object]
$run = Start-DrivesRun 'silent' 20
$result = Complete-Run $run 90 $samples
$wakeUtc = [DateTime]::UtcNow
$verdict = Get-LateShareVerdict -HookLog $result.HookLog -Report $result.Report -Exit $result.Exit -LaunchUtc $run.LaunchUtc -WakeUtc $wakeUtc `
    -WakeSeconds 0 -Share $ShareLetter -Observations $samples.ToArray() -ShareReadIndex $shareIndex
Write-Arm 'silent' $result $verdict $samples
$negativeHeld = ($verdict.Verdict -ceq 'FAILED' -and $verdict.Cause -like 'still blocked*' -and $result.Exit -ceq '0' -and ($verdict.Buttons -join ',') -ceq 'C:,Z:')
L ("arm silent: read as FAILED, cause '$($verdict.Cause)', exit $($result.Exit) -> " + $(if ($negativeHeld) { 'held (the check can fail, and says why)' } else { 'FAILED (the check did not fail as it should)' }))
$record = New-ArmRecord 'silent' $run $wakeUtc 0 $result $verdict $samples 0
$record.Held = $negativeHeld # this arm holds when the check failed as it should; Verdict and Checks still show the failure
$arms.Add($record)
Wake

$summary = [pscustomobject]@{ Build = "$($cv.CurrentBuild).$($cv.UBR)"; Arms = $arms.ToArray() }
$summary | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $Out 'late-share.json') -Encoding ascii
$failed = @($arms | Where-Object { -not $_.Held })
L ("end: {0} of {1} arms held" -f ($arms.Count - $failed.Count), $arms.Count)
if ($failed.Count) { exit 1 }
exit 0
