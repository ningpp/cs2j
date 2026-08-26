$ErrorActionPreference = 'Continue'
$sln = 'D:\cs-java-generic-sdk\.cs2j-generic-src\csharp-sdk.sln'
$args = @('build',$sln,'-c','Release','--nologo')
$pinfo = New-Object System.Diagnostics.ProcessStartInfo
$pinfo.FileName = 'dotnet'
$pinfo.Arguments = ($args -join ' ')
$pinfo.UseShellExecute = $false
$pinfo.RedirectStandardOutput = $true
$pinfo.RedirectStandardError = $true
$pinfo.CreateNoWindow = $true
$pinfo.Environment['MSBUILDDISABLENODEREUSE'] = '1'
$pinfo.Environment['DOTNET_CLI_USE_MSBUILD_SERVER'] = '0'
$proc = [System.Diagnostics.Process]::Start($pinfo)
$outTask = $proc.StandardOutput.ReadToEndAsync()
$errTask = $proc.StandardError.ReadToEndAsync()
$exited = $proc.WaitForExit(200000)
if (-not $exited) {
    try { $proc.Kill() } catch {}
    Write-Output 'TIMEOUT'
    exit 124
}
$out = $outTask.Result
$err = $errTask.Result
$out | Out-File 'D:\cs-java-generic-sdk\sdk-generic-build.log' -Encoding UTF8
$err | Out-File 'D:\cs-java-generic-sdk\sdk-generic-build.err' -Encoding UTF8
Write-Output ("EXIT:" + $proc.ExitCode)
# Print last 30 lines of stdout for visibility
$out -split "`r?`n" | Select-Object -Last 30 | ForEach-Object { Write-Output $_ }
exit $proc.ExitCode
