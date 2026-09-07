@echo off
setlocal EnableDelayedExpansion

rem Local one-click defaults for the SystemExplorer private Roslyn V5 build.
set "ROS_REPO=C:\Temp\roslyn"
set "THIRDPARTY_V4=C:\Temp\Service.ThirdParty_V4.zip"
set "PATCH_0005=C:\Temp\buildpatch\0005-Enable-SystemExplorer-import-completion-with-vs-extensions.patch"
rem Reuse the already-established persistent Roslyn SDK/runtime cache from the V4 builder.
set "DOTNET_CACHE=C:\Temp\SECR4\cache\dotnet"

if "%~1"=="" (
    echo Building SystemExplorer private Roslyn V5 from pinned upstream + 0001 + 0002 + 0003 + 0004 + 0005...
    echo.
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionImportCompletionContractRuntime_v1.ps1" ^
      -RoslynRepositoryRoot "%ROS_REPO%" ^
      -CurrentServiceThirdPartyZip "%THIRDPARTY_V4%" ^
      -ImportCompletionContractPatch "%PATCH_0005%" ^
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

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionImportCompletionContractRuntime_v1.ps1" %*
exit /b %ERRORLEVEL%
