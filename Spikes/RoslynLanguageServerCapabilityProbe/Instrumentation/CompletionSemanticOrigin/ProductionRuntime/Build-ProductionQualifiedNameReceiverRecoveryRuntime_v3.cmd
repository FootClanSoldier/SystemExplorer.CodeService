@echo off
setlocal EnableDelayedExpansion

rem One-click defaults for the corrected SystemExplorer private Roslyn V9 qualified-name receiver-recovery build.
set "ROS_REPO=C:\Temp\roslyn"
set "THIRDPARTY_V8=C:\Temp\Service.ThirdParty_V8.zip"
rem Use the corrected canonical 0009 bundled beside this CMD to avoid accidentally rebuilding the superseded CS0136 patch.
set "SCRIPT_DIR=%~dp0"
set "PATCH_0009=%SCRIPT_DIR%0009-Preserve-receiver-relative-completion-through-qualified-name-recovery.patch"
if not exist "%PATCH_0009%" set "PATCH_0009=C:\Temp\buildpatch\0009-Preserve-receiver-relative-completion-through-qualified-name-recovery.patch"
rem Reuse the established persistent Roslyn SDK/runtime cache from the V4-V8 builders.
set "DOTNET_CACHE=C:\Temp\SECR4\cache\dotnet"

if "%~1"=="" (
    echo Building SystemExplorer private Roslyn V9 from pinned upstream + corrected canonical 0001 through 0009...
    echo.
    echo Roslyn repo : %ROS_REPO%
    echo V8 baseline : %THIRDPARTY_V8%
    echo Patch 0009  : %PATCH_0009%
    echo .NET cache  : %DOTNET_CACHE%
    echo.
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionQualifiedNameReceiverRecoveryRuntime_v3.ps1" ^
      -RoslynRepositoryRoot "%ROS_REPO%" ^
      -CurrentServiceThirdPartyZip "%THIRDPARTY_V8%" ^
      -QualifiedNameReceiverRecoveryPatch "%PATCH_0009%" ^
      -DotNetCacheRoot "%DOTNET_CACHE%"
    set "EXITCODE=!ERRORLEVEL!"
    echo.
    if not "!EXITCODE!"=="0" (
        echo Build failed with exit code !EXITCODE!.
    ) else (
        echo Build completed successfully.
        echo The V9 output directory is printed above by the PowerShell builder.
    )
    echo.
    pause
    exit /b !EXITCODE!
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionQualifiedNameReceiverRecoveryRuntime_v3.ps1" %*
exit /b %ERRORLEVEL%
