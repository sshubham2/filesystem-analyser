<#
SL-004: what a run of the published app, with a mapped share that answers only after "Drives" is shown, has to show, as one
function both the Sandbox runner (SL-004-LateShareInSandbox.ps1) and the host's test (SL-004-LateShareButton.Tests.ps1) call.
Dot-sourced; it runs nothing and touches nothing, so it works under Windows PowerShell 5.1 (the Sandbox's) and pwsh 7.

Get-LateShareVerdict reads what the drives hook mode (tests/window/FsaWindowHook/DrivesMode.cs) logged, the report the app wrote,
and the runner's own clock:

  -HookLog      the hook's log lines
  -Report       the report JSON as an object, or $null when the run wrote none
  -Exit         the app's exit code as text, or TIMEOUT
  -LaunchUtc    when the runner started the app
  -WakeUtc      when the runner began waking the share's server
  -WakeSeconds  the wake delay the run was asked for: about this long after the launch
  -Share        the late share's letter ('Y:')
  -Observations what the runner saw of the app's drive-read threads, one per sample: @{ Utc = <DateTime>; Alive = <int[]>; Named = <int> },
                the indexes of the "FsAnalyser drive read N" threads then alive and how many of the process's threads had a name
                at all (0: names could not be read; a sample without Named is taken as readable); empty when nothing was sampled
  -ShareReadIndex  the share's position in the app's drive list, which is the N of its read thread

and returns Verdict ('held' or 'FAILED'), Checks (Name, Ok, Detail), the buttons in the order the picker showed them at the end,
and Cause: for a share that got no button, whether its read was still blocked, had ended (it returned not ready, or failed), or
could not be told. A check that cannot be made is a failed check, never a skipped one.
#>

$script:LoadingText = 'Looking for drives...'
$script:SettledWithin = 3000  # ms after the window loads; the picker waits 2 s for the drives (MainWindow.DriveStatusWait)

function Read-DrivesHookLog([string[]] $Lines) {
    $seen = New-Object System.Collections.Generic.List[object]
    $armed = $null
    $done = $null
    $refused = New-Object System.Collections.Generic.List[string]
    foreach ($line in $Lines) {
        if ($line -cmatch '^drives-armed want=(\S*) deadline=(\d+) ms=(\d+)$') {
            $armed = [pscustomobject]@{ Want = @($Matches[1] -split ','); DeadlineSeconds = [int]$Matches[2]; Ms = [long]$Matches[3] }
        }
        elseif ($line -cmatch '^drives-seen ms=(\d+) utc=(\S+) status=(.*) buttons=(\S*)$') {
            $seen.Add([pscustomobject]@{
                    Ms = [long]$Matches[1]; Utc = ConvertTo-Utc $Matches[2]; Status = $Matches[3]
                    Buttons = @(if ($Matches[4]) { $Matches[4] -split ',' })
                })
        }
        elseif ($line -cmatch '^drives-done ms=(\d+) utc=(\S+) held=(\d+) found=(True|False) status=(.*) buttons=(\S*)$') {
            $done = [pscustomobject]@{
                Ms = [long]$Matches[1]; Utc = ConvertTo-Utc $Matches[2]; HeldMs = [long]$Matches[3]; Found = ($Matches[4] -ceq 'True')
                Status = $Matches[5]; Buttons = @(if ($Matches[6]) { $Matches[6] -split ',' })
            }
        }
        elseif ($line.StartsWith('refused ')) { $refused.Add($line) }
    }
    [pscustomobject]@{ Armed = $armed; Seen = $seen.ToArray(); Done = $done; Refused = $refused.ToArray() }
}

# A UTC time as the hook and the runner write it (ISO 8601 round trip, "2026-11-18T00:25:06.3546115Z"), whether it arrives as that
# text or as the DateTime pwsh 7's ConvertFrom-Json makes of it.
function ConvertTo-Utc($value) {
    if ($value -is [datetime]) { return $value.ToUniversalTime() }
    [datetime]::Parse([string]$value, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}

function Get-LateShareVerdict {
    param(
        [string[]] $HookLog,
        $Report,
        [string] $Exit,
        [datetime] $LaunchUtc,
        [datetime] $WakeUtc,
        [int] $WakeSeconds,
        [string] $Share = 'Y:',
        $Observations = @(),
        [int] $ShareReadIndex = -1
    )
    $checks = New-Object System.Collections.Generic.List[object]
    function Add-Check([string] $key, [bool] $ok, [string] $name, [string] $detail) { $checks.Add([pscustomobject]@{ Key = $key; Name = $name; Ok = $ok; Detail = $detail }) }

    $log = Read-DrivesHookLog $HookLog
    $seen = @($log.Seen)
    $final = $null
    if ($log.Done) { $final = @($log.Done.Buttons) }
    elseif ($seen.Count -gt 0) { $final = @($seen[-1].Buttons) }
    else { $final = @() }

    Add-Check 'exit' ($Exit -ceq '0' -and $null -ne $Report) 'the app exited 0 and wrote its report' "exit=$Exit report=$($null -ne $Report)"
    Add-Check 'hook' ($null -ne $log.Armed -and $null -ne $log.Done) 'the hook watched the picker and let the report go (drives-armed and drives-done)' "armed=$($null -ne $log.Armed) done=$($null -ne $log.Done) refused=$($log.Refused -join ' | ')"

    # The picker settles, with the share still silent, within about 2 s of the window loading.
    $left = $null
    foreach ($reading in $seen) { if ($reading.Status -cne $script:LoadingText) { $left = $reading; break } }
    $leftAfter = $null
    if ($left -and $log.Armed) { $leftAfter = $left.Ms - $log.Armed.Ms }
    Add-Check 'settled' ($null -ne $left -and $null -ne $leftAfter -and $leftAfter -le $script:SettledWithin) "DriveStatus left '$($script:LoadingText)' within $($script:SettledWithin) ms of the window loading" "after=$leftAfter ms"
    $atLeft = if ($left) { @($left.Buttons) } else { @() }
    Add-Check 'silent' ($null -ne $left -and $atLeft.Count -gt 0 -and $atLeft -notcontains $Share) "when it left, the answering drives had their buttons and the silent share had none ($Share)" "buttons=$($atLeft -join ',')"

    # The share's button came in after the wake, from a share that had not answered for the whole delay.
    $first = $null
    foreach ($reading in $seen) { if (@($reading.Buttons) -contains $Share) { $first = $reading; break } }
    $late = $null
    if ($first) { $late = ($first.Utc - $LaunchUtc).TotalSeconds }
    Add-Check 'late' ($null -ne $first -and $first.Utc -ge $WakeUtc -and $late -ge ($WakeSeconds - 1)) "the share's button ($Share) came in after the wake, about $WakeSeconds s after the launch" $(if ($first) { 'seen {0:N1} s after the launch, {1:N1} s after the wake began' -f $late, ($first.Utc - $WakeUtc).TotalSeconds } else { 'no reading has it' })
    Add-Check 'afterwake' ($null -ne $log.Done -and $log.Done.Utc -ge $WakeUtc) 'the report was read after the wake' $(if ($log.Done) { '{0:N1} s after the wake began' -f ($log.Done.Utc - $WakeUtc).TotalSeconds } else { 'no drives-done line' })

    # Letter order, the share between neighbours on both sides.
    $at = [array]::IndexOf($final, $Share)
    $sorted = @($final | Sort-Object { $_ } -CaseSensitive)
    $inOrder = ($final -join ',') -ceq ($sorted -join ',')
    Add-Check 'order' ($at -ge 1 -and $at -lt ($final.Count - 1) -and $inOrder) "the buttons are in letter order and $Share sits between its neighbours" "buttons=$($final -join ',') at=$at"
    Add-Check 'status' ($null -ne $log.Done -and $log.Done.Status -ceq 'Drives') "DriveStatus reads 'Drives' at the end" $(if ($log.Done) { "status=$($log.Done.Status)" } else { 'no drives-done line' })
    $count = $null
    if ($Report -and $Report.window -and $Report.window.DrivePanel) { $count = $Report.window.DrivePanel.buttons }
    Add-Check 'report' ($null -ne $count -and $count -eq $final.Count -and $final.Count -ge 3) "the report's DrivePanel.buttons is the number the picker showed, the share's among them" "report=$count hook=$($final.Count)"

    # Why a share with no button has none.
    $cause = $null
    if ($final -notcontains $Share) {
        $obs = @($Observations)
        # A sample that names no thread at all (Named is 0) could not read thread names, so "no read thread alive" in it means nothing.
        $readable = @($obs | Where-Object { $null -eq $_.Named -or $_.Named -gt 0 }).Count -gt 0
        if ($obs.Count -eq 0 -or $ShareReadIndex -lt 0 -or -not $readable) { $cause = 'unknown: the app''s drive-read threads could not be read' }
        elseif (@($obs[-1].Alive) -contains $ShareReadIndex) { $cause = 'still blocked: the share''s read thread was alive at the last sample' }
        elseif (@($obs | Where-Object { @($_.Alive) -contains $ShareReadIndex }).Count -gt 0) { $cause = 'ended without a button: the share''s read thread was alive, then returned (not ready, or failed)' }
        else { $cause = 'ended without a button: the share''s read thread was never seen alive' }
    }

    [pscustomobject]@{
        Verdict = if (@($checks | Where-Object { -not $_.Ok }).Count -eq 0) { 'held' } else { 'FAILED' }
        Checks  = $checks.ToArray()
        Buttons = $final
        LeftAfterMs = $leftAfter
        ShareSeenAfterLaunchS = if ($null -ne $late) { [math]::Round($late, 1) } else { $null }
        Cause   = $cause
    }
}
