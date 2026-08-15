$ErrorActionPreference = 'Continue'
$mvn = (Get-Command mvn.cmd -ErrorAction SilentlyContinue).Source
if (-not $mvn) { $mvn = 'mvn' }
$args = @(
    '-f','D:\code\cs2j\java\csharptojava-compat\pom.xml',
    '-Dmaven.repo.local=D:\mvnrepo',
    '-Dtest=MemoryStreamTest,ReadOnlySpanNullArrayTest,Utf8FormatterDateTimeTest',
    '-Dsurefire.failIfNoSpecifiedTests=false',
    'test'
)
$p = Start-Process -FilePath $mvn -ArgumentList $args -WorkingDirectory 'D:\code\cs2j\java\csharptojava-compat' -WindowStyle Hidden -RedirectStandardOutput 'D:\code\cs2j\mvn-compat-memorystream.log' -RedirectStandardError 'D:\code\cs2j\mvn-compat-memorystream.err.log' -PassThru
if (-not $p.WaitForExit(300000)) {
    $p.Kill()
    Write-Output 'TIMEOUT'
} else {
    Write-Output "EXIT=$($p.ExitCode)"
}
Write-Output '--- TAIL STDOUT ---'
Get-Content 'D:\code\cs2j\mvn-compat-memorystream.log' -Tail 40
Write-Output '--- TAIL STDERR ---'
Get-Content 'D:\code\cs2j\mvn-compat-memorystream.err.log' -Tail 20
