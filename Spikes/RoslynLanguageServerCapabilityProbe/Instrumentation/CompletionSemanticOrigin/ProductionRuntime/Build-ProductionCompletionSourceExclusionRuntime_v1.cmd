@echo off
setlocal EnableDelayedExpansion

rem One-click defaults for the SystemExplorer private Roslyn V11 completion-source exclusion build.
set "ROS_REPO=C:\Temp\roslyn"
set "THIRDPARTY_V10=C:\Temp\Service.ThirdParty_V10.zip"
set "SCRIPT_DIR=%~dp0"
set "PATCH_0011=%SCRIPT_DIR%0011-Exclude-configured-source-paths-from-SystemExplorer-completion-source-view.patch"
if not exist "%PATCH_0011%" set "PATCH_0011=C:\Temp\buildpatch\0011-Exclude-configured-source-paths-from-SystemExplorer-completion-source-view.patch"
rem Reuse the established persistent Roslyn SDK/runtime cache from prior builders.
set "DOTNET_CACHE=C:\Temp\SECR4\cache\dotnet"

if "%~1"=="" (
    echo Building SystemExplorer private Roslyn V11 from exact V10 baseline plus canonical 0011...
    echo.
    echo Roslyn repo  : %ROS_REPO%
    echo V10 baseline : %THIRDPARTY_V10%
    echo Patch 0011   : %PATCH_0011%
    echo .NET cache   : %DOTNET_CACHE%
    echo.
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCompletionSourceExclusionRuntime_v1.ps1" ^
      -RoslynRepositoryRoot "%ROS_REPO%" ^
      -CurrentServiceThirdPartyZip "%THIRDPARTY_V10%" ^
      -CompletionSourceExclusionPatch "%PATCH_0011%" ^
      -DotNetCacheRoot "%DOTNET_CACHE%"
    set "EXITCODE=!ERRORLEVEL!"
    echo.
    if not "!EXITCODE!"=="0" (
        echo Build failed with exit code !EXITCODE!.
    ) else (
        echo Build completed successfully.
        echo The V11 output directory is printed above by the PowerShell builder.
    )
    echo.
    pause
    exit /b !EXITCODE!
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCompletionSourceExclusionRuntime_v1.ps1" %*
exit /b %ERRORLEVEL%
