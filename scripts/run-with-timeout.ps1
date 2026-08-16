param(
    [Parameter(Mandatory = $true)][string]$CommandLine,
    [Parameter(Mandatory = $false)][string]$WorkingDirectory = (Get-Location).Path,
    [Parameter(Mandatory = $false)][int]$TimeoutSeconds = 200,
    [Parameter(Mandatory = $false)][string]$LogFile = '',
    [Parameter(Mandatory = $false)][string]$ErrLogFile = ''
)

$ErrorActionPreference = 'Continue'

if (-not $LogFile) { $LogFile = Join-Path $env:TEMP ("timeout-" + [guid]::NewGuid().ToString('N') + '.log') }
$CommandLine = "$CommandLine > `"$LogFile`" 2>&1"

$cmd = (Get-Command cmd.exe).Source
$p = Start-Process -FilePath $cmd -ArgumentList @('/c', $CommandLine) -WorkingDirectory $WorkingDirectory -WindowStyle Hidden -PassThru

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$timedOut = $false
while (-not $p.HasExited) {
    $p.Refresh()
    if ([DateTime]::UtcNow -ge $deadline) {
        $timedOut = $true
        break
    }
    Start-Sleep -Seconds 5
}

if ($timedOut) {
    Write-Output "TIMEOUT after $TimeoutSeconds s (pid=$($p.Id))"
    & taskkill /PID $p.Id /T /F | Out-Null
    Write-Output "LOG=$LogFile"
    Write-Output "ERRLOG=$ErrLogFile"
    exit 124
}

$p.WaitForExit()
$p.Refresh()
$exitCode = $p.ExitCode
if ($null -eq $exitCode) { $exitCode = 1 }
Write-Output "EXIT=$exitCode"
Write-Output "LOG=$LogFile"
Write-Output "ERRLOG=$ErrLogFile"
exit $exitCode
