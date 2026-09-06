$l = Get-Content 'd:\cs-json-0905-sdk\mvn-build.log' -Encoding Default
$da = ($l | Select-String 'deserializeAsync').Count
$sw = ($l | Select-String 'serializeWrapper').Count
$nf = ($l | Select-String '找不到符号').Count
$ic = ($l | Select-String '不兼容').Count
$ns = ($l | Select-String '不是语句').Count
$te = ($l | Select-String '^\[ERROR\].*\.java').Count
Write-Output "deserializeAsync: $da"
Write-Output "serializeWrapper: $sw"
Write-Output "symbol_not_found: $nf"
Write-Output "incompatible_type: $ic"
Write-Output "not_statement: $ns"
Write-Output "total_error_lines: $te"
