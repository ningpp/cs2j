@echo off
setlocal

for %%I in ("%~dp0.") do set "REPO_DIR=%%~fI"
set "SOURCE_PROJECT=E:\agl-master\GraphLayout\Test\MSAGLTests\MSAGLTests.csproj"
set "MAPPING_CONFIG=%REPO_DIR%\config\TypeMappings.json"

if not "%~1"=="" (
	set "OUTPUT_DIR=%~f1"
) else if defined CSHARP_TO_JAVA_MULTI_OUT (
	set "OUTPUT_DIR=%CSHARP_TO_JAVA_MULTI_OUT%"
) else (
	for /f %%I in ('powershell -NoProfile -Command "(Get-Date).ToString('yyyyMMdd-HHmmss')"') do set "STAMP=%%I"
	set "OUTPUT_DIR=C:\agl\msagltests-multi-%STAMP%"
)

echo Output directory: %OUTPUT_DIR%
dotnet run --project "%REPO_DIR%\src\CSharpToJava.CLI\CSharpToJava.CLI.csproj" -- convert-project -s "%SOURCE_PROJECT%" -d "%OUTPUT_DIR%" -m "%MAPPING_CONFIG%" --mode multi-module --include-tests true

endlocal