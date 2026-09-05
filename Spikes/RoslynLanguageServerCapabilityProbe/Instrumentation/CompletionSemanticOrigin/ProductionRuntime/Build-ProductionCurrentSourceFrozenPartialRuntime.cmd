@echo off
setlocal EnableDelayedExpansion

rem Local one-click defaults for the current SystemExplorer V3 build.
set "ROS_REPO=C:\Temp\roslyn"
set "THIRDPARTY_V2=C:\Temp\Service.ThirdParty_V2.zip"
set "PATCH_0003=C:\Temp\buildpatch\0003-Preserve-current-source-for-frozen-partial-completion.patch"

if "%~1"=="" (
    echo Building SystemExplorer private Roslyn V3 from pinned upstream + 0001 + 0002 + 0003...
    echo.
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCurrentSourceFrozenPartialRuntime.ps1" ^
      -RoslynRepositoryRoot "%ROS_REPO%" ^
      -CurrentServiceThirdPartyZip "%THIRDPARTY_V2%" ^
      -CurrentSourceFrozenPartialPatch "%PATCH_0003%"
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

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCurrentSourceFrozenPartialRuntime.ps1" %*
exit /b %ERRORLEVEL%
