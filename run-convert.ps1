$ErrorActionPreference = 'Continue'
$lockPath = 'D:\code\cs2j\.dotnet-build.lock'
while ($true) {
    try {
        $lock = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        break
    } catch {
        Start-Sleep -Seconds 5
    }
}
try {
    $args = @('run','-c','Release','--no-build','--project','D:\code\cs2j\src\CSharpToJava.CLI\CSharpToJava.CLI.csproj','--','convert-project','-s','D:\csharp-sdk','-d','D:\cs-java-81319-8')
    $p = Start-Process -FilePath 'dotnet' -ArgumentList $args -WorkingDirectory 'D:\code\cs2j' -WindowStyle Hidden -RedirectStandardOutput 'D:\code\cs2j\convert-81319-8.log' -RedirectStandardError 'D:\code\cs2j\convert-81319-8.err.log' -PassThru
    if (-not $p.WaitForExit(300000)) {
        $p.Kill()
        Write-Output 'TIMEOUT'
    } else {
        Write-Output "EXIT=$($p.ExitCode)"
    }
    Write-Output '--- STDOUT ---'
    Get-Content 'D:\code\cs2j\convert-81319-8.log' -Tail 40
    Write-Output '--- STDERR ---'
    Get-Content 'D:\code\cs2j\convert-81319-8.err.log' -Tail 30
} finally {
    $lock.Dispose()
    Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
}
