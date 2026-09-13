@echo off
setlocal EnableDelayedExpansion

rem One-click defaults for the SystemExplorer private Roslyn V7 receiver-relative semantic-origin build.
set "ROS_REPO=C:\Temp\roslyn"
set "THIRDPARTY_V6=C:\Temp\Service.ThirdParty_V6.zip"
set "PATCH_0007=C:\Temp\buildpatch\0007-Classify-member-completion-relative-to-receiver-type.patch"
rem Reuse the established persistent Roslyn SDK/runtime cache from the V4-V6 builders.
set "DOTNET_CACHE=C:\Temp\SECR4\cache\dotnet"

if "%~1"=="" (
    echo Building SystemExplorer private Roslyn V7 from pinned upstream + 0001 + 0002 + 0003 + 0004 + 0005 + 0006 + 0007...
    echo.
    echo Roslyn repo : %ROS_REPO%
    echo V6 baseline : %THIRDPARTY_V6%
    echo Patch 0007  : %PATCH_0007%
    echo .NET cache  : %DOTNET_CACHE%
    echo.
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionReceiverRelativeSemanticOriginRuntime_v1.ps1" ^
      -RoslynRepositoryRoot "%ROS_REPO%" ^
      -CurrentServiceThirdPartyZip "%THIRDPARTY_V6%" ^
      -ReceiverRelativeSemanticOriginPatch "%PATCH_0007%" ^
      -DotNetCacheRoot "%DOTNET_CACHE%"
    set "EXITCODE=!ERRORLEVEL!"
    echo.
    if not "!EXITCODE!"=="0" (
        echo Build failed with exit code !EXITCODE!.
    ) else (
        echo Build completed successfully.
        echo The V7 output directory is printed above by the PowerShell builder.
    )
    echo.
    pause
    exit /b !EXITCODE!
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionReceiverRelativeSemanticOriginRuntime_v1.ps1" %*
exit /b %ERRORLEVEL%
