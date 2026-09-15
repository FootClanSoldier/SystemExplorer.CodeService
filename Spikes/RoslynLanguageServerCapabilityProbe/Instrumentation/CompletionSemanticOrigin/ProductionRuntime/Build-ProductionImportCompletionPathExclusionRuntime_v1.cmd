@echo off
setlocal EnableDelayedExpansion

rem One-click defaults for the SystemExplorer private Roslyn V10 import-completion path-exclusion build.
set "ROS_REPO=C:\Temp\roslyn"
set "THIRDPARTY_V9=C:\Temp\Service.ThirdParty_V9.zip"
set "SCRIPT_DIR=%~dp0"
set "PATCH_0010=%SCRIPT_DIR%0010-Exclude-configured-source-paths-from-SystemExplorer-import-completion.patch"
if not exist "%PATCH_0010%" set "PATCH_0010=C:\Temp\buildpatch\0010-Exclude-configured-source-paths-from-SystemExplorer-import-completion.patch"
rem Reuse the established persistent Roslyn SDK/runtime cache from prior builders.
set "DOTNET_CACHE=C:\Temp\SECR4\cache\dotnet"

if "%~1"=="" (
    echo Building SystemExplorer private Roslyn V10 from exact V9 baseline plus canonical 0010...
    echo.
    echo Roslyn repo : %ROS_REPO%
    echo V9 baseline : %THIRDPARTY_V9%
    echo Patch 0010  : %PATCH_0010%
    echo .NET cache  : %DOTNET_CACHE%
    echo.
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionImportCompletionPathExclusionRuntime_v1.ps1" ^
      -RoslynRepositoryRoot "%ROS_REPO%" ^
      -CurrentServiceThirdPartyZip "%THIRDPARTY_V9%" ^
      -ImportCompletionPathExclusionPatch "%PATCH_0010%" ^
      -DotNetCacheRoot "%DOTNET_CACHE%"
    set "EXITCODE=!ERRORLEVEL!"
    echo.
    if not "!EXITCODE!"=="0" (
        echo Build failed with exit code !EXITCODE!.
    ) else (
        echo Build completed successfully.
        echo The V10 output directory is printed above by the PowerShell builder.
    )
    echo.
    pause
    exit /b !EXITCODE!
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionImportCompletionPathExclusionRuntime_v1.ps1" %*
exit /b %ERRORLEVEL%
