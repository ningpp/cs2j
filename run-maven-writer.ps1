$ErrorActionPreference = 'Continue'
$mvn = (Get-Command mvn.cmd -ErrorAction SilentlyContinue).Source
if (-not $mvn) { $mvn = 'mvn' }
$args = @(
    '-f','D:\cs-java-81319-8\pom.xml',
    '-Dmaven.repo.local=D:\mvnrepo',
    '-Dtest=dotnet.text.json.Tests.Utf8JsonWriterTests',
    '-Dsurefire.failIfNoSpecifiedTests=false',
    'test','-e'
)
$p = Start-Process -FilePath $mvn -ArgumentList $args -WorkingDirectory 'D:\cs-java-81319-8' -WindowStyle Hidden -RedirectStandardOutput 'D:\cs-java-81319-8\mvn-agent1-writer.log' -RedirectStandardError 'D:\cs-java-81319-8\mvn-agent1-writer.err.log' -PassThru
if (-not $p.WaitForExit(300000)) {
    $p.Kill()
    Write-Output 'TIMEOUT'
} else {
    Write-Output "EXIT=$($p.ExitCode)"
}
Write-Output '--- TAIL STDOUT ---'
Get-Content 'D:\cs-java-81319-8\mvn-agent1-writer.log' -Tail 60
Write-Output '--- TAIL STDERR ---'
Get-Content 'D:\cs-java-81319-8\mvn-agent1-writer.err.log' -Tail 20
