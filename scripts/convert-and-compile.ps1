param(
    [string]$RunLabel = (Get-Date -Format "yyyyMMdd_HHmmss"),
    [string]$SourcePath = "E:\agl-master\GraphLayout\GraphLayout.sln",
    [string]$DestinationPath = "E:\jmsagl521",
    [switch]$SkipConverterBuild,
    [switch]$SkipCompatInstall,
    [switch]$KeepDestination
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$logDir = Join-Path $repoRoot ("iteration-logs\{0}" -f $RunLabel)
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$StepName,
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$LogFileName,
        [switch]$IgnoreExitCode
    )

    $mergedPath = Join-Path $logDir ($LogFileName + ".log")

    Write-Host "[$StepName] $FileName $($Arguments -join ' ')"
    Push-Location -LiteralPath $WorkingDirectory
    $oldErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        & $FileName @Arguments > $mergedPath 2>&1
        $exitCode = $LASTEXITCODE
        if ($null -eq $exitCode) {
            $exitCode = if ($?) { 0 } else { 1 }
        }
    }
    finally {
        $ErrorActionPreference = $oldErrorActionPreference
        Pop-Location
    }

    $exitPath = Join-Path $logDir ($LogFileName + ".exitcode.txt")
    $exitCode | Set-Content -LiteralPath $exitPath -Encoding ASCII

    if ($exitCode -ne 0 -and -not $IgnoreExitCode) {
        throw "$StepName failed with exit code $exitCode. See $mergedPath"
    }

    return $exitCode
}

function Write-MavenErrorSummary {
    param([Parameter(Mandatory = $true)][string]$MavenLogPath)

    $summaryPath = Join-Path $logDir "05-error-summary.log"
    $byFilePath = Join-Path $logDir "06-errors-by-file.log"
    $patternsPath = Join-Path $logDir "07-error-patterns.log"

    if (-not (Test-Path $MavenLogPath)) {
        "Maven log not found: $MavenLogPath" | Set-Content -LiteralPath $summaryPath -Encoding UTF8
        return
    }

    $errorLines = Get-Content -LiteralPath $MavenLogPath | Where-Object { $_ -match '^\[ERROR\]' }
    $javaErrors = $errorLines | Where-Object { $_ -match '\.java:\[[0-9]+,[0-9]+\]' }

    $javaErrors |
        ForEach-Object { $_ -replace '^\[ERROR\]\s+', '' } |
        Set-Content -LiteralPath $byFilePath -Encoding UTF8

    $javaErrors |
        ForEach-Object { $_ -replace '^\[ERROR\]\s+.*\.java:\[[0-9]+,[0-9]+\]\s+', '' } |
        Group-Object |
        Sort-Object Count -Descending |
        ForEach-Object { "{0,6} {1}" -f $_.Count, $_.Name } |
        Set-Content -LiteralPath $patternsPath -Encoding UTF8

    @(
        "RunLabel: $RunLabel"
        "SourcePath: $SourcePath"
        "DestinationPath: $DestinationPath"
        "Total [ERROR] lines: $($errorLines.Count)"
        "Java compiler error lines: $($javaErrors.Count)"
        ""
        "Top error patterns:"
    ) + (Get-Content -LiteralPath $patternsPath -ErrorAction SilentlyContinue | Select-Object -First 40) |
        Set-Content -LiteralPath $summaryPath -Encoding UTF8
}

Write-Host "============================================"
Write-Host "Run label:   $RunLabel"
Write-Host "Repo root:   $repoRoot"
Write-Host "Source:      $SourcePath"
Write-Host "Destination: $DestinationPath"
Write-Host "Logs:        $logDir"
Write-Host "============================================"

if (-not (Test-Path -LiteralPath $SourcePath)) {
    throw "Source path not found: $SourcePath"
}

if (-not $SkipConverterBuild) {
    Invoke-LoggedCommand `
        -StepName "1/5 build converter" `
        -FileName "dotnet" `
        -Arguments @("build", $repoRoot, "--configuration", "Release") `
        -WorkingDirectory $repoRoot `
        -LogFileName "01-build-converter" | Out-Null
}

if (-not $SkipCompatInstall) {
    $compatDir = Join-Path $repoRoot "java\csharptojava-compat"
    Invoke-LoggedCommand `
        -StepName "2/5 install compat" `
        -FileName "mvn" `
        -Arguments @("-DskipTests", "install") `
        -WorkingDirectory $compatDir `
        -LogFileName "02-install-compat" | Out-Null
}

if (-not $KeepDestination -and (Test-Path -LiteralPath $DestinationPath)) {
    Write-Host "[3/5 clean destination] $DestinationPath"
    Remove-Item -LiteralPath $DestinationPath -Recurse -Force
}

$cliProject = Join-Path $repoRoot "src\CSharpToJava.CLI\CSharpToJava.CLI.csproj"
$convertExit = Invoke-LoggedCommand `
    -StepName "4/5 convert project" `
    -FileName "dotnet" `
    -Arguments @(
        "run",
        "--project", $cliProject,
        "--configuration", "Release",
        "--",
        "convert-project",
        "-s", $SourcePath,
        "-d", $DestinationPath,
        "--prefer-procedural",
        "--linq-report",
        "-v"
    ) `
    -WorkingDirectory $repoRoot `
    -LogFileName "03-convert" `
    -IgnoreExitCode

if (-not (Test-Path -LiteralPath (Join-Path $DestinationPath "pom.xml"))) {
    throw "Conversion did not produce a root pom.xml under $DestinationPath. Convert exit code: $convertExit"
}

$mavenExit = Invoke-LoggedCommand `
    -StepName "5/5 maven compile" `
    -FileName "mvn" `
    -Arguments @("clean", "compile", "-e") `
    -WorkingDirectory $DestinationPath `
    -LogFileName "04-maven-compile" `
    -IgnoreExitCode

$mavenLog = Join-Path $logDir "04-maven-compile.log"
Write-MavenErrorSummary -MavenLogPath $mavenLog

$result = if ($mavenExit -eq 0) { "SUCCESS" } else { "FAILED" }
@(
    "Result: $result"
    "ConvertExitCode: $convertExit"
    "MavenExitCode: $mavenExit"
    "LogDir: $logDir"
) | Set-Content -LiteralPath (Join-Path $logDir "RESULT.txt") -Encoding UTF8

Write-Host "============================================"
Write-Host "Result: $result"
Write-Host "Convert exit code: $convertExit"
Write-Host "Maven exit code: $mavenExit"
Write-Host "Summary: $(Join-Path $logDir '05-error-summary.log')"
Write-Host "Logs: $logDir"
Write-Host "============================================"

exit $mavenExit
