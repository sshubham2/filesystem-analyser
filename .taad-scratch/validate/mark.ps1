param([string] $Label, [string] $Out)
$src = Get-Content (Join-Path $PSScriptRoot '..\..\tests\window\PoolWatch.ps1') -Raw
$m = [regex]::Match($src, "(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@")
if (-not ('PoolWatchNative' -as [type])) { Add-Type -TypeDefinition $m.Groups[1].Value }
$want = 'WCsc','WCfc','WCri','WCfn','WCse','WCss','FMwi','FMfc','FMfn','File','NtfF','Toke'
$tags = [ordered]@{}
foreach ($t in [PoolWatchNative]::PoolTags()) { if ($want -ccontains $t[0]) { $tags[$t[0]] = @{ live = ($t[1]-$t[2]) + ($t[4]-$t[5]); bytes = $t[3] + $t[6]; allocs = $t[1] + $t[4] } } }
$h = [PoolWatchNative]::HandlesOf(4, 0x120089)
$mem = Get-CimInstance Win32_PerfRawData_PerfOS_Memory
$mark = [ordered]@{ label = $Label; utc = (Get-Date).ToUniversalTime().ToString('o'); paged = [long]$mem.PoolPagedBytes; nonpaged = [long]$mem.PoolNonpagedBytes; availMB = [long]$mem.AvailableMBytes; sys = $h[0]; sys120089 = $h[1]; tags = $tags }
$mark | ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath $Out
"{0}: paged {1:N1} MB nonpaged {2:N1} MB avail {3} MB sys {4} (0x120089 {5}) WCsc {6} WCfc {7} WCri {8} FMwi {9}" -f $Label, ($mark.paged/1MB), ($mark.nonpaged/1MB), $mark.availMB, $h[0], $h[1], $tags['WCsc'].live, $tags['WCfc'].live, $tags['WCri'].live, $tags['FMwi'].live
