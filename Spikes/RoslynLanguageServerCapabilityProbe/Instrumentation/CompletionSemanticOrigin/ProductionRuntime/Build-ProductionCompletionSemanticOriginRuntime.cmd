@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCompletionSemanticOriginRuntime.ps1" %*
exit /b %ERRORLEVEL%
