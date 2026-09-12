# Summarizes surefire XML reports: per-class tests/failures/errors + total, and the
# failing/erroring test names (so two runs can be diffed).
param(
    [string]$Dir = 'd:\cs-json-0911-sdk\system-text-json-tests\target\surefire-reports',
    [string]$Out = ''
)
$rows = @()
foreach ($f in Get-ChildItem -Path $Dir -Filter 'TEST-*.xml' -File) {
    [xml]$d = Get-Content $f.FullName -Raw
    $ts = $d.testsuite
    $cls = $ts.name
    $rows += [pscustomobject]@{
        Class = $cls
        Tests = [int]$ts.tests
        Fail  = [int]$ts.failures
        Err   = [int]$ts.errors
    }
    foreach ($tc in $ts.testcase) {
        if ($tc.failure -or $tc.error) {
            $kind = if ($tc.error) { 'E' } else { 'F' }
            $msg = if ($tc.error) { $tc.error.message } else { $tc.failure.message }
            $msg = ($msg -replace '\s+', ' ')
            if ($msg.Length -gt 130) { $msg = $msg.Substring(0, 130) }
            $rows += [pscustomobject]@{ Class = "  $kind $($tc.name)"; Tests = 0; Fail = 0; Err = 0 }
            $rows += [pscustomobject]@{ Class = "      $msg"; Tests = 0; Fail = 0; Err = 0 }
        }
    }
}
$sum = $rows | Where-Object { $_.Tests -gt 0 }
$tot = [pscustomobject]@{ Class = 'TOTAL'; Tests = ($sum | Measure-Object Tests -Sum).Sum; Fail = ($sum | Measure-Object Fail -Sum).Sum; Err = ($sum | Measure-Object Err -Sum).Sum }
$lines = @()
$lines += 'CLASS SUMMARY'
$lines += ($sum | Sort-Object Class | ForEach-Object { '{0,-70} tests={1,4} F={2,3} E={3,3}' -f $_.Class, $_.Tests, $_.Fail, $_.Err })
$lines += ('{0,-70} tests={1,4} F={2,3} E={3,3}' -f $tot.Class, $tot.Tests, $tot.Fail, $tot.Err)
$lines += ''
$lines += 'FAILING TESTS'
$lines += ($rows | Where-Object { $_.Class -like '  *' } | ForEach-Object { $_.Class })
$text = $lines -join "`n"
Write-Output $text
if ($Out) { Set-Content -Path $Out -Value $text }
