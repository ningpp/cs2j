# Compare before/after console failure sets.
$b = Get-Content 'D:\code\cs2j\tools\writer-cluster\console-records.json' -Raw | ConvertFrom-Json
$a = Get-Content 'D:\code\cs2j\tools\writer-cluster\console-records-after.json' -Raw | ConvertFrom-Json

function Key($r) { "$($r.outcome)|$($r.cls).$($r.meth)|$($r.lines[0])" }

$bk = New-Object System.Collections.Generic.HashSet[string]
foreach ($r in $b.records) { [void]$bk.Add((Key $r)) }
$ak = New-Object System.Collections.Generic.HashSet[string]
foreach ($r in $a.records) { [void]$ak.Add((Key $r)) }

"BEFORE total=$($b.records.Count)  AFTER total=$($a.records.Count)"
""
"=== FIXED (in before, not after) ==="
$fixed = @(); foreach ($r in $b.records) { if (-not $ak.Contains((Key $r))) { $fixed += $r } }
$fixed | ForEach-Object { "  [$($_.cls).$($_.meth)]"; $_.full; "" }
"count fixed = $($fixed.Count)"
""
"=== NEW (in after, not before) ==="
$new = @(); foreach ($r in $a.records) { if (-not $bk.Contains((Key $r))) { $new += $r } }
$new | ForEach-Object { "  $($_.full)" }
"count new = $($new.Count)"
