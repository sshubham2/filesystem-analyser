$ErrorActionPreference = 'Continue'
Set-Location (Split-Path (Split-Path $PSScriptRoot))
$m = Join-Path $PSScriptRoot 'marks-v3.jsonl'
function Mark($l) { pwsh -NoProfile -File (Join-Path $PSScriptRoot 'mark.ps1') -Label $l -Out $m }
function Run($label, $root) {
    "--- $label root=$root"
    $o = pwsh -NoProfile -ExecutionPolicy Bypass -File eng/read-window.ps1 -Root $root -Outcome 2>&1
    $o | Where-Object { "$_" -match '^(EXIT|TIMEOUT|NO REPORT|OUTCOME)' } | Select-Object -First 4
    Mark $label
}
$id = '1f35079a-cec9-427c-b64d-e12db8dd08c0'
$rel = "ProgramData\Microsoft\Windows\Containers\Layers\$id\Files\Windows\Media"
$ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254*' } | Select-Object -First 1).IPAddress
"ip=$ip"
Mark W0
Run D1 "\\.\C:\$rel"
Run D2 ("C:/" + ($rel -replace '\\','/'))
Run D3 "C:\ProgramData\Microsoft\Windows\Containers\Layers\..\Layers\$id\Files\Windows\Media"
Run D4 "\\0--1.ipv6-literal.net\C$\$rel"
if ($ip) { Run D5 "\\$ip\C$\$rel" }
Run D6 "\\LOCALHOST\c$\$($rel.ToLowerInvariant())"
Run D7 "\\?\C:\$rel\"
Run D8 "C:\ProgramData\Microsoft\Windows\Containers\Layers.\$id\Files\Windows\Media"
Mark W1; Start-Sleep 30; Mark W2
