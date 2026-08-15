$ErrorActionPreference = 'Continue'
$mvn = (Get-Command mvn.cmd -ErrorAction SilentlyContinue).Source
if (-not $mvn) { $mvn = 'mvn' }
$tests = 'dotnet.text.json.Tests.BitStackTests,dotnet.text.json.Tests.JsonReaderStateAndOptionsTests,dotnet.text.json.Serialization.Tests.ValueTests,dotnet.text.json.Serialization.Tests.ExtensionDataTests,dotnet.text.json.Serialization.Tests.PropertyVisibilityTests,dotnet.text.json.Serialization.Tests.EnumTests'
$args = @(
    '-f','D:\cs-java-81319-8\pom.xml',
    '-Dmaven.repo.local=D:\mvnrepo',
    "-Dtest=$tests",
    '-Dsurefire.failIfNoSpecifiedTests=false',
    'test','-e'
)
$p = Start-Process -FilePath $mvn -ArgumentList $args -WorkingDirectory 'D:\cs-java-81319-8' -WindowStyle Hidden -RedirectStandardOutput 'D:\cs-java-81319-8\mvn-agent8-inline-tests.log' -RedirectStandardError 'D:\cs-java-81319-8\mvn-agent8-inline-tests.err.log' -PassThru
if (-not $p.WaitForExit(300000)) {
    $p.Kill()
    Write-Output 'TIMEOUT'
} else {
    Write-Output "EXIT=$($p.ExitCode)"
}
Write-Output '--- TAIL STDOUT ---'
Get-Content 'D:\cs-java-81319-8\mvn-agent8-inline-tests.log' -Tail 80
Write-Output '--- TAIL STDERR ---'
Get-Content 'D:\cs-java-81319-8\mvn-agent8-inline-tests.err.log' -Tail 30
