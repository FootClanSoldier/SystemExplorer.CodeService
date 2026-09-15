@echo off
setlocal EnableDelayedExpansion

rem One-click defaults for the SystemExplorer private Roslyn V12 completion-method-shape build.
set "ROS_REPO=C:\Temp\roslyn"
set "THIRDPARTY_V11=C:\Temp\Service.ThirdParty_V11.zip"
set "PATCH_0012=C:\Temp\0012-Expose-SystemExplorer-completion-method-parameter-shape.patch"
rem Reuse the established persistent Roslyn SDK/runtime cache from prior builders.
set "DOTNET_CACHE=C:\Temp\SECR4\cache\dotnet"

if "%~1"=="" (
    echo Building SystemExplorer private Roslyn V12 from exact V11 baseline plus canonical 0012...
    echo.
    echo Roslyn repo  : %ROS_REPO%
    echo V11 baseline : %THIRDPARTY_V11%
    echo Patch 0012   : %PATCH_0012%
    echo .NET cache   : %DOTNET_CACHE%
    echo.
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCompletionMethodShapeRuntime_v1.ps1" ^
      -RoslynRepositoryRoot "%ROS_REPO%" ^
      -CurrentServiceThirdPartyZip "%THIRDPARTY_V11%" ^
      -CompletionMethodShapePatch "%PATCH_0012%" ^
      -DotNetCacheRoot "%DOTNET_CACHE%"
    set "EXITCODE=!ERRORLEVEL!"
    echo.
    if not "!EXITCODE!"=="0" (
        echo Build failed with exit code !EXITCODE!.
    ) else (
        echo Build completed successfully.
        echo The V12 output directory is printed above by the PowerShell builder.
    )
    echo.
    pause
    exit /b !EXITCODE!
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCompletionMethodShapeRuntime_v1.ps1" %*
exit /b %ERRORLEVEL%
