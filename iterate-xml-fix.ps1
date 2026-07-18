#Requires -Version 5.1
<#
.SYNOPSIS
    一键完成 C#->Java 转换 + Maven 编译，用于 xml 项目迭代修复。
.DESCRIPTION
    1. 安装 csharptojava-compat 到本地 Maven 仓库
    2. 使用 CSharpToJava.CLI 转换 d:\csharpxml -> d:\cs-xml-20260716
    3. 在目标目录执行 mvn clean package -e
    4. 保存完整 Maven 日志到 mvn-build.log
    5. 输出首个编译错误，便于进入 Step 3/4
#>
param(
    [string]$CompatPom = "d:\code\cs2j\java\csharptojava-compat\pom.xml",
    [string]$CliProject = "d:\code\cs2j\src\CSharpToJava.CLI\CSharpToJava.CLI.csproj",
    [string]$SourceDir = "d:\csharpxml",
    [string]$DestDir = "d:\cs-xml-20260716",
    [string]$ExtraDeps = "io.github.ningpp:system-private-uri:0.0.1-SNAPSHOT",
    [string]$CompatInstallLog = "d:\code\cs2j\compat-install.log",
    [string]$BuildLog = "d:\cs-xml-20260716\mvn-build.log"
)

$ErrorActionPreference = "Stop"

function Write-StepHeader {
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Invoke-Process {
    param(
        [string]$Executable,
        [string[]]$Arguments,
        [string]$LogFile
    )
    Write-Host "Running: $Executable $Arguments"

    # Use Start-Process so we can reliably capture the native exit code
    # while merging both stdout and stderr into a single UTF-8 log file.
    $errLog = "$LogFile.stderr"
    try {
        $proc = Start-Process -FilePath $Executable `
            -ArgumentList $Arguments `
            -RedirectStandardOutput $LogFile `
            -RedirectStandardError $errLog `
            -NoNewWindow -Wait -PassThru
        if (Test-Path $errLog) {
            Get-Content -Raw $errLog | Add-Content -Path $LogFile -Encoding utf8
            Remove-Item $errLog -Force -ErrorAction SilentlyContinue
        }
        return $proc.ExitCode
    }
    catch {
        Write-Host "Failed to start ${Executable}: $_" -ForegroundColor Red
        return 1
    }
}

# Step 1: install compat library
Write-StepHeader "Step 1: Install compat library"
$compatArgs = @("-f", $CompatPom, "clean", "install", "-e")
$compatExit = Invoke-Process -Executable "mvn" -Arguments $compatArgs -LogFile $CompatInstallLog
if ($compatExit -ne 0) {
    Write-Host "Compat library install FAILED (exit $compatExit). See $CompatInstallLog" -ForegroundColor Red
    exit 1
}
Write-Host "Compat library installed successfully." -ForegroundColor Green

# Step 2: C# -> Java conversion
Write-StepHeader "Step 2: C# -> Java conversion"
$convertArgs = @(
    "run", "--project", $CliProject, "--",
    "convert-project", "-s", $SourceDir, "-d", $DestDir,
    "--extra-deps", $ExtraDeps
)
$convertExit = Invoke-Process -Executable "dotnet" -Arguments $convertArgs -LogFile "$DestDir\convert.log"
if ($convertExit -ne 0) {
    Write-Host "Conversion FAILED (exit $convertExit). See $DestDir\convert.log" -ForegroundColor Red
    exit 1
}
Write-Host "Conversion completed successfully." -ForegroundColor Green

# Step 3: Maven build
Write-StepHeader "Step 3: Maven build"
$buildArgs = @("-f", "$DestDir\pom.xml", "clean", "package", "-e")
$buildExit = Invoke-Process -Executable "mvn" -Arguments $buildArgs -LogFile $BuildLog

# Step 4: report
Write-StepHeader "Step 4: Build report"
if ($buildExit -eq 0) {
    Write-Host "BUILD SUCCESS" -ForegroundColor Green
    Write-Host "Log saved to: $BuildLog" -ForegroundColor Gray
    exit 0
}

Write-Host "BUILD FAILED (exit $buildExit)." -ForegroundColor Red
Write-Host "Log saved to: $BuildLog" -ForegroundColor Gray

$firstError = Select-String -Path $BuildLog -Pattern "\[ERROR\].*:\[\d+,\d+\]" | Select-Object -First 1
if ($firstError) {
    Write-Host "`nFirst compilation error:" -ForegroundColor Yellow
    Write-Host $firstError.Line -ForegroundColor Yellow
} else {
    Write-Host "No line-numbered [ERROR] found. Check the full log." -ForegroundColor Yellow
}

exit $buildExit
