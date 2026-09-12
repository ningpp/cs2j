param(
    [string]$Dir = 'd:\cs-json-0911-sdk\system-text-json-tests\target\surefire-reports',
    [string]$Out = 'D:\code\cs2j\probe\results.tsv'
)
$rows = @()
foreach ($f in Get-ChildItem -Path $Dir -Filter 'TEST-*.xml' -File) {
    [xml]$d = Get-Content $f.FullName -Raw
    foreach ($tc in $d.testsuite.testcase) {
        $st = if ($tc.error) { 'E' } elseif ($tc.failure) { 'F' } else { 'P' }
        $rows += "$($d.testsuite.name)#$($tc.name)`t$st"
    }
}
$rows | Sort-Object | Set-Content -Path $Out
"wrote $($rows.Count) cases to $Out"
$rows | Group-Object { ($_ -split "`t")[1] } | ForEach-Object { "$($_.Name) = $($_.Count)" }
