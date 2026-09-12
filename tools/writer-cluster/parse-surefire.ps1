# Parse surefire .txt reports into a structured JSON of FAILURE/ERROR records.
# Usage: pwsh -File parse-surefire.ps1 [-ReportDir <path>] [-Out <path>]
param(
  [string]$ReportDir = 'd:\cs-json-0911-sdk\system-text-json-tests\target\surefire-reports',
  [string]$Out = 'D:\code\cs2j\tools\writer-cluster\surefire-records.json'
)

$records = New-Object System.Collections.Generic.List[object]
$summary = New-Object System.Collections.Generic.List[object]

foreach ($f in Get-ChildItem $ReportDir -Filter '*.txt') {
  $lines = [System.IO.File]::ReadAllLines($f.FullName)
  # summary line: "Tests run: N, Failures: N, Errors: N, Skipped: N, ... -- in <class>"
  $cls = $null
  foreach ($l in $lines) {
    if ($l -match '-- in (\S+)$') { $cls = $Matches[1] }
    if ($l -match '^Tests run: (\d+), Failures: (\d+), Errors: (\d+), Skipped: (\d+)') {
      $summary.Add([pscustomobject]@{ cls=$cls; tests=[int]$Matches[1]; failures=[int]$Matches[2]; errors=[int]$Matches[3]; skipped=[int]$Matches[4] })
      break
    }
  }

  $cur = $null
  for ($i = 0; $i -lt $lines.Count; $i++) {
    $l = $lines[$i]
    if ($l -match '^(\S+) -- Time elapsed: .*<<< (FAILURE|ERROR)!$') {
      if ($cur) { $records.Add($cur) }
      $cur = [pscustomobject]@{
        cls = $cls
        test = $Matches[1]
        outcome = $Matches[2]
        kind = ''
        msg = ''
        expected = ''
        actual = ''
        frames = New-Object System.Collections.Generic.List[string]
        raw = New-Object System.Collections.Generic.List[string]
      }
      continue
    }
    if ($cur) {
      if ($l -match '^(\S+) -- Time elapsed: ') {
        # next test header without failure marker => close current
        if ($l -notmatch '<<< (FAILURE|ERROR)!$') {
          # keep accumulating only if it's a continuation; headers end the block
        }
      }
      if ($l -match '^\tat ') {
        if ($cur.frames.Count -lt 40) { $cur.frames.Add(($l -replace '^\tat ','').Trim()) }
      } elseif ($l -match '^\s*Expected: (.*)$') {
        $cur.expected = $Matches[1]
      } elseif ($l -match '^\s*Actual:   (.*)$') {
        $cur.actual = $Matches[1]
      } elseif ($l -match '^\s*Expected : (.*)$') {
        $cur.expected = $Matches[1]
      } elseif ($l -match '^\s*Actual   : (.*)$') {
        $cur.actual = $Matches[1]
      } elseif ($l -match '^(\S+?)(: .*)?$' -and $cur.kind -eq '' -and $l -notmatch '^\s*$' -and $l -notmatch '^-----') {
        $cur.kind = $l.Trim()
      } elseif ($cur.msg -eq '' -and $l -notmatch '^\s*$' -and $l -notmatch '^-----') {
        $cur.msg = $l.Trim()
      }
      if ($cur.raw.Count -lt 60) { $cur.raw.Add($l) }
    }
  }
  if ($cur) { $records.Add($cur) }
}

$result = [pscustomobject]@{
  summary = $summary
  records = $records
  totals = [pscustomobject]@{
    tests = ($summary | Measure-Object tests -Sum).Sum
    failures = ($summary | Measure-Object failures -Sum).Sum
    errors = ($summary | Measure-Object errors -Sum).Sum
    skipped = ($summary | Measure-Object skipped -Sum).Sum
    records = $records.Count
  }
}
$json = $result | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))
"totals: tests=$($result.totals.tests) failures=$($result.totals.failures) errors=$($result.totals.errors) skipped=$($result.totals.skipped) records=$($records.Count)"
