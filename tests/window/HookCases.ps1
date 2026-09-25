# The window hook's armed mode on a local tree (SL-038): the hook's own behaviour that needs no SMB share,
# so it runs by default and changes nothing on the machine. The stall rig would write the armed marker when
# its server stops answering; here the case writes it once the window is up, so every armed action runs
# against a scan that is not stalled.
#
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tests/window/HookCases.ps1 -Case H-001
#
# H-001  An armed close checks ScanningPanel, not CancelButton (case C-020). With no Cancel pressed the
#        hook closes the window of the scan that is still running: window gone within 1 s, process within
#        5 s, exit 0. After a fast local Cancel the scan has already returned when the close comes due, so
#        the hook refuses with 'the scan was no longer running' and closes nothing.
# H-002  An armed hook refuses what it cannot do, says why and presses nothing (case C-026): a marker that
#        never appears (refused 60 s after the hook loaded), stop with no FSA_HOOK_CANCEL_DELAY_MS, and
#        throw mode with FSA_HOOK_ARMED set.
#
# -Case All runs both, about 90 s. Exit 0 when every check held, 1 when any failed.
#
# Why a script of its own: any change to WindowCases.ps1 makes the person's network pass stale (the
# staleness rule in docs/design/SL-020-scanner.md), so these cases are not added there. They reuse its
# helpers and settings, loaded from it by name below, so there is one copy of each. A helper renamed or
# removed there fails here at once, by name.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('All', 'H-001', 'H-002')]
    [string] $Case
)

$ErrorActionPreference = 'Stop'

$Repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Failures = [System.Collections.Generic.List[string]]::new()

# ---- WindowCases.ps1's helpers and settings, by name ----

$Helpers = 'Show', 'Check', 'CheckEqual', 'New-CaseFolder', 'New-File', 'New-SmallTree', 'New-LargeTree', 'Remove-CaseFolder',
    'Update-App', 'Update-Hook', 'Start-App', 'Wait-App', 'Invoke-WithHook', 'Field', 'Read-Stopping', 'CheckCloseEscape'
$Settings = 'Exe', 'HookProject', 'HookDll', 'HookVariables', 'KiB64'
$parseErrors = $null
$source = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'WindowCases.ps1'), [ref]$null, [ref]$parseErrors)
if ($parseErrors) {
    throw "tests/window/WindowCases.ps1 does not parse: $($parseErrors[0].Message)"
}
$loaded = @()
foreach ($statement in $source.EndBlock.Statements) {
    $name = if ($statement -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $statement.Name }
    elseif ($statement -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $statement.Left -is [System.Management.Automation.Language.VariableExpressionAst]) { $statement.Left.VariablePath.UserPath }
    if ($name -in $Helpers -or $name -in $Settings) {
        . ([scriptblock]::Create($statement.Extent.Text))
        $loaded += $name
    }
}
$missing = @(@($Helpers) + @($Settings) | Where-Object { $_ -notin $loaded })
if ($missing.Count -gt 0) {
    throw "tests/window/WindowCases.ps1 no longer defines $($missing -join ', ') at its top level"
}

# ---- Local stand-ins for the rig ----

# Writes the armed marker once the app's window is on screen, where the rig would write it when its server
# stops answering. The hook waits for it, so its delays run from a moment the scan is under way.
function Write-Marker([System.Diagnostics.Process] $process, [string] $marker) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $process.HasExited -and $process.MainWindowHandle -eq [IntPtr]::Zero -and $clock.Elapsed.TotalSeconds -lt 30) {
        Start-Sleep -Milliseconds 10
        $process.Refresh()
    }
    Set-Content -LiteralPath $marker -Value 'armed by the case, not by a stall'
}

# Waits until the hook's log has a line matching $pattern, the process ends, or $timeoutSec pass. Returns
# whether the line appeared.
function Wait-HookLine([System.Diagnostics.Process] $process, [string] $pattern, [int] $timeoutSec) {
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $timeoutSec) {
        $text = ''
        try { $text = [System.IO.File]::ReadAllText($env:FSA_HOOK_LOG) } catch { } # absent, or mid-append
        if ($text -match "(?m)$pattern") { return $true }
        if ($process.HasExited) { return $false }
        Start-Sleep -Milliseconds 100
    }
    $false
}

# ---- Cases ----

# H-001: an armed close decides on ScanningPanel (case C-020). The close delay leaves the local scan of
# New-LargeTree (1.0 to 1.3 s) running when the close comes due; the Cancel delay presses Cancel while it
# runs, and a local cancel returns within milliseconds, long before the close comes due.
function Test-H001 {
    Write-Output '  no Cancel pressed, the scan running'
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'large'
        [void](New-LargeTree $root)
        $marker = Join-Path $case 'armed.txt'
        $run = Invoke-WithHook 'close' $HookDll {
            $process = Start-App "`"$root`""
            Write-Marker $process $marker
            $exit = Wait-App $process 60
            [pscustomobject]@{ Exit = $exit; ExitUtc = if ($exit -ne 'TIMEOUT') { $process.ExitTime.ToUniversalTime() } }
        } @{ FSA_HOOK_ARMED = $marker; FSA_HOOK_CLOSE_DELAY_MS = '300' }
        foreach ($line in $run.Log) { Write-Output "        hook: $line" }
        $close = @($run.Log | Where-Object { $_.StartsWith('acted mode=close ') })
        Check ($close.Count -eq 1) "the hook closed the window (one 'acted mode=close' line)"
        if ($close.Count -eq 1) {
            CheckEqual 'True' (Field $close[0] 'scanning') 'the scan was still running when the window closed'
            CheckCloseEscape $run.Log $run.Result.Exit $run.Result.ExitUtc $false
        }
    }
    finally { Remove-CaseFolder $case }

    Write-Output '  after a fast local Cancel'
    $case = New-CaseFolder
    try {
        $root = Join-Path $case 'large'
        [void](New-LargeTree $root)
        $marker = Join-Path $case 'armed.txt'
        $run = Invoke-WithHook 'close' $HookDll {
            $process = Start-App "`"$root`""
            try {
                Write-Marker $process $marker
                $settled = Wait-HookLine $process '^(refused|closed) ' 30
                [pscustomobject]@{ Settled = $settled; StillOpen = -not $process.HasExited }
            }
            finally {
                if (-not $process.HasExited) {
                    $process.Kill()
                    $process.WaitForExit()
                }
            }
        } @{ FSA_HOOK_ARMED = $marker; FSA_HOOK_CANCEL_DELAY_MS = '100'; FSA_HOOK_CLOSE_DELAY_MS = '1000' }
        foreach ($line in $run.Log) { Write-Output "        hook: $line" }
        Check $run.Result.Settled 'the hook refused or closed within 30 s'
        Check (@($run.Log | Where-Object { $_.StartsWith('acted mode=stop ') }).Count -eq 1) 'Cancel was pressed while the scan ran'
        $returned = @(Read-Stopping $run.Log | Where-Object Kind -eq 'returned')
        Check ($returned.Count -eq 1 -and $returned[0].Ms -lt 2000) "the scan returned within 2 s of Cancel ($(($returned | ForEach-Object Ms) -join ', ') ms)"
        $refused = @($run.Log | Where-Object { $_ -match '^refused mode=close reason=the scan was no longer running ' })
        Check ($refused.Count -eq 1) "the hook refused the close with 'the scan was no longer running'"
        Check (@($run.Log | Where-Object { $_ -match '^(acted mode=close|closed) ' }).Count -eq 0) 'the hook closed nothing'
        Check $run.Result.StillOpen 'the window was still open after the refusal'
    }
    finally { Remove-CaseFolder $case }
}

# H-002: armed configurations the hook must refuse, pressing nothing (case C-026). The app is left open
# and killed by the case once the hook has said why.
function Test-H002 {
    $shapes = @(
        [pscustomobject]@{ Name = 'the marker never appears'; Mode = 'stop'; Marker = $false; Cancel = '100'; Within = 80
            Reason = "^refused mode=stop reason=the rig's marker .* did not appear within 60 s, so the stall never armed$" }
        [pscustomobject]@{ Name = 'stop with no Cancel delay'; Mode = 'stop'; Marker = $true; Cancel = $null; Within = 20
            Reason = '^refused mode=stop reason=armed stop needs FSA_HOOK_CANCEL_DELAY_MS$' }
        [pscustomobject]@{ Name = 'throw mode armed'; Mode = 'throw'; Marker = $true; Cancel = '100'; Within = 20
            Reason = '^refused mode=throw reason=throw mode is not armed$' }
    )
    foreach ($shape in $shapes) {
        Write-Output "  $($shape.Name)"
        $case = New-CaseFolder
        try {
            $root = Join-Path $case 'small'
            [void](New-SmallTree $root)
            $marker = Join-Path $case 'armed.txt'
            if ($shape.Marker) { Set-Content -LiteralPath $marker -Value 'armed by the case' }
            $armed = @{ FSA_HOOK_ARMED = $marker }
            if ($shape.Cancel) { $armed.FSA_HOOK_CANCEL_DELAY_MS = $shape.Cancel }
            $run = Invoke-WithHook $shape.Mode $HookDll {
                $clock = [System.Diagnostics.Stopwatch]::StartNew()
                $process = Start-App "`"$root`""
                try {
                    $said = Wait-HookLine $process '^(refused|acted) ' $shape.Within
                    [pscustomobject]@{ Said = $said; Seconds = $clock.Elapsed.TotalSeconds; StillOpen = -not $process.HasExited }
                }
                finally {
                    if (-not $process.HasExited) {
                        $process.Kill()
                        $process.WaitForExit()
                    }
                }
            } $armed
            foreach ($line in $run.Log) { Write-Output "        hook: $line" }
            Check $run.Result.Said "the hook said something within $($shape.Within) s"
            $refused = @($run.Log | Where-Object { $_ -match $shape.Reason })
            Check ($refused.Count -eq 1) "it refused, saying why ($(Show $shape.Reason))"
            Check (@($run.Log | Where-Object { $_ -match '^(acted|rethrown|closed) ' }).Count -eq 0) 'it pressed, closed and threw nothing'
            Check $run.Result.StillOpen 'the app was still running, as a person left it'
            if (-not $shape.Marker) {
                Check ($run.Result.Seconds -ge 59) ("it waited out the 60 s deadline first ({0:N1} s after launch)" -f $run.Result.Seconds)
            }
        }
        finally { Remove-CaseFolder $case }
    }
}

# ---- Run ----

$cases = if ($Case -eq 'All') { 'H-001', 'H-002' } else { @($Case) }
Update-Hook
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
