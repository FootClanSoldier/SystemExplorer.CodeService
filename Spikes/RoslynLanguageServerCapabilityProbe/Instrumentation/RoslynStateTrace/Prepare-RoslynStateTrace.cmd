@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Prepare-RoslynStateTrace.ps1" %*
exit /b %ERRORLEVEL%
