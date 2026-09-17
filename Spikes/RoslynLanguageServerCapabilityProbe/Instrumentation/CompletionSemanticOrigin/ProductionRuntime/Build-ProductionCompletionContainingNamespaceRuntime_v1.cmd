@echo off
setlocal EnableDelayedExpansion

rem One-click defaults for the SystemExplorer private Roslyn V13 containing-namespace build.
set "ROS_REPO=C:\Temp\roslyn"
set "THIRDPARTY_V12=C:\Temp\Service.ThirdParty_V12.zip"
set "PATCH_0013=C:\Temp\0013-Expose-SystemExplorer-completion-containing-namespace.patch"
rem Reuse the established persistent Roslyn SDK/runtime cache from prior builders.
set "DOTNET_CACHE=C:\Temp\SECR4\cache\dotnet"

if "%~1"=="" (
    echo Building SystemExplorer private Roslyn V13 from exact V12 baseline plus canonical 0013...
    echo.
    echo Roslyn repo  : %ROS_REPO%
    echo V12 baseline : %THIRDPARTY_V12%
    echo Patch 0013   : %PATCH_0013%
    echo .NET cache   : %DOTNET_CACHE%
    echo.
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCompletionContainingNamespaceRuntime_v1.ps1" ^
      -RoslynRepositoryRoot "%ROS_REPO%" ^
      -CurrentServiceThirdPartyZip "%THIRDPARTY_V12%" ^
      -CompletionContainingNamespacePatch "%PATCH_0013%" ^
      -DotNetCacheRoot "%DOTNET_CACHE%"
    set "EXITCODE=!ERRORLEVEL!"
    echo.
    if not "!EXITCODE!"=="0" (
        echo Build failed with exit code !EXITCODE!.
    ) else (
        echo Build completed successfully.
        echo The V13 output directory is printed above by the PowerShell builder.
    )
    echo.
    pause
    exit /b !EXITCODE!
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCompletionContainingNamespaceRuntime_v1.ps1" %*
exit /b %ERRORLEVEL%
