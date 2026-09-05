[CmdletBinding()]
param(
    [string] $RoslynRepositoryRoot,
    [string] $CurrentServiceThirdPartyZip,
    [string] $CurrentSourceFrozenPartialPatch,
    [string] $WorkRoot,
    [string] $OutputRoot,
    [switch] $KeepArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:MSBUILDDISABLENODEREUSE = '1'

$ExpectedCurrentServiceThirdPartyZipSha256 = '45f152e900326520626b5f17248fdf608d7a7e61f01da42b480dce138f5453d8'
$ExpectedUpstreamCommit = '3aeb96c9ecc56a5ee483558f9e648e33e7bfe756'
$ExpectedDotNetSdkVersion = '11.0.100-preview.6.26359.118'
$ExpectedSemanticReusePatchSha256 = '11076630b66576961cfd3e56120b15c9e95b352e08f3f551053a79a647d2f2be'
$ExpectedSemanticReuseSourceCommit = '405fb7f9860'
$ExpectedLicenseBlob = 'a616ed188dfce68ee308f674350ad242d8588c2b'
$ExpectedThirdPartyNoticesBlob = '1e4323dea600de34692caf3b4b844b7321b03407'
$ExpectedCompletionSemanticOriginPatchSha256 = '6818cc1b3a10c97b31782cce20b7590a4a7f1b39710d7b48dd5b234e1b3bc1fb'
$ExpectedCurrentSourceFrozenPartialPatchSha256 = '17827506d20d05b63764c3959a698e35584776fc5c3fb559e70b9b9ffcbdb4e6'
$ExpectedPreparationBaselineDistributionId = 'roslyn-3aeb96c9-systemexplorer-6818cc1b3a10-win-x64-v2'
$CanonicalSemanticReusePatchEntry = 'ThirdParty/RoslynLanguageServer/patches/0001-Fix-semantic-model-reuse-after-cross-document-semant.patch'
$CanonicalCurrentProvenanceEntry = 'ThirdParty/RoslynLanguageServer/PROVENANCE.txt'
$CanonicalLicenseEntry = 'ThirdParty/RoslynLanguageServer/LICENSE.txt'
$CanonicalThirdPartyNoticesEntry = 'ThirdParty/RoslynLanguageServer/ThirdPartyNotices.rtf'
$CanonicalSemanticOriginPatchName = '0002-Expose-SystemExplorer-completion-semantic-origin.patch'
$CanonicalSemanticOriginPatchThirdPartyEntry = 'ThirdParty/RoslynLanguageServer/patches/0002-Expose-SystemExplorer-completion-semantic-origin.patch'
$CanonicalCurrentSourceFrozenPartialPatchName = '0003-Preserve-current-source-for-frozen-partial-completion.patch'
$CanonicalCurrentSourceFrozenPartialPatchThirdPartyEntry = 'ThirdParty/RoslynLanguageServer/patches/0003-Preserve-current-source-for-frozen-partial-completion.patch'
$RuntimeArchiveName = 'roslyn-current-source-frozen-partial-server.zip'
$EvidenceFileName = 'CurrentSourceFrozenPartialProductionRuntimeEvidence.json'
$AdoptionValuesFileName = 'ServiceRuntimeAdoptionValues.txt'
$OwnershipMarkerName = '.systemexplorer-current-source-frozen-partial-production-owned'
$MaxBuildLogBytes = 256 * 1024
$InvalidArgumentsExitCode = 2
$BuildFailureExitCode = 3

$ScriptRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$RunId = [Guid]::NewGuid().ToString('N')
$RunRoot = $null
$OwnedWorktree = $null
$ThirdPartyInputRoot = $null
$RuntimeOutputRoot = $null
$PackageStagingRoot = $null
$OwnershipMarkerPath = $null
$BuildLogPath = $null
$LogsRoot = $null
$FinalOutputRoot = $null
$FinalExitCode = $BuildFailureExitCode
$CleanupFailure = $null
$PathComparison = if ($env:OS -eq 'Windows_NT') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

function Show-Usage {
    Write-Host 'Usage:'
    Write-Host '  Build-ProductionCurrentSourceFrozenPartialRuntime.cmd -RoslynRepositoryRoot <path> -CurrentServiceThirdPartyZip <path> -CurrentSourceFrozenPartialPatch <path> [-WorkRoot <path>] [-OutputRoot <path>] [-KeepArtifacts]'
    Write-Host ''
    Write-Host 'Environment fallbacks:'
    Write-Host '  SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT'
    Write-Host '  SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP'
    Write-Host '  SYSTEMEXPLORER_CURRENT_SOURCE_FROZEN_PARTIAL_PATCH'
}

function Fail-Closed([string] $Message) {
    throw "FAIL CLOSED: $Message"
}

function Get-NormalizedPath([string] $Path) {
    return [IO.Path]::GetFullPath($Path)
}

function Test-IsSameOrChildPath([string] $Path, [string] $Root) {
    $normalizedPath = (Get-NormalizedPath $Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $normalizedRoot = (Get-NormalizedPath $Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ([string]::Equals($normalizedPath, $normalizedRoot, $PathComparison)) { return $true }
    $rootWithSeparator = $normalizedRoot + [IO.Path]::DirectorySeparatorChar
    return $normalizedPath.StartsWith($rootWithSeparator, $PathComparison)
}

function Get-RelativePathUnderRoot([string] $Path, [string] $Root) {
    $normalizedPath = Get-NormalizedPath $Path
    $normalizedRoot = (Get-NormalizedPath $Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-IsSameOrChildPath $normalizedPath $normalizedRoot)) {
        Fail-Closed "path is not under expected root: path=$normalizedPath root=$normalizedRoot"
    }
    if ([string]::Equals($normalizedPath, $normalizedRoot, $PathComparison)) { return '' }
    return $normalizedPath.Substring($normalizedRoot.Length + 1)
}

function Get-FileSha256Lower([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-UnifiedDiffHunkCounts([string] $PatchPath) {
    $lines = [IO.File]::ReadAllLines($PatchPath)
    for ($index = 0; $index -lt $lines.Length; $index++) {
        $line = $lines[$index]
        if (-not $line.StartsWith('@@ ', [StringComparison]::Ordinal)) { continue }

        $match = [Text.RegularExpressions.Regex]::Match(
            $line,
            '^@@ -\d+(?:,(\d+))? \+\d+(?:,(\d+))? @@')
        if (-not $match.Success) {
            Fail-Closed "canonical patch contains an unsupported unified-diff hunk header at line $($index + 1): $line"
        }

        $expectedOld = if ($match.Groups[1].Success) { [int]$match.Groups[1].Value } else { 1 }
        $expectedNew = if ($match.Groups[2].Success) { [int]$match.Groups[2].Value } else { 1 }
        $actualOld = 0
        $actualNew = 0
        $cursor = $index + 1

        while ($cursor -lt $lines.Length) {
            $hunkLine = $lines[$cursor]
            if ($hunkLine.StartsWith('@@ ', [StringComparison]::Ordinal) -or
                $hunkLine.StartsWith('diff --git ', [StringComparison]::Ordinal)) {
                break
            }

            if ($hunkLine.StartsWith(' ', [StringComparison]::Ordinal)) {
                $actualOld++
                $actualNew++
            }
            elseif ($hunkLine.StartsWith('-', [StringComparison]::Ordinal)) {
                $actualOld++
            }
            elseif ($hunkLine.StartsWith('+', [StringComparison]::Ordinal)) {
                $actualNew++
            }
            elseif ($hunkLine.StartsWith('\ No newline at end of file', [StringComparison]::Ordinal)) {
                # Does not contribute to either side's line count.
            }
            else {
                break
            }

            $cursor++
        }

        if ($actualOld -ne $expectedOld -or $actualNew -ne $expectedNew) {
            Fail-Closed "canonical patch unified-diff hunk count mismatch at line $($index + 1); declared old/new=$expectedOld/$expectedNew actual=$actualOld/$actualNew"
        }

        $index = $cursor - 1
    }
}

function Get-StreamSha256Lower($Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash($Stream)
        return ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Write-BuildLog([string] $Message) {
    if ([string]::IsNullOrWhiteSpace($BuildLogPath)) { return }
    $line = "{0:o} {1}{2}" -f [DateTimeOffset]::UtcNow, $Message, [Environment]::NewLine
    [IO.File]::AppendAllText($BuildLogPath, $line, [Text.UTF8Encoding]::new($false))
    $info = [IO.FileInfo]::new($BuildLogPath)
    if ($info.Length -gt $MaxBuildLogBytes) {
        $text = [IO.File]::ReadAllText($BuildLogPath)
        $keepChars = [Math]::Min(131072, $text.Length)
        [IO.File]::WriteAllText(
            $BuildLogPath,
            "[older bounded build log content removed]$([Environment]::NewLine)" + $text.Substring($text.Length - $keepChars),
            [Text.UTF8Encoding]::new($false))
    }
}

function Invoke-GitNoThrow([string] $RepositoryRoot, [string[]] $Arguments) {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $lines = @(& git -C $RepositoryRoot @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $output = (($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
    if (-not [string]::IsNullOrWhiteSpace($output)) { Write-BuildLog "git $($Arguments -join ' '): $output" }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Invoke-GitText([string] $RepositoryRoot, [string[]] $Arguments) {
    $result = Invoke-GitNoThrow $RepositoryRoot $Arguments
    if ($result.ExitCode -ne 0) {
        $bounded = if ($result.Output.Length -le 2048) { $result.Output } else { $result.Output.Substring(0, 2048) + '...' }
        Fail-Closed "git $($Arguments -join ' ') failed with exit code $($result.ExitCode). $bounded"
    }
    return $result.Output
}

function Invoke-NativeLogged(
    [string] $FilePath,
    [string[]] $Arguments,
    [string] $Description,
    [string] $WorkingDirectory = $null) {

    Write-Host $Description
    Write-BuildLog "$Description :: $FilePath $($Arguments -join ' ')"
    $previousErrorActionPreference = $ErrorActionPreference
    $locationPushed = $false
    try {
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            Push-Location -LiteralPath $WorkingDirectory
            $locationPushed = $true
        }

        $ErrorActionPreference = 'Continue'
        & $FilePath @Arguments 2>&1 | ForEach-Object {
            $line = $_.ToString()
            Write-Host $line
            Write-BuildLog $line
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        if ($locationPushed) { Pop-Location }
    }
    Write-BuildLog "$Description exit code: $exitCode"
    if ($exitCode -ne 0) {
        Fail-Closed "$Description failed with exit code $exitCode."
    }
}

function Invoke-NativeText(
    [string] $FilePath,
    [string[]] $Arguments,
    [string] $Description,
    [string] $WorkingDirectory = $null) {

    Write-BuildLog "$Description :: $FilePath $($Arguments -join ' ')"
    $previousErrorActionPreference = $ErrorActionPreference
    $locationPushed = $false
    try {
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            Push-Location -LiteralPath $WorkingDirectory
            $locationPushed = $true
        }

        $ErrorActionPreference = 'Continue'
        $lines = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        if ($locationPushed) { Pop-Location }
    }

    $output = (($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
    if (-not [string]::IsNullOrWhiteSpace($output)) { Write-BuildLog "$Description output: $output" }
    Write-BuildLog "$Description exit code: $exitCode"
    if ($exitCode -ne 0) {
        $bounded = if ($output.Length -le 2048) { $output } else { $output.Substring(0, 2048) + '...' }
        Fail-Closed "$Description failed with exit code $exitCode. $bounded"
    }
    return $output
}

function Resolve-RoslynDotNetHost([string] $WorktreeRoot) {
    $repoLocalDotNet = Join-Path $WorktreeRoot '.dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $repoLocalDotNet -PathType Leaf) {
        return [IO.Path]::GetFullPath($repoLocalDotNet)
    }

    $pathDotNet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $pathDotNet) {
        Fail-Closed "Roslyn restore completed but neither the repository-local .dotnet/dotnet.exe nor a PATH dotnet host is available."
    }

    return [IO.Path]::GetFullPath($pathDotNet.Source)
}


function Resolve-VisualStudioVSTestConsole {
    $candidateInstallRoots = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($env:VSINSTALLDIR) -and (Test-Path -LiteralPath $env:VSINSTALLDIR -PathType Container)) {
        $candidateInstallRoots.Add([IO.Path]::GetFullPath($env:VSINSTALLDIR))
    }

    $vswhereCandidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $vswhereCandidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'))
    }
    $vswhereOnPath = Get-Command vswhere.exe -ErrorAction SilentlyContinue
    if ($null -ne $vswhereOnPath) {
        $vswhereCandidates.Add([IO.Path]::GetFullPath($vswhereOnPath.Source))
    }

    foreach ($vswhere in @($vswhereCandidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { continue }
        $output = Invoke-NativeText $vswhere @('-all', '-products', '*', '-property', 'installationPath') 'Discovering Visual Studio installations with vswhere'
        foreach ($line in ($output -split "`r?`n")) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $trimmed = $line.Trim()
            if (Test-Path -LiteralPath $trimmed -PathType Container) {
                $candidateInstallRoots.Add([IO.Path]::GetFullPath($trimmed))
            }
        }
    }

    $directVSTest = Get-Command vstest.console.exe -ErrorAction SilentlyContinue
    if ($null -ne $directVSTest -and (Test-Path -LiteralPath $directVSTest.Source -PathType Leaf)) {
        $resolved = [IO.Path]::GetFullPath($directVSTest.Source)
        Write-BuildLog "Using PATH Visual Studio TestPlatform: $resolved"
        return $resolved
    }

    foreach ($installRoot in @($candidateInstallRoots | Select-Object -Unique)) {
        foreach ($relative in @(
            'Common7/IDE/Extensions/TestPlatform/vstest.console.exe',
            'Common7/IDE/CommonExtensions/Microsoft/TestWindow/vstest.console.exe')) {
            $candidate = Join-Path $installRoot $relative
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $resolved = [IO.Path]::GetFullPath($candidate)
                Write-BuildLog "Using Visual Studio TestPlatform: $resolved"
                return $resolved
            }
        }
    }

    Fail-Closed 'Visual Studio vstest.console.exe could not be located. Install the Visual Studio Test Platform / Test Tools component, or expose vstest.console.exe on PATH.'
}

function Resolve-CompletionProjectAssetsJson([string] $WorktreeRoot) {
    $candidateRoots = @(
        (Join-Path $WorktreeRoot 'artifacts/obj/Microsoft.CodeAnalysis.CSharp.EditorFeatures.UnitTests'),
        (Join-Path $WorktreeRoot 'src/EditorFeatures/CSharpTest/obj')
    )

    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($root in $candidateRoots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        foreach ($file in @(Get-ChildItem -LiteralPath $root -Filter 'project.assets.json' -File -Recurse)) {
            $fullPath = [IO.Path]::GetFullPath($file.FullName)
            if (-not $candidates.Contains($fullPath)) {
                $candidates.Add($fullPath)
            }
        }
    }

    if ($candidates.Count -eq 0) {
        Fail-Closed "completion test project assets file was not found under Roslyn's artifacts/obj layout or the project-local obj fallback."
    }

    $matching = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in $candidates) {
        try {
            $assets = Get-Content -LiteralPath $candidate -Raw | ConvertFrom-Json
            $hasXunitAdapter = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -like 'xunit.runner.visualstudio/*' }).Count -gt 0
            if ($hasXunitAdapter) {
                $matching.Add($candidate)
            }
        }
        catch {
            Write-BuildLog "Ignoring unreadable project.assets.json candidate '$candidate': $($_.Exception.Message)"
        }
    }

    if ($matching.Count -ne 1) {
        $listed = if ($matching.Count -gt 0) { $matching -join '; ' } else { $candidates -join '; ' }
        Fail-Closed "expected exactly one completion test project assets file containing xunit.runner.visualstudio; found $($matching.Count). Candidates: $listed"
    }

    $resolved = $matching[0]
    Write-BuildLog "Using completion test project assets file: $resolved"
    return $resolved
}

function Resolve-XunitVisualStudioAdapterPath([string] $ProjectAssetsJson) {
    if (-not (Test-Path -LiteralPath $ProjectAssetsJson -PathType Leaf)) {
        Fail-Closed "completion test project assets file is missing: $ProjectAssetsJson"
    }

    $assets = Get-Content -LiteralPath $ProjectAssetsJson -Raw | ConvertFrom-Json
    $libraryProperty = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -like 'xunit.runner.visualstudio/*' } | Select-Object -First 1)
    if ($libraryProperty.Count -ne 1) {
        Fail-Closed 'xunit.runner.visualstudio was not found in the completion test project assets file.'
    }

    $packageIdentity = $libraryProperty[0].Name
    $separatorIndex = $packageIdentity.IndexOf('/')
    if ($separatorIndex -le 0 -or $separatorIndex -ge ($packageIdentity.Length - 1)) {
        Fail-Closed "unexpected xunit.runner.visualstudio package identity: $packageIdentity"
    }

    $packageName = $packageIdentity.Substring(0, $separatorIndex)
    $packageVersion = $packageIdentity.Substring($separatorIndex + 1)
    foreach ($folderProperty in $assets.packageFolders.PSObject.Properties) {
        $packageRoot = Join-Path $folderProperty.Name (Join-Path $packageName $packageVersion)
        if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) { continue }

        $adapter = @(Get-ChildItem -LiteralPath $packageRoot -Filter 'xunit.runner.visualstudio.testadapter.dll' -File -Recurse | Select-Object -First 1)
        if ($adapter.Count -eq 1) {
            $resolved = [IO.Path]::GetFullPath($adapter[0].Directory.FullName)
            Write-BuildLog "Using xunit Visual Studio adapter path: $resolved"
            return $resolved
        }
    }

    Fail-Closed "xunit.runner.visualstudio adapter DLL could not be located for restored package $packageIdentity."
}

function Get-UniqueZipEntry($Archive, [string] $EntryName) {
    $matches = @($Archive.Entries | Where-Object { [string]::Equals($_.FullName, $EntryName, [StringComparison]::Ordinal) })
    if ($matches.Count -ne 1) {
        Fail-Closed "archive must contain exactly one '$EntryName' entry; found $($matches.Count)."
    }
    return $matches[0]
}

function Copy-ZipEntryExactly($Archive, [string] $EntryName, [string] $DestinationPath) {
    $entry = Get-UniqueZipEntry $Archive $EntryName
    $destinationDirectory = Split-Path -Parent $DestinationPath
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    $source = $entry.Open()
    try {
        $destination = [IO.File]::Open($DestinationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $source.CopyTo($destination) }
        finally { $destination.Dispose() }
    }
    finally { $source.Dispose() }
}

function Get-ZipEntrySha256Lower($Archive, [string] $EntryName) {
    $entry = Get-UniqueZipEntry $Archive $EntryName
    $stream = $entry.Open()
    try { return Get-StreamSha256Lower $stream }
    finally { $stream.Dispose() }
}

function Copy-DirectoryContents([string] $SourceRoot, [string] $DestinationRoot) {
    if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        Fail-Closed "source directory is missing: $SourceRoot"
    }
    New-Item -ItemType Directory -Force -Path $DestinationRoot | Out-Null
    $files = @(Get-ChildItem -LiteralPath $SourceRoot -Recurse -File | Sort-Object FullName)
    if ($files.Count -eq 0) { Fail-Closed "source directory contains no files: $SourceRoot" }
    foreach ($file in $files) {
        $relative = Get-RelativePathUnderRoot $file.FullName $SourceRoot
        $destination = Join-Path $DestinationRoot $relative
        $destinationDirectory = Split-Path -Parent $destination
        New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}

function New-ZipFromDirectory([string] $SourceRoot, [string] $DestinationZip) {
    if (Test-Path -LiteralPath $DestinationZip) {
        Fail-Closed "refusing to overwrite existing archive: $DestinationZip"
    }
    $destinationDirectory = Split-Path -Parent $DestinationZip
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    $files = @(Get-ChildItem -LiteralPath $SourceRoot -Recurse -File | Sort-Object FullName)
    if ($files.Count -eq 0) { Fail-Closed "cannot create archive from empty directory: $SourceRoot" }

    $stream = [IO.File]::Open($DestinationZip, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($file in $files) {
                $relative = (Get-RelativePathUnderRoot $file.FullName $SourceRoot).Replace([IO.Path]::DirectorySeparatorChar, '/').Replace([IO.Path]::AltDirectorySeparatorChar, '/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive,
                    $file.FullName,
                    $relative,
                    [IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Verify-CurrentThirdPartyProvenance([string] $Path) {
    $text = [IO.File]::ReadAllText($Path)
    foreach ($required in @(
        "Upstream commit:`r`n$ExpectedUpstreamCommit",
        "Stable distribution id:`r`n$ExpectedPreparationBaselineDistributionId",
        'patches/0001-Fix-semantic-model-reuse-after-cross-document-semant.patch',
        $ExpectedSemanticReusePatchSha256.ToUpperInvariant(),
        'patches/0002-Expose-SystemExplorer-completion-semantic-origin.patch',
        $ExpectedCompletionSemanticOriginPatchSha256.ToUpperInvariant()
    )) {
        if (-not $text.Contains($required)) {
            $lfRequired = $required.Replace("`r`n", "`n")
            if (-not $text.Contains($lfRequired)) {
                Fail-Closed "current ThirdParty provenance is missing expected pinned value: $($required.Replace("`r`n", ' '))"
            }
        }
    }
}

function Test-OwnershipForCleanup {
    if ([string]::IsNullOrWhiteSpace($RunRoot) -or [string]::IsNullOrWhiteSpace($OwnedWorktree) -or [string]::IsNullOrWhiteSpace($OwnershipMarkerPath)) { return $false }
    if (-not (Test-IsSameOrChildPath $OwnedWorktree $RunRoot)) { return $false }
    if (-not (Test-Path -LiteralPath $OwnershipMarkerPath -PathType Leaf)) { return $false }
    $marker = ([IO.File]::ReadAllText($OwnershipMarkerPath)).Trim()
    if (-not [string]::Equals($marker, $RunId, [StringComparison]::Ordinal)) { return $false }
    if (Test-Path -LiteralPath $OwnedWorktree -PathType Container) {
        $top = Invoke-GitNoThrow $OwnedWorktree @('rev-parse', '--show-toplevel')
        if ($top.ExitCode -ne 0) { return $false }
        if (-not [string]::Equals((Get-NormalizedPath $top.Output), (Get-NormalizedPath $OwnedWorktree), $PathComparison)) { return $false }
    }
    return $true
}

function Test-OwnedWorktreeRegistered {
    $list = Invoke-GitNoThrow $RoslynRepositoryRoot @('worktree', 'list', '--porcelain')
    if ($list.ExitCode -ne 0) { throw "Unable to inspect git worktree registration during cleanup: $($list.Output)" }
    foreach ($line in ($list.Output -split '\r?\n')) {
        if (-not $line.StartsWith('worktree ', [StringComparison]::Ordinal)) { continue }
        $candidate = $line.Substring(9)
        try {
            if ([string]::Equals((Get-NormalizedPath $candidate), (Get-NormalizedPath $OwnedWorktree), $PathComparison)) { return $true }
        }
        catch { }
    }
    return $false
}

function Remove-RunnerOwnedPathWithRetry([string] $Path, [string] $Description) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if (-not (Test-IsSameOrChildPath $Path $RunRoot)) { throw "Refusing to remove $Description outside current run root: $Path" }
    $lastError = $null
    foreach ($delayMs in @(0, 250, 750, 1500)) {
        if ($delayMs -gt 0) { Start-Sleep -Milliseconds $delayMs }
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            if (-not (Test-Path -LiteralPath $Path)) { return }
        }
        catch { $lastError = $_.Exception.Message }
    }
    throw "Unable to remove runner-owned $Description after bounded retries: $Path. $lastError"
}

function Remove-OwnedWorktreeSafely {
    if (-not (Test-Path -LiteralPath $OwnedWorktree) -and -not (Test-OwnedWorktreeRegistered)) { return }
    $lastRemove = $null
    foreach ($delayMs in @(0, 500, 1500, 3000)) {
        if ($delayMs -gt 0) { Start-Sleep -Milliseconds $delayMs }
        if (-not (Test-OwnedWorktreeRegistered)) {
            if (Test-Path -LiteralPath $OwnedWorktree) { Remove-RunnerOwnedPathWithRetry $OwnedWorktree 'deregistered Roslyn worktree residue' }
            return
        }
        $lastRemove = Invoke-GitNoThrow $RoslynRepositoryRoot @('worktree', 'remove', '--force', $OwnedWorktree)
        if ($lastRemove.ExitCode -eq 0) {
            if (Test-Path -LiteralPath $OwnedWorktree) { Remove-RunnerOwnedPathWithRetry $OwnedWorktree 'Roslyn worktree residue' }
            if (Test-OwnedWorktreeRegistered) { throw "git worktree remove reported success but the owned worktree remains registered: $OwnedWorktree" }
            return
        }
        if (-not (Test-OwnedWorktreeRegistered)) {
            if (Test-Path -LiteralPath $OwnedWorktree) { Remove-RunnerOwnedPathWithRetry $OwnedWorktree 'deregistered Roslyn worktree residue' }
            return
        }
    }
    $detail = if ($null -eq $lastRemove) { 'no git removal result was produced' } else { "exit code $($lastRemove.ExitCode): $($lastRemove.Output)" }
    throw "git worktree remove --force failed after bounded retries ($detail). Runner-owned state remains at: $OwnedWorktree"
}

function Remove-OwnedTransientState {
    if ($KeepArtifacts) {
        Write-Host "Runner-owned build state retained: $RunRoot"
        Write-BuildLog "KeepArtifacts retained runner-owned state at $RunRoot"
        return
    }
    if (-not (Test-OwnershipForCleanup)) { throw "Safe cleanup ownership verification failed. Runner-owned state remains at: $RunRoot" }
    Remove-OwnedWorktreeSafely
    foreach ($path in @($ThirdPartyInputRoot, $RuntimeOutputRoot, $PackageStagingRoot)) {
        if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path)) {
            Remove-RunnerOwnedPathWithRetry $path 'transient staging path'
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($LogsRoot) -and (Test-Path -LiteralPath $LogsRoot)) {
        Remove-RunnerOwnedPathWithRetry $LogsRoot 'transient log staging'
    }
    Remove-Item -LiteralPath $OwnershipMarkerPath -Force -ErrorAction Stop
    Remove-Item -LiteralPath $RunRoot -Force -ErrorAction Stop
}

if ($env:OS -ne 'Windows_NT') {
    [Console]::Error.WriteLine('This production runtime builder requires Windows because the shipped payload is win-x64.')
    exit $InvalidArgumentsExitCode
}

if ([string]::IsNullOrWhiteSpace($RoslynRepositoryRoot)) { $RoslynRepositoryRoot = $env:SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT }
if ([string]::IsNullOrWhiteSpace($CurrentServiceThirdPartyZip)) { $CurrentServiceThirdPartyZip = $env:SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP }
if ([string]::IsNullOrWhiteSpace($CurrentSourceFrozenPartialPatch)) { $CurrentSourceFrozenPartialPatch = $env:SYSTEMEXPLORER_CURRENT_SOURCE_FROZEN_PARTIAL_PATCH }
if ([string]::IsNullOrWhiteSpace($RoslynRepositoryRoot) -or [string]::IsNullOrWhiteSpace($CurrentServiceThirdPartyZip) -or [string]::IsNullOrWhiteSpace($CurrentSourceFrozenPartialPatch)) {
    [Console]::Error.WriteLine('Roslyn repository, current V2 Service.ThirdParty.zip, and canonical 0003 patch are required via explicit parameters or supported environment variables.')
    Show-Usage
    exit $InvalidArgumentsExitCode
}

try {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail-Closed 'git is not available on PATH.' }

    $RoslynRepositoryRoot = Get-NormalizedPath $RoslynRepositoryRoot
    $CurrentServiceThirdPartyZip = Get-NormalizedPath $CurrentServiceThirdPartyZip
    $CurrentSourceFrozenPartialPatch = Get-NormalizedPath $CurrentSourceFrozenPartialPatch
    $shortBaseRoot = Join-Path $env:SystemDrive 'Temp/SECR3'
    if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
        # Keep the Roslyn worktree deliberately short. The net472 EditorFeatures tests execute
        # under .NET Framework/xUnit, where legacy MAX_PATH behavior can still surface even when
        # the build itself tolerates long paths.
        $WorkRoot = Join-Path $shortBaseRoot 'w'
    }
    $WorkRoot = Get-NormalizedPath $WorkRoot

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $outputBase = Join-Path $shortBaseRoot 'o'
        $OutputRoot = Join-Path $outputBase ("o-{0:yyyyMMdd_HHmmss}-{1}" -f [DateTimeOffset]::UtcNow, $RunId.Substring(0, 8))
    }
    $FinalOutputRoot = Get-NormalizedPath $OutputRoot

    if (-not (Test-Path -LiteralPath $RoslynRepositoryRoot -PathType Container)) { Fail-Closed "RoslynRepositoryRoot does not exist: $RoslynRepositoryRoot" }
    if (-not (Test-Path -LiteralPath $CurrentServiceThirdPartyZip -PathType Leaf)) { Fail-Closed "CurrentServiceThirdPartyZip does not exist: $CurrentServiceThirdPartyZip" }
    if (-not (Test-Path -LiteralPath $CurrentSourceFrozenPartialPatch -PathType Leaf)) { Fail-Closed "CurrentSourceFrozenPartialPatch does not exist: $CurrentSourceFrozenPartialPatch" }
    if (Test-IsSameOrChildPath $WorkRoot $RoslynRepositoryRoot) { Fail-Closed 'WorkRoot must not be inside RoslynRepositoryRoot.' }
    if (Test-IsSameOrChildPath $FinalOutputRoot $RoslynRepositoryRoot) { Fail-Closed 'OutputRoot must not be inside RoslynRepositoryRoot.' }
    if (Test-IsSameOrChildPath $WorkRoot $ScriptRoot) { Fail-Closed 'WorkRoot must not be inside the runtime build-kit directory.' }
    if (Test-IsSameOrChildPath $FinalOutputRoot $ScriptRoot) { Fail-Closed 'OutputRoot must not be inside the runtime build-kit directory.' }

    if (Test-Path -LiteralPath $FinalOutputRoot) {
        $existing = @(Get-ChildItem -LiteralPath $FinalOutputRoot -Force)
        if ($existing.Count -ne 0) { Fail-Closed "OutputRoot must be empty when supplied: $FinalOutputRoot" }
    }
    else {
        New-Item -ItemType Directory -Force -Path $FinalOutputRoot | Out-Null
    }

    $RunRoot = Join-Path $WorkRoot ("r-$($RunId.Substring(0, 12))")
    $OwnedWorktree = Join-Path $RunRoot 'r'
    $ThirdPartyInputRoot = Join-Path $RunRoot 'i'
    $RuntimeOutputRoot = Join-Path $RunRoot 'rt'
    $PackageStagingRoot = Join-Path $RunRoot 'p'
    $LogsRoot = Join-Path $RunRoot 'l'
    $BuildLogPath = Join-Path $LogsRoot 'bounded-build.log'
    $OwnershipMarkerPath = Join-Path $RunRoot $OwnershipMarkerName

    New-Item -ItemType Directory -Force -Path $LogsRoot | Out-Null
    [IO.File]::WriteAllText($OwnershipMarkerPath, $RunId, [Text.UTF8Encoding]::new($false))
    Write-BuildLog "Current-source frozen-partial production runtime build started. RoslynRepositoryRoot=$RoslynRepositoryRoot"
    Write-BuildLog "CurrentServiceThirdPartyZip=$CurrentServiceThirdPartyZip"
    Write-BuildLog "CurrentSourceFrozenPartialPatch=$CurrentSourceFrozenPartialPatch"
    Write-BuildLog "FinalOutputRoot=$FinalOutputRoot"

    $repoTop = Invoke-GitText $RoslynRepositoryRoot @('rev-parse', '--show-toplevel')
    if (-not [string]::Equals((Get-NormalizedPath $repoTop), $RoslynRepositoryRoot, $PathComparison)) {
        Fail-Closed "RoslynRepositoryRoot must name the repository root; actual root=$repoTop"
    }
    [void](Invoke-GitText $RoslynRepositoryRoot @('cat-file', '-e', "$ExpectedUpstreamCommit^{commit}"))
    $licenseBlob = Invoke-GitText $RoslynRepositoryRoot @('rev-parse', "${ExpectedUpstreamCommit}:License.txt")
    if ($licenseBlob -ne $ExpectedLicenseBlob) { Fail-Closed "pinned Roslyn License.txt blob mismatch; expected=$ExpectedLicenseBlob actual=$licenseBlob" }
    $noticesBlob = Invoke-GitText $RoslynRepositoryRoot @('rev-parse', "${ExpectedUpstreamCommit}:src/NuGet/ThirdPartyNotices.rtf")
    if ($noticesBlob -ne $ExpectedThirdPartyNoticesBlob) { Fail-Closed "pinned Roslyn ThirdPartyNotices.rtf blob mismatch; expected=$ExpectedThirdPartyNoticesBlob actual=$noticesBlob" }

    $currentThirdPartyHash = Get-FileSha256Lower $CurrentServiceThirdPartyZip
    if ($currentThirdPartyHash -ne $ExpectedCurrentServiceThirdPartyZipSha256) {
        Fail-Closed "current Service.ThirdParty.zip SHA-256 mismatch; expected=$ExpectedCurrentServiceThirdPartyZipSha256 actual=$currentThirdPartyHash"
    }
    Write-Host "Verified current Service.ThirdParty.zip SHA-256: $currentThirdPartyHash"

    $currentSourceFrozenPartialPatchSha256 = Get-FileSha256Lower $CurrentSourceFrozenPartialPatch
    if ($currentSourceFrozenPartialPatchSha256 -ne $ExpectedCurrentSourceFrozenPartialPatchSha256) {
        Fail-Closed "canonical 0003 SHA-256 mismatch; expected=$ExpectedCurrentSourceFrozenPartialPatchSha256 actual=$currentSourceFrozenPartialPatchSha256"
    }
    $distributionId = "roslyn-3aeb96c9-systemexplorer-$($currentSourceFrozenPartialPatchSha256.Substring(0, 12))-win-x64-v3"
    Write-Host "Canonical 0003 SHA-256: $currentSourceFrozenPartialPatchSha256"
    Write-Host "Production distribution id: $distributionId"

    Assert-UnifiedDiffHunkCounts $CurrentSourceFrozenPartialPatch
    $patchText = [IO.File]::ReadAllText($CurrentSourceFrozenPartialPatch)
    foreach ($requiredText in @(
        'WithFrozenPartialSemanticsForCompletion',
        'WithFrozenPartialCompilationIncludingSpecificDocumentForCompletion',
        'GettingCompletionListUsesCurrentSourceAfterConsecutiveDocumentChanges',
        'GettingCompletionListUsesCurrentSourceFromSameLanguageProjectReference',
        'GettingCompletionListUsesCurrentSourceEditWithoutRunningSourceGenerator')) {
        if (-not $patchText.Contains($requiredText)) { Fail-Closed "canonical 0003 is missing required production/regression content: $requiredText" }
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($CurrentServiceThirdPartyZip)
    try {
        $provenancePath = Join-Path $ThirdPartyInputRoot $CanonicalCurrentProvenanceEntry
        $semanticReusePatchPath = Join-Path $ThirdPartyInputRoot $CanonicalSemanticReusePatchEntry
        $semanticOriginPatchPath = Join-Path $ThirdPartyInputRoot $CanonicalSemanticOriginPatchThirdPartyEntry
        $licensePath = Join-Path $ThirdPartyInputRoot $CanonicalLicenseEntry
        $noticesPath = Join-Path $ThirdPartyInputRoot $CanonicalThirdPartyNoticesEntry
        Copy-ZipEntryExactly $archive $CanonicalCurrentProvenanceEntry $provenancePath
        Copy-ZipEntryExactly $archive $CanonicalSemanticReusePatchEntry $semanticReusePatchPath
        Copy-ZipEntryExactly $archive $CanonicalSemanticOriginPatchThirdPartyEntry $semanticOriginPatchPath
        Copy-ZipEntryExactly $archive $CanonicalLicenseEntry $licensePath
        Copy-ZipEntryExactly $archive $CanonicalThirdPartyNoticesEntry $noticesPath
    }
    finally { $archive.Dispose() }

    Verify-CurrentThirdPartyProvenance $provenancePath
    $semanticReusePatchSha256 = Get-FileSha256Lower $semanticReusePatchPath
    if ($semanticReusePatchSha256 -ne $ExpectedSemanticReusePatchSha256) {
        Fail-Closed "canonical 0001 SHA-256 mismatch; expected=$ExpectedSemanticReusePatchSha256 actual=$semanticReusePatchSha256"
    }
    $semanticOriginPatchSha256 = Get-FileSha256Lower $semanticOriginPatchPath
    if ($semanticOriginPatchSha256 -ne $ExpectedCompletionSemanticOriginPatchSha256) {
        Fail-Closed "canonical 0002 SHA-256 mismatch; expected=$ExpectedCompletionSemanticOriginPatchSha256 actual=$semanticOriginPatchSha256"
    }
    $licenseSha256 = Get-FileSha256Lower $licensePath
    $thirdPartyNoticesSha256 = Get-FileSha256Lower $noticesPath

    Write-Host "Creating runner-owned detached worktree at $ExpectedUpstreamCommit"
    [void](Invoke-GitText $RoslynRepositoryRoot @('worktree', 'add', '--quiet', '--detach', $OwnedWorktree, $ExpectedUpstreamCommit))
    $ownedHead = Invoke-GitText $OwnedWorktree @('rev-parse', 'HEAD')
    if ($ownedHead -ne $ExpectedUpstreamCommit) { Fail-Closed "owned worktree HEAD mismatch; expected=$ExpectedUpstreamCommit actual=$ownedHead" }
    $ownedStatus = Invoke-GitText $OwnedWorktree @('status', '--porcelain')
    if (-not [string]::IsNullOrWhiteSpace($ownedStatus)) { Fail-Closed 'new owned Roslyn worktree was not clean.' }

    $globalJsonPath = Join-Path $OwnedWorktree 'global.json'
    if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) { Fail-Closed "pinned global.json is missing: $globalJsonPath" }
    $globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
    if ($null -eq $globalJson.sdk -or $globalJson.sdk.version -ne $ExpectedDotNetSdkVersion) {
        Fail-Closed "pinned global.json SDK mismatch; expected=$ExpectedDotNetSdkVersion actual=$($globalJson.sdk.version)"
    }
    if ($null -eq $globalJson.tools -or $globalJson.tools.dotnet -ne $ExpectedDotNetSdkVersion) {
        Fail-Closed "pinned global.json tools.dotnet mismatch; expected=$ExpectedDotNetSdkVersion actual=$($globalJson.tools.dotnet)"
    }

    $restoreCommand = Join-Path $OwnedWorktree 'Restore.cmd'
    if (-not (Test-Path -LiteralPath $restoreCommand -PathType Leaf)) { Fail-Closed "pinned Restore.cmd is missing: $restoreCommand" }

    # The pinned Roslyn global.json also declares tools.vs, so eng/build.ps1 defaults to
    # Visual Studio MSBuild on Windows. The production runtime needs only the managed
    # LanguageServer build, and Roslyn explicitly supports -msbuildEngine dotnet. Select
    # that engine so preview SDK tasks execute under the matching .NET MSBuild runtime
    # instead of falling back to the full-framework/net472 task host.
    Invoke-NativeLogged $restoreCommand @(
        '-msbuildEngine', 'dotnet',
        '-nodeReuse:$false'
    ) 'Restoring pinned Roslyn repository with dotnet MSBuild' $OwnedWorktree

    # Restore.cmd launches eng/build.ps1 in a child PowerShell process. Any PATH update made
    # while Roslyn bootstraps its pinned SDK therefore cannot be relied on by this parent
    # builder. Resolve the repository-local host first and verify SDK selection from the
    # worktree before any production build is allowed to continue.
    $roslynDotNetHost = Resolve-RoslynDotNetHost $OwnedWorktree
    $selectedDotNetSdk = Invoke-NativeText $roslynDotNetHost @('--version') 'Verifying Roslyn pinned .NET SDK' $OwnedWorktree
    if ($selectedDotNetSdk -ne $ExpectedDotNetSdkVersion) {
        Fail-Closed "Roslyn dotnet SDK mismatch after restore; expected=$ExpectedDotNetSdkVersion actual=$selectedDotNetSdk host=$roslynDotNetHost"
    }
    Write-Host "Verified Roslyn .NET SDK: $selectedDotNetSdk"
    Write-BuildLog "Roslyn dotnet host: $roslynDotNetHost"

    $postRestoreHead = Invoke-GitText $OwnedWorktree @('rev-parse', 'HEAD')
    $postRestoreStatus = Invoke-GitText $OwnedWorktree @('status', '--porcelain')
    if ($postRestoreHead -ne $ExpectedUpstreamCommit -or -not [string]::IsNullOrWhiteSpace($postRestoreStatus)) {
        Fail-Closed 'repository-native restore changed HEAD or dirtied the runner-owned pristine worktree.'
    }

    [void](Invoke-GitText $OwnedWorktree @('apply', '--check', '--', $semanticReusePatchPath))
    [void](Invoke-GitText $OwnedWorktree @('apply', '--', $semanticReusePatchPath))
    [void](Invoke-GitText $OwnedWorktree @('apply', '--check', '--', $semanticOriginPatchPath))
    [void](Invoke-GitText $OwnedWorktree @('apply', '--', $semanticOriginPatchPath))
    [void](Invoke-GitText $OwnedWorktree @('apply', '--check', '--', $CurrentSourceFrozenPartialPatch))
    [void](Invoke-GitText $OwnedWorktree @('apply', '--', $CurrentSourceFrozenPartialPatch))
    [void](Invoke-GitText $OwnedWorktree @('diff', '--check'))

    $helperPath = Join-Path $OwnedWorktree 'src/Features/Core/Portable/Completion/Providers/SystemExplorerCompletionSemanticOrigin.cs'
    if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) { Fail-Closed 'patched production semantic-origin helper is missing.' }
    $helperText = [IO.File]::ReadAllText($helperPath)
    if ($helperText.Contains('SYSTEMEXPLORER_COMPLETION_SEMANTIC_ORIGIN')) { Fail-Closed 'patched production helper unexpectedly contains the diagnostic environment gate.' }
    foreach ($requiredHelper in @('ILocalSymbol', 'IParameterSymbol', 'IRangeVariableSymbol', 'MethodKind.LocalFunction', 'ReducedFrom', 'OriginalDefinition', 'GetEnclosingSymbol', 'DeclaringSyntaxReferences', 'IsInSource', 'IsInMetadata')) {
        if (-not $helperText.Contains($requiredHelper)) { Fail-Closed "patched production helper is missing classifier authority: $requiredHelper" }
    }

    $normalizedHelperText = $helperText.Replace("`r`n", "`n").TrimEnd([char[]]"`r`n")
    $expectedHelperSuffix = "    private readonly record struct OriginEvidence(string Kind, int? InheritanceDepth);`n}"
    if (-not $normalizedHelperText.EndsWith($expectedHelperSuffix, [StringComparison]::Ordinal)) {
        Fail-Closed 'patched production helper is structurally incomplete; expected final OriginEvidence declaration and closing type brace are missing.'
    }

    $completionServicePath = Join-Path $OwnedWorktree 'src/Features/Core/Portable/Completion/CompletionService_GetCompletions.cs'
    $solutionCompilationStatePath = Join-Path $OwnedWorktree 'src/Workspaces/Core/Portable/Workspace/Solution/SolutionCompilationState.cs'
    if (-not (Test-Path -LiteralPath $completionServicePath -PathType Leaf)) { Fail-Closed 'patched CompletionService_GetCompletions.cs is missing.' }
    if (-not (Test-Path -LiteralPath $solutionCompilationStatePath -PathType Leaf)) { Fail-Closed 'patched SolutionCompilationState.cs is missing.' }
    if (-not ([IO.File]::ReadAllText($completionServicePath)).Contains('WithFrozenPartialSemanticsForCompletion')) { Fail-Closed 'completion service is not routed through the completion-specific frozen-partial API.' }
    if (-not ([IO.File]::ReadAllText($solutionCompilationStatePath)).Contains('WithFrozenPartialCompilationIncludingSpecificDocumentForCompletion')) { Fail-Closed 'completion-specific current-source frozen-partial implementation is missing.' }

    $workspaceCoreTestProject = Join-Path $OwnedWorktree 'src/Workspaces/CoreTest/Microsoft.CodeAnalysis.Workspaces.UnitTests.csproj'
    if (-not (Test-Path -LiteralPath $workspaceCoreTestProject -PathType Leaf)) { Fail-Closed "Workspace Core test project is missing: $workspaceCoreTestProject" }
    Invoke-NativeLogged $roslynDotNetHost @(
        'test', $workspaceCoreTestProject,
        '-c', 'Release',
        '-f', 'net10.0',
        '--no-restore',
        '--disable-build-servers',
        '-p:UseSharedCompilation=false',
        '--filter', 'FullyQualifiedName~WithFrozenPartialSemanticsForCompletionRestoresCurrentProjectDocumentsAfterLegacyFreeze'
    ) 'Running current-source frozen-partial workspace regression test' $OwnedWorktree

    $completionTestProject = Join-Path $OwnedWorktree 'src/EditorFeatures/CSharpTest/Microsoft.CodeAnalysis.CSharp.EditorFeatures.UnitTests.csproj'
    if (-not (Test-Path -LiteralPath $completionTestProject -PathType Leaf)) { Fail-Closed "CSharp completion test project is missing: $completionTestProject" }

    # This project targets net472. The pinned .NET 11 preview SDK's bundled TestHostNetFramework
    # is incomplete on this toolset and aborts before xUnit starts because it cannot probe
    # Microsoft.TestPlatform.PlatformAbstractions. Build the test assembly with the pinned Roslyn SDK,
    # but execute the net472 tests with Visual Studio's complete TestPlatform installation. The xUnit
    # adapter is resolved from this exact restored project's project.assets.json, so no global adapter
    # version is guessed.
    Invoke-NativeLogged $roslynDotNetHost @(
        'build', $completionTestProject,
        '-c', 'Release',
        '--no-restore',
        '--disable-build-servers',
        '-p:UseSharedCompilation=false'
    ) 'Building CSharp completion regression test assembly' $OwnedWorktree

    $completionTestDll = Join-Path $OwnedWorktree 'artifacts/bin/Microsoft.CodeAnalysis.CSharp.EditorFeatures.UnitTests/Release/net472/Microsoft.CodeAnalysis.CSharp.EditorFeatures.UnitTests.dll'
    if (-not (Test-Path -LiteralPath $completionTestDll -PathType Leaf)) { Fail-Closed "CSharp completion regression test assembly is missing after build: $completionTestDll" }

    $completionProjectAssets = Resolve-CompletionProjectAssetsJson $OwnedWorktree
    $xunitAdapterPath = Resolve-XunitVisualStudioAdapterPath $completionProjectAssets
    $visualStudioVSTest = Resolve-VisualStudioVSTestConsole

    $visualStudioTestResults = Join-Path $LogsRoot 'visualstudio-vstest'
    New-Item -ItemType Directory -Force -Path $visualStudioTestResults | Out-Null

    # Keep TestPlatform/xUnit temp and shadow-copy paths short as well. This is intentionally
    # scoped only to the net472 test execution and restored immediately afterwards.
    $visualStudioTestTemp = Join-Path $RunRoot 't'
    New-Item -ItemType Directory -Force -Path $visualStudioTestTemp | Out-Null
    $previousTemp = $env:TEMP
    $previousTmp = $env:TMP
    try {
        $env:TEMP = $visualStudioTestTemp
        $env:TMP = $visualStudioTestTemp
        Write-BuildLog "Visual Studio TestPlatform short-path execution: assemblyLength=$($completionTestDll.Length) temp=$visualStudioTestTemp"
        Invoke-NativeLogged $visualStudioVSTest @(
            $completionTestDll,
            '/Platform:x64',
            "/TestAdapterPath:$xunitAdapterPath",
            '/TestCaseFilter:FullyQualifiedName~Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Completion.CompletionServiceTests',
            "/ResultsDirectory:$visualStudioTestResults",
            '/Logger:trx;LogFileName=CompletionServiceTests.trx'
        ) 'Running completion frozen-partial regression test class through Visual Studio TestPlatform' $OwnedWorktree
    }
    finally {
        $env:TEMP = $previousTemp
        $env:TMP = $previousTmp
    }

    $languageServerProject = Join-Path $OwnedWorktree 'src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Microsoft.CodeAnalysis.LanguageServer.csproj'
    if (-not (Test-Path -LiteralPath $languageServerProject -PathType Leaf)) { Fail-Closed "LanguageServer project is missing: $languageServerProject" }
    Invoke-NativeLogged $roslynDotNetHost @(
        'build', $languageServerProject,
        '-c', 'Release',
        '--no-restore',
        '--disable-build-servers',
        '-p:UseSharedCompilation=false'
    ) 'Building patched Roslyn LanguageServer Release payload with pinned .NET SDK' $OwnedWorktree

    $artifactRoot = Join-Path $OwnedWorktree 'artifacts/bin/Microsoft.CodeAnalysis.LanguageServer/Release'
    if (-not (Test-Path -LiteralPath $artifactRoot -PathType Container)) { Fail-Closed "LanguageServer artifact root is missing: $artifactRoot" }
    $candidateDlls = @(Get-ChildItem -LiteralPath $artifactRoot -Filter 'Microsoft.CodeAnalysis.LanguageServer.dll' -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/]ref[\\/]' })
    $candidateDirectories = @()
    foreach ($candidateDll in $candidateDlls) {
        $dir = $candidateDll.Directory.FullName
        $required = @(
            'Microsoft.CodeAnalysis.LanguageServer.dll',
            'Microsoft.CodeAnalysis.LanguageServer.deps.json',
            'Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json',
            'Microsoft.CodeAnalysis.Features.dll',
            'Microsoft.CodeAnalysis.LanguageServer.Protocol.dll'
        )
        $allPresent = $true
        foreach ($name in $required) {
            if (-not (Test-Path -LiteralPath (Join-Path $dir $name) -PathType Leaf)) { $allPresent = $false; break }
        }
        if ($allPresent) { $candidateDirectories += $dir }
    }
    $candidateDirectories = @($candidateDirectories | Sort-Object -Unique)
    if ($candidateDirectories.Count -ne 1) {
        Fail-Closed "expected exactly one coherent LanguageServer output directory with all required runtime files; found $($candidateDirectories.Count)."
    }
    $coherentOutputDirectory = $candidateDirectories[0]
    Write-BuildLog "Coherent LanguageServer output: $coherentOutputDirectory"

    Copy-DirectoryContents $coherentOutputDirectory $RuntimeOutputRoot
    foreach ($name in @(
        'Microsoft.CodeAnalysis.LanguageServer.dll',
        'Microsoft.CodeAnalysis.LanguageServer.deps.json',
        'Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json',
        'Microsoft.CodeAnalysis.Features.dll',
        'Microsoft.CodeAnalysis.LanguageServer.Protocol.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $RuntimeOutputRoot $name) -PathType Leaf)) { Fail-Closed "coherent runtime copy is missing required file: $name" }
    }

    $languageServerDllSha256 = Get-FileSha256Lower (Join-Path $RuntimeOutputRoot 'Microsoft.CodeAnalysis.LanguageServer.dll')
    $featuresDllSha256 = Get-FileSha256Lower (Join-Path $RuntimeOutputRoot 'Microsoft.CodeAnalysis.Features.dll')
    $languageServerProtocolDllSha256 = Get-FileSha256Lower (Join-Path $RuntimeOutputRoot 'Microsoft.CodeAnalysis.LanguageServer.Protocol.dll')

    $runtimeArchivePath = Join-Path $FinalOutputRoot $RuntimeArchiveName
    New-ZipFromDirectory $RuntimeOutputRoot $runtimeArchivePath
    $runtimeArchiveSha256 = Get-FileSha256Lower $runtimeArchivePath
    $runtimeArchiveSize = ([IO.FileInfo]::new($runtimeArchivePath)).Length

    $thirdPartyRoot = Join-Path $PackageStagingRoot 'ThirdParty/RoslynLanguageServer'
    $patchesRoot = Join-Path $thirdPartyRoot 'patches'
    $winX64Root = Join-Path $thirdPartyRoot 'win-x64'
    New-Item -ItemType Directory -Force -Path $patchesRoot | Out-Null
    Copy-Item -LiteralPath $semanticReusePatchPath -Destination (Join-Path $patchesRoot '0001-Fix-semantic-model-reuse-after-cross-document-semant.patch')
    Copy-Item -LiteralPath $semanticOriginPatchPath -Destination (Join-Path $patchesRoot $CanonicalSemanticOriginPatchName)
    Copy-Item -LiteralPath $CurrentSourceFrozenPartialPatch -Destination (Join-Path $patchesRoot $CanonicalCurrentSourceFrozenPartialPatchName)
    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $thirdPartyRoot 'LICENSE.txt')
    Copy-Item -LiteralPath $noticesPath -Destination (Join-Path $thirdPartyRoot 'ThirdPartyNotices.rtf')
    Copy-DirectoryContents $RuntimeOutputRoot $winX64Root

    $staged0001Hash = Get-FileSha256Lower (Join-Path $patchesRoot '0001-Fix-semantic-model-reuse-after-cross-document-semant.patch')
    $staged0002Hash = Get-FileSha256Lower (Join-Path $patchesRoot $CanonicalSemanticOriginPatchName)
    $staged0003Hash = Get-FileSha256Lower (Join-Path $patchesRoot $CanonicalCurrentSourceFrozenPartialPatchName)
    if ($staged0001Hash -ne $ExpectedSemanticReusePatchSha256) { Fail-Closed 'staged 0001 bytes changed unexpectedly.' }
    if ($staged0002Hash -ne $ExpectedCompletionSemanticOriginPatchSha256) { Fail-Closed 'staged 0002 bytes changed unexpectedly.' }
    if ($staged0003Hash -ne $currentSourceFrozenPartialPatchSha256) { Fail-Closed 'staged 0003 bytes are not byte-identical to the source build-input copy.' }

    $provenanceText = @"
Product:
Microsoft.CodeAnalysis.LanguageServer

Distribution identity:
SystemExplorer patched private Roslyn Language Server win-x64 v3

Stable distribution id:
$distributionId

Upstream repository:
dotnet/roslyn

Upstream commit:
$ExpectedUpstreamCommit

Semantic reuse historical/source commit:
$ExpectedSemanticReuseSourceCommit
Fix semantic model reuse after cross-document semantic changes

Semantic reuse canonical patch:
patches/0001-Fix-semantic-model-reuse-after-cross-document-semant.patch

Semantic reuse canonical patch SHA-256:
$($ExpectedSemanticReusePatchSha256.ToUpperInvariant())

Completion semantic-origin canonical patch:
patches/$CanonicalSemanticOriginPatchName

Completion semantic-origin canonical patch SHA-256:
$($semanticOriginPatchSha256.ToUpperInvariant())

Completion current-source frozen-partial canonical patch:
patches/$CanonicalCurrentSourceFrozenPartialPatchName

Completion current-source frozen-partial canonical patch SHA-256:
$($currentSourceFrozenPartialPatchSha256.ToUpperInvariant())

Canonical production server archive:
$RuntimeArchiveName

Canonical production server archive SHA-256:
$($runtimeArchiveSha256.ToUpperInvariant())

Canonical production server archive size:
$runtimeArchiveSize bytes

Microsoft.CodeAnalysis.LanguageServer.dll SHA-256:
$($languageServerDllSha256.ToUpperInvariant())

Microsoft.CodeAnalysis.Features.dll SHA-256:
$($featuresDllSha256.ToUpperInvariant())

Microsoft.CodeAnalysis.LanguageServer.Protocol.dll SHA-256:
$($languageServerProtocolDllSha256.ToUpperInvariant())

Redistribution license provenance:
LICENSE.txt is preserved byte-for-byte from the verified current Service.ThirdParty.zip and corresponds to pinned Roslyn repository root License.txt (Git blob $ExpectedLicenseBlob).
LICENSE.txt SHA-256:
$($licenseSha256.ToUpperInvariant())

ThirdParty notices provenance:
ThirdPartyNotices.rtf is preserved byte-for-byte from the verified current Service.ThirdParty.zip and corresponds to src/NuGet/ThirdPartyNotices.rtf at the pinned Roslyn commit (Git blob $ExpectedThirdPartyNoticesBlob).
ThirdPartyNotices.rtf SHA-256:
$($thirdPartyNoticesSha256.ToUpperInvariant())

This is a private SystemExplorer production build from the pinned Roslyn source commit plus canonical 0001, canonical 0002, and canonical 0003.
It is not the unmodified official roslyn-language-server 5.12.0-1.26426.8 package runtime.
"@
    $finalProvenancePath = Join-Path $thirdPartyRoot 'PROVENANCE.txt'
    [IO.File]::WriteAllText($finalProvenancePath, $provenanceText.TrimStart(), [Text.UTF8Encoding]::new($false))
    $provenanceSha256 = Get-FileSha256Lower $finalProvenancePath

    $newThirdPartyZipPath = Join-Path $FinalOutputRoot 'Service.ThirdParty_V3.zip'
    New-ZipFromDirectory $PackageStagingRoot $newThirdPartyZipPath
    $newThirdPartyZipSha256 = Get-FileSha256Lower $newThirdPartyZipPath

    $verifyArchive = [IO.Compression.ZipFile]::OpenRead($newThirdPartyZipPath)
    try {
        foreach ($requiredEntry in @(
            'ThirdParty/RoslynLanguageServer/PROVENANCE.txt',
            'ThirdParty/RoslynLanguageServer/LICENSE.txt',
            'ThirdParty/RoslynLanguageServer/ThirdPartyNotices.rtf',
            'ThirdParty/RoslynLanguageServer/patches/0001-Fix-semantic-model-reuse-after-cross-document-semant.patch',
            $CanonicalSemanticOriginPatchThirdPartyEntry,
            $CanonicalCurrentSourceFrozenPartialPatchThirdPartyEntry,
            'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.LanguageServer.dll',
            'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.LanguageServer.deps.json',
            'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json',
            'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.Features.dll',
            'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.LanguageServer.Protocol.dll')) {
            [void](Get-UniqueZipEntry $verifyArchive $requiredEntry)
        }

        if ((Get-ZipEntrySha256Lower $verifyArchive 'ThirdParty/RoslynLanguageServer/patches/0001-Fix-semantic-model-reuse-after-cross-document-semant.patch') -ne $ExpectedSemanticReusePatchSha256) { Fail-Closed 'generated ThirdParty 0001 SHA mismatch.' }
        if ((Get-ZipEntrySha256Lower $verifyArchive $CanonicalSemanticOriginPatchThirdPartyEntry) -ne $ExpectedCompletionSemanticOriginPatchSha256) { Fail-Closed 'generated ThirdParty 0002 SHA mismatch.' }
        if ((Get-ZipEntrySha256Lower $verifyArchive $CanonicalCurrentSourceFrozenPartialPatchThirdPartyEntry) -ne $currentSourceFrozenPartialPatchSha256) { Fail-Closed 'generated ThirdParty 0003 SHA mismatch.' }
        if ((Get-ZipEntrySha256Lower $verifyArchive 'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.LanguageServer.dll') -ne $languageServerDllSha256) { Fail-Closed 'generated ThirdParty LanguageServer DLL SHA mismatch.' }
        if ((Get-ZipEntrySha256Lower $verifyArchive 'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.Features.dll') -ne $featuresDllSha256) { Fail-Closed 'generated ThirdParty Features DLL SHA mismatch.' }
        if ((Get-ZipEntrySha256Lower $verifyArchive 'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.LanguageServer.Protocol.dll') -ne $languageServerProtocolDllSha256) { Fail-Closed 'generated ThirdParty LanguageServer.Protocol DLL SHA mismatch.' }
    }
    finally { $verifyArchive.Dispose() }

    $evidence = [ordered]@{
        schemaVersion = 1
        upstreamRepository = 'dotnet/roslyn'
        upstreamCommit = $ExpectedUpstreamCommit
        semanticReusePatchPath = 'ThirdParty/RoslynLanguageServer/patches/0001-Fix-semantic-model-reuse-after-cross-document-semant.patch'
        semanticReusePatchSha256 = $ExpectedSemanticReusePatchSha256
        completionSemanticOriginPatchPath = $CanonicalSemanticOriginPatchThirdPartyEntry
        completionSemanticOriginPatchSha256 = $semanticOriginPatchSha256
        completionCurrentSourceFrozenPartialPatchPath = $CanonicalCurrentSourceFrozenPartialPatchThirdPartyEntry
        completionCurrentSourceFrozenPartialPatchSha256 = $currentSourceFrozenPartialPatchSha256
        distributionId = $distributionId
        runtimeArchiveName = $RuntimeArchiveName
        runtimeArchiveSha256 = $runtimeArchiveSha256
        runtimeArchiveSize = $runtimeArchiveSize
        languageServerDllSha256 = $languageServerDllSha256
        featuresDllSha256 = $featuresDllSha256
        languageServerProtocolDllSha256 = $languageServerProtocolDllSha256
        provenanceSha256 = $provenanceSha256
        serviceThirdPartyZipSha256 = $newThirdPartyZipSha256
        licenseSha256 = $licenseSha256
        thirdPartyNoticesSha256 = $thirdPartyNoticesSha256
        builtAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    $evidencePath = Join-Path $FinalOutputRoot $EvidenceFileName
    $evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidencePath -Encoding UTF8

    $adoptionValuesPath = Join-Path $FinalOutputRoot $AdoptionValuesFileName
    $adoptionValuesText = @"
DistributionId=$distributionId
CurrentSourceFrozenPartialPatchSha256=$($currentSourceFrozenPartialPatchSha256.ToUpperInvariant())
LanguageServerDllSha256=$($languageServerDllSha256.ToUpperInvariant())
FeaturesDllSha256=$($featuresDllSha256.ToUpperInvariant())
LanguageServerProtocolDllSha256=$($languageServerProtocolDllSha256.ToUpperInvariant())
ServiceThirdPartyV3ZipSha256=$($newThirdPartyZipSha256.ToUpperInvariant())
"@
    [IO.File]::WriteAllText($adoptionValuesPath, $adoptionValuesText.TrimStart(), [Text.UTF8Encoding]::new($false))

    $outputLogsRoot = Join-Path $FinalOutputRoot 'logs'
    New-Item -ItemType Directory -Force -Path $outputLogsRoot | Out-Null
    Copy-Item -LiteralPath $BuildLogPath -Destination (Join-Path $outputLogsRoot 'bounded-build.log')

    Write-Host ''
    Write-Host 'Production runtime build outputs:'
    Write-Host "  Service.ThirdParty_V3.zip: $newThirdPartyZipPath"
    Write-Host "  Evidence: $evidencePath"
    Write-Host "  Service adoption values: $adoptionValuesPath"
    Write-Host "  Runtime archive: $runtimeArchivePath"
    Write-Host "  Build log: $(Join-Path $outputLogsRoot 'bounded-build.log')"
    Write-Host "  Service.ThirdParty_V3.zip SHA-256: $newThirdPartyZipSha256"
    Write-Host "  Distribution id: $distributionId"
    $FinalExitCode = 0
}
catch {
    $message = $_.Exception.Message
    [Console]::Error.WriteLine($message)
    if (-not [string]::IsNullOrWhiteSpace($BuildLogPath)) {
        try { Write-BuildLog "Build failure: $message" } catch { }
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($RunRoot) -and (Test-Path -LiteralPath $RunRoot -PathType Container)) {
        try {
            Remove-OwnedTransientState
        }
        catch {
            $CleanupFailure = $_.Exception.Message
            [Console]::Error.WriteLine("Cleanup failure: $CleanupFailure")
            if (-not [string]::IsNullOrWhiteSpace($BuildLogPath)) {
                try { Write-BuildLog "Cleanup failure: $CleanupFailure" } catch { }
            }
            Write-Host "Runner-owned state retained for safe inspection: $RunRoot"
            if ($FinalExitCode -eq 0) { $FinalExitCode = $BuildFailureExitCode }
        }
    }
}

exit $FinalExitCode
