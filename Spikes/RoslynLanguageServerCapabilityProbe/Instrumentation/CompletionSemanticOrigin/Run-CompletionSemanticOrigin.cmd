@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-CompletionSemanticOrigin.ps1" %*
exit /b %ERRORLEVEL%
