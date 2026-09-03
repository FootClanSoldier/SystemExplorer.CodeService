[CmdletBinding()]
param(
    [string] $RoslynRepositoryRoot,
    [string] $ServiceThirdPartyZip,
    [string] $WorkRoot,
    [string] $ReportPath,
    [switch] $KeepArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# This script already runs in a dedicated child PowerShell process. Prevent
# persistent MSBuild worker nodes from retaining handles into runner-owned temp
# state without changing any machine/user setting.
$env:MSBUILDDISABLENODEREUSE = '1'

$ExpectedCurrentServiceThirdPartyZipSha256 = '45f152e900326520626b5f17248fdf608d7a7e61f01da42b480dce138f5453d8'
$ExpectedCommit = '3aeb96c9ecc56a5ee483558f9e648e33e7bfe756'
$ExpectedSemanticReusePatchSha256 = '11076630b66576961cfd3e56120b15c9e95b352e08f3f551053a79a647d2f2be'
$ExpectedCompletionSemanticOriginPatchSha256 = '6818cc1b3a10c97b31782cce20b7590a4a7f1b39710d7b48dd5b234e1b3bc1fb'
$ExpectedCurrentProductionDistributionId = 'roslyn-3aeb96c9-systemexplorer-6818cc1b3a10-win-x64-v2'
$ExpectedPreparationBaselineDistributionId = 'roslyn-3aeb96c9-systemexplorer-405fb7f9860-win-x64-v1'
$ExpectedInstrumentationVersion = 1
$CanonicalProvenanceEntry = 'ThirdParty/RoslynLanguageServer/PROVENANCE.txt'
$CanonicalSemanticReusePatchEntry = 'ThirdParty/RoslynLanguageServer/patches/0001-Fix-semantic-model-reuse-after-cross-document-semant.patch'
$CanonicalCompletionSemanticOriginPatchEntry = 'ThirdParty/RoslynLanguageServer/patches/0002-Expose-SystemExplorer-completion-semantic-origin.patch'
$OwnershipMarkerName = '.systemexplorer-completion-semantic-origin-owned'
$InvalidArgumentsExitCode = 2
$ServerSetupFailureExitCode = 3
$InfrastructureFailureExitCode = 4
$MaxRunnerLogBytes = 64 * 1024

$ScriptRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$ProbeRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $ScriptRoot)))
$CodeServiceRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $ProbeRoot)))
$PrepareScript = Join-Path $ScriptRoot 'Prepare-CompletionSemanticOrigin.ps1'
$ProbeProject = Join-Path $ProbeRoot 'RoslynLanguageServerCapabilityProbe.csproj'
$RunId = [Guid]::NewGuid().ToString('N')
$RunRoot = $null
$OwnedWorktree = $null
$ThirdPartyInputRoot = $null
$InstrumentationOutput = $null
$ProbeBuildArtifacts = $null
$OwnershipMarkerPath = $null
$RunnerLogPath = $null
$FinalExitCode = $ServerSetupFailureExitCode
$ProbeWasInvoked = $false
$CleanupFailure = $null

$PathComparison = if ($env:OS -eq 'Windows_NT') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

function Show-Usage {
    Write-Host 'Usage:'
    Write-Host '  Run-CompletionSemanticOrigin.cmd -RoslynRepositoryRoot <path> -ServiceThirdPartyZip <path> [-WorkRoot <path>] [-ReportPath <path>] [-KeepArtifacts]'
    Write-Host ''
    Write-Host 'Environment fallbacks:'
    Write-Host '  SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT'
    Write-Host '  SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP'
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

function Fail-Setup([string] $Message) {
    throw "FAIL CLOSED: $Message"
}

function Write-RunnerLog([string] $Message) {
    if ([string]::IsNullOrWhiteSpace($RunnerLogPath)) { return }
    $line = "{0:o} {1}{2}" -f [DateTimeOffset]::UtcNow, $Message, [Environment]::NewLine
    [IO.File]::AppendAllText($RunnerLogPath, $line, [Text.UTF8Encoding]::new($false))
    $info = [IO.FileInfo]::new($RunnerLogPath)
    if ($info.Length -gt $MaxRunnerLogBytes) {
        $text = [IO.File]::ReadAllText($RunnerLogPath)
        $keepChars = [Math]::Min(32768, $text.Length)
        [IO.File]::WriteAllText(
            $RunnerLogPath,
            "[older bounded runner log content removed]$([Environment]::NewLine)" + $text.Substring($text.Length - $keepChars),
            [Text.UTF8Encoding]::new($false))
    }
}

function Invoke-GitNoThrow([string] $RepositoryRoot, [string[]] $Arguments) {
    # Windows PowerShell can turn normal native stderr (for example Git progress
    # messages) into a terminating NativeCommandError while ErrorActionPreference
    # is Stop. Native Git success/failure is defined by its process exit code, so
    # capture merged output under Continue and restore the caller preference.
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
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Invoke-GitText([string] $RepositoryRoot, [string[]] $Arguments) {
    $result = Invoke-GitNoThrow $RepositoryRoot $Arguments
    if ($result.ExitCode -ne 0) {
        $bounded = if ($result.Output.Length -le 2048) { $result.Output } else { $result.Output.Substring(0, 2048) + '...' }
        Fail-Setup "git $($Arguments -join ' ') failed with exit code $($result.ExitCode). $bounded"
    }
    return $result.Output
}

function Copy-ZipEntryExactly($Archive, [string] $EntryName, [string] $DestinationPath) {
    $matches = @($Archive.Entries | Where-Object { [string]::Equals($_.FullName, $EntryName, [StringComparison]::Ordinal) })
    if ($matches.Count -ne 1) {
        Fail-Setup "Service.ThirdParty.zip must contain exactly one '$EntryName' entry; found $($matches.Count)."
    }

    $destinationDirectory = Split-Path -Parent $DestinationPath
    New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
    $source = $matches[0].Open()
    try {
        $destination = [IO.File]::Open($DestinationPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $source.CopyTo($destination) }
        finally { $destination.Dispose() }
    }
    finally { $source.Dispose() }
}

function Get-ZipEntrySha256Lower($Archive, [string] $EntryName) {
    $matches = @($Archive.Entries | Where-Object { [string]::Equals($_.FullName, $EntryName, [StringComparison]::Ordinal) })
    if ($matches.Count -ne 1) {
        Fail-Setup "Service.ThirdParty.zip must contain exactly one '$EntryName' entry; found $($matches.Count)."
    }

    $stream = $matches[0].Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash($stream)
        return ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Verify-ThirdPartyProvenance([string] $ProvenancePath) {
    $text = [IO.File]::ReadAllText($ProvenancePath)
    foreach ($required in @(
        "Upstream commit:`r`n$ExpectedCommit",
        "Stable distribution id:`r`n$ExpectedCurrentProductionDistributionId",
        'Semantic reuse canonical patch:',
        'patches/0001-Fix-semantic-model-reuse-after-cross-document-semant.patch',
        'Semantic reuse canonical patch SHA-256:',
        $ExpectedSemanticReusePatchSha256.ToUpperInvariant(),
        'Completion semantic-origin canonical patch:',
        'patches/0002-Expose-SystemExplorer-completion-semantic-origin.patch',
        'Completion semantic-origin canonical patch SHA-256:',
        $ExpectedCompletionSemanticOriginPatchSha256.ToUpperInvariant()
    )) {
        if (-not $text.Contains($required)) {
            $lfRequired = $required.Replace("`r`n", "`n")
            if (-not $text.Contains($lfRequired)) {
                $displayRequired = $required.Replace("`r`n", ' ')
                Fail-Setup "Extracted ThirdParty provenance did not contain expected pinned value: $displayRequired"
            }
        }
    }
}

function Verify-GeneratedProvenance([string] $ProvenancePath, [string] $ExpectedOutputRoot) {
    if (-not (Test-Path -LiteralPath $ProvenancePath -PathType Leaf)) {
        Fail-Setup "generated semantic-origin provenance is missing: $ProvenancePath"
    }

    try { $provenance = Get-Content -LiteralPath $ProvenancePath -Raw | ConvertFrom-Json }
    catch { Fail-Setup "generated semantic-origin provenance is not valid JSON: $($_.Exception.Message)" }

    if ([int]$provenance.schemaVersion -ne 1) {
        Fail-Setup "generated provenance schemaVersion mismatch; expected=1 actual=$($provenance.schemaVersion)"
    }
    if ([int]$provenance.instrumentationVersion -ne $ExpectedInstrumentationVersion) {
        Fail-Setup "generated instrumentationVersion mismatch; expected=$ExpectedInstrumentationVersion actual=$($provenance.instrumentationVersion)"
    }
    if (-not [string]::Equals([string]$provenance.repository, 'dotnet/roslyn', [StringComparison]::Ordinal)) {
        Fail-Setup "generated repository mismatch; expected=dotnet/roslyn actual=$($provenance.repository)"
    }
    if (-not [string]::Equals([string]$provenance.baseCommit, $ExpectedCommit, [StringComparison]::Ordinal)) {
        Fail-Setup "generated baseCommit mismatch; expected=$ExpectedCommit actual=$($provenance.baseCommit)"
    }
    if (-not [string]::Equals([string]$provenance.canonicalSystemExplorerPatchSha256, $ExpectedSemanticReusePatchSha256, [StringComparison]::OrdinalIgnoreCase)) {
        Fail-Setup 'generated canonical patch SHA-256 mismatch.'
    }
    if (-not [string]::Equals([string]$provenance.baselineDistributionId, $ExpectedPreparationBaselineDistributionId, [StringComparison]::Ordinal)) {
        Fail-Setup "generated baseline distribution mismatch; expected=$ExpectedPreparationBaselineDistributionId actual=$($provenance.baselineDistributionId)"
    }

    $serverCommandPath = [string]$provenance.serverCommandPath
    if ([string]::IsNullOrWhiteSpace($serverCommandPath) -or -not [IO.Path]::IsPathRooted($serverCommandPath)) {
        Fail-Setup 'generated serverCommandPath must be absolute.'
    }
    $serverCommandPath = Get-NormalizedPath $serverCommandPath
    if (-not (Test-IsSameOrChildPath $serverCommandPath $ExpectedOutputRoot)) {
        Fail-Setup "generated serverCommandPath must lie under current instrumentation output: $serverCommandPath"
    }
    if (-not (Test-Path -LiteralPath $serverCommandPath -PathType Leaf)) {
        Fail-Setup "generated semantic-origin server command does not exist: $serverCommandPath"
    }

    return $serverCommandPath
}

function Test-OwnershipForCleanup {
    if ([string]::IsNullOrWhiteSpace($RunRoot) -or [string]::IsNullOrWhiteSpace($OwnedWorktree) -or [string]::IsNullOrWhiteSpace($OwnershipMarkerPath)) {
        return $false
    }
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
    if ($list.ExitCode -ne 0) {
        throw "Unable to inspect git worktree registration during cleanup: $($list.Output)"
    }

    foreach ($line in ($list.Output -split '\r?\n')) {
        if (-not $line.StartsWith('worktree ', [StringComparison]::Ordinal)) { continue }
        $candidate = $line.Substring(9)
        try {
            if ([string]::Equals((Get-NormalizedPath $candidate), (Get-NormalizedPath $OwnedWorktree), $PathComparison)) {
                return $true
            }
        }
        catch { }
    }

    return $false
}

function Remove-RunnerOwnedPathWithRetry([string] $Path, [string] $Description) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if (-not (Test-IsSameOrChildPath $Path $RunRoot)) {
        throw "Refusing to remove $Description outside current run root: $Path"
    }

    $lastError = $null
    foreach ($delayMs in @(0, 250, 750, 1500)) {
        if ($delayMs -gt 0) { Start-Sleep -Milliseconds $delayMs }
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            if (-not (Test-Path -LiteralPath $Path)) { return }
        }
        catch {
            $lastError = $_.Exception.Message
        }
    }

    throw "Unable to remove runner-owned $Description after bounded retries: $Path. $lastError"
}

function Remove-OwnedWorktreeSafely {
    if (-not (Test-Path -LiteralPath $OwnedWorktree) -and -not (Test-OwnedWorktreeRegistered)) {
        return
    }

    $lastRemove = $null
    foreach ($delayMs in @(0, 500, 1500, 3000)) {
        if ($delayMs -gt 0) { Start-Sleep -Milliseconds $delayMs }

        if (-not (Test-OwnedWorktreeRegistered)) {
            if (Test-Path -LiteralPath $OwnedWorktree) {
                Remove-RunnerOwnedPathWithRetry $OwnedWorktree 'deregistered Roslyn worktree residue'
            }
            return
        }

        $lastRemove = Invoke-GitNoThrow $RoslynRepositoryRoot @('worktree', 'remove', '--force', $OwnedWorktree)
        if ($lastRemove.ExitCode -eq 0) {
            if (Test-Path -LiteralPath $OwnedWorktree) {
                Remove-RunnerOwnedPathWithRetry $OwnedWorktree 'Roslyn worktree residue'
            }
            if (Test-OwnedWorktreeRegistered) {
                throw "git worktree remove reported success but the owned worktree remains registered: $OwnedWorktree"
            }
            return
        }

        # Git for Windows can fail directory deletion transiently when a just-finished
        # build process still has a handle open. If Git already deregistered the
        # worktree, ownership verification allows us to remove only the residual
        # runner-owned directory ourselves. Otherwise retry the exact git removal.
        if (-not (Test-OwnedWorktreeRegistered)) {
            if (Test-Path -LiteralPath $OwnedWorktree) {
                Remove-RunnerOwnedPathWithRetry $OwnedWorktree 'deregistered Roslyn worktree residue'
            }
            return
        }
    }

    $detail = if ($null -eq $lastRemove) { 'no git removal result was produced' } else { "exit code $($lastRemove.ExitCode): $($lastRemove.Output)" }
    throw "git worktree remove --force failed after bounded retries ($detail). Runner-owned state remains at: $OwnedWorktree"
}

function Remove-OwnedTransientState {
    if ($KeepArtifacts) {
        Write-Host "Artifacts retained: $RunRoot"
        Write-RunnerLog "KeepArtifacts retained runner-owned state at $RunRoot"
        return
    }

    if (-not (Test-OwnershipForCleanup)) {
        throw "Safe cleanup ownership verification failed. Runner-owned state remains at: $RunRoot"
    }

    Remove-OwnedWorktreeSafely

    foreach ($path in @($ThirdPartyInputRoot, $InstrumentationOutput, $ProbeBuildArtifacts)) {
        if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path)) {
            Remove-RunnerOwnedPathWithRetry $path 'transient staging path'
        }
    }

    Remove-Item -LiteralPath $OwnershipMarkerPath -Force -ErrorAction SilentlyContinue
    Write-RunnerLog 'Owned Roslyn worktree and transient staging were removed safely.'
}

if ([string]::IsNullOrWhiteSpace($RoslynRepositoryRoot)) {
    $RoslynRepositoryRoot = $env:SYSTEMEXPLORER_ROSLYN_REPOSITORY_ROOT
}
if ([string]::IsNullOrWhiteSpace($ServiceThirdPartyZip)) {
    $ServiceThirdPartyZip = $env:SYSTEMEXPLORER_SERVICE_THIRDPARTY_ZIP
}
if ([string]::IsNullOrWhiteSpace($RoslynRepositoryRoot) -or [string]::IsNullOrWhiteSpace($ServiceThirdPartyZip)) {
    [Console]::Error.WriteLine('Roslyn repository and Service.ThirdParty.zip are required via explicit parameters or supported environment variables.')
    Show-Usage
    exit $InvalidArgumentsExitCode
}

try {
    $RoslynRepositoryRoot = Get-NormalizedPath $RoslynRepositoryRoot
    $ServiceThirdPartyZip = Get-NormalizedPath $ServiceThirdPartyZip
    if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
        $WorkRoot = Join-Path ([IO.Path]::GetTempPath()) 'SystemExplorer.CodeService/CompletionSemanticOrigin'
    }
    $WorkRoot = Get-NormalizedPath $WorkRoot

    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        $reportDirectory = Join-Path ([IO.Path]::GetTempPath()) 'SystemExplorer.CodeService/RoslynProbe'
        $ReportPath = Join-Path $reportDirectory ("completion_semantic_origin_{0:yyyyMMdd_HHmmss_fff}_{1}.json" -f [DateTimeOffset]::UtcNow, $RunId.Substring(0, 8))
    }
    $ReportPath = Get-NormalizedPath $ReportPath

    if (-not (Test-Path -LiteralPath $RoslynRepositoryRoot -PathType Container)) {
        Fail-Setup "RoslynRepositoryRoot does not exist: $RoslynRepositoryRoot"
    }
    if (-not (Test-Path -LiteralPath $ServiceThirdPartyZip -PathType Leaf)) {
        Fail-Setup "ServiceThirdPartyZip does not exist: $ServiceThirdPartyZip"
    }
    if (-not (Test-Path -LiteralPath $PrepareScript -PathType Leaf)) {
        Fail-Setup "low-level preparation script is missing: $PrepareScript"
    }
    if (-not (Test-Path -LiteralPath $ProbeProject -PathType Leaf)) {
        Fail-Setup "capability probe project is missing: $ProbeProject"
    }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail-Setup 'git is not available on PATH.' }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail-Setup 'dotnet is not available on PATH.' }

    if (Test-IsSameOrChildPath $WorkRoot $CodeServiceRoot) {
        Fail-Setup "WorkRoot must be outside the SystemExplorer.CodeService source tree: $CodeServiceRoot"
    }
    if (Test-IsSameOrChildPath $WorkRoot $RoslynRepositoryRoot) {
        Fail-Setup 'WorkRoot must not be inside RoslynRepositoryRoot; the source repository working tree must remain unmodified.'
    }
    if (Test-IsSameOrChildPath $ReportPath $CodeServiceRoot) {
        Fail-Setup 'ReportPath must be outside the SystemExplorer.CodeService source tree.'
    }
    if (Test-IsSameOrChildPath $ReportPath $RoslynRepositoryRoot) {
        Fail-Setup 'ReportPath must be outside RoslynRepositoryRoot.'
    }

    $RunRoot = Join-Path $WorkRoot ("run-$RunId")
    $OwnedWorktree = Join-Path $RunRoot 'roslyn-worktree'
    $ThirdPartyInputRoot = Join-Path $RunRoot 'thirdparty-input'
    $InstrumentationOutput = Join-Path $RunRoot 'instrumentation-output'
    $ProbeBuildArtifacts = Join-Path $RunRoot 'probe-build-artifacts'
    $LogsRoot = Join-Path $RunRoot 'logs'
    $RunnerLogPath = Join-Path $LogsRoot 'runner.log'
    $OwnershipMarkerPath = Join-Path $RunRoot $OwnershipMarkerName

    New-Item -ItemType Directory -Force -Path $LogsRoot | Out-Null
    [IO.File]::WriteAllText($OwnershipMarkerPath, $RunId, [Text.UTF8Encoding]::new($false))
    Write-RunnerLog "Run started. RoslynRepositoryRoot=$RoslynRepositoryRoot"
    Write-RunnerLog "ServiceThirdPartyZip=$ServiceThirdPartyZip"
    Write-RunnerLog "ReportPath=$ReportPath"

    $repoTop = Invoke-GitText $RoslynRepositoryRoot @('rev-parse', '--show-toplevel')
    if (-not [string]::Equals((Get-NormalizedPath $repoTop), $RoslynRepositoryRoot, $PathComparison)) {
        Fail-Setup "RoslynRepositoryRoot must name the repository root; actual root=$repoTop"
    }
    [void](Invoke-GitText $RoslynRepositoryRoot @('cat-file', '-e', "$ExpectedCommit^{commit}"))

    $archiveHash = (Get-FileHash -LiteralPath $ServiceThirdPartyZip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($archiveHash -ne $ExpectedCurrentServiceThirdPartyZipSha256) {
        Fail-Setup "Service.ThirdParty.zip SHA-256 mismatch; expected=$ExpectedCurrentServiceThirdPartyZipSha256 actual=$archiveHash"
    }
    Write-Host "Verified Service.ThirdParty.zip SHA-256: $archiveHash"
    Write-RunnerLog "Verified Service.ThirdParty.zip SHA-256: $archiveHash"

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ServiceThirdPartyZip)
    try {
        $semanticReusePatchHash = Get-ZipEntrySha256Lower $archive $CanonicalSemanticReusePatchEntry
        if ($semanticReusePatchHash -ne $ExpectedSemanticReusePatchSha256) {
            Fail-Setup "canonical semantic-reuse patch SHA-256 mismatch; expected=$ExpectedSemanticReusePatchSha256 actual=$semanticReusePatchHash"
        }

        $semanticOriginPatchHash = Get-ZipEntrySha256Lower $archive $CanonicalCompletionSemanticOriginPatchEntry
        if ($semanticOriginPatchHash -ne $ExpectedCompletionSemanticOriginPatchSha256) {
            Fail-Setup "canonical completion semantic-origin patch SHA-256 mismatch; expected=$ExpectedCompletionSemanticOriginPatchSha256 actual=$semanticOriginPatchHash"
        }

        $provenanceDestination = Join-Path $ThirdPartyInputRoot $CanonicalProvenanceEntry
        $patchDestination = Join-Path $ThirdPartyInputRoot $CanonicalSemanticReusePatchEntry
        Copy-ZipEntryExactly $archive $CanonicalProvenanceEntry $provenanceDestination
        Copy-ZipEntryExactly $archive $CanonicalSemanticReusePatchEntry $patchDestination
    }
    finally { $archive.Dispose() }

    Verify-ThirdPartyProvenance $provenanceDestination
    $extractedSemanticReusePatchHash = (Get-FileHash -LiteralPath $patchDestination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($extractedSemanticReusePatchHash -ne $ExpectedSemanticReusePatchSha256) {
        Fail-Setup "extracted canonical semantic-reuse patch SHA-256 mismatch; expected=$ExpectedSemanticReusePatchSha256 actual=$extractedSemanticReusePatchHash"
    }
    Write-RunnerLog 'Verified production-v2 ThirdParty provenance plus canonical 0001/0002; extracted only provenance and semantic-reuse 0001 for temporary preparation.'

    Write-Host "Creating owned Roslyn worktree at pinned commit $ExpectedCommit"
    [void](Invoke-GitText $RoslynRepositoryRoot @('worktree', 'add', '--quiet', '--detach', $OwnedWorktree, $ExpectedCommit))
    $ownedHead = Invoke-GitText $OwnedWorktree @('rev-parse', 'HEAD')
    if ($ownedHead -ne $ExpectedCommit) { Fail-Setup "owned worktree HEAD mismatch; expected=$ExpectedCommit actual=$ownedHead" }
    $ownedStatus = Invoke-GitText $OwnedWorktree @('status', '--porcelain')
    if (-not [string]::IsNullOrWhiteSpace($ownedStatus)) { Fail-Setup 'new owned Roslyn worktree was not clean.' }
    Write-RunnerLog "Owned worktree created: $OwnedWorktree"

    Write-Host 'Preparing and building temporary semantic-origin Roslyn...'
    try {
        & $PrepareScript -RoslynRoot $OwnedWorktree -CanonicalSystemExplorerPatchPath $patchDestination -OutputRoot $InstrumentationOutput
    }
    catch {
        Write-RunnerLog "Preparation/build failure: $($_.Exception.Message)"
        throw
    }

    $generatedProvenance = Join-Path $InstrumentationOutput 'provenance.json'
    $generatedServer = Verify-GeneratedProvenance $generatedProvenance $InstrumentationOutput
    Write-RunnerLog "Generated semantic-origin server verified: $generatedServer"

    $reportDirectory = Split-Path -Parent $ReportPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
    }
    New-Item -ItemType Directory -Force -Path $ProbeBuildArtifacts | Out-Null

    # Build the probe separately so a compiler/restore failure cannot be confused with
    # semantic-origin capability failure exit code 1 from the C# verification process.
    $FinalExitCode = $InfrastructureFailureExitCode
    Write-Host 'Building capability probe...'
    & dotnet build $ProbeProject --configuration Release --artifacts-path $ProbeBuildArtifacts --disable-build-servers -p:UseSharedCompilation=false
    $probeBuildExitCode = $LASTEXITCODE
    Write-RunnerLog "Capability probe build exit code: $probeBuildExitCode"
    if ($probeBuildExitCode -ne 0) {
        throw "Capability probe build failed with exit code $probeBuildExitCode."
    }

    $probeArguments = @(
        'run',
        '--project', $ProbeProject,
        '--configuration', 'Release',
        '--artifacts-path', $ProbeBuildArtifacts,
        '--no-build',
        '--',
        '--semantic-origin-only',
        '--semantic-origin-server', $generatedServer,
        '--semantic-origin-provenance', $generatedProvenance,
        '--report', $ReportPath
    )
    if ($KeepArtifacts) { $probeArguments += '--keep-artifacts' }

    Write-Host ''
    Write-Host 'Running dedicated semantic-origin verification...'
    Write-RunnerLog 'Invoking prebuilt C# probe in --semantic-origin-only mode.'
    $ProbeWasInvoked = $true
    & dotnet @probeArguments
    $FinalExitCode = $LASTEXITCODE
    Write-RunnerLog "C# probe exit code: $FinalExitCode"
    Write-Host "Runner log: $RunnerLogPath"
    if (Test-Path -LiteralPath $ReportPath -PathType Leaf) {
        Write-Host "Report: $ReportPath"
    }
}
catch {
    if ($ProbeWasInvoked -and $FinalExitCode -eq 0) {
        $FinalExitCode = $InfrastructureFailureExitCode
    }

    $message = $_.Exception.Message
    [Console]::Error.WriteLine($message)
    if (-not [string]::IsNullOrWhiteSpace($RunnerLogPath)) {
        try {
            Write-RunnerLog "Runner failure: $message"
            Write-Host "Runner diagnostics: $RunnerLogPath"
        }
        catch { }
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
            if (-not [string]::IsNullOrWhiteSpace($RunnerLogPath)) {
                try { Write-RunnerLog "Cleanup failure: $CleanupFailure" } catch { }
            }
            Write-Host "Runner-owned state retained for safe inspection: $RunRoot"
            if ($FinalExitCode -eq 0) { $FinalExitCode = $InfrastructureFailureExitCode }
        }
    }
}

exit $FinalExitCode
