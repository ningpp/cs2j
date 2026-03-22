@echo off
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Invoke-MsaglConversionLoop.ps1" %*
exit /b %errorlevel%