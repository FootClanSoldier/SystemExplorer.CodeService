@echo off
setlocal EnableDelayedExpansion

rem Local one-click defaults for the SystemExplorer private Roslyn V4 build.
set "ROS_REPO=C:\Temp\roslyn"
set "THIRDPARTY_V3=C:\Temp\Service.ThirdParty_V3.zip"
set "PATCH_0004=C:\Temp\buildpatch\0004-Preserve-incremental-reuse-for-current-source-completion.patch"

if "%~1"=="" (
    echo Building SystemExplorer private Roslyn V4 from pinned upstream + 0001 + 0002 + 0003 + 0004...
    echo.
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCompletionIncrementalReuseRuntime.ps1" ^
      -RoslynRepositoryRoot "%ROS_REPO%" ^
      -CurrentServiceThirdPartyZip "%THIRDPARTY_V3%" ^
      -CompletionIncrementalReusePatch "%PATCH_0004%"
    set "EXITCODE=!ERRORLEVEL!"
    echo.
    if not "!EXITCODE!"=="0" (
        echo Build failed with exit code !EXITCODE!.
    ) else (
        echo Build completed successfully.
    )
    echo.
    pause
    exit /b !EXITCODE!
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCompletionIncrementalReuseRuntime.ps1" %*
exit /b %ERRORLEVEL%
