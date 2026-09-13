@echo off
setlocal EnableDelayedExpansion

rem One-click defaults for the SystemExplorer private Roslyn V8 named-type receiver semantic-origin build.
set "ROS_REPO=C:\Temp\roslyn"
set "THIRDPARTY_V7=C:\Temp\Service.ThirdParty_V7.zip"
set "PATCH_0008=C:\Temp\buildpatch\0008-Classify-static-member-completion-relative-to-type-receiver.patch"
rem Reuse the established persistent Roslyn SDK/runtime cache from the V4-V7 builders.
set "DOTNET_CACHE=C:\Temp\SECR4\cache\dotnet"

if "%~1"=="" (
    echo Building SystemExplorer private Roslyn V8 from pinned upstream + 0001 + 0002 + 0003 + 0004 + 0005 + 0006 + 0007 + 0008...
    echo.
    echo Roslyn repo : %ROS_REPO%
    echo V7 baseline : %THIRDPARTY_V7%
    echo Patch 0008  : %PATCH_0008%
    echo .NET cache  : %DOTNET_CACHE%
    echo.
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionTypeReceiverSemanticOriginRuntime_v2.ps1" ^
      -RoslynRepositoryRoot "%ROS_REPO%" ^
      -CurrentServiceThirdPartyZip "%THIRDPARTY_V7%" ^
      -TypeReceiverSemanticOriginPatch "%PATCH_0008%" ^
      -DotNetCacheRoot "%DOTNET_CACHE%"
    set "EXITCODE=!ERRORLEVEL!"
    echo.
    if not "!EXITCODE!"=="0" (
        echo Build failed with exit code !EXITCODE!.
    ) else (
        echo Build completed successfully.
        echo The V8 output directory is printed above by the PowerShell builder.
    )
    echo.
    pause
    exit /b !EXITCODE!
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionTypeReceiverSemanticOriginRuntime_v2.ps1" %*
exit /b %ERRORLEVEL%
