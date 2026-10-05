param([string] $File, [string] $From, [string] $To)
$marks = @{}; Get-Content $File | % { $o = $_ | ConvertFrom-Json; $marks[$o.label] = $o }
$a = $marks[$From]; $b = $marks[$To]
"{0} -> {1}: paged {2:+#,0;-#,0;0} B, nonpaged {3:+#,0;-#,0;0} B, sys handles {4:+0;-0;0}, sys 0x120089 {5:+0;-0;0}" -f $From, $To, ($b.paged-$a.paged), ($b.nonpaged-$a.nonpaged), ($b.sys-$a.sys), ($b.sys120089-$a.sys120089)
foreach ($t in $a.tags.PSObject.Properties.Name) { "  {0}: live {1:+#,0;-#,0;0}  allocs {2:+#,0;-#,0;0}  bytes {3:+#,0;-#,0;0}" -f $t, ($b.tags.$t.live - $a.tags.$t.live), ($b.tags.$t.allocs - $a.tags.$t.allocs), ($b.tags.$t.bytes - $a.tags.$t.bytes) }
