# Extract the surefire "Failures:" / "Errors:" sections from the maven console log (UTF-16LE)
# and split them into per-test records.  This is the authoritative list that the baseline
# console summary (Tests run / Failures / Errors) is computed from.
param(
  [string]$Log = 'D:\code\cs2j\mvn-build-r140.log',
  [string]$Out = 'D:\code\cs2j\tools\writer-cluster\console-records.json'
)

$lines = [System.IO.File]::ReadAllLines($Log, [System.Text.Encoding]::Unicode)

function Get-Section([string]$startPattern, [string[]]$stopPatterns) {
  $start = -1
  for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match $startPattern) { $start = $i + 1; break }
  }
  if ($start -lt 0) { return @() }
  $out = New-Object System.Collections.Generic.List[string]
  for ($i = $start; $i -lt $lines.Count; $i++) {
    $l = $lines[$i]
    $stop = $false
    foreach ($p in $stopPatterns) { if ($l -match $p) { $stop = $true; break } }
    if ($stop) { break }
    $out.Add($l)
  }
  return $out
}

$failLines = Get-Section '^\[ERROR\] Failures:\s*$' @('^\[ERROR\] Errors:\s*$')
$errLines  = Get-Section '^\[ERROR\] Errors:\s*$'   @('^\[ERROR\] Tests run:', '^\[INFO\] ', '^\[ERROR\] $', '^\[ERROR\] Re-run Maven')

# A record starts on a line like:  [ERROR]   Class.method:123->helper:45 message
# continuation lines are not indented by 3 spaces after [ERROR].
$entryRe = '^\[ERROR\]   (\S+?)[:.\s]'

function Convert-Section($sec, [string]$outcome) {
  $recs = New-Object System.Collections.Generic.List[object]
  $cur = $null
  foreach ($l in $sec) {
    $body = $l -replace '^\[ERROR\] ?', ''
    $isStart = ($l -match '^\[ERROR\]   \S')
    if ($isStart) {
      if ($cur) { $recs.Add($cur) }
      $cur = [pscustomobject]@{ outcome=$outcome; entry=$body.Trim(); lines=New-Object System.Collections.Generic.List[string] }
      $cur.lines.Add($body.Trim())
    } elseif ($cur) {
      $cur.lines.Add($body)
    }
  }
  if ($cur) { $recs.Add($cur) }
  foreach ($r in $recs) {
    $head = $r.lines[0]
    # Class.method:line->helper:line  or Class.method:line » Kind msg  or Class.method:line message
    $m = [regex]::Match($head, '^([A-Za-z_$][\w$]*)\.([^\s:]+)')
    $r | Add-Member -NotePropertyName cls  -NotePropertyValue $m.Groups[1].Value -Force
    $r | Add-Member -NotePropertyName meth -NotePropertyValue $m.Groups[2].Value -Force
    $r | Add-Member -NotePropertyName full -NotePropertyValue (($r.lines) -join "`n") -Force
  }
  return $recs
}

$failRecs = Convert-Section $failLines 'FAILURE'
$errRecs  = Convert-Section $errLines  'ERROR'
$all = @($failRecs) + @($errRecs)

$byClass = $all | Group-Object cls | Sort-Object Count -Descending | Select-Object Count,Name

$result = [pscustomobject]@{
  totals = [pscustomobject]@{ failures=$failRecs.Count; errors=$errRecs.Count; records=$all.Count }
  byClass = $byClass
  records = $all
}
[System.IO.File]::WriteAllText($Out, ($result | ConvertTo-Json -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
"parsed: failures=$($failRecs.Count) errors=$($errRecs.Count) total=$($all.Count)"
"--- top classes ---"
$byClass | Select-Object -First 15 | ForEach-Object { "{0,5}  {1}" -f $_.Count, $_.Name }
