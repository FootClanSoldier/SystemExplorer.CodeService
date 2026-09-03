[CmdletBinding()]
param(
    [string] $RoslynRepositoryRoot,
    [string] $CurrentServiceThirdPartyZip,
    [string] $WorkRoot,
    [string] $OutputRoot,
    [switch] $KeepArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:MSBUILDDISABLENODEREUSE = '1'

$ExpectedCurrentServiceThirdPartyZipSha256 = '8dbdbf20327467ebb39d24c9a57b4093f21de8894d63fafcec9bdc10e0616d1a'
$ExpectedUpstreamCommit = '3aeb96c9ecc56a5ee483558f9e648e33e7bfe756'
$ExpectedDotNetSdkVersion = '11.0.100-preview.6.26359.118'
$ExpectedSemanticReusePatchSha256 = '11076630b66576961cfd3e56120b15c9e95b352e08f3f551053a79a647d2f2be'
$ExpectedSemanticReuseSourceCommit = '405fb7f9860'
$ExpectedLicenseBlob = 'a616ed188dfce68ee308f674350ad242d8588c2b'
$ExpectedThirdPartyNoticesBlob = '1e4323dea600de34692caf3b4b844b7321b03407'
$ExpectedPreparationBaselineDistributionId = 'roslyn-3aeb96c9-systemexplorer-405fb7f9860-win-x64-v1'
$CanonicalSemanticReusePatchEntry = 'ThirdParty/RoslynLanguageServer/patches/0001-Fix-semantic-model-reuse-after-cross-document-semant.patch'
$CanonicalCurrentProvenanceEntry = 'ThirdParty/RoslynLanguageServer/PROVENANCE.txt'
$CanonicalLicenseEntry = 'ThirdParty/RoslynLanguageServer/LICENSE.txt'
$CanonicalThirdPartyNoticesEntry = 'ThirdParty/RoslynLanguageServer/ThirdPartyNotices.rtf'
$CanonicalSemanticOriginPatchName = '0002-Expose-SystemExplorer-completion-semantic-origin.patch'
$CanonicalSemanticOriginPatchRelativePath = 'patches/0002-Expose-SystemExplorer-completion-semantic-origin.patch'
$CanonicalSemanticOriginPatchThirdPartyEntry = 'ThirdParty/RoslynLanguageServer/patches/0002-Expose-SystemExplorer-completion-semantic-origin.patch'
$RuntimeArchiveName = 'roslyn-completion-semantic-origin-server.zip'
$EvidenceFileName = 'CompletionSemanticOriginProductionRuntimeEvidence.json'
$OwnershipMarkerName = '.systemexplorer-completion-semantic-origin-production-owned'
$MaxBuildLogBytes = 256 * 1024
$InvalidArgumentsExitCode = 2
$BuildFailureExitCode = 3

$ScriptRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$CanonicalSemanticOriginPatchPath = [IO.Path]::GetFullPath((Join-Path $ScriptRoot $CanonicalSemanticOriginPatchRelativePath))
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
    Write-Host '  Build-ProductionCompletionSemanticOriginRuntime.cmd -RoslynRepositoryRoot <path> -CurrentServiceThirdPartyZip <path> [-WorkRoot <path>] [-OutputRoot <path>] [-KeepArtifacts]'
    Write-Host ''
    Write-Host 'Environment fallbacks:'
    Write-Host '  SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT'
    Write-Host '  SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP'
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
            Fail-Closed "canonical 0002 contains an unsupported unified-diff hunk header at line $($index + 1): $line"
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
            Fail-Closed "canonical 0002 unified-diff hunk count mismatch at line $($index + 1); declared old/new=$expectedOld/$expectedNew actual=$actualOld/$actualNew"
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
        $ExpectedSemanticReusePatchSha256.ToUpperInvariant()
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
if ([string]::IsNullOrWhiteSpace($RoslynRepositoryRoot) -or [string]::IsNullOrWhiteSpace($CurrentServiceThirdPartyZip)) {
    [Console]::Error.WriteLine('Roslyn repository and current Service.ThirdParty.zip are required via explicit parameters or supported environment variables.')
    Show-Usage
    exit $InvalidArgumentsExitCode
}

try {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail-Closed 'git is not available on PATH.' }
    if (-not (Test-Path -LiteralPath $CanonicalSemanticOriginPatchPath -PathType Leaf)) { Fail-Closed "canonical 0002 patch is missing: $CanonicalSemanticOriginPatchPath" }

    $RoslynRepositoryRoot = Get-NormalizedPath $RoslynRepositoryRoot
    $CurrentServiceThirdPartyZip = Get-NormalizedPath $CurrentServiceThirdPartyZip
    if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
        $WorkRoot = Join-Path ([IO.Path]::GetTempPath()) 'SystemExplorer.CodeService/CompletionSemanticOriginProduction/work'
    }
    $WorkRoot = Get-NormalizedPath $WorkRoot

    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $outputBase = Join-Path ([IO.Path]::GetTempPath()) 'SystemExplorer.CodeService/CompletionSemanticOriginProduction/output'
        $OutputRoot = Join-Path $outputBase ("run-{0:yyyyMMdd_HHmmss_fff}-{1}" -f [DateTimeOffset]::UtcNow, $RunId.Substring(0, 8))
    }
    $FinalOutputRoot = Get-NormalizedPath $OutputRoot

    if (-not (Test-Path -LiteralPath $RoslynRepositoryRoot -PathType Container)) { Fail-Closed "RoslynRepositoryRoot does not exist: $RoslynRepositoryRoot" }
    if (-not (Test-Path -LiteralPath $CurrentServiceThirdPartyZip -PathType Leaf)) { Fail-Closed "CurrentServiceThirdPartyZip does not exist: $CurrentServiceThirdPartyZip" }
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

    $RunRoot = Join-Path $WorkRoot ("run-$RunId")
    $OwnedWorktree = Join-Path $RunRoot 'roslyn-worktree'
    $ThirdPartyInputRoot = Join-Path $RunRoot 'thirdparty-input'
    $RuntimeOutputRoot = Join-Path $RunRoot 'runtime-output'
    $PackageStagingRoot = Join-Path $RunRoot 'package-staging'
    $LogsRoot = Join-Path $RunRoot 'logs'
    $BuildLogPath = Join-Path $LogsRoot 'bounded-build.log'
    $OwnershipMarkerPath = Join-Path $RunRoot $OwnershipMarkerName

    New-Item -ItemType Directory -Force -Path $LogsRoot | Out-Null
    [IO.File]::WriteAllText($OwnershipMarkerPath, $RunId, [Text.UTF8Encoding]::new($false))
    Write-BuildLog "Production runtime build started. RoslynRepositoryRoot=$RoslynRepositoryRoot"
    Write-BuildLog "CurrentServiceThirdPartyZip=$CurrentServiceThirdPartyZip"
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

    $semanticOriginPatchSha256 = Get-FileSha256Lower $CanonicalSemanticOriginPatchPath
    if ([string]::IsNullOrWhiteSpace($semanticOriginPatchSha256) -or $semanticOriginPatchSha256.Length -ne 64) {
        Fail-Closed 'canonical 0002 SHA-256 could not be calculated.'
    }
    $distributionId = "roslyn-3aeb96c9-systemexplorer-$($semanticOriginPatchSha256.Substring(0, 12))-win-x64-v2"
    Write-Host "Canonical 0002 SHA-256: $semanticOriginPatchSha256"
    Write-Host "Production distribution id: $distributionId"

    Assert-UnifiedDiffHunkCounts $CanonicalSemanticOriginPatchPath
    $patchText = [IO.File]::ReadAllText($CanonicalSemanticOriginPatchPath)
    foreach ($requiredText in @(
        'SystemExplorer.CompletionSemanticOrigin',
        'SystemExplorer.CompletionInheritanceDepth',
        '_systemExplorer_completionSemanticOrigin',
        '_systemExplorer_completionInheritanceDepth',
        'symbolList.SelectAsArray(static entry => entry.Symbol)',
        'SystemExplorerCompletionSemanticOrigin.cs')) {
        if (-not $patchText.Contains($requiredText)) { Fail-Closed "canonical 0002 is missing required production content: $requiredText" }
    }
    if ($patchText.Contains('SYSTEMEXPLORER_COMPLETION_SEMANTIC_ORIGIN')) { Fail-Closed 'production 0002 must not depend on the diagnostic environment gate.' }

    $archive = [IO.Compression.ZipFile]::OpenRead($CurrentServiceThirdPartyZip)
    try {
        $provenancePath = Join-Path $ThirdPartyInputRoot $CanonicalCurrentProvenanceEntry
        $semanticReusePatchPath = Join-Path $ThirdPartyInputRoot $CanonicalSemanticReusePatchEntry
        $licensePath = Join-Path $ThirdPartyInputRoot $CanonicalLicenseEntry
        $noticesPath = Join-Path $ThirdPartyInputRoot $CanonicalThirdPartyNoticesEntry
        Copy-ZipEntryExactly $archive $CanonicalCurrentProvenanceEntry $provenancePath
        Copy-ZipEntryExactly $archive $CanonicalSemanticReusePatchEntry $semanticReusePatchPath
        Copy-ZipEntryExactly $archive $CanonicalLicenseEntry $licensePath
        Copy-ZipEntryExactly $archive $CanonicalThirdPartyNoticesEntry $noticesPath
    }
    finally { $archive.Dispose() }

    Verify-CurrentThirdPartyProvenance $provenancePath
    $semanticReusePatchSha256 = Get-FileSha256Lower $semanticReusePatchPath
    if ($semanticReusePatchSha256 -ne $ExpectedSemanticReusePatchSha256) {
        Fail-Closed "canonical 0001 SHA-256 mismatch; expected=$ExpectedSemanticReusePatchSha256 actual=$semanticReusePatchSha256"
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
    [void](Invoke-GitText $OwnedWorktree @('apply', '--check', '--', $CanonicalSemanticOriginPatchPath))
    [void](Invoke-GitText $OwnedWorktree @('apply', '--', $CanonicalSemanticOriginPatchPath))
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
    Copy-Item -LiteralPath $CanonicalSemanticOriginPatchPath -Destination (Join-Path $patchesRoot $CanonicalSemanticOriginPatchName)
    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $thirdPartyRoot 'LICENSE.txt')
    Copy-Item -LiteralPath $noticesPath -Destination (Join-Path $thirdPartyRoot 'ThirdPartyNotices.rtf')
    Copy-DirectoryContents $RuntimeOutputRoot $winX64Root

    $staged0001Hash = Get-FileSha256Lower (Join-Path $patchesRoot '0001-Fix-semantic-model-reuse-after-cross-document-semant.patch')
    $staged0002Hash = Get-FileSha256Lower (Join-Path $patchesRoot $CanonicalSemanticOriginPatchName)
    if ($staged0001Hash -ne $ExpectedSemanticReusePatchSha256) { Fail-Closed 'staged 0001 bytes changed unexpectedly.' }
    if ($staged0002Hash -ne $semanticOriginPatchSha256) { Fail-Closed 'staged 0002 bytes are not byte-identical to the source build-input copy.' }

    $provenanceText = @"
Product:
Microsoft.CodeAnalysis.LanguageServer

Distribution identity:
SystemExplorer patched private Roslyn Language Server win-x64 v2

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

This is a private SystemExplorer production build from the pinned Roslyn source commit plus canonical 0001 and canonical 0002.
It is not the unmodified official roslyn-language-server 5.12.0-1.26426.8 package runtime.
"@
    $finalProvenancePath = Join-Path $thirdPartyRoot 'PROVENANCE.txt'
    [IO.File]::WriteAllText($finalProvenancePath, $provenanceText.TrimStart(), [Text.UTF8Encoding]::new($false))
    $provenanceSha256 = Get-FileSha256Lower $finalProvenancePath

    $newThirdPartyZipPath = Join-Path $FinalOutputRoot 'Service.ThirdParty.zip'
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
            'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.LanguageServer.dll',
            'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.LanguageServer.deps.json',
            'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.LanguageServer.runtimeconfig.json',
            'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.Features.dll',
            'ThirdParty/RoslynLanguageServer/win-x64/Microsoft.CodeAnalysis.LanguageServer.Protocol.dll')) {
            [void](Get-UniqueZipEntry $verifyArchive $requiredEntry)
        }

        if ((Get-ZipEntrySha256Lower $verifyArchive 'ThirdParty/RoslynLanguageServer/patches/0001-Fix-semantic-model-reuse-after-cross-document-semant.patch') -ne $ExpectedSemanticReusePatchSha256) { Fail-Closed 'generated ThirdParty 0001 SHA mismatch.' }
        if ((Get-ZipEntrySha256Lower $verifyArchive $CanonicalSemanticOriginPatchThirdPartyEntry) -ne $semanticOriginPatchSha256) { Fail-Closed 'generated ThirdParty 0002 SHA mismatch.' }
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

    $outputLogsRoot = Join-Path $FinalOutputRoot 'logs'
    New-Item -ItemType Directory -Force -Path $outputLogsRoot | Out-Null
    Copy-Item -LiteralPath $BuildLogPath -Destination (Join-Path $outputLogsRoot 'bounded-build.log')

    Write-Host ''
    Write-Host 'Production runtime build outputs:'
    Write-Host "  Service.ThirdParty.zip: $newThirdPartyZipPath"
    Write-Host "  Evidence: $evidencePath"
    Write-Host "  Runtime archive: $runtimeArchivePath"
    Write-Host "  Build log: $(Join-Path $outputLogsRoot 'bounded-build.log')"
    Write-Host "  Service.ThirdParty.zip SHA-256: $newThirdPartyZipSha256"
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
