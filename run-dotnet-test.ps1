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
    $args = @('test','D:\code\cs2j\tests\CSharpToJava.Tests\CSharpToJava.Tests.csproj','-c','Release','--filter','FullyQualifiedName~InlineDataCsvMappingTests|FullyQualifiedName~MemberDataPropertySourceTests')
    $p = Start-Process -FilePath 'dotnet' -ArgumentList $args -WorkingDirectory 'D:\code\cs2j' -WindowStyle Hidden -RedirectStandardOutput 'D:\code\cs2j\dotnet-inlinedata-test.log' -RedirectStandardError 'D:\code\cs2j\dotnet-inlinedata-test.err.log' -PassThru
    if (-not $p.WaitForExit(300000)) {
        $p.Kill()
        Write-Output 'TIMEOUT'
    } else {
        Write-Output "EXIT=$($p.ExitCode)"
    }
    Write-Output '--- STDOUT ---'
    Get-Content 'D:\code\cs2j\dotnet-inlinedata-test.log' -Tail 60
    Write-Output '--- STDERR ---'
    Get-Content 'D:\code\cs2j\dotnet-inlinedata-test.err.log' -Tail 30
} finally {
    $lock.Dispose()
    Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
}
