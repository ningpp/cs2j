@echo off
setlocal

set "SOURCE_DIR=E:\agl-master\GraphLayout"
if not exist "%SOURCE_DIR%" (
    echo [ERROR] Source path not found: %SOURCE_DIR%
    exit /b 1
)

for /f %%I in ('powershell -NoProfile -Command "(Get-Date).ToString(\"yyyyMMdd-HHmmss\")"') do set "TIMESTAMP=%%I"
if not defined TIMESTAMP (
    echo [ERROR] Failed to generate timestamp.
    exit /b 1
)

set "OUTPUT_DIR=E:\agl-java-%TIMESTAMP%"
set "SCRIPT_DIR=%~dp0"

echo Source: %SOURCE_DIR%
echo Output: %OUTPUT_DIR%
echo.

if /I "%~1"=="--print-only" (
    echo dotnet run --project "src\CSharpToJava.CLI\CSharpToJava.CLI.csproj" --configuration Release -- convert-project -s "%SOURCE_DIR%" -d "%OUTPUT_DIR%" --mode multi-module --verbose
    echo pushd "%OUTPUT_DIR%" ^>nul
    echo mvn clean package -e
    echo popd ^>nul
    exit /b 0
)

pushd "%SCRIPT_DIR%" >nul
dotnet run --project "src\CSharpToJava.CLI\CSharpToJava.CLI.csproj" --configuration Release -- convert-project -s "%SOURCE_DIR%" -d "%OUTPUT_DIR%" --mode multi-module --verbose
set "EXIT_CODE=%ERRORLEVEL%"
popd >nul

if not "%EXIT_CODE%"=="0" (
    echo.
    echo [ERROR] Conversion failed with exit code %EXIT_CODE%.
    exit /b %EXIT_CODE%
)

where mvn >nul 2>nul
if errorlevel 1 (
    echo.
    echo [ERROR] Maven ^(mvn^) not found in PATH.
    exit /b 1
)

echo Running Maven build in %OUTPUT_DIR%
pushd "%OUTPUT_DIR%" >nul
mvn clean package -e
set "EXIT_CODE=%ERRORLEVEL%"
popd >nul

if not "%EXIT_CODE%"=="0" (
    echo.
    echo [ERROR] Maven build failed with exit code %EXIT_CODE%.
    exit /b %EXIT_CODE%
)

echo.
echo [OK] Conversion and Maven build completed: %OUTPUT_DIR%
exit /b 0