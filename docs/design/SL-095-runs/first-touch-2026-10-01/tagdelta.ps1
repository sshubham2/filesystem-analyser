param([string]$Before, [string]$After)
# Prints alloc/free deltas for the wcifs leak graph and neighbours between two poolwatch marks.
$b = (Get-Content $Before -Raw | ConvertFrom-Json -AsHashtable)['tags']
$a = (Get-Content $After -Raw | ConvertFrom-Json -AsHashtable)['tags']
$rows = foreach ($t in 'WCsc','WCfc','WCri','WCfn','WCse','FMwi','FMfc','FMfn','File','NtfF') {
    $x = $b[$t]; $y = $a[$t]
    if (-not $x) { $x = @(0,0,0,0,0) }; if (-not $y) { $y = @(0,0,0,0,0) }
    $al = ($y[0]-$x[0]) + ($y[2]-$x[2]); $fr = ($y[1]-$x[1]) + ($y[3]-$x[3])
    '{0}:{1}/{2}' -f $t, $al, $fr
}
'   allocs/frees  ' + ($rows -join '  ')
