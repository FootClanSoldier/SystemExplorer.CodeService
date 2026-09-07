@echo off
setlocal EnableDelayedExpansion

rem Local one-click defaults for the SystemExplorer private Roslyn V6 build.
set "ROS_REPO=C:\Temp\roslyn"
set "THIRDPARTY_V5=C:\Temp\Service.ThirdParty_V5.zip"
set "PATCH_0006=C:\Temp\buildpatch\0006-Warm-SystemExplorer-import-completion-during-semantic-readiness.patch"
rem Reuse the already-established persistent Roslyn SDK/runtime cache from the V4/V5 builders.
set "DOTNET_CACHE=C:\Temp\SECR4\cache\dotnet"

if "%~1"=="" (
    echo Building SystemExplorer private Roslyn V6 from pinned upstream + 0001 + 0002 + 0003 + 0004 + 0005 + 0006...
    echo.
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionImportCompletionReadinessRuntime_v1.ps1" ^
      -RoslynRepositoryRoot "%ROS_REPO%" ^
      -CurrentServiceThirdPartyZip "%THIRDPARTY_V5%" ^
      -ImportCompletionReadinessPatch "%PATCH_0006%" ^
      -DotNetCacheRoot "%DOTNET_CACHE%"
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

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionImportCompletionReadinessRuntime_v1.ps1" %*
exit /b %ERRORLEVEL%
