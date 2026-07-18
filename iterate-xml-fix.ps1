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
    [string]$BuildLog = "d:\cs-xml-20260716\mvn-build.log",
    [string]$MavenRepoLocal = "D:\mvnrepo"
)

$ErrorActionPreference = "Stop"

function Write-StepHeader {
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Test-Command {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

function Format-Arguments {
    param([string[]]$Arguments)
    return ($Arguments | ForEach-Object {
        if ($_ -match '[\s"'']') {
            '"' + $_.Replace('\"', '\\\"') + '"'
        } else { $_ }
    }) -join ' '
}

function Invoke-Process {
    param(
        [string]$Executable,
        [string[]]$Arguments,
        [string]$LogFile
    )
    $argString = Format-Arguments -Arguments $Arguments
    Write-Host "Running: $Executable $argString"

    # Stream output to console while also writing to log so progress is visible.
    # Merge stderr into stdout to keep a single chronological UTF-8 log.
    try {
        if (Test-Path $LogFile) {
            Remove-Item $LogFile -Force -ErrorAction SilentlyContinue
        }
        $exitCode = & {
            # Native commands (mvn.cmd, dotnet) may write warnings to stderr;
            # don't let $ErrorActionPreference = Stop treat them as fatal.
            $ErrorActionPreference = "Continue"
            & $Executable @Arguments 2>&1 | Tee-Object -FilePath $LogFile | Out-Null
            $LASTEXITCODE
        }
        return $exitCode
    }
    catch {
        Write-Host "Failed to start ${Executable}: $_" -ForegroundColor Red
        return 1
    }
}

# Ensure destination directory exists so pre-conversion logs can be written
if (-not (Test-Path $DestDir)) {
    New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
}

# Preflight checks
Write-StepHeader "Preflight checks"
if (-not (Test-Command "dotnet")) {
    Write-Host "dotnet command not found in PATH." -ForegroundColor Red
    exit 1
}
if (-not (Test-Command "mvn")) {
    Write-Host "mvn command not found in PATH." -ForegroundColor Red
    exit 1
}
Write-Host "dotnet and mvn are available." -ForegroundColor Green

# Shared Maven options (use array elements so PowerShell never splits on ':'/ '=')
$mvnRepoArgs = @()
if (-not [string]::IsNullOrWhiteSpace($MavenRepoLocal)) {
    $mvnRepoArgs = @("-Dmaven.repo.local=$MavenRepoLocal")
    Write-Host "Using local Maven repository: $MavenRepoLocal" -ForegroundColor Gray
}

# Step 1: install compat library
Write-StepHeader "Step 1: Install compat library"
$compatArgs = @("-f", $CompatPom) + $mvnRepoArgs + @("clean", "install", "-e")
$compatExit = Invoke-Process -Executable "mvn" -Arguments $compatArgs -LogFile $CompatInstallLog
if ($compatExit -ne 0) {
    Write-Host "Compat library install FAILED (exit $compatExit). See $CompatInstallLog" -ForegroundColor Red
    exit 1
}
Write-Host "Compat library installed successfully." -ForegroundColor Green

# Step 2: build the CLI once, then run the emitted DLL directly.
# Using 'dotnet run' makes MSBuild spawn persistent node processes that keep
# Start-Process -Wait from returning even after conversion is done.
Write-StepHeader "Step 2a: Build C#->Java CLI"
$cliProjectDir = Split-Path -Parent $CliProject
$cliDll = Join-Path $cliProjectDir "bin\Release\net10.0\CSharpToJava.CLI.dll"
$buildCliArgs = @("build", $CliProject, "-c", "Release", "-p:NodeReuse=false")
$buildCliExit = Invoke-Process -Executable "dotnet" -Arguments $buildCliArgs -LogFile "$DestDir\cli-build.log"
if ($buildCliExit -ne 0) {
    Write-Host "CLI build FAILED (exit $buildCliExit). See $DestDir\cli-build.log" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $cliDll)) {
    # Fallback to Debug configuration if Release DLL is not present
    $cliDll = Join-Path $cliProjectDir "bin\Debug\net10.0\CSharpToJava.CLI.dll"
}
if (-not (Test-Path $cliDll)) {
    Write-Host "Could not find CLI DLL at $cliDll" -ForegroundColor Red
    exit 1
}

Write-StepHeader "Step 2b: C# -> Java conversion"
$convertArgs = @(
    $cliDll,
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
$buildArgs = @("-f", "$DestDir\pom.xml") + $mvnRepoArgs + @("clean", "package", "-e")
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
