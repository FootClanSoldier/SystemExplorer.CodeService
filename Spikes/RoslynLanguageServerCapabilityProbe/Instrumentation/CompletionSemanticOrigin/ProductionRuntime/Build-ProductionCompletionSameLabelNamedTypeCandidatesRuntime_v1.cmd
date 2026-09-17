@echo off
setlocal EnableDelayedExpansion

rem One-click defaults for the SystemExplorer private Roslyn V14 same-label named-type completion-candidate build.
set "ROS_REPO=C:\Temp\roslyn"
set "THIRDPARTY_V13=C:\Temp\Service.ThirdParty_V13.zip"
set "PATCH_0014=C:\Temp\0014-Preserve-SystemExplorer-same-label-named-type-completion-candidates.patch"
rem Reuse the established persistent Roslyn SDK/runtime cache from prior builders.
set "DOTNET_CACHE=C:\Temp\SECR4\cache\dotnet"

if "%~1"=="" (
    echo Building SystemExplorer private Roslyn V14 from exact V13 baseline plus canonical 0014...
    echo.
    echo Roslyn repo  : %ROS_REPO%
    echo V13 baseline : %THIRDPARTY_V13%
    echo Patch 0014   : %PATCH_0014%
    echo .NET cache   : %DOTNET_CACHE%
    echo.
    powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCompletionSameLabelNamedTypeCandidatesRuntime_v1.ps1" ^
      -RoslynRepositoryRoot "%ROS_REPO%" ^
      -CurrentServiceThirdPartyZip "%THIRDPARTY_V13%" ^
      -CompletionSameLabelNamedTypeCandidatesPatch "%PATCH_0014%" ^
      -DotNetCacheRoot "%DOTNET_CACHE%"
    set "EXITCODE=!ERRORLEVEL!"
    echo.
    if not "!EXITCODE!"=="0" (
        echo Build failed with exit code !EXITCODE!.
    ) else (
        echo Build completed successfully.
        echo The V14 output directory is printed above by the PowerShell builder.
    )
    echo.
    pause
    exit /b !EXITCODE!
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-ProductionCompletionSameLabelNamedTypeCandidatesRuntime_v1.ps1" %*
exit /b %ERRORLEVEL%
