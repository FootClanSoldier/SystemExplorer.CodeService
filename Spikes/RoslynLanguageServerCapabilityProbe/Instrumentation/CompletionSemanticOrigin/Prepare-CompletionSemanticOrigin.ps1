[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $RoslynRoot,
    [Parameter(Mandatory = $true)][string] $CanonicalSystemExplorerPatchPath,
    [Parameter(Mandatory = $true)][string] $OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ExpectedCommit = '3aeb96c9ecc56a5ee483558f9e648e33e7bfe756'
$ExpectedPatchSha256 = '11076630b66576961cfd3e56120b15c9e95b352e08f3f551053a79a647d2f2be'
$BaselineDistributionId = 'roslyn-3aeb96c9-systemexplorer-405fb7f9860-win-x64-v1'
$InstrumentationVersion = 1
$OriginJsonPropertyName = '_systemExplorer_completionSemanticOrigin'
$DepthJsonPropertyName = '_systemExplorer_completionInheritanceDepth'
$MutationStarted = $false

$RoslynRoot = [IO.Path]::GetFullPath($RoslynRoot)
$CanonicalSystemExplorerPatchPath = [IO.Path]::GetFullPath($CanonicalSystemExplorerPatchPath)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$PathComparison = if ($env:OS -eq 'Windows_NT') { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$RoslynRootWithSeparator = $RoslynRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ([string]::Equals($OutputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), $RoslynRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), $PathComparison) -or $OutputRoot.StartsWith($RoslynRootWithSeparator, $PathComparison)) {
    throw 'FAIL CLOSED: OutputRoot must be outside the throwaway Roslyn checkout.'
}
$TemplateRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$HelperTemplate = Join-Path $TemplateRoot 'SystemExplorerCompletionSemanticOrigin.cs'

function Fail([string] $Message) { throw "FAIL CLOSED: $Message" }
function Invoke-GitText([string[]] $Arguments) {
    # Windows PowerShell can promote ordinary native stderr into a terminating
    # NativeCommandError when the caller uses ErrorActionPreference=Stop. Git is
    # authoritative by exit code, so capture its merged output under Continue and
    # restore the caller preference immediately afterwards.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $lines = @(& git -C $RoslynRoot @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $text = (($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
    if ($exitCode -ne 0) { Fail "git $($Arguments -join ' ') failed with exit code $exitCode`: $text" }
    return $text
}
function Convert-ToSourceLineEndings([string] $Text, [string] $Template) {
    # Roslyn's .gitattributes leaves normal text files subject to the caller's Git
    # checkout line-ending policy. The spike templates are LF in the Service zip,
    # while a normal Windows checkout can therefore be CRLF. Keep anchor matching
    # exact except for that representational difference, and preserve the source
    # file's existing line-ending convention in the replacement.
    $sourceLineEnding = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $normalizedTemplate = $Template.Replace("`r`n", "`n").Replace("`r", "`n")
    return $normalizedTemplate.Replace("`n", $sourceLineEnding)
}
function Count-OrdinalOccurrences([string] $Text, [string] $Anchor) {
    $count = 0; $start = 0
    while ($start -le $Text.Length - $Anchor.Length) {
        $index = $Text.IndexOf($Anchor, $start, [StringComparison]::Ordinal)
        if ($index -lt 0) { break }
        $count++; $start = $index + $Anchor.Length
    }
    return $count
}
function Replace-ExactlyOnceInMemory([string] $Text, [string] $Anchor, [string] $Replacement, [string] $Description) {
    $effectiveAnchor = Convert-ToSourceLineEndings $Text $Anchor
    $effectiveReplacement = Convert-ToSourceLineEndings $Text $Replacement
    $count = Count-OrdinalOccurrences $Text $effectiveAnchor
    if ($count -ne 1) { Fail "expected source anchor '$Description' exactly once; found $count. No semantic-origin instrumentation source has been written." }
    return $Text.Replace($effectiveAnchor, $effectiveReplacement)
}

$PreviousMsBuildDisableNodeReuse = $env:MSBUILDDISABLENODEREUSE
try {
    # Keep build/restore processes from retaining handles into a runner-owned
    # temporary worktree after preparation completes or fails.
    $env:MSBUILDDISABLENODEREUSE = '1'
    if (-not (Test-Path -LiteralPath $RoslynRoot -PathType Container)) { Fail "RoslynRoot does not exist: $RoslynRoot" }
    if (-not (Test-Path -LiteralPath (Join-Path $RoslynRoot '.git'))) { Fail "RoslynRoot is not a git checkout: $RoslynRoot" }
    if (-not (Test-Path -LiteralPath $CanonicalSystemExplorerPatchPath -PathType Leaf)) { Fail "canonical SystemExplorer patch does not exist: $CanonicalSystemExplorerPatchPath" }
    if (-not (Test-Path -LiteralPath $HelperTemplate -PathType Leaf)) { Fail "semantic-origin helper template is missing: $HelperTemplate" }

    $Head = Invoke-GitText @('rev-parse', 'HEAD')
    if ($Head -ne $ExpectedCommit) { Fail "Roslyn HEAD must be exactly $ExpectedCommit; actual=$Head" }
    $Status = Invoke-GitText @('status', '--porcelain')
    if (-not [string]::IsNullOrWhiteSpace($Status)) { Fail 'Roslyn worktree must be completely clean. Use a throwaway checkout.' }

    $PatchHash = (Get-FileHash -LiteralPath $CanonicalSystemExplorerPatchPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($PatchHash -ne $ExpectedPatchSha256) { Fail "canonical SystemExplorer patch SHA-256 mismatch; expected=$ExpectedPatchSha256 actual=$PatchHash" }

    [void](Invoke-GitText @('apply', '--check', '--', $CanonicalSystemExplorerPatchPath))

    $Paths = @{
        SymbolProvider = 'src/Features/Core/Portable/Completion/Providers/AbstractSymbolCompletionProvider.cs'
        ResultFactory = 'src/LanguageServer/Protocol/Handler/Completion/CompletionResultFactory.cs'
        VSItem = 'src/LanguageServer/Protocol/Protocol/Internal/VSInternalCompletionItem.cs'
        OptimizedSerializer = 'src/LanguageServer/Protocol/Protocol/Internal/Efficiency/OptimizedVSCompletionListJsonConverter.cs'
    }
    foreach ($relative in $Paths.Values) {
        if (-not (Test-Path -LiteralPath (Join-Path $RoslynRoot $relative) -PathType Leaf)) { Fail "expected pinned source file is missing: $relative" }
    }

    $IsWindowsHost = $env:OS -eq 'Windows_NT'
    if ($IsWindowsHost) {
        $RestoreCommand = Join-Path $RoslynRoot 'Restore.cmd'
        if (-not (Test-Path -LiteralPath $RestoreCommand -PathType Leaf)) { Fail "pinned Restore.cmd is missing: $RestoreCommand" }
        & $RestoreCommand
    }
    else {
        $RestoreCommand = Join-Path $RoslynRoot 'build.sh'
        if (-not (Test-Path -LiteralPath $RestoreCommand -PathType Leaf)) { Fail "pinned build.sh is missing: $RestoreCommand" }
        & $RestoreCommand --restore
    }
    if ($LASTEXITCODE -ne 0) { Fail "repository-native Roslyn restore failed with exit code $LASTEXITCODE" }

    $PostRestoreHead = Invoke-GitText @('rev-parse', 'HEAD')
    $PostRestoreStatus = Invoke-GitText @('status', '--porcelain')
    if ($PostRestoreHead -ne $ExpectedCommit -or -not [string]::IsNullOrWhiteSpace($PostRestoreStatus)) {
        Fail 'repository-native restore changed HEAD or dirtied the pristine checkout.'
    }

    $MutationStarted = $true
    [void](Invoke-GitText @('apply', '--', $CanonicalSystemExplorerPatchPath))

    # Read the canonical-patched tree, then validate every instrumentation anchor before any instrumentation write.
    $Pending = @{}
    foreach ($entry in $Paths.GetEnumerator()) {
        $Pending[$entry.Key] = [IO.File]::ReadAllText((Join-Path $RoslynRoot $entry.Value))
    }

    $anchor = @'
                var item = CreateItem(
                    completionContext, symbolGroup.Key.displayText, symbolGroup.Key.suffix, symbolGroup.Key.insertionText, symbolList, arbitraryFirstContext, supportedPlatformData);

                if (includeItemInTargetTypedCompletion)
'@
    $replacement = @'
                var item = CreateItem(
                    completionContext, symbolGroup.Key.displayText, symbolGroup.Key.suffix, symbolGroup.Key.insertionText, symbolList, arbitraryFirstContext, supportedPlatformData);
                item = SystemExplorerCompletionSemanticOrigin.Attach(
                    item,
                    symbolList.SelectAsArray(static entry => entry.Symbol),
                    arbitraryFirstContext);

                if (includeItemInTargetTypedCompletion)
'@
    $Pending.SymbolProvider = Replace-ExactlyOnceInMemory $Pending.SymbolProvider $anchor $replacement 'symbol completion item creation authority'

    $anchor = @'
            var lspItem = await CreateItemAndPopulateTextEditAsync(
                document,
                documentText,
                lspVSClientCapability,
                capabilityHelper.SupportSnippets,
                defaultEditRangeSupported,
                defaultSpan,
                typedText,
                item,
                completionService,
                cancellationToken).ConfigureAwait(false);

            if (!item.InlineDescription.IsEmpty())
'@
    $replacement = @'
            var lspItem = await CreateItemAndPopulateTextEditAsync(
                document,
                documentText,
                lspVSClientCapability,
                capabilityHelper.SupportSnippets,
                defaultEditRangeSupported,
                defaultSpan,
                typedText,
                item,
                completionService,
                cancellationToken).ConfigureAwait(false);

            if (lspItem is LSP.VSInternalCompletionItem systemExplorerVsItem)
            {
                if (item.Properties.TryGetValue("SystemExplorer.CompletionSemanticOrigin", out var systemExplorerOrigin))
                    systemExplorerVsItem.SystemExplorerCompletionSemanticOrigin = systemExplorerOrigin;

                if (item.Properties.TryGetValue("SystemExplorer.CompletionInheritanceDepth", out var systemExplorerDepthText)
                    && int.TryParse(systemExplorerDepthText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var systemExplorerDepth))
                {
                    systemExplorerVsItem.SystemExplorerCompletionInheritanceDepth = systemExplorerDepth;
                }
            }

            if (!item.InlineDescription.IsEmpty())
'@
    $Pending.ResultFactory = Replace-ExactlyOnceInMemory $Pending.ResultFactory $anchor $replacement 'CompletionResultFactory temporary property projection'

    $anchor = @'
    internal const string MatchPrioritySerializedName = "_vs_matchPriority";
'@
    $replacement = @'
    internal const string MatchPrioritySerializedName = "_vs_matchPriority";
    internal const string SystemExplorerCompletionSemanticOriginSerializedName = "_systemExplorer_completionSemanticOrigin";
    internal const string SystemExplorerCompletionInheritanceDepthSerializedName = "_systemExplorer_completionInheritanceDepth";
'@
    $Pending.VSItem = Replace-ExactlyOnceInMemory $Pending.VSItem $anchor $replacement 'VSInternalCompletionItem temporary serialized names'

    $anchor = @'
    public int MatchPriority { get; set; }
}
'@
    $replacement = @'
    public int MatchPriority { get; set; }

    [JsonPropertyName(SystemExplorerCompletionSemanticOriginSerializedName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemExplorerCompletionSemanticOrigin { get; set; }

    [JsonPropertyName(SystemExplorerCompletionInheritanceDepthSerializedName)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SystemExplorerCompletionInheritanceDepth { get; set; }
}
'@
    $Pending.VSItem = Replace-ExactlyOnceInMemory $Pending.VSItem $anchor $replacement 'VSInternalCompletionItem temporary fields'

    $anchor = @'
            if (vsCompletionItem.MatchPriority != 0)
            {
                writer.WriteNumber(VSInternalCompletionItem.MatchPrioritySerializedName, vsCompletionItem.MatchPriority);
            }
'@
    $replacement = @'
            if (vsCompletionItem.MatchPriority != 0)
            {
                writer.WriteNumber(VSInternalCompletionItem.MatchPrioritySerializedName, vsCompletionItem.MatchPriority);
            }

            if (vsCompletionItem.SystemExplorerCompletionSemanticOrigin != null)
            {
                writer.WriteString(VSInternalCompletionItem.SystemExplorerCompletionSemanticOriginSerializedName, vsCompletionItem.SystemExplorerCompletionSemanticOrigin);
            }

            if (vsCompletionItem.SystemExplorerCompletionInheritanceDepth is int systemExplorerDepth)
            {
                writer.WriteNumber(VSInternalCompletionItem.SystemExplorerCompletionInheritanceDepthSerializedName, systemExplorerDepth);
            }
'@
    $Pending.OptimizedSerializer = Replace-ExactlyOnceInMemory $Pending.OptimizedSerializer $anchor $replacement 'optimized VS completion custom-field serialization'

    $HelperDestinationRelative = 'src/Features/Core/Portable/Completion/Providers/SystemExplorerCompletionSemanticOrigin.cs'
    $HelperDestination = Join-Path $RoslynRoot $HelperDestinationRelative
    if (Test-Path -LiteralPath $HelperDestination) { Fail "helper destination already exists: $HelperDestinationRelative" }

    foreach ($entry in $Paths.GetEnumerator()) {
        [IO.File]::WriteAllText((Join-Path $RoslynRoot $entry.Value), $Pending[$entry.Key], [Text.UTF8Encoding]::new($false))
    }
    Copy-Item -LiteralPath $HelperTemplate -Destination $HelperDestination

    $LanguageServerProject = Join-Path $RoslynRoot 'src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Microsoft.CodeAnalysis.LanguageServer.csproj'
    if (-not (Test-Path -LiteralPath $LanguageServerProject -PathType Leaf)) { Fail "LanguageServer project is missing: $LanguageServerProject" }
    & dotnet build $LanguageServerProject -c Release --no-restore --disable-build-servers -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { Fail "instrumented Roslyn LanguageServer build failed with exit code $LASTEXITCODE" }

    $ArtifactRoot = Join-Path $RoslynRoot 'artifacts/bin/Microsoft.CodeAnalysis.LanguageServer/Release'
    $DllCandidates = @(Get-ChildItem -LiteralPath $ArtifactRoot -Filter 'Microsoft.CodeAnalysis.LanguageServer.dll' -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/]ref[\\/]' })
    if ($DllCandidates.Count -ne 1) { Fail "expected exactly one built LanguageServer DLL below $ArtifactRoot; found $($DllCandidates.Count)" }
    $ServerDll = [IO.Path]::GetFullPath($DllCandidates[0].FullName)

    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    if ($IsWindowsHost) {
        $WrapperPath = Join-Path $OutputRoot 'roslyn-completion-semantic-origin.cmd'
        $Wrapper = "@echo off`r`nsetlocal`r`nset `"SYSTEMEXPLORER_COMPLETION_SEMANTIC_ORIGIN=1`"`r`ndotnet `"$ServerDll`" %*`r`n"
        [IO.File]::WriteAllText($WrapperPath, $Wrapper, [Text.ASCIIEncoding]::new())
    }
    else {
        $WrapperPath = Join-Path $OutputRoot 'roslyn-completion-semantic-origin.sh'
        $EscapedServerDll = $ServerDll.Replace("'", "'\''")
        $Wrapper = "#!/usr/bin/env sh`nexport SYSTEMEXPLORER_COMPLETION_SEMANTIC_ORIGIN=1`nexec dotnet '$EscapedServerDll' `"`$@`"`n"
        [IO.File]::WriteAllText($WrapperPath, $Wrapper, [Text.UTF8Encoding]::new($false))
        & chmod +x $WrapperPath
        if ($LASTEXITCODE -ne 0) { Fail 'unable to mark semantic-origin wrapper executable.' }
    }

    $WrapperPath = [IO.Path]::GetFullPath($WrapperPath)
    $ProvenancePath = [IO.Path]::GetFullPath((Join-Path $OutputRoot 'provenance.json'))
    $Provenance = [ordered]@{
        schemaVersion = 1
        instrumentationVersion = $InstrumentationVersion
        repository = 'dotnet/roslyn'
        baseCommit = $ExpectedCommit
        baselineDistributionId = $BaselineDistributionId
        canonicalSystemExplorerPatchSha256 = $ExpectedPatchSha256
        serverCommandPath = $WrapperPath
        semanticOriginJsonPropertyName = $OriginJsonPropertyName
        inheritanceDepthJsonPropertyName = $DepthJsonPropertyName
    }
    $Provenance | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $ProvenancePath -Encoding UTF8
    Write-Host "Semantic-origin wrapper: $WrapperPath"
    Write-Host "Provenance: $ProvenancePath"
    Write-Host 'Throwaway Roslyn checkout remains modified by design. Do not commit it.'
}
catch {
    if ($MutationStarted) {
        Write-Host 'Preparation failed after source mutation. The throwaway checkout is now modified; discard/reset the checkout before retry. No automatic reset/clean was performed.' -ForegroundColor Red
    }
    throw
}
finally {
    if ($null -eq $PreviousMsBuildDisableNodeReuse) {
        Remove-Item Env:MSBUILDDISABLENODEREUSE -ErrorAction SilentlyContinue
    }
    else {
        $env:MSBUILDDISABLENODEREUSE = $PreviousMsBuildDisableNodeReuse
    }
}
