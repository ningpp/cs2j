# Parse surefire XML reports (authoritative) into structured failure/error records.
# Usage: & parse-surefire-xml.ps1
param(
  [string]$ReportDir = 'd:\cs-json-0911-sdk\system-text-json-tests\target\surefire-reports',
  [string]$Out = 'D:\code\cs2j\tools\writer-cluster\surefire-xml-records.json'
)

$records = New-Object System.Collections.Generic.List[object]
$sumTests = 0; $sumFail = 0; $sumErr = 0; $sumSkip = 0
$perClass = New-Object System.Collections.Generic.List[object]

foreach ($f in Get-ChildItem $ReportDir -Filter 'TEST-*.xml') {
  $settings = New-Object System.Xml.XmlReaderSettings
  $settings.DtdProcessing = [System.Xml.DtdProcessing]::Ignore
  $settings.IgnoreComments = $true
  $r = [System.Xml.XmlReader]::Create($f.FullName, $settings)
  $clsName = $null; $tTests=0; $tFail=0; $tErr=0; $tSkip=0
  $cur = $null
  $inFailure = $false; $failText = New-Object System.Text.StringBuilder; $failType = ''
  try {
    while ($r.Read()) {
      switch ($r.NodeType) {
        ([System.Xml.XmlNodeType]::Element) {
          switch ($r.Name) {
            'testsuite' {
              $clsName = $r.GetAttribute('name')
              $tTests = [int]$r.GetAttribute('tests'); $tFail = [int]$r.GetAttribute('failures')
              $tErr = [int]$r.GetAttribute('errors'); $tSkip = [int]$r.GetAttribute('skipped')
              $sumTests += $tTests; $sumFail += $tFail; $sumErr += $tErr; $sumSkip += $tSkip
            }
            'testcase' {
              $cur = [pscustomobject]@{
                cls = $clsName
                test = $r.GetAttribute('classname')
                name = $r.GetAttribute('name')
                time = $r.GetAttribute('time')
                outcome = ''
                kind = ''
                msg = ''
                text = ''
                frames = New-Object System.Collections.Generic.List[string]
              }
            }
            'failure' { if ($cur) { $cur.outcome = 'FAILURE'; $failType = $r.GetAttribute('type'); $failText.Clear() | Out-Null; $inFailure = $true } }
            'error'   { if ($cur) { $cur.outcome = 'ERROR';   $failType = $r.GetAttribute('type'); $failText.Clear() | Out-Null; $inFailure = $true } }
          }
        }
        ([System.Xml.XmlNodeType]::Text) {
          if ($inFailure) { [void]$failText.Append($r.Value) }
        }
        ([System.Xml.XmlNodeType]::EndElement) {
          if ($r.Name -eq 'failure' -or $r.Name -eq 'error') {
            if ($cur) {
              $cur.kind = $failType
              $body = $failText.ToString()
              $cur.text = $body
              $lines = $body -split "`r?`n"
              $msgs = New-Object System.Collections.Generic.List[string]
              foreach ($ln in $lines) {
                if ($ln -match '^\s+at ') { if ($cur.frames.Count -lt 40) { $cur.frames.Add(($ln.Trim() -replace '^at ','')) } }
                elseif ($ln.Trim() -ne '') { if ($msgs.Count -lt 6) { $msgs.Add($ln) } }
              }
              $cur.msg = ($msgs -join ' | ')
              $records.Add($cur)
            }
            $inFailure = $false
          }
          if ($r.Name -eq 'testcase') { $cur = $null }
        }
      }
    }
  } finally { $r.Close() }
  if ($tFail -gt 0 -or $tErr -gt 0) { $perClass.Add([pscustomobject]@{ cls=$clsName; tests=$tTests; failures=$tFail; errors=$tErr }) }
}

$result = [pscustomobject]@{
  records = $records
  byClass = ($perClass | Sort-Object { $_.failures + $_.errors } -Descending)
  totals = [pscustomobject]@{ tests=$sumTests; failures=$sumFail; errors=$sumErr; skipped=$sumSkip; records=$records.Count }
}
[System.IO.File]::WriteAllText($Out, ($result | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
"totals: tests=$sumTests failures=$sumFail errors=$sumErr skipped=$sumSkip records=$($records.Count)"
