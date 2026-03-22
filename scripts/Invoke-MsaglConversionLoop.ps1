param(
    [string]$RepoRoot = "D:\code\CSharpToJavaConverter",
    [string]$SourceProject = "E:\agl-master\GraphLayout\Test\MSAGLTests\MSAGLTests.csproj",
    [string]$OutputRoot = "C:\agl\v20260322-msagltests-multi",
    [string]$MappingFile = "D:\code\CSharpToJavaConverter\config\TypeMappings.json",
    [string]$JavaVersion = "Java17",
    [string]$MavenExecutable = "mvn",
    [switch]$SkipTests = $true,
    [switch]$KeepExistingOutput,
    [string]$LogDirectory,
    [int]$ErrorPreviewCount = 20,
    [int]$ErrorContextLineCount = 8
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Write-Step {
    param([string]$Message)

    Write-Host "`n=== $Message ==="
}

function Assert-PathExists {
    param(
        [string]$Path,
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }
}

function Invoke-LoggedCommand {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$LogPath,
        [string]$WorkingDirectory,
        [string]$FailureMessage
    )

    if ([string]::IsNullOrWhiteSpace($LogPath)) {
        throw "LogPath is required."
    }

    $stdoutLog = "${LogPath}.stdout"
    $stderrLog = "${LogPath}.stderr"

    foreach ($path in @($LogPath, $stdoutLog, $stderrLog) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    $argumentLine = ($Arguments | ForEach-Object {
        if ($_ -match '\s') { '"{0}"' -f $_ } else { $_ }
    }) -join ' '

    Write-Host ("Running: {0} {1}" -f $FilePath, $argumentLine)

    $process = Start-Process `
        -FilePath $FilePath `
        -ArgumentList $Arguments `
        -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog `
        -NoNewWindow `
        -PassThru `
        -Wait

    Start-Sleep -Milliseconds 100

    $mergedOutput = @()
    foreach ($path in @($stdoutLog, $stderrLog)) {
        if (Test-Path -LiteralPath $path) {
            $mergedOutput += Get-Content -LiteralPath $path
        }
    }

    $mergedOutput | Out-Host
    Set-Content -LiteralPath $LogPath -Value $mergedOutput -Encoding UTF8

    if ($process.ExitCode -ne 0) {
        throw "$FailureMessage ExitCode=$($process.ExitCode). See log: $LogPath"
    }
}

function Get-FirstCompilationErrors {
    param(
        [string]$LogPath,
        [int]$MaxCount
    )

    if (-not (Test-Path -LiteralPath $LogPath)) {
        return @()
    }

    $lines = Get-Content -LiteralPath $LogPath
    $errorLines = $lines | Where-Object { $_ -like '[ERROR]*' }
    if (-not $errorLines) {
        return @()
    }

    $filtered = @()
    foreach ($line in $errorLines) {
        if ($line -match 'COMPILATION ERROR' -or $line -match 'Failed to execute goal' -or $line -match '^\[ERROR\]\s*$') {
            continue
        }

        $filtered += $line
        if ($filtered.Count -ge $MaxCount) {
            break
        }
    }

    return $filtered
}

function Convert-MavenFilePathToWindowsPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }

    if ($Path -match '^/([A-Za-z]):/(.+)$') {
        return ('{0}:\{1}' -f $matches[1], ($matches[2] -replace '/', '\'))
    }

    return $Path
}

function Get-FirstCompilationErrorDetail {
    param(
        [string]$LogPath,
        [int]$ContextLineCount
    )

    if (-not (Test-Path -LiteralPath $LogPath)) {
        return $null
    }

    $lines = Get-Content -LiteralPath $LogPath
    $pattern = '^\[ERROR\]\s+(?<file>/.+?):\[(?<line>\d+),(?<column>\d+)\]\s+(?<message>.+)$'
    $regex = [regex]::new($pattern)

    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        $match = $regex.Match($line)
        if (-not $match.Success) {
            continue
        }

        $details = New-Object System.Collections.Generic.List[string]
        for ($detailIndex = $index + 1; $detailIndex -lt $lines.Count; $detailIndex++) {
            $detailLine = $lines[$detailIndex]
            if ($regex.IsMatch($detailLine)) {
                break
            }

            if ($detailLine -match '^\[ERROR\]\s+(Failed to execute goal|-> \[Help|$)') {
                break
            }

            if ($detailLine -match '^\[ERROR\]\s+') {
                $details.Add(($detailLine -replace '^\[ERROR\]\s+', ''))
                if ($details.Count -ge $ContextLineCount) {
                    break
                }
            }
        }

        $normalizedFile = Convert-MavenFilePathToWindowsPath -Path $match.Groups['file'].Value
        return [ordered]@{
            File = $normalizedFile
            Line = [int]$match.Groups['line'].Value
            Column = [int]$match.Groups['column'].Value
            Message = $match.Groups['message'].Value.Trim()
            Details = @($details)
        }
    }

    return $null
}

function Write-JsonSummaryFile {
    param(
        [string]$SummaryPath,
        [hashtable]$Data
    )

    $firstErrors = @($Data.FirstErrors)

    $json = [ordered]@{
        timestamp = $Data.Timestamp
        repoRoot = $Data.RepoRoot
        sourceProject = $Data.SourceProject
        outputRoot = $Data.OutputRoot
        conversionSucceeded = $Data.ConversionSucceeded
        failureStage = $Data.FailureStage
        conversionLog = $Data.ConversionLog
        packageLog = $Data.PackageLog
        packageSucceeded = $Data.PackageSucceeded
        firstCompilationErrors = $firstErrors
        firstCompilationErrorDetail = $Data.FirstErrorDetail
    } | ConvertTo-Json -Depth 6

    $summaryDirectory = Split-Path -Parent $SummaryPath
    if (-not [string]::IsNullOrWhiteSpace($summaryDirectory)) {
        New-Item -ItemType Directory -Path $summaryDirectory -Force | Out-Null
    }

    Set-Content -LiteralPath $SummaryPath -Value $json -Encoding UTF8
}

function Write-SummaryFile {
    param(
        [string]$SummaryPath,
        [hashtable]$Data
    )

    $firstErrors = @($Data.FirstErrors)

    $lines = @(
        "Timestamp: $($Data.Timestamp)",
        "RepoRoot: $($Data.RepoRoot)",
        "SourceProject: $($Data.SourceProject)",
        "OutputRoot: $($Data.OutputRoot)",
        "ConversionSucceeded: $($Data.ConversionSucceeded)",
        "FailureStage: $($Data.FailureStage)",
        "ConversionLog: $($Data.ConversionLog)",
        "PackageLog: $($Data.PackageLog)",
        "PackageSucceeded: $($Data.PackageSucceeded)"
    )

    if ($null -ne $Data.FirstErrorDetail) {
        $firstErrorDetails = @($Data.FirstErrorDetail.Details)
        $lines += "FirstErrorFile: $($Data.FirstErrorDetail.File)"
        $lines += "FirstErrorLine: $($Data.FirstErrorDetail.Line)"
        $lines += "FirstErrorColumn: $($Data.FirstErrorDetail.Column)"
        $lines += "FirstErrorMessage: $($Data.FirstErrorDetail.Message)"

        if ($firstErrorDetails.Count -gt 0) {
            $lines += "FirstErrorDetails:"
            $lines += $firstErrorDetails
        }
    }

    if ($firstErrors.Count -gt 0) {
        $lines += "FirstCompilationErrors:"
        $lines += $firstErrors
    }

    $summaryDirectory = Split-Path -Parent $SummaryPath
    if (-not [string]::IsNullOrWhiteSpace($summaryDirectory)) {
        New-Item -ItemType Directory -Path $summaryDirectory -Force | Out-Null
    }

    Set-Content -LiteralPath $SummaryPath -Value $lines -Encoding UTF8
}

Assert-PathExists -Path $RepoRoot -Description "Repo root"
Assert-PathExists -Path $SourceProject -Description "Source project"
Assert-PathExists -Path $MappingFile -Description "Mapping file"

$cliProject = Join-Path $RepoRoot "src\CSharpToJava.CLI\CSharpToJava.CLI.csproj"
Assert-PathExists -Path $cliProject -Description "CLI project"

if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $outputLeafName = Split-Path -Leaf $OutputRoot
    if ([string]::IsNullOrWhiteSpace($outputLeafName)) {
        $outputLeafName = "output"
    }

    $LogDirectory = Join-Path $RepoRoot (Join-Path ".automation-logs" $outputLeafName)
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$conversionLog = Join-Path $LogDirectory ("convert-{0}.log" -f $timestamp)
$packageLog = Join-Path $LogDirectory ("package-{0}.log" -f $timestamp)
$summaryFile = Join-Path $LogDirectory ("summary-{0}.txt" -f $timestamp)
$summaryJsonFile = Join-Path $LogDirectory ("summary-{0}.json" -f $timestamp)

if ((Test-Path -LiteralPath $OutputRoot) -and -not $KeepExistingOutput) {
    Write-Step "Cleaning previous output"
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null

Write-Step "Running conversion"
$convertArgs = @(
    "run",
    "--project", $cliProject,
    "--",
    "convert-project",
    "-s", $SourceProject,
    "-d", $OutputRoot,
    "-m", $MappingFile,
    "-j", $JavaVersion,
    "--mode", "multi-module",
    "--include-tests", "true"
)

$conversionSucceeded = $true
$failureStage = "None"
try {
    $convertCommandArgs = @{
        FilePath = "dotnet"
        Arguments = $convertArgs
        LogPath = $conversionLog
        WorkingDirectory = $RepoRoot
        FailureMessage = "Conversion failed."
    }

    Invoke-LoggedCommand @convertCommandArgs
}
catch {
    $conversionSucceeded = $false
    $failureStage = "Conversion"
}

if (-not $conversionSucceeded) {
    Write-SummaryFile -SummaryPath $summaryFile -Data @{
        Timestamp = $timestamp
        RepoRoot = $RepoRoot
        SourceProject = $SourceProject
        OutputRoot = $OutputRoot
        ConversionSucceeded = $false
        FailureStage = $failureStage
        ConversionLog = $conversionLog
        PackageLog = $packageLog
        PackageSucceeded = $false
        FirstErrors = @()
        FirstErrorDetail = $null
    }

    Write-JsonSummaryFile -SummaryPath $summaryJsonFile -Data @{
        Timestamp = $timestamp
        RepoRoot = $RepoRoot
        SourceProject = $SourceProject
        OutputRoot = $OutputRoot
        ConversionSucceeded = $false
        FailureStage = $failureStage
        ConversionLog = $conversionLog
        PackageLog = $packageLog
        PackageSucceeded = $false
        FirstErrors = @()
        FirstErrorDetail = $null
    }

    Write-Step "Summary"
    Write-Host "Conversion log: $conversionLog"
    Write-Host "Summary file:   $summaryFile"
    Write-Host "Summary json:   $summaryJsonFile"
    throw
}

$rootPom = Join-Path $OutputRoot "pom.xml"
Assert-PathExists -Path $rootPom -Description "Generated parent pom.xml"

Write-Step "Running Maven package"
$packageArgs = @()
if ($SkipTests) {
    $packageArgs += "-DskipTests"
}
$packageArgs += @("-f", $rootPom, "package")

$packageSucceeded = $true
try {
    $packageCommandArgs = @{
        FilePath = $MavenExecutable
        Arguments = $packageArgs
        LogPath = $packageLog
        WorkingDirectory = $OutputRoot
        FailureMessage = "Maven package failed."
    }

    Invoke-LoggedCommand @packageCommandArgs
}
catch {
    $packageSucceeded = $false
    $failureStage = "Package"
    if (-not (Test-Path -LiteralPath $packageLog)) {
        throw
    }
}

$firstErrors = @(Get-FirstCompilationErrors -LogPath $packageLog -MaxCount $ErrorPreviewCount)
$firstErrorDetail = Get-FirstCompilationErrorDetail -LogPath $packageLog -ContextLineCount $ErrorContextLineCount

Write-SummaryFile -SummaryPath $summaryFile -Data @{
    Timestamp = $timestamp
    RepoRoot = $RepoRoot
    SourceProject = $SourceProject
    OutputRoot = $OutputRoot
    ConversionSucceeded = $conversionSucceeded
    FailureStage = $failureStage
    ConversionLog = $conversionLog
    PackageLog = $packageLog
    PackageSucceeded = $packageSucceeded
    FirstErrors = $firstErrors
    FirstErrorDetail = $firstErrorDetail
}

Write-JsonSummaryFile -SummaryPath $summaryJsonFile -Data @{
    Timestamp = $timestamp
    RepoRoot = $RepoRoot
    SourceProject = $SourceProject
    OutputRoot = $OutputRoot
    ConversionSucceeded = $conversionSucceeded
    FailureStage = $failureStage
    ConversionLog = $conversionLog
    PackageLog = $packageLog
    PackageSucceeded = $packageSucceeded
    FirstErrors = $firstErrors
    FirstErrorDetail = $firstErrorDetail
}

Write-Step "Summary"
Write-Host "Conversion log: $conversionLog"
Write-Host "Package log:    $packageLog"
Write-Host "Summary file:   $summaryFile"
Write-Host "Summary json:   $summaryJsonFile"
Write-Host "Package status: $([bool]$packageSucceeded)"

if ($null -ne $firstErrorDetail) {
    Write-Host "First error file:    $($firstErrorDetail.File)"
    Write-Host "First error line:    $($firstErrorDetail.Line)"
    Write-Host "First error column:  $($firstErrorDetail.Column)"
    Write-Host "First error message: $($firstErrorDetail.Message)"
    if ($firstErrorDetail.Details.Count -gt 0) {
        Write-Host "First error details:"
        $firstErrorDetail.Details | ForEach-Object { Write-Host $_ }
    }
}

if (@($firstErrors).Count -gt 0) {
    Write-Host "First compilation errors:"
    @($firstErrors) | ForEach-Object { Write-Host $_ }
}

if (-not $packageSucceeded) {
    exit 1
}

exit 0