$ErrorActionPreference = 'Continue'
$mvn = (Get-Command mvn.cmd -ErrorAction SilentlyContinue).Source
if (-not $mvn) { $mvn = 'mvn' }
$args = @(
    '-f','D:\code\cs2j\java\csharptojava-compat\pom.xml',
    '-Dmaven.repo.local=D:\mvnrepo',
    '-DskipTests',
    'install'
)
$p = Start-Process -FilePath $mvn -ArgumentList $args -WorkingDirectory 'D:\code\cs2j\java\csharptojava-compat' -WindowStyle Hidden -RedirectStandardOutput 'D:\code\cs2j\mvn-compat-install.log' -RedirectStandardError 'D:\code\cs2j\mvn-compat-install.err.log' -PassThru
if (-not $p.WaitForExit(300000)) {
    $p.Kill()
    Write-Output 'TIMEOUT'
} else {
    Write-Output "EXIT=$($p.ExitCode)"
}
Write-Output '--- TAIL STDOUT ---'
Get-Content 'D:\code\cs2j\mvn-compat-install.log' -Tail 30
Write-Output '--- TAIL STDERR ---'
Get-Content 'D:\code\cs2j\mvn-compat-install.err.log' -Tail 15
