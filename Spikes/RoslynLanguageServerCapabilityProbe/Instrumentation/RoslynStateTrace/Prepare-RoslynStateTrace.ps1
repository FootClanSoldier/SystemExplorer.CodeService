[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RoslynRoot,

    [Parameter(Mandatory = $true)]
    [string] $OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ExpectedCommit = '3aeb96c9ecc56a5ee483558f9e648e33e7bfe756'
$InstrumentationVersion = 1
$TargetFileName = 'ProbeTarget.cs'

$RoslynRoot = [System.IO.Path]::GetFullPath($RoslynRoot)
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$TemplateRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$HelperTemplate = Join-Path $TemplateRoot 'SystemExplorerRoslynStateTrace.cs'

if (-not (Test-Path -LiteralPath $RoslynRoot -PathType Container)) {
    throw "RoslynRoot does not exist: $RoslynRoot"
}
if (-not (Test-Path -LiteralPath $HelperTemplate -PathType Leaf)) {
    throw "Instrumentation helper template does not exist: $HelperTemplate"
}

$Head = (& git -C $RoslynRoot rev-parse HEAD 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $Head -ne $ExpectedCommit) {
    throw "FAIL CLOSED: Roslyn checkout must be exactly $ExpectedCommit; actual=$Head"
}

$Status = (& git -C $RoslynRoot status --porcelain 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw 'FAIL CLOSED: unable to verify Roslyn worktree status.'
}
if (-not [string]::IsNullOrWhiteSpace($Status)) {
    throw 'FAIL CLOSED: Roslyn worktree is not clean. Use a THROWAWAY clean checkout; this script performs local source instrumentation edits.'
}

Write-Host "Verified throwaway Roslyn checkout: $ExpectedCommit"
Write-Host 'Verified clean worktree. No commit will be created.'

$Paths = @{
    DidChange = 'src/LanguageServer/Protocol/Handler/DocumentChanges/DidChangeHandler.cs'
    Workspace = 'src/LanguageServer/Protocol/Workspaces/LspWorkspaceManager.cs'
    Diagnostic = 'src/LanguageServer/Protocol/Handler/Diagnostics/DiagnosticSources/DocumentDiagnosticSource.cs'
    Completion = 'src/Features/Core/Portable/Completion/CompletionService_GetCompletions.cs'
    Tracker = 'src/Workspaces/Core/Portable/Workspace/Solution/SolutionCompilationState.RegularCompilationTracker.cs'
    Translation = 'src/Workspaces/Core/Portable/Workspace/Solution/SolutionCompilationState.TranslationAction_Actions.cs'
}

$HelperDestinationRelative = 'src/Workspaces/Core/Portable/Workspace/Solution/SystemExplorerRoslynStateTrace.cs'
$HelperDestination = Join-Path $RoslynRoot $HelperDestinationRelative
if (Test-Path -LiteralPath $HelperDestination) {
    throw "FAIL CLOSED: helper destination already exists: $HelperDestinationRelative"
}

foreach ($relative in $Paths.Values) {
    $absolute = Join-Path $RoslynRoot $relative
    if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) {
        throw "FAIL CLOSED: expected pinned source file is missing: $relative"
    }
}

function Count-OrdinalOccurrences([string] $Text, [string] $Anchor) {
    $count = 0
    $start = 0
    while ($start -le $Text.Length - $Anchor.Length) {
        $index = $Text.IndexOf($Anchor, $start, [System.StringComparison]::Ordinal)
        if ($index -lt 0) { break }
        $count++
        $start = $index + $Anchor.Length
    }
    return $count
}

function Replace-ExactlyOnceInMemory([string] $Text, [string] $Anchor, [string] $Replacement, [string] $Description) {
    $count = Count-OrdinalOccurrences $Text $Anchor
    if ($count -ne 1) {
        throw "FAIL CLOSED: expected source anchor '$Description' exactly once; found $count. No source has been written."
    }
    return $Text.Replace($Anchor, $Replacement)
}

# Read all affected pinned files first. Every source transformation is validated in memory before any write occurs.
$Pending = @{}
foreach ($entry in $Paths.GetEnumerator()) {
    $absolute = Join-Path $RoslynRoot $entry.Value
    $Pending[$entry.Key] = [System.IO.File]::ReadAllText($absolute)
}

$Anchor = @'
        context.UpdateTrackedDocument(request.TextDocument.DocumentUri, text, request.TextDocument.Version);

        return null;
'@
$Replacement = @'
        context.UpdateTrackedDocument(request.TextDocument.DocumentUri, text, request.TextDocument.Version);
        SystemExplorerRoslynStateTrace.TraceTrackedText(
            "didchange.applied", request.TextDocument.DocumentUri.ToString(), request.TextDocument.Version, text);

        return null;
'@
$Pending.DidChange = Replace-ExactlyOnceInMemory $Pending.DidChange $Anchor $Replacement 'DidChange tracked update boundary'

$Anchor = @'
        _trackedDocuments = _trackedDocuments.SetItem(uri, new(newSourceText, language, lspVersion));

        // If LSP changed, we need to compare against the workspace again to get the updated solution.
'@
$Replacement = @'
        _trackedDocuments = _trackedDocuments.SetItem(uri, new(newSourceText, language, lspVersion));
        SystemExplorerRoslynStateTrace.TraceTrackedText("lsp.tracked_updated", uri.ToString(), lspVersion, newSourceText);

        // If LSP changed, we need to compare against the workspace again to get the updated solution.
'@
$Pending.Workspace = Replace-ExactlyOnceInMemory $Pending.Workspace $Anchor $Replacement 'LspWorkspaceManager tracked update boundary'

$Anchor = @'
    public ImmutableDictionary<DocumentUri, TrackedDocumentInfo> GetTrackedLspText() => _trackedDocuments;

    #endregion
'@
$Replacement = @'
    public ImmutableDictionary<DocumentUri, TrackedDocumentInfo> GetTrackedLspText() => _trackedDocuments;

    private SourceText? GetSystemExplorerTrackedTargetText()
    {
        try
        {
            foreach (var (uri, trackedDocument) in _trackedDocuments)
            {
                if (SystemExplorerRoslynStateTrace.IsTargetIdentifier(uri.ToString()))
                    return trackedDocument.SourceText;
            }
        }
        catch
        {
        }

        return null;
    }

    #endregion
'@
$Pending.Workspace = Replace-ExactlyOnceInMemory $Pending.Workspace $Anchor $Replacement 'LspWorkspaceManager tracked-target observer helper'

$Anchor = @'
            var workspaceCurrentSolution = workspace.CurrentSolution;

            // At a high level these are the steps we take to compute what the desired LSP solution should be.
'@
$Replacement = @'
            var workspaceCurrentSolution = workspace.CurrentSolution;
            SystemExplorerRoslynStateTrace.TraceSolutionSelection(
                "lsp_solution.entry",
                workspaceCurrentSolution,
                workspaceCurrentSolution,
                GetSystemExplorerTrackedTargetText(),
                forkKind: "entry",
                returnPath: "entry");

            // At a high level these are the steps we take to compute what the desired LSP solution should be.
'@
$Pending.Workspace = Replace-ExactlyOnceInMemory $Pending.Workspace $Anchor $Replacement 'LspWorkspaceManager solution entry'

$Anchor = @'
            if (_cachedLspSolutions.TryGetValue(workspace, out var cachedSolution) && cachedSolution.solution == workspaceCurrentSolution)
                return (workspaceCurrentSolution, IsForked: false);
'@
$Replacement = @'
            if (_cachedLspSolutions.TryGetValue(workspace, out var cachedSolution) && cachedSolution.solution == workspaceCurrentSolution)
            {
                SystemExplorerRoslynStateTrace.TraceSolutionSelection(
                    "lsp_solution.return_workspace",
                    workspaceCurrentSolution,
                    workspaceCurrentSolution,
                    GetSystemExplorerTrackedTargetText(),
                    forkKind: "workspace",
                    returnPath: "cached_workspace");
                return (workspaceCurrentSolution, IsForked: false);
            }
'@
$Pending.Workspace = Replace-ExactlyOnceInMemory $Pending.Workspace $Anchor $Replacement 'LspWorkspaceManager cached workspace return'

$Anchor = @'
            workspaceCurrentSolution = workspace.CurrentSolution;

            // Step 3: Check to see if the LSP text matches the workspace text.
'@
$Replacement = @'
            workspaceCurrentSolution = workspace.CurrentSolution;
            SystemExplorerRoslynStateTrace.TraceSolutionSelection(
                "lsp_solution.after_mutation",
                workspaceCurrentSolution,
                workspaceCurrentSolution,
                GetSystemExplorerTrackedTargetText(),
                forkKind: "workspace",
                returnPath: "after_mutation");

            // Step 3: Check to see if the LSP text matches the workspace text.
'@
$Pending.Workspace = Replace-ExactlyOnceInMemory $Pending.Workspace $Anchor $Replacement 'LspWorkspaceManager post-mutation boundary'

$Anchor = @'
                _cachedLspSolutions[workspace] = (forkedFromVersion: null, sourceGeneratorChecksum: null, workspaceCurrentSolution);
                return (workspaceCurrentSolution, IsForked: false);
'@
$Replacement = @'
                _cachedLspSolutions[workspace] = (forkedFromVersion: null, sourceGeneratorChecksum: null, workspaceCurrentSolution);
                SystemExplorerRoslynStateTrace.TraceSolutionSelection(
                    "lsp_solution.return_workspace",
                    workspaceCurrentSolution,
                    workspaceCurrentSolution,
                    GetSystemExplorerTrackedTargetText(),
                    forkKind: "workspace",
                    returnPath: "text_match");
                return (workspaceCurrentSolution, IsForked: false);
'@
$Pending.Workspace = Replace-ExactlyOnceInMemory $Pending.Workspace $Anchor $Replacement 'LspWorkspaceManager text-match workspace return'

$Anchor = @'
            if (cachedSolution != default &&
                cachedSolution.forkedFromVersion == forkedFromVersion &&
                cachedSolution.sourceGeneratorChecksum == sourceGeneratorChecksum)
            {
                return (cachedSolution.solution, IsForked: true);
            }
'@
$Replacement = @'
            if (cachedSolution != default &&
                cachedSolution.forkedFromVersion == forkedFromVersion &&
                cachedSolution.sourceGeneratorChecksum == sourceGeneratorChecksum)
            {
                SystemExplorerRoslynStateTrace.TraceSolutionSelection(
                    "lsp_solution.return_cached_fork",
                    workspaceCurrentSolution,
                    cachedSolution.solution,
                    GetSystemExplorerTrackedTargetText(),
                    forkKind: "cached_fork",
                    returnPath: "cached_fork");
                return (cachedSolution.solution, IsForked: true);
            }
'@
$Pending.Workspace = Replace-ExactlyOnceInMemory $Pending.Workspace $Anchor $Replacement 'LspWorkspaceManager cached fork return'

$Anchor = @'
            _cachedLspSolutions[workspace] = (forkedFromVersion, sourceGeneratorChecksum, lspSolution);
            return (lspSolution, IsForked: true);
'@
$Replacement = @'
            _cachedLspSolutions[workspace] = (forkedFromVersion, sourceGeneratorChecksum, lspSolution);
            SystemExplorerRoslynStateTrace.TraceSolutionSelection(
                "lsp_solution.return_new_fork",
                workspaceCurrentSolution,
                lspSolution,
                GetSystemExplorerTrackedTargetText(),
                forkKind: "new_fork",
                returnPath: "new_fork");
            return (lspSolution, IsForked: true);
'@
$Pending.Workspace = Replace-ExactlyOnceInMemory $Pending.Workspace $Anchor $Replacement 'LspWorkspaceManager new fork return'

$Anchor = @'
        var service = this.Solution.Services.GetRequiredService<IDiagnosticAnalyzerService>();
        var allSpanDiagnostics = await service.GetDiagnosticsForSpanAsync(
            Document, range: null, diagnosticKind: this.DiagnosticKind, cancellationToken).ConfigureAwait(false);
'@
$Replacement = @'
        var service = this.Solution.Services.GetRequiredService<IDiagnosticAnalyzerService>();
        SystemExplorerRoslynStateTrace.TraceSolution("diagnostic.before", this.Solution, Document.Id);
        var allSpanDiagnostics = await service.GetDiagnosticsForSpanAsync(
            Document, range: null, diagnosticKind: this.DiagnosticKind, cancellationToken).ConfigureAwait(false);
        SystemExplorerRoslynStateTrace.TraceSolution("diagnostic.after", this.Solution, Document.Id);
'@
$Pending.Diagnostic = Replace-ExactlyOnceInMemory $Pending.Diagnostic $Anchor $Replacement 'DocumentDiagnosticSource diagnostic computation boundary'

$Anchor = @'
        // We don't need SemanticModel here, just want to make sure it won't get GC'd before CompletionProviders are able to get it.
        document = GetDocumentWithFrozenPartialSemantics(document, cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
'@
$Replacement = @'
        // We don't need SemanticModel here, just want to make sure it won't get GC'd before CompletionProviders are able to get it.
        SystemExplorerRoslynStateTrace.TraceSolution("completion.pre_freeze", document.Project.Solution, document.Id);
        document = GetDocumentWithFrozenPartialSemantics(document, cancellationToken);
        SystemExplorerRoslynStateTrace.TraceSolution("completion.post_freeze", document.Project.Solution, document.Id);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
'@
$Pending.Completion = Replace-ExactlyOnceInMemory $Pending.Completion $Anchor $Replacement 'Completion frozen-partial pre/post boundary'

$Anchor = @'
            else if (state is InProgressState inProgressState)
            {
                // If we have an in progress state with no steps, then we're just at the current project state.
'@
$Replacement = @'
            else if (state is InProgressState inProgressState)
            {
                TraceSystemExplorerRoslynFreezePending(inProgressState);

                // If we have an in progress state with no steps, then we're just at the current project state.
'@
$Pending.Tracker = Replace-ExactlyOnceInMemory $Pending.Tracker $Anchor $Replacement 'RegularCompilationTracker InProgress freeze boundary'

$Anchor = @'
                if (priorAction is TouchDocumentsAction priorTouchAction &&
                    priorTouchAction._newStates.SequenceEqual(_oldStates))
                {
                    // As we're merging ourselves with the prior touch action, we want to keep the old project state
                    // that we are translating from.
                    return new TouchDocumentsAction(priorAction.OldProjectState, NewProjectState, priorTouchAction._oldStates, _newStates);
                }
'@
$Replacement = @'
                if (priorAction is TouchDocumentsAction priorTouchAction &&
                    priorTouchAction._newStates.SequenceEqual(_oldStates))
                {
                    SystemExplorerRoslynStateTrace.TraceTouchMerge(
                        priorAction.OldProjectState,
                        priorTouchAction.NewProjectState,
                        NewProjectState);

                    // As we're merging ourselves with the prior touch action, we want to keep the old project state
                    // that we are translating from.
                    return new TouchDocumentsAction(priorAction.OldProjectState, NewProjectState, priorTouchAction._oldStates, _newStates);
                }
'@
$Pending.Translation = Replace-ExactlyOnceInMemory $Pending.Translation $Anchor $Replacement 'TouchDocumentsAction successful merge boundary'

# All anchors have now been validated exactly once. Before source writes, run the repository-native restore
# path documented by this pinned Roslyn checkout. This establishes the exact SDK/tooling dependency graph
# while the checkout is still pristine. The targeted LanguageServer build below then uses --no-restore.
$IsWindowsHost = $env:OS -eq 'Windows_NT'
if ($IsWindowsHost) {
    $RestoreCommand = Join-Path $RoslynRoot 'Restore.cmd'
    if (-not (Test-Path -LiteralPath $RestoreCommand -PathType Leaf)) {
        throw "FAIL CLOSED: pinned repository-native Restore.cmd is missing: $RestoreCommand"
    }

    Write-Host 'Running pinned Roslyn repository-native restore (Restore.cmd)...'
    & $RestoreCommand
}
else {
    $RestoreCommand = Join-Path $RoslynRoot 'build.sh'
    if (-not (Test-Path -LiteralPath $RestoreCommand -PathType Leaf)) {
        throw "FAIL CLOSED: pinned repository-native build.sh is missing: $RestoreCommand"
    }

    Write-Host 'Running pinned Roslyn repository-native restore (./build.sh --restore)...'
    & $RestoreCommand --restore
}
if ($LASTEXITCODE -ne 0) {
    throw "Pinned Roslyn repository-native restore failed with exit code $LASTEXITCODE"
}

$PostRestoreHead = (& git -C $RoslynRoot rev-parse HEAD 2>&1 | Out-String).Trim()
$PostRestoreStatus = (& git -C $RoslynRoot status --porcelain 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or $PostRestoreHead -ne $ExpectedCommit -or -not [string]::IsNullOrWhiteSpace($PostRestoreStatus)) {
    throw 'FAIL CLOSED: repository-native restore changed or dirtied the pinned Roslyn checkout; no instrumentation source has been written.'
}

# Only after commit/worktree/anchors/restore are all verified do we perform local instrumentation writes.
foreach ($entry in $Paths.GetEnumerator()) {
    $absolute = Join-Path $RoslynRoot $entry.Value
    [System.IO.File]::WriteAllText($absolute, $Pending[$entry.Key], [System.Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath $HelperTemplate -Destination $HelperDestination

$LanguageServerProject = Join-Path $RoslynRoot 'src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/Microsoft.CodeAnalysis.LanguageServer.csproj'
if (-not (Test-Path -LiteralPath $LanguageServerProject -PathType Leaf)) {
    throw "Pinned LanguageServer project is missing: $LanguageServerProject"
}

Write-Host 'Building pinned instrumented Microsoft.CodeAnalysis.LanguageServer with repository project settings...'
& dotnet build $LanguageServerProject -c Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Instrumented Roslyn LanguageServer build failed with exit code $LASTEXITCODE"
}

$ArtifactRoot = Join-Path $RoslynRoot 'artifacts/bin/Microsoft.CodeAnalysis.LanguageServer/Release'
$DllCandidates = @()
if (Test-Path -LiteralPath $ArtifactRoot -PathType Container) {
    $DllCandidates = @(Get-ChildItem -LiteralPath $ArtifactRoot -Filter 'Microsoft.CodeAnalysis.LanguageServer.dll' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/]ref[\\/]' })
}
if ($DllCandidates.Count -ne 1) {
    throw "FAIL CLOSED: expected exactly one built LanguageServer DLL below $ArtifactRoot; found $($DllCandidates.Count)."
}
$ServerDll = $DllCandidates[0].FullName

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
if ($IsWindowsHost) {
    $WrapperPath = Join-Path $OutputRoot 'roslyn-state-trace.cmd'
    $Wrapper = @"
@echo off
set "SYSTEMEXPLORER_ROSLYN_STATE_TRACE=1"
set "SYSTEMEXPLORER_ROSLYN_TRACE_TARGET=$TargetFileName"
dotnet "$ServerDll" %*
"@
    [System.IO.File]::WriteAllText($WrapperPath, $Wrapper, [System.Text.ASCIIEncoding]::new())
}
else {
    $WrapperPath = Join-Path $OutputRoot 'roslyn-state-trace.sh'
    $ShellSingleQuoteEscape =
    ([string]([char]39)) +
    ([string]([char]34)) +
    ([string]([char]39)) +
    ([string]([char]34)) +
    ([string]([char]39))

$EscapedServerDll = $ServerDll.Replace("'", $ShellSingleQuoteEscape)
    $Wrapper = @"
#!/usr/bin/env sh
export SYSTEMEXPLORER_ROSLYN_STATE_TRACE=1
export SYSTEMEXPLORER_ROSLYN_TRACE_TARGET=$TargetFileName
exec dotnet '$EscapedServerDll' "`$@"
"@
    [System.IO.File]::WriteAllText($WrapperPath, $Wrapper, [System.Text.UTF8Encoding]::new($false))
    & chmod +x $WrapperPath
    if ($LASTEXITCODE -ne 0) { throw 'Unable to mark trace wrapper executable.' }
}

$WrapperPath = [System.IO.Path]::GetFullPath($WrapperPath)
$ProvenancePath = Join-Path $OutputRoot 'provenance.json'
$Provenance = [ordered]@{
    schemaVersion = 1
    instrumentationVersion = $InstrumentationVersion
    repository = 'dotnet/roslyn'
    baseCommit = $ExpectedCommit
    serverCommandPath = $WrapperPath
    targetFileName = $TargetFileName
}
$Provenance | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $ProvenancePath -Encoding UTF8

Write-Host "Instrumented trace wrapper: $WrapperPath"
Write-Host "Provenance: $ProvenancePath"
Write-Host 'Roslyn checkout remains locally modified by design. Do not commit these diagnostic edits.'
