$ErrorActionPreference = 'Continue'
Set-Location 'D:\code\cs2j'
$dest = 'D:\cs-java-generic-sdk'
$args = @('run','--project','src/CSharpToJava.CLI/CSharpToJava.CLI.csproj','-c','Release','--','generic-preprocess','-s','D:\csharp-sdk','-d',$dest,'--no-verify-build','--no-verify-tests')
$pinfo = New-Object System.Diagnostics.ProcessStartInfo
$pinfo.FileName = 'dotnet'
$pinfo.Arguments = ($args -join ' ')
$pinfo.UseShellExecute = $false
$pinfo.RedirectStandardOutput = $true
$pinfo.RedirectStandardError = $true
$pinfo.CreateNoWindow = $true
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
Write-Output $out
Write-Output $err
Write-Output ("EXIT:" + $proc.ExitCode)
exit $proc.ExitCode
