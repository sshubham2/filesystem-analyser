param()
$ErrorActionPreference = 'Continue'
Set-Location (Split-Path (Split-Path $PSScriptRoot))
$m = Join-Path $PSScriptRoot 'marks-v3.jsonl'
function Mark($l) { pwsh -NoProfile -File (Join-Path $PSScriptRoot 'mark.ps1') -Label $l -Out $m }
function Run($label, $root) {
    "--- $label root=$root"
    $o = pwsh -NoProfile -ExecutionPolicy Bypass -File eng/read-window.ps1 -Root $root -Outcome 2>&1
    $o | Where-Object { "$_" -match '^(EXIT|TIMEOUT|NO REPORT|OUTCOME)|"(files|folders|status|title)"\s*:' } | Select-Object -First 8
    Mark $label
}
Add-Type -Name Dd -Namespace V3 -MemberDefinition '[DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern uint QueryDosDevice(string d, System.Text.StringBuilder t, int n);'
$sb = [Text.StringBuilder]::new(512); [void][V3.Dd]::QueryDosDevice('C:', $sb, 512); $dev = $sb.ToString()
$guid = (Get-CimInstance Win32_Volume -Filter "DriveLetter='C:'").DeviceID
$id = '1f35079a-cec9-427c-b64d-e12db8dd08c0'
$rel = "ProgramData\Microsoft\Windows\Containers\Layers\$id\Files\Windows\Media"
$media = "C:\$rel"
$short = (cmd /c "for %I in (`"$media`") do @echo %~sI").Trim()
"device=$dev guid=$guid short=$short"
Mark I0; Start-Sleep 30; Mark I1; Start-Sleep 30; Mark I2
$ctl = Join-Path $env:TEMP ("fsa-v3-ctl-" + [guid]::NewGuid().ToString('N').Substring(0,8)); New-Item -ItemType Directory $ctl | Out-Null; 1..3 | % { Set-Content (Join-Path $ctl "f$_.txt") 'x' }
Run CTL $ctl
Run U1 "\\localhost\C$\$rel"
Run U2 "\\127.0.0.1\C$\$rel"
Run U3 "\\$env:COMPUTERNAME\C$\$rel"
Run U4 "\\?\UNC\localhost\C$\$rel"
Run G1 ("\\?\GLOBALROOT" + $dev + "\" + $rel)
Run V1 ($guid + $rel)
Run S83 $short
$letter = @('Q','R','T','W','Y') | Where-Object { -not (Test-Path "$($_):\") } | Select-Object -First 1
subst "$($letter):" $media; Run SUB "$($letter):\"; subst "$($letter):" /d; "subst removed: $(-not (Test-Path "$($letter):\"))"
$j = Join-Path $ctl 'junction'; New-Item -ItemType Junction -Path $j -Target $media | Out-Null
Run JUN $j
[IO.Directory]::Delete($j); [IO.Directory]::Delete($ctl, $true); "ctl removed: $(-not (Test-Path $ctl))"
Run L1 'C:\ProgramData\Microsoft\Windows\Containers'
Run L2 '\\localhost\C$\ProgramData\Microsoft\Windows\Containers'
Mark I3; Start-Sleep 30; Mark I4
