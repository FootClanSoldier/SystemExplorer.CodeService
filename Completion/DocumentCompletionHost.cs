using System.Diagnostics;

namespace SystemExplorer.CodeService;

internal sealed class DocumentCompletionHost : IDisposable
{
    private readonly object _sync = new();
    private readonly WorkloadCoordinator _workloadCoordinator;
    private readonly WorkspaceHost _workspaceHost;
    private readonly DocumentSynchronizationHost _documentSynchronizationHost;
    private readonly DocumentSemanticReadinessHost _documentSemanticReadinessHost;
    private readonly RoslynLanguageServerHost _roslynLanguageServerHost;
    private readonly DiagnosticLogging _diagnosticLogging;
    private readonly CompletionResolveHandleStore _resolveHandleStore = new();
    private ImportResolveWarmupState _importResolveWarmupState = ImportResolveWarmupState.NotStarted;
    private long? _importResolveWarmupRoslynGeneration;
    private Task? _importResolveWarmupTask;
    private CancellationTokenSource? _importResolveWarmupPreemptionSource;
    private WorkloadExecutionLease? _importResolveWarmupLease;
    private bool _importResolveWarmupGoalSatisfied;
    private bool _importResolveWarmupRpcStarted;
    private bool _importResolveWarmupForegroundPreemptionRequested;
    private bool _importResolveWarmupForegroundOperationObservedDuringRpc;
    private bool _importResolveWarmupShutdownCancellationRequested;
    private bool _shuttingDown;
    private bool _disposed;

    public DocumentCompletionHost(
        WorkloadCoordinator workloadCoordinator,
        WorkspaceHost workspaceHost,
        DocumentSynchronizationHost documentSynchronizationHost,
        DocumentSemanticReadinessHost documentSemanticReadinessHost,
        RoslynLanguageServerHost roslynLanguageServerHost,
        DiagnosticLogging diagnosticLogging)
    {
        _workloadCoordinator = workloadCoordinator ?? throw new ArgumentNullException(nameof(workloadCoordinator));
        _workspaceHost = workspaceHost ?? throw new ArgumentNullException(nameof(workspaceHost));
        _documentSynchronizationHost = documentSynchronizationHost ?? throw new ArgumentNullException(nameof(documentSynchronizationHost));
        _documentSemanticReadinessHost = documentSemanticReadinessHost ?? throw new ArgumentNullException(nameof(documentSemanticReadinessHost));
        _roslynLanguageServerHost = roslynLanguageServerHost ?? throw new ArgumentNullException(nameof(roslynLanguageServerHost));
        _diagnosticLogging = diagnosticLogging ?? throw new ArgumentNullException(nameof(diagnosticLogging));
    }

    public WorkloadAdmissionResult TryAdmitTransportOperation()
    {
        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                return WorkloadAdmissionResult.ShuttingDown();
            }
        }

        RequestImportResolveWarmupPreemption();
        return _workloadCoordinator.TryAdmitExclusive(WorkloadLane.Completion);
    }

    public async Task<DocumentCompletionResult> CompleteAsync(
        DocumentCompletionRequest request,
        WorkloadExecutionLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Lane != WorkloadLane.Completion)
        {
            throw new InvalidOperationException("document completion requires the completion workload lane.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool diagnosticsEnabled = _diagnosticLogging.IsEnabled;
        long started = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        CompletionTimingState? timing = diagnosticsEnabled
            ? new CompletionTimingState(started)
            : null;
        WriteEvent(
            "completion_request_started",
            request,
            null,
            "Started",
            null,
            null,
            false,
            lease.OperationId,
            timing);
        timing?.StartAdmissionValidation();

        if (request.SchemaVersion != CodeServiceProtocol.CompletionSchemaVersion
            || request.ClientGeneration <= 0
            || request.EpochId == Guid.Empty
            || request.ClientVersion <= 0
            || string.IsNullOrWhiteSpace(request.DocumentPath)
            || request.Line < 0
            || request.Line > DocumentCompletionLimits.MaxCompletionLine
            || request.Character < 0
            || request.Character > DocumentCompletionLimits.MaxCompletionCharacter
            || !DocumentCompletionLimits.IsCompletionPrefixWithinBounds(request.Prefix))
        {
            return Reject(DocumentCompletionOutcome.InvalidRequest, request, null, null, lease.OperationId, timing);
        }

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                return Reject(DocumentCompletionOutcome.Unavailable, request, null, null, lease.OperationId, timing);
            }
        }

        DocumentCompletionOutcome initialOutcome = CaptureInitialAuthorityState(
            request.ClientGeneration,
            request.EpochId,
            request.DocumentPath,
            request.ClientVersion,
            out WorkspacePublication? initialPublication,
            out DocumentIdentity? initialIdentity,
            out DocumentSynchronizationDocumentSnapshot? initialSnapshotValue,
            out string? canonicalDocumentPath);
        if (initialOutcome != DocumentCompletionOutcome.Success)
        {
            return Reject(
                initialOutcome,
                request,
                initialSnapshotValue,
                initialPublication,
                lease.OperationId,
                timing,
                canonicalDocumentPath);
        }

        WorkspacePublication publication = initialPublication!;
        DocumentIdentity identity = initialIdentity!;
        DocumentSynchronizationDocumentSnapshot initialSnapshot = initialSnapshotValue!.Value;

        if (!_documentSynchronizationHost.TryGetRoslynOverlayRevisionToken(
                initialSnapshot.RoslynOverlayRevision,
                out CancellationToken overlayRevisionToken))
        {
            return Reject(DocumentCompletionOutcome.Unavailable, request, initialSnapshot, publication, lease.OperationId, timing);
        }

        using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            overlayRevisionToken);

        timing?.CompleteAdmissionValidation();

        DocumentSemanticReadinessRequest startupReadinessRequest = new(
            CodeServiceProtocol.SemanticReadinessSchemaVersion,
            request.ClientGeneration,
            request.EpochId,
            initialSnapshot.DocumentPath,
            request.ClientVersion);
        long startupReadinessWaitStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            _ = await _documentSemanticReadinessHost.JoinStartupCompletionReadinessAsync(
                startupReadinessRequest,
                publication.Identity,
                initialSnapshot.RoslynGeneration,
                initialSnapshot.RoslynLspVersion,
                initialSnapshot.RoslynOverlayRevision,
                operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            overlayRevisionToken.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            timing?.SetStartupReadinessWaitDuration(startupReadinessWaitStarted);
            return _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(initialSnapshot.RoslynOverlayRevision)
                ? Superseded(request, initialSnapshot, publication, lease.OperationId, timing)
                : Reject(DocumentCompletionOutcome.Unavailable, request, initialSnapshot, publication, lease.OperationId, timing);
        }
        timing?.SetStartupReadinessWaitDuration(startupReadinessWaitStarted);

        long preCompletionRevalidationStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        if (!TryRevalidateExactState(
                request.ClientGeneration,
                request.EpochId,
                request.ClientVersion,
                identity,
                publication.Identity,
                initialSnapshot,
                out WorkspacePublication completionPublication,
                out DocumentSynchronizationDocumentSnapshot completionSnapshot))
        {
            timing?.SetPreCompletionRevalidationDuration(preCompletionRevalidationStarted);
            return !cancellationToken.IsCancellationRequested
                    && _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(initialSnapshot.RoslynOverlayRevision)
                ? Superseded(request, initialSnapshot, publication, lease.OperationId, timing)
                : Reject(DocumentCompletionOutcome.Unavailable, request, initialSnapshot, publication, lease.OperationId, timing);
        }

        if (!IsRoslynCorrelationCurrent(completionPublication, completionSnapshot))
        {
            timing?.SetPreCompletionRevalidationDuration(preCompletionRevalidationStarted);
            return Reject(DocumentCompletionOutcome.RoslynUnavailable, request, completionSnapshot, completionPublication, lease.OperationId, timing);
        }
        timing?.SetPreCompletionRevalidationDuration(preCompletionRevalidationStarted);

        RoslynCompletionResult roslynResult;
        long roslynCompletionStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            roslynResult = await _roslynLanguageServerHost.CompleteAsync(
                completionPublication.WorkspaceIdentity,
                completionPublication.Identity,
                completionSnapshot.RoslynGeneration,
                identity,
                request.Line,
                request.Character,
                operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            overlayRevisionToken.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            timing?.SetRoslynCompletionObservedDuration(roslynCompletionStarted);
            return _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(initialSnapshot.RoslynOverlayRevision)
                ? Superseded(request, initialSnapshot, publication, lease.OperationId, timing)
                : Reject(DocumentCompletionOutcome.Unavailable, request, initialSnapshot, publication, lease.OperationId, timing);
        }
        timing?.SetRoslynCompletionObservedDuration(roslynCompletionStarted);
        timing?.SetRoslynCompletionTiming(roslynResult.Timing);

        WriteEvent(
            "completion_roslyn_completed",
            request,
            completionSnapshot,
            roslynResult.Outcome.ToString(),
            roslynResult.RawItemCount,
            roslynResult.Items.Count,
            roslynResult.IsIncomplete,
            lease.OperationId,
            timing);

        CompletionResolveBatchToken? observedBatchToken = null;
        if (roslynResult.RawItemCount > 0)
        {
            CompletionResolveBatchContext batchContext = new(
                request.ClientGeneration,
                request.EpochId,
                completionSnapshot.DocumentPath,
                request.ClientVersion,
                completionPublication.Identity,
                completionSnapshot.RoslynGeneration,
                completionSnapshot.RoslynLspVersion,
                completionSnapshot.RoslynOverlayRevision);
            if (_resolveHandleStore.TryObserveBatch(batchContext, out CompletionResolveBatchToken observedToken))
            {
                observedBatchToken = observedToken;
            }
        }

        if (roslynResult.Outcome != RoslynCompletionOutcome.Success)
        {
            if (!cancellationToken.IsCancellationRequested
                && _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(initialSnapshot.RoslynOverlayRevision))
            {
                return Superseded(request, initialSnapshot, publication, lease.OperationId, timing);
            }

            DocumentCompletionOutcome failure = roslynResult.Outcome switch
            {
                RoslynCompletionOutcome.RoslynUnavailable => DocumentCompletionOutcome.RoslynUnavailable,
                RoslynCompletionOutcome.CompletionUnavailable => DocumentCompletionOutcome.CompletionUnavailable,
                RoslynCompletionOutcome.Stale => DocumentCompletionOutcome.Unavailable,
                _ => DocumentCompletionOutcome.CompletionUnavailable,
            };
            return Reject(failure, request, completionSnapshot, completionPublication, lease.OperationId, timing);
        }

        long postCompletionRevalidationStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        if (roslynResult.RoslynGeneration != completionSnapshot.RoslynGeneration
            || !TryRevalidateExactState(
                request.ClientGeneration,
                request.EpochId,
                request.ClientVersion,
                identity,
                completionPublication.Identity,
                completionSnapshot,
                out WorkspacePublication completedPublication,
                out DocumentSynchronizationDocumentSnapshot completedSnapshot)
            || !IsRoslynCorrelationCurrent(completedPublication, completedSnapshot))
        {
            timing?.SetPostCompletionRevalidationDuration(postCompletionRevalidationStarted);
            return !cancellationToken.IsCancellationRequested
                    && _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(initialSnapshot.RoslynOverlayRevision)
                ? Superseded(request, initialSnapshot, publication, lease.OperationId, timing)
                : Reject(DocumentCompletionOutcome.Unavailable, request, completionSnapshot, completionPublication, lease.OperationId, timing);
        }

        if (!cancellationToken.IsCancellationRequested
            && _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(initialSnapshot.RoslynOverlayRevision))
        {
            timing?.SetPostCompletionRevalidationDuration(postCompletionRevalidationStarted);
            return Superseded(request, initialSnapshot, publication, lease.OperationId, timing);
        }
        timing?.SetPostCompletionRevalidationDuration(postCompletionRevalidationStarted);

        long itemProjectionStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        List<DocumentCompletionItem> projectedItems = new(roslynResult.Items.Count);
        bool handleProjectionReduced = false;
        foreach (RoslynCompletionItem item in roslynResult.Items)
        {
            if (!item.HasValidCommitContract)
            {
                timing?.SetItemProjectionDuration(itemProjectionStarted);
                return Reject(
                    DocumentCompletionOutcome.CompletionUnavailable,
                    request,
                    completedSnapshot,
                    completedPublication,
                    lease.OperationId,
                    timing);
            }

            DocumentCompletionItem projectedItem;
            if (item.RequiresImport)
            {
                if (observedBatchToken is not CompletionResolveBatchToken batchToken
                    || item.ResolvePayload is null
                    || !_resolveHandleStore.TryRegister(batchToken, item.ResolvePayload, out Guid completionHandle))
                {
                    handleProjectionReduced = true;
                    continue;
                }

                projectedItem = new DocumentCompletionItem(
                    item.DisplayText,
                    null,
                    item.Kind,
                    item.FilterText,
                    item.SortText,
                    item.Preselect,
                    item.SemanticOrigin,
                    item.InheritanceDepth,
                    true,
                    completionHandle);
            }
            else
            {
                projectedItem = new DocumentCompletionItem(
                    item.DisplayText,
                    item.InsertText,
                    item.Kind,
                    item.FilterText,
                    item.SortText,
                    item.Preselect,
                    item.SemanticOrigin,
                    item.InheritanceDepth,
                    RequiresImport: false,
                    CompletionHandle: null);
            }

            if (!projectedItem.HasValidCommitContract)
            {
                timing?.SetItemProjectionDuration(itemProjectionStarted);
                return Reject(
                    DocumentCompletionOutcome.CompletionUnavailable,
                    request,
                    completedSnapshot,
                    completedPublication,
                    lease.OperationId,
                    timing);
            }

            projectedItems.Add(projectedItem);
        }
        timing?.SetItemProjectionDuration(itemProjectionStarted);

        long candidateSelectionStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        CompletionCandidateSelectionResult selection = CompletionCandidateSelector.Select(projectedItems, request.Prefix);
        timing?.SetCandidateSelectionDuration(candidateSelectionStarted);
        bool isIncomplete = roslynResult.IsIncomplete || handleProjectionReduced || selection.WasReduced;

        DocumentCompletionResult result = new(
            DocumentCompletionOutcome.Success,
            request.ClientGeneration,
            request.EpochId,
            completedSnapshot.DocumentPath,
            completedSnapshot.AcceptedClientVersion,
            completedSnapshot.LastWorkspacePublicationIdentity,
            completedSnapshot.RoslynGeneration,
            completedSnapshot.RoslynLspVersion,
            completedSnapshot.RoslynOverlayRevision,
            selection.Items,
            isIncomplete,
            roslynResult.RawItemCount);

        WriteEvent(
            "completion_request_completed",
            request,
            completedSnapshot,
            DocumentCompletionOutcome.Success.ToString(),
            roslynResult.RawItemCount,
            selection.Items.Count,
            isIncomplete,
            lease.OperationId,
            timing,
            commitSafeItemCount: selection.Statistics.CommitSafeInputCount,
            selectionStatistics: selection.Statistics,
            candidateSelectionWasReduced: selection.WasReduced);
        return result;
    }

    internal void ObservePublishedCompletionForImportResolveWarmup(
        DocumentCompletionResult result)
    {
        try
        {
            ObservePublishedCompletionForImportResolveWarmupCore(result);
        }
        catch (Exception exception)
        {
            _diagnosticLogging.WriteFault(
                "completion_import_resolve_warmup_fault",
                exception,
                CreateImportResolveWarmupResultDetails(result, "ObserverFault"));
        }
    }

    private void ObservePublishedCompletionForImportResolveWarmupCore(
        DocumentCompletionResult result)
    {
        if (result.Outcome != DocumentCompletionOutcome.Success
            || result.RoslynGeneration is not long roslynGeneration
            || roslynGeneration <= 0
            || result.Items is null)
        {
            return;
        }

        DocumentCompletionItem? publishedImportItem = null;
        foreach (DocumentCompletionItem item in result.Items)
        {
            if (item.RequiresImport
                && item.CompletionHandle is Guid completionHandle
                && completionHandle != Guid.Empty)
            {
                publishedImportItem = item;
                break;
            }
        }

        if (publishedImportItem is null)
        {
            return;
        }

        Guid selectedHandle = publishedImportItem.CompletionHandle!.Value;
        if (!_resolveHandleStore.TryGet(selectedHandle, out CompletionResolveHandleEntry handleEntry))
        {
            _diagnosticLogging.WriteEvent(
                "completion_import_resolve_warmup_skipped",
                CreateImportResolveWarmupResultDetails(result, "HandleUnavailable"));
            return;
        }

        if (!TryCreateImportResolveWarmupCandidate(result, handleEntry, out ImportResolveWarmupCandidate candidate))
        {
            _diagnosticLogging.WriteEvent(
                "completion_import_resolve_warmup_skipped",
                CreateImportResolveWarmupResultDetails(result, "HandleContextMismatch"));
            return;
        }

        CancellationTokenSource preemptionSource;
        string? stateSkipReason = null;
        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                stateSkipReason = "ServiceShuttingDown";
                preemptionSource = null!;
            }
            else if (_importResolveWarmupRoslynGeneration is long trackedGeneration
                && trackedGeneration != candidate.RoslynGeneration
                && IsImportResolveWarmupActiveLocked())
            {
                stateSkipReason = "DifferentGenerationActive";
                preemptionSource = null!;
            }
            else
            {
                if (_importResolveWarmupRoslynGeneration != candidate.RoslynGeneration)
                {
                    ResetImportResolveWarmupGenerationLocked(candidate.RoslynGeneration);
                }

                if (_importResolveWarmupGoalSatisfied)
                {
                    stateSkipReason = "GenerationAlreadySatisfied";
                    preemptionSource = null!;
                }
                else if (_importResolveWarmupState == ImportResolveWarmupState.Terminal)
                {
                    stateSkipReason = "GenerationAttemptConsumed";
                    preemptionSource = null!;
                }
                else if (_importResolveWarmupState != ImportResolveWarmupState.NotStarted)
                {
                    stateSkipReason = "AttemptAlreadyActive";
                    preemptionSource = null!;
                }
                else
                {
                    _importResolveWarmupState = ImportResolveWarmupState.Starting;
                    _importResolveWarmupRpcStarted = false;
                    _importResolveWarmupForegroundPreemptionRequested = false;
                    _importResolveWarmupForegroundOperationObservedDuringRpc = false;
                    _importResolveWarmupShutdownCancellationRequested = false;
                    _importResolveWarmupTask = null;
                    _importResolveWarmupLease = null;
                    preemptionSource = new CancellationTokenSource();
                    _importResolveWarmupPreemptionSource = preemptionSource;
                }
            }
        }

        if (stateSkipReason is not null)
        {
            _diagnosticLogging.WriteEvent(
                "completion_import_resolve_warmup_skipped",
                CreateImportResolveWarmupCandidateDetails(candidate, stateSkipReason));
            return;
        }

        WorkloadAdmissionResult admission;
        try
        {
            admission = _workloadCoordinator.TryAdmitExclusive(WorkloadLane.CompletionResolveWarmup);
        }
        catch (Exception exception)
        {
            ResetImportResolveWarmupPreRpcAttempt(candidate.RoslynGeneration, preemptionSource, terminal: false);
            _diagnosticLogging.WriteFault(
                "completion_import_resolve_warmup_fault",
                exception,
                CreateImportResolveWarmupCandidateDetails(candidate, "AdmissionFault"));
            return;
        }

        if (admission.Status != WorkloadAdmissionStatus.Admitted
            || admission.Lease is not WorkloadExecutionLease lease)
        {
            bool terminal = admission.Status == WorkloadAdmissionStatus.ShuttingDown;
            ResetImportResolveWarmupPreRpcAttempt(candidate.RoslynGeneration, preemptionSource, terminal);
            _diagnosticLogging.WriteEvent(
                "completion_import_resolve_warmup_skipped",
                CreateImportResolveWarmupCandidateDetails(candidate, admission.Status.ToString()));
            return;
        }

        lock (_sync)
        {
            if (_importResolveWarmupRoslynGeneration == candidate.RoslynGeneration
                && ReferenceEquals(_importResolveWarmupPreemptionSource, preemptionSource))
            {
                _importResolveWarmupState = ImportResolveWarmupState.Running;
                _importResolveWarmupLease = lease;
            }
        }

        _diagnosticLogging.WriteEvent(
            "completion_import_resolve_warmup_started",
            CreateImportResolveWarmupCandidateDetails(
                candidate,
                "Started",
                lease.OperationId,
                warmupRpcStarted: false,
                foregroundPreemptionRequested: false));

        Task warmupTask;
        try
        {
            warmupTask = RunImportResolveWarmupAsync(candidate, lease, preemptionSource);
        }
        catch (Exception exception)
        {
            ResetImportResolveWarmupPreRpcAttempt(candidate.RoslynGeneration, preemptionSource, terminal: false);
            _diagnosticLogging.WriteFault(
                "completion_import_resolve_warmup_fault",
                exception,
                CreateImportResolveWarmupCandidateDetails(candidate, "StartFault", lease.OperationId));
            lease.Retire();
            return;
        }

        lock (_sync)
        {
            if (_importResolveWarmupRoslynGeneration == candidate.RoslynGeneration
                && ReferenceEquals(_importResolveWarmupPreemptionSource, preemptionSource)
                && IsImportResolveWarmupActiveLocked())
            {
                _importResolveWarmupTask = warmupTask;
            }
        }
    }

    private async Task RunImportResolveWarmupAsync(
        ImportResolveWarmupCandidate candidate,
        WorkloadExecutionLease lease,
        CancellationTokenSource preemptionSource)
    {
        await Task.Yield();

        long started = Stopwatch.GetTimestamp();
        string outcome = "Unavailable";
        bool warmupRpcStarted = false;
        bool warmupRpcReachedTerminal = false;
        RoslynCompletionResolveResult? roslynResult = null;
        Exception? fault = null;
        CancellationToken overlayRevisionToken = default;
        bool overlayRevisionTokenCaptured = false;

        try
        {
            ThrowIfImportResolveWarmupCanceled(lease, preemptionSource);

            DocumentCompletionOutcome initialOutcome = CaptureInitialAuthorityState(
                candidate.ClientGeneration,
                candidate.EpochId,
                candidate.DocumentPath,
                candidate.ClientVersion,
                out WorkspacePublication? initialPublication,
                out DocumentIdentity? initialIdentity,
                out DocumentSynchronizationDocumentSnapshot? initialSnapshotValue,
                out _);
            if (initialOutcome != DocumentCompletionOutcome.Success)
            {
                outcome = initialOutcome.ToString();
                return;
            }

            WorkspacePublication publication = initialPublication!;
            DocumentIdentity identity = initialIdentity!;
            DocumentSynchronizationDocumentSnapshot initialSnapshot = initialSnapshotValue!.Value;

            if (!IsImportResolveWarmupCandidateCurrent(candidate, publication, initialSnapshot))
            {
                outcome = "CandidateStale";
                return;
            }

            if (!_documentSynchronizationHost.TryGetRoslynOverlayRevisionToken(
                    candidate.RoslynOverlayRevision,
                    out overlayRevisionToken))
            {
                outcome = "OverlayUnavailable";
                return;
            }
            overlayRevisionTokenCaptured = true;

            WorkspacePublication resolvePublication;
            DocumentSynchronizationDocumentSnapshot resolveSnapshot;
            using (CancellationTokenSource preRpcCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                       lease.ServiceWorkShutdownToken,
                       preemptionSource.Token,
                       overlayRevisionToken))
            {
                preRpcCancellation.Token.ThrowIfCancellationRequested();

                if (!TryRevalidateExactState(
                        candidate.ClientGeneration,
                        candidate.EpochId,
                        candidate.ClientVersion,
                        identity,
                        candidate.WorkspacePublicationIdentity,
                        initialSnapshot,
                        out resolvePublication,
                        out resolveSnapshot)
                    || !IsImportResolveWarmupCandidateCurrent(candidate, resolvePublication, resolveSnapshot))
                {
                    outcome = "CandidateStale";
                    return;
                }

                if (!IsRoslynCorrelationCurrent(resolvePublication, resolveSnapshot))
                {
                    outcome = DocumentCompletionOutcome.RoslynUnavailable.ToString();
                    return;
                }

                preRpcCancellation.Token.ThrowIfCancellationRequested();

                if (!TryMarkImportResolveWarmupRpcStarted(
                        candidate.RoslynGeneration,
                        preemptionSource,
                        lease.ServiceWorkShutdownToken,
                        overlayRevisionToken))
                {
                    outcome = GetImportResolveWarmupPreRpcAbandonmentOutcome(
                        lease,
                        preemptionSource,
                        overlayRevisionToken);
                    return;
                }
            }

            warmupRpcStarted = true;
            using CancellationTokenSource inFlightCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lease.ServiceWorkShutdownToken,
                preemptionSource.Token);

            roslynResult = await _roslynLanguageServerHost.ResolveImportCompletionAsync(
                resolvePublication.WorkspaceIdentity,
                resolvePublication.Identity,
                resolveSnapshot.RoslynGeneration,
                identity,
                candidate.Payload,
                inFlightCancellation.Token).ConfigureAwait(false);
            warmupRpcReachedTerminal = true;
            MarkImportResolveWarmupBackgroundTerminal(candidate.RoslynGeneration, preemptionSource);
            outcome = roslynResult.Value.Outcome.ToString();
        }
        catch (OperationCanceledException)
        {
            outcome = GetImportResolveWarmupCancellationOutcome(
                lease,
                preemptionSource,
                warmupRpcStarted,
                overlayRevisionTokenCaptured,
                overlayRevisionToken);
        }
        catch (Exception exception)
        {
            fault = exception;
            outcome = "Fault";
        }
        finally
        {
            bool foregroundPreemptionRequested;
            bool foregroundOperationObservedDuringRpc;
            lock (_sync)
            {
                foregroundPreemptionRequested = _importResolveWarmupForegroundPreemptionRequested;
                foregroundOperationObservedDuringRpc = _importResolveWarmupForegroundOperationObservedDuringRpc;

                if (_importResolveWarmupRoslynGeneration == candidate.RoslynGeneration
                    && ReferenceEquals(_importResolveWarmupPreemptionSource, preemptionSource))
                {
                    bool mayRetry = !warmupRpcStarted
                        && !_importResolveWarmupGoalSatisfied
                        && !_shuttingDown
                        && !lease.ServiceWorkShutdownToken.IsCancellationRequested;

                    _importResolveWarmupState = mayRetry
                        ? ImportResolveWarmupState.NotStarted
                        : ImportResolveWarmupState.Terminal;
                    _importResolveWarmupPreemptionSource = null;
                    _importResolveWarmupTask = null;
                    _importResolveWarmupLease = null;
                }
            }

            bool overlaySupersededDuringRpc = warmupRpcStarted
                && overlayRevisionTokenCaptured
                && overlayRevisionToken.IsCancellationRequested;
            double durationMs = Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;
            if (fault is null)
            {
                WriteImportResolveWarmupCompletedEvent(
                    candidate,
                    lease.OperationId,
                    outcome,
                    durationMs,
                    warmupRpcStarted,
                    warmupRpcReachedTerminal,
                    overlaySupersededDuringRpc,
                    foregroundPreemptionRequested,
                    foregroundOperationObservedDuringRpc,
                    roslynResult?.Timing);
            }
            else
            {
                _diagnosticLogging.WriteFault(
                    "completion_import_resolve_warmup_fault",
                    fault,
                    CreateImportResolveWarmupCandidateDetails(
                        candidate,
                        outcome,
                        lease.OperationId,
                        warmupRpcStarted,
                        warmupRpcReachedTerminal,
                        overlaySupersededDuringRpc,
                        foregroundPreemptionRequested,
                        foregroundOperationObservedDuringRpc,
                        durationMs,
                        roslynResult?.Timing));
            }

            try
            {
                preemptionSource.Dispose();
            }
            catch (Exception disposalException)
            {
                _diagnosticLogging.WriteFault(
                    "completion_import_resolve_warmup_fault",
                    disposalException,
                    CreateImportResolveWarmupCandidateDetails(
                        candidate,
                        "CancellationSourceDisposalFault",
                        lease.OperationId,
                        warmupRpcStarted,
                        warmupRpcReachedTerminal,
                        overlaySupersededDuringRpc,
                        foregroundPreemptionRequested,
                        foregroundOperationObservedDuringRpc,
                        durationMs,
                        roslynResult?.Timing));
            }

            try
            {
                lease.Retire();
            }
            catch (Exception retirementException)
            {
                _diagnosticLogging.WriteFault(
                    "completion_import_resolve_warmup_fault",
                    retirementException,
                    CreateImportResolveWarmupCandidateDetails(
                        candidate,
                        "RetirementFault",
                        lease.OperationId,
                        warmupRpcStarted,
                        warmupRpcReachedTerminal,
                        overlaySupersededDuringRpc,
                        foregroundPreemptionRequested,
                        foregroundOperationObservedDuringRpc,
                        durationMs,
                        roslynResult?.Timing));
            }
        }
    }

    public async Task<DocumentCompletionResolveResult> ResolveAsync(
        DocumentCompletionResolveRequest request,
        WorkloadExecutionLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Lane != WorkloadLane.Completion)
        {
            throw new InvalidOperationException("document completion resolve requires the completion workload lane.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        bool diagnosticsEnabled = _diagnosticLogging.IsEnabled;
        long started = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        CompletionResolveTimingState? timing = diagnosticsEnabled
            ? new CompletionResolveTimingState(started)
            : null;
        WriteResolveEvent(
            "completion_resolve_request_started",
            request,
            null,
            "Started",
            lease.OperationId,
            timing,
            cacheHit: null,
            edit: null);
        timing?.StartAdmissionValidation();

        if (request.SchemaVersion != CodeServiceProtocol.CompletionResolveSchemaVersion
            || request.ClientGeneration <= 0
            || request.EpochId == Guid.Empty
            || request.ClientVersion <= 0
            || request.CompletionHandle == Guid.Empty
            || string.IsNullOrWhiteSpace(request.DocumentPath))
        {
            return RejectResolve(
                DocumentCompletionResolveOutcome.InvalidRequest,
                request,
                null,
                null,
                lease.OperationId,
                timing);
        }

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                return RejectResolve(
                    DocumentCompletionResolveOutcome.Unavailable,
                    request,
                    null,
                    null,
                    lease.OperationId,
                    timing);
            }
        }

        DocumentCompletionOutcome initialCompletionOutcome = CaptureInitialAuthorityState(
            request.ClientGeneration,
            request.EpochId,
            request.DocumentPath,
            request.ClientVersion,
            out WorkspacePublication? initialPublication,
            out DocumentIdentity? initialIdentity,
            out DocumentSynchronizationDocumentSnapshot? initialSnapshotValue,
            out string? canonicalDocumentPath);
        if (initialCompletionOutcome != DocumentCompletionOutcome.Success)
        {
            return RejectResolve(
                MapCompletionOutcomeToResolveOutcome(initialCompletionOutcome),
                request,
                initialSnapshotValue,
                initialPublication,
                lease.OperationId,
                timing,
                canonicalDocumentPath);
        }

        WorkspacePublication publication = initialPublication!;
        DocumentIdentity identity = initialIdentity!;
        DocumentSynchronizationDocumentSnapshot initialSnapshot = initialSnapshotValue!.Value;

        if (!_resolveHandleStore.TryGet(request.CompletionHandle, out CompletionResolveHandleEntry handleEntry))
        {
            return RejectResolve(
                DocumentCompletionResolveOutcome.CompletionExpired,
                request,
                initialSnapshot,
                publication,
                lease.OperationId,
                timing,
                cacheHit: false);
        }

        CompletionResolveBatchContext handleContext = handleEntry.Context;
        if (handleContext.ClientGeneration != request.ClientGeneration
            || handleContext.EpochId != request.EpochId
            || !DocumentIdentity.PlatformPathComparer.Equals(handleContext.DocumentPath, initialSnapshot.DocumentPath)
            || handleContext.ClientVersion != request.ClientVersion
            || handleContext.WorkspacePublicationIdentity != publication.Identity
            || handleContext.RoslynGeneration != initialSnapshot.RoslynGeneration
            || handleContext.RoslynDocumentVersion != initialSnapshot.RoslynLspVersion
            || handleContext.RoslynOverlayRevision != initialSnapshot.RoslynOverlayRevision)
        {
            return RejectResolve(
                DocumentCompletionResolveOutcome.CompletionExpired,
                request,
                initialSnapshot,
                publication,
                lease.OperationId,
                timing,
                cacheHit: true);
        }

        if (!_documentSynchronizationHost.TryGetRoslynOverlayRevisionToken(
                initialSnapshot.RoslynOverlayRevision,
                out CancellationToken overlayRevisionToken))
        {
            return RejectResolve(
                DocumentCompletionResolveOutcome.Unavailable,
                request,
                initialSnapshot,
                publication,
                lease.OperationId,
                timing,
                cacheHit: true);
        }

        using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            overlayRevisionToken);

        timing?.CompleteAdmissionValidation();

        long preResolveRevalidationStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        if (!TryRevalidateExactState(
                request.ClientGeneration,
                request.EpochId,
                request.ClientVersion,
                identity,
                publication.Identity,
                initialSnapshot,
                out WorkspacePublication resolvePublication,
                out DocumentSynchronizationDocumentSnapshot resolveSnapshot))
        {
            timing?.SetPreResolveRevalidationDuration(preResolveRevalidationStarted);
            return !cancellationToken.IsCancellationRequested
                    && _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(initialSnapshot.RoslynOverlayRevision)
                ? SupersededResolve(request, initialSnapshot, publication, lease.OperationId, timing, cacheHit: true)
                : RejectResolve(
                    DocumentCompletionResolveOutcome.Unavailable,
                    request,
                    initialSnapshot,
                    publication,
                    lease.OperationId,
                    timing,
                    cacheHit: true);
        }

        if (!IsRoslynCorrelationCurrent(resolvePublication, resolveSnapshot))
        {
            timing?.SetPreResolveRevalidationDuration(preResolveRevalidationStarted);
            return RejectResolve(
                DocumentCompletionResolveOutcome.RoslynUnavailable,
                request,
                resolveSnapshot,
                resolvePublication,
                lease.OperationId,
                timing,
                cacheHit: true);
        }
        timing?.SetPreResolveRevalidationDuration(preResolveRevalidationStarted);

        MarkImportResolveWarmupSatisfiedByForegroundResolve(resolveSnapshot.RoslynGeneration);

        RoslynCompletionResolveResult roslynResult;
        long roslynResolveStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            roslynResult = await _roslynLanguageServerHost.ResolveImportCompletionAsync(
                resolvePublication.WorkspaceIdentity,
                resolvePublication.Identity,
                resolveSnapshot.RoslynGeneration,
                identity,
                handleEntry.Payload,
                operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            overlayRevisionToken.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            timing?.SetRoslynResolveObservedDuration(roslynResolveStarted);
            return _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(initialSnapshot.RoslynOverlayRevision)
                ? SupersededResolve(request, initialSnapshot, publication, lease.OperationId, timing, cacheHit: true)
                : RejectResolve(
                    DocumentCompletionResolveOutcome.Unavailable,
                    request,
                    initialSnapshot,
                    publication,
                    lease.OperationId,
                    timing,
                    cacheHit: true);
        }
        timing?.SetRoslynResolveObservedDuration(roslynResolveStarted);
        timing?.SetRoslynResolveTiming(roslynResult.Timing);

        WriteResolveEvent(
            "completion_resolve_roslyn_completed",
            request,
            resolveSnapshot,
            roslynResult.Outcome.ToString(),
            lease.OperationId,
            timing,
            cacheHit: true,
            edit: roslynResult.Edit);

        if (roslynResult.Outcome != RoslynCompletionResolveOutcome.Success)
        {
            if (!cancellationToken.IsCancellationRequested
                && _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(initialSnapshot.RoslynOverlayRevision))
            {
                return SupersededResolve(request, initialSnapshot, publication, lease.OperationId, timing, cacheHit: true);
            }

            DocumentCompletionResolveOutcome failure = roslynResult.Outcome switch
            {
                RoslynCompletionResolveOutcome.CompletionExpired => DocumentCompletionResolveOutcome.CompletionExpired,
                RoslynCompletionResolveOutcome.RoslynUnavailable => DocumentCompletionResolveOutcome.RoslynUnavailable,
                RoslynCompletionResolveOutcome.CompletionUnavailable => DocumentCompletionResolveOutcome.CompletionUnavailable,
                RoslynCompletionResolveOutcome.Stale => DocumentCompletionResolveOutcome.Unavailable,
                _ => DocumentCompletionResolveOutcome.CompletionUnavailable,
            };
            return RejectResolve(
                failure,
                request,
                resolveSnapshot,
                resolvePublication,
                lease.OperationId,
                timing,
                cacheHit: true);
        }

        if (roslynResult.Edit is null)
        {
            return RejectResolve(
                DocumentCompletionResolveOutcome.CompletionUnavailable,
                request,
                resolveSnapshot,
                resolvePublication,
                lease.OperationId,
                timing,
                cacheHit: true);
        }

        DocumentCompletionTextEdit resolvedEdit = roslynResult.Edit;

        long postResolveRevalidationStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        if (roslynResult.RoslynGeneration != resolveSnapshot.RoslynGeneration
            || !TryRevalidateExactState(
                request.ClientGeneration,
                request.EpochId,
                request.ClientVersion,
                identity,
                resolvePublication.Identity,
                resolveSnapshot,
                out WorkspacePublication completedPublication,
                out DocumentSynchronizationDocumentSnapshot completedSnapshot)
            || !IsRoslynCorrelationCurrent(completedPublication, completedSnapshot))
        {
            timing?.SetPostResolveRevalidationDuration(postResolveRevalidationStarted);
            return !cancellationToken.IsCancellationRequested
                    && _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(initialSnapshot.RoslynOverlayRevision)
                ? SupersededResolve(request, initialSnapshot, publication, lease.OperationId, timing, cacheHit: true)
                : RejectResolve(
                    DocumentCompletionResolveOutcome.Unavailable,
                    request,
                    resolveSnapshot,
                    resolvePublication,
                    lease.OperationId,
                    timing,
                    cacheHit: true);
        }

        if (!cancellationToken.IsCancellationRequested
            && _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(initialSnapshot.RoslynOverlayRevision))
        {
            timing?.SetPostResolveRevalidationDuration(postResolveRevalidationStarted);
            return SupersededResolve(request, initialSnapshot, publication, lease.OperationId, timing, cacheHit: true);
        }
        timing?.SetPostResolveRevalidationDuration(postResolveRevalidationStarted);

        DocumentCompletionResolveResult result = DocumentCompletionResolveResult.Success(
            request,
            completedSnapshot,
            resolvedEdit);
        WriteResolveEvent(
            "completion_resolve_request_completed",
            request,
            completedSnapshot,
            DocumentCompletionResolveOutcome.Success.ToString(),
            lease.OperationId,
            timing,
            cacheHit: true,
            edit: resolvedEdit);
        return result;
    }

    public void RecordResolveTransportRejection(
        DocumentCompletionResolveOutcome outcome,
        DocumentCompletionResolveRequest? request = null)
    {
        WriteResolveEvent(
            "completion_resolve_request_rejected",
            request,
            null,
            outcome.ToString(),
            workloadOperationId: null,
            timing: null,
            cacheHit: null,
            edit: null);
    }

    public void RecordTransportRejection(
        DocumentCompletionOutcome outcome,
        DocumentCompletionRequest? request = null)
    {
        WriteEvent(
            "completion_request_rejected",
            request,
            null,
            outcome.ToString(),
            null,
            null,
            false,
            workloadOperationId: null,
            timing: null);
    }

    public void BeginShutdown()
    {
        CancellationTokenSource? warmupPreemptionSource = null;
        lock (_sync)
        {
            if (!_disposed)
            {
                _shuttingDown = true;
                if (IsImportResolveWarmupActiveLocked())
                {
                    _importResolveWarmupShutdownCancellationRequested = true;
                    warmupPreemptionSource = _importResolveWarmupPreemptionSource;
                }
            }
        }

        CancelNoThrow(warmupPreemptionSource);
        _resolveHandleStore.BeginShutdown();
    }

    public void Dispose()
    {
        CancellationTokenSource? warmupPreemptionSource;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _shuttingDown = true;
            _disposed = true;
            warmupPreemptionSource = _importResolveWarmupPreemptionSource;
            _importResolveWarmupPreemptionSource = null;
            _importResolveWarmupTask = null;
            _importResolveWarmupLease = null;
        }

        warmupPreemptionSource?.Dispose();
        _resolveHandleStore.Dispose();
    }

    private static bool TryCreateImportResolveWarmupCandidate(
        DocumentCompletionResult result,
        CompletionResolveHandleEntry handleEntry,
        out ImportResolveWarmupCandidate candidate)
    {
        candidate = null!;
        if (result.ClientGeneration is not long clientGeneration
            || clientGeneration <= 0
            || result.EpochId is not Guid epochId
            || epochId == Guid.Empty
            || string.IsNullOrWhiteSpace(result.DocumentPath)
            || result.AcceptedClientVersion is not long clientVersion
            || clientVersion <= 0
            || result.WorkspacePublicationIdentity is not WorkspacePublicationIdentity publicationIdentity
            || result.RoslynGeneration is not long roslynGeneration
            || roslynGeneration <= 0
            || result.RoslynDocumentVersion is not int roslynDocumentVersion
            || roslynDocumentVersion <= 0
            || result.RoslynOverlayRevision is not long roslynOverlayRevision)
        {
            return false;
        }

        CompletionResolveBatchContext context = handleEntry.Context;
        if (context.ClientGeneration != clientGeneration
            || context.EpochId != epochId
            || !DocumentIdentity.PlatformPathComparer.Equals(context.DocumentPath, result.DocumentPath)
            || context.ClientVersion != clientVersion
            || context.WorkspacePublicationIdentity != publicationIdentity
            || context.RoslynGeneration != roslynGeneration
            || context.RoslynDocumentVersion != roslynDocumentVersion
            || context.RoslynOverlayRevision != roslynOverlayRevision)
        {
            return false;
        }

        candidate = new ImportResolveWarmupCandidate(
            clientGeneration,
            epochId,
            result.DocumentPath!,
            clientVersion,
            publicationIdentity,
            roslynGeneration,
            roslynDocumentVersion,
            roslynOverlayRevision,
            handleEntry.Payload);
        return true;
    }

    private static bool IsImportResolveWarmupCandidateCurrent(
        ImportResolveWarmupCandidate candidate,
        WorkspacePublication publication,
        DocumentSynchronizationDocumentSnapshot snapshot)
        => publication.Identity == candidate.WorkspacePublicationIdentity
            && snapshot.ClientGeneration == candidate.ClientGeneration
            && snapshot.EpochId == candidate.EpochId
            && DocumentIdentity.PlatformPathComparer.Equals(snapshot.DocumentPath, candidate.DocumentPath)
            && snapshot.AcceptedClientVersion == candidate.ClientVersion
            && snapshot.LastWorkspacePublicationIdentity == candidate.WorkspacePublicationIdentity
            && snapshot.RoslynGeneration == candidate.RoslynGeneration
            && snapshot.RoslynLspVersion == candidate.RoslynDocumentVersion
            && snapshot.RoslynOverlayRevision == candidate.RoslynOverlayRevision
            && snapshot.HasCurrentAuthoritySnapshot
            && snapshot.IsOpenInRoslyn
            && snapshot.IsCurrentWorkspaceSource;

    private void ResetImportResolveWarmupGenerationLocked(long roslynGeneration)
    {
        _importResolveWarmupRoslynGeneration = roslynGeneration;
        _importResolveWarmupState = ImportResolveWarmupState.NotStarted;
        _importResolveWarmupGoalSatisfied = false;
        _importResolveWarmupRpcStarted = false;
        _importResolveWarmupForegroundPreemptionRequested = false;
        _importResolveWarmupForegroundOperationObservedDuringRpc = false;
        _importResolveWarmupShutdownCancellationRequested = false;
        _importResolveWarmupPreemptionSource = null;
        _importResolveWarmupTask = null;
        _importResolveWarmupLease = null;
    }

    private void ResetImportResolveWarmupPreRpcAttempt(
        long roslynGeneration,
        CancellationTokenSource preemptionSource,
        bool terminal)
    {
        lock (_sync)
        {
            if (_importResolveWarmupRoslynGeneration == roslynGeneration
                && ReferenceEquals(_importResolveWarmupPreemptionSource, preemptionSource))
            {
                _importResolveWarmupState = terminal || _importResolveWarmupGoalSatisfied || _shuttingDown
                    ? ImportResolveWarmupState.Terminal
                    : ImportResolveWarmupState.NotStarted;
                _importResolveWarmupPreemptionSource = null;
                _importResolveWarmupTask = null;
                _importResolveWarmupLease = null;
            }
        }

        preemptionSource.Dispose();
    }

    private bool TryMarkImportResolveWarmupRpcStarted(
        long roslynGeneration,
        CancellationTokenSource preemptionSource,
        CancellationToken serviceWorkShutdownToken,
        CancellationToken overlayRevisionToken)
    {
        lock (_sync)
        {
            if (_disposed
                || _shuttingDown
                || _importResolveWarmupRoslynGeneration != roslynGeneration
                || _importResolveWarmupState != ImportResolveWarmupState.Running
                || !ReferenceEquals(_importResolveWarmupPreemptionSource, preemptionSource)
                || _importResolveWarmupGoalSatisfied
                || _importResolveWarmupRpcStarted
                || _importResolveWarmupForegroundPreemptionRequested
                || serviceWorkShutdownToken.IsCancellationRequested
                || preemptionSource.IsCancellationRequested
                || overlayRevisionToken.IsCancellationRequested)
            {
                return false;
            }

            _importResolveWarmupRpcStarted = true;
            return true;
        }
    }

    private void MarkImportResolveWarmupBackgroundTerminal(
        long roslynGeneration,
        CancellationTokenSource preemptionSource)
    {
        lock (_sync)
        {
            if (_importResolveWarmupRoslynGeneration == roslynGeneration
                && _importResolveWarmupState == ImportResolveWarmupState.Running
                && ReferenceEquals(_importResolveWarmupPreemptionSource, preemptionSource)
                && _importResolveWarmupRpcStarted)
            {
                _importResolveWarmupGoalSatisfied = true;
            }
        }
    }

    private void MarkImportResolveWarmupSatisfiedByForegroundResolve(long roslynGeneration)
    {
        if (roslynGeneration <= 0)
        {
            return;
        }

        CancellationTokenSource? preemptionSource = null;
        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                return;
            }

            if (_importResolveWarmupRoslynGeneration is long trackedGeneration
                && trackedGeneration != roslynGeneration
                && IsImportResolveWarmupActiveLocked())
            {
                return;
            }

            if (_importResolveWarmupRoslynGeneration != roslynGeneration)
            {
                ResetImportResolveWarmupGenerationLocked(roslynGeneration);
            }

            _importResolveWarmupGoalSatisfied = true;
            if (IsImportResolveWarmupActiveLocked())
            {
                _importResolveWarmupForegroundPreemptionRequested = true;
                preemptionSource = _importResolveWarmupPreemptionSource;
            }
            else
            {
                _importResolveWarmupState = ImportResolveWarmupState.Terminal;
            }
        }

        CancelNoThrow(preemptionSource);
    }

    private void RequestImportResolveWarmupPreemption()
    {
        CancellationTokenSource? preemptionSource = null;
        long? roslynGeneration = null;
        long? workloadOperationId = null;

        lock (_sync)
        {
            if (!IsImportResolveWarmupActiveLocked()
                || _importResolveWarmupPreemptionSource is not CancellationTokenSource activeSource)
            {
                return;
            }

            if (_importResolveWarmupRpcStarted)
            {
                _importResolveWarmupForegroundOperationObservedDuringRpc = true;
                return;
            }

            if (activeSource.IsCancellationRequested
                || _importResolveWarmupForegroundPreemptionRequested)
            {
                return;
            }

            _importResolveWarmupForegroundPreemptionRequested = true;
            preemptionSource = activeSource;
            roslynGeneration = _importResolveWarmupRoslynGeneration;
            workloadOperationId = _importResolveWarmupLease?.OperationId;
        }

        CancelNoThrow(preemptionSource);
        _diagnosticLogging.WriteEvent(
            "completion_import_resolve_warmup_preemption_requested",
            new
            {
                workloadOperationId,
                roslynGeneration,
                warmupRpcStarted = false,
            });
    }

    private bool IsImportResolveWarmupActiveLocked()
        => _importResolveWarmupState is ImportResolveWarmupState.Starting
            or ImportResolveWarmupState.Running;

    private static void ThrowIfImportResolveWarmupCanceled(
        WorkloadExecutionLease lease,
        CancellationTokenSource preemptionSource)
    {
        lease.ServiceWorkShutdownToken.ThrowIfCancellationRequested();
        preemptionSource.Token.ThrowIfCancellationRequested();
    }

    private string GetImportResolveWarmupPreRpcAbandonmentOutcome(
        WorkloadExecutionLease lease,
        CancellationTokenSource preemptionSource,
        CancellationToken overlayRevisionToken)
    {
        if (lease.ServiceWorkShutdownToken.IsCancellationRequested)
        {
            return "ServiceShutdown";
        }

        lock (_sync)
        {
            if (_importResolveWarmupShutdownCancellationRequested)
            {
                return "ServiceShutdown";
            }

            if (_importResolveWarmupGoalSatisfied && !_importResolveWarmupRpcStarted)
            {
                return "ForegroundResolveSatisfied";
            }

            if (_importResolveWarmupForegroundPreemptionRequested)
            {
                return "ForegroundPreempted";
            }
        }

        if (preemptionSource.IsCancellationRequested)
        {
            return "ForegroundPreempted";
        }

        if (overlayRevisionToken.IsCancellationRequested)
        {
            return "OverlaySuperseded";
        }

        return "PreRpcAbandoned";
    }

    private string GetImportResolveWarmupCancellationOutcome(
        WorkloadExecutionLease lease,
        CancellationTokenSource preemptionSource,
        bool warmupRpcStarted,
        bool overlayRevisionTokenCaptured,
        CancellationToken overlayRevisionToken)
    {
        if (lease.ServiceWorkShutdownToken.IsCancellationRequested)
        {
            return "ServiceShutdown";
        }

        lock (_sync)
        {
            if (_importResolveWarmupShutdownCancellationRequested)
            {
                return "ServiceShutdown";
            }

            if (_importResolveWarmupGoalSatisfied)
            {
                return "ForegroundResolveSatisfied";
            }
        }

        if (!warmupRpcStarted)
        {
            if (preemptionSource.IsCancellationRequested)
            {
                return "ForegroundPreempted";
            }

            if (overlayRevisionTokenCaptured && overlayRevisionToken.IsCancellationRequested)
            {
                return "OverlaySuperseded";
            }
        }

        return "Canceled";
    }

    private void WriteImportResolveWarmupCompletedEvent(
        ImportResolveWarmupCandidate candidate,
        long workloadOperationId,
        string outcome,
        double durationMs,
        bool warmupRpcStarted,
        bool warmupRpcReachedTerminal,
        bool overlaySupersededDuringRpc,
        bool foregroundPreemptionRequested,
        bool foregroundOperationObservedDuringRpc,
        RoslynCompletionResolveTiming? timing)
    {
        _diagnosticLogging.WriteEvent(
            "completion_import_resolve_warmup_completed",
            CreateImportResolveWarmupCandidateDetails(
                candidate,
                outcome,
                workloadOperationId,
                warmupRpcStarted,
                warmupRpcReachedTerminal,
                overlaySupersededDuringRpc,
                foregroundPreemptionRequested,
                foregroundOperationObservedDuringRpc,
                durationMs,
                timing));
    }

    private static object CreateImportResolveWarmupCandidateDetails(
        ImportResolveWarmupCandidate candidate,
        string outcome,
        long? workloadOperationId = null,
        bool? warmupRpcStarted = null,
        bool? warmupRpcReachedTerminal = null,
        bool? overlaySupersededDuringRpc = null,
        bool? foregroundPreemptionRequested = null,
        bool? foregroundOperationObservedDuringRpc = null,
        double? durationMs = null,
        RoslynCompletionResolveTiming? timing = null)
        => new
        {
            workloadOperationId,
            documentPath = candidate.DocumentPath,
            clientGeneration = candidate.ClientGeneration,
            clientVersion = candidate.ClientVersion,
            workspaceGeneration = candidate.WorkspacePublicationIdentity.WorkspaceGeneration,
            workspacePublicationVersion = candidate.WorkspacePublicationIdentity.PublicationVersion,
            roslynGeneration = candidate.RoslynGeneration,
            roslynDocumentVersion = candidate.RoslynDocumentVersion,
            roslynOverlayRevision = candidate.RoslynOverlayRevision,
            warmupRpcStarted,
            warmupRpcReachedTerminal,
            overlaySupersededDuringRpc,
            foregroundPreemptionRequested,
            foregroundOperationObservedDuringRpc,
            outcome,
            durationMs,
            senderCaptureDurationMs = timing?.SenderCaptureDurationMs,
            resolveClientTotalDurationMs = timing?.ResolveClientTotalDurationMs,
            resolveRpcDurationMs = timing?.ResolveRpcDurationMs,
            resolveNormalizationDurationMs = timing?.ResolveNormalizationDurationMs,
            postRpcGenerationValidationDurationMs = timing?.PostRpcGenerationValidationDurationMs,
            hostTotalDurationMs = timing?.HostTotalDurationMs,
        };

    private static object CreateImportResolveWarmupResultDetails(
        DocumentCompletionResult result,
        string outcome)
    {
        WorkspacePublicationIdentity? publicationIdentity = result.WorkspacePublicationIdentity;
        return new
        {
            documentPath = result.DocumentPath,
            clientGeneration = result.ClientGeneration,
            clientVersion = result.AcceptedClientVersion,
            workspaceGeneration = publicationIdentity?.WorkspaceGeneration,
            workspacePublicationVersion = publicationIdentity?.PublicationVersion,
            roslynGeneration = result.RoslynGeneration,
            roslynDocumentVersion = result.RoslynDocumentVersion,
            roslynOverlayRevision = result.RoslynOverlayRevision,
            outcome,
        };
    }

    private static void CancelNoThrow(CancellationTokenSource? cancellationSource)
    {
        if (cancellationSource is null)
        {
            return;
        }

        try
        {
            cancellationSource.Cancel(throwOnFirstException: false);
        }
        catch (Exception)
        {
            // Best-effort preemption/shutdown must not turn foreground admission into a failure.
        }
    }


    private DocumentCompletionOutcome CaptureInitialAuthorityState(
        long clientGeneration,
        Guid epochId,
        string documentPath,
        long clientVersion,
        out WorkspacePublication? publication,
        out DocumentIdentity? identity,
        out DocumentSynchronizationDocumentSnapshot? snapshot,
        out string? canonicalDocumentPath)
    {
        publication = null;
        identity = null;
        snapshot = null;
        canonicalDocumentPath = null;

        if (!_workspaceHost.TryGetCurrentPublication(out WorkspacePublication currentPublication))
        {
            return DocumentCompletionOutcome.WorkspaceUnavailable;
        }
        publication = currentPublication;

        DocumentIdentityCreationResult identityResult = DocumentIdentity.TryCreate(
            documentPath,
            currentPublication.WorkspaceIdentity,
            currentPublication.ProjectSnapshot);
        if (!identityResult.IsSuccess)
        {
            return DocumentCompletionOutcome.InvalidRequest;
        }

        identity = identityResult.Identity!;
        canonicalDocumentPath = identity.RelativePath;
        if (!identityResult.IsCurrentWorkspaceSource)
        {
            return DocumentCompletionOutcome.DocumentNotInWorkspace;
        }

        if (!_documentSynchronizationHost.TryGetCurrentAuthority(out DocumentClientAuthority authority))
        {
            return DocumentCompletionOutcome.DocumentNotSynchronized;
        }

        if (clientGeneration < authority.ClientGeneration)
        {
            return DocumentCompletionOutcome.StaleEpoch;
        }

        if (clientGeneration > authority.ClientGeneration)
        {
            return DocumentCompletionOutcome.DocumentNotSynchronized;
        }

        if (epochId != authority.EpochId)
        {
            return DocumentCompletionOutcome.EpochConflict;
        }

        if (!_documentSynchronizationHost.TryGetDocumentSnapshot(
                identity.RelativePath,
                currentPublication.ProjectSnapshot,
                out DocumentSynchronizationDocumentSnapshot currentSnapshot))
        {
            return DocumentCompletionOutcome.DocumentNotSynchronized;
        }
        snapshot = currentSnapshot;

        DocumentCompletionOutcome? synchronizedFailure = ValidateSynchronizedState(
            clientGeneration,
            epochId,
            clientVersion,
            currentPublication,
            currentSnapshot);
        if (synchronizedFailure is not null)
        {
            return synchronizedFailure.Value;
        }

        if (!IsRoslynCorrelationCurrent(currentPublication, currentSnapshot))
        {
            return DocumentCompletionOutcome.RoslynUnavailable;
        }

        return DocumentCompletionOutcome.Success;
    }

    private static DocumentCompletionResolveOutcome MapCompletionOutcomeToResolveOutcome(
        DocumentCompletionOutcome outcome)
        => outcome switch
        {
            DocumentCompletionOutcome.Success => DocumentCompletionResolveOutcome.Success,
            DocumentCompletionOutcome.InvalidRequest => DocumentCompletionResolveOutcome.InvalidRequest,
            DocumentCompletionOutcome.VersionMismatch => DocumentCompletionResolveOutcome.VersionMismatch,
            DocumentCompletionOutcome.Busy => DocumentCompletionResolveOutcome.Busy,
            DocumentCompletionOutcome.WorkspaceUnavailable => DocumentCompletionResolveOutcome.WorkspaceUnavailable,
            DocumentCompletionOutcome.RoslynUnavailable => DocumentCompletionResolveOutcome.RoslynUnavailable,
            DocumentCompletionOutcome.CompletionUnavailable => DocumentCompletionResolveOutcome.CompletionUnavailable,
            DocumentCompletionOutcome.StaleEpoch => DocumentCompletionResolveOutcome.StaleEpoch,
            DocumentCompletionOutcome.EpochConflict => DocumentCompletionResolveOutcome.EpochConflict,
            DocumentCompletionOutcome.StaleVersion => DocumentCompletionResolveOutcome.StaleVersion,
            DocumentCompletionOutcome.DocumentNotSynchronized => DocumentCompletionResolveOutcome.DocumentNotSynchronized,
            DocumentCompletionOutcome.DocumentNotOpen => DocumentCompletionResolveOutcome.DocumentNotOpen,
            DocumentCompletionOutcome.DocumentNotInWorkspace => DocumentCompletionResolveOutcome.DocumentNotInWorkspace,
            _ => DocumentCompletionResolveOutcome.Unavailable,
        };

    private static DocumentCompletionOutcome? ValidateSynchronizedState(
        long clientGeneration,
        Guid epochId,
        long clientVersion,
        WorkspacePublication publication,
        DocumentSynchronizationDocumentSnapshot snapshot)
    {
        if (snapshot.ClientGeneration != clientGeneration)
        {
            return DocumentCompletionOutcome.StaleEpoch;
        }

        if (snapshot.EpochId != epochId)
        {
            return DocumentCompletionOutcome.EpochConflict;
        }

        if (clientVersion < snapshot.AcceptedClientVersion)
        {
            return DocumentCompletionOutcome.StaleVersion;
        }

        if (clientVersion > snapshot.AcceptedClientVersion)
        {
            return DocumentCompletionOutcome.DocumentNotSynchronized;
        }

        if (!snapshot.HasCurrentAuthoritySnapshot)
        {
            return DocumentCompletionOutcome.DocumentNotSynchronized;
        }

        if (!snapshot.IsCurrentWorkspaceSource)
        {
            return DocumentCompletionOutcome.DocumentNotInWorkspace;
        }

        if (!snapshot.IsOpenInRoslyn || snapshot.RoslynGeneration <= 0 || snapshot.RoslynLspVersion <= 0)
        {
            return DocumentCompletionOutcome.DocumentNotOpen;
        }

        if (snapshot.LastWorkspacePublicationIdentity != publication.Identity)
        {
            return DocumentCompletionOutcome.WorkspaceUnavailable;
        }

        return null;
    }

    private bool TryRevalidateExactState(
        long clientGeneration,
        Guid epochId,
        long clientVersion,
        DocumentIdentity identity,
        WorkspacePublicationIdentity expectedPublicationIdentity,
        DocumentSynchronizationDocumentSnapshot expectedSnapshot,
        out WorkspacePublication publication,
        out DocumentSynchronizationDocumentSnapshot snapshot)
    {
        if (!_workspaceHost.TryGetCurrentPublication(out publication)
            || publication.Identity != expectedPublicationIdentity
            || !_documentSynchronizationHost.TryGetCurrentAuthority(out DocumentClientAuthority authority)
            || authority.ClientGeneration != clientGeneration
            || authority.EpochId != epochId
            || !_documentSynchronizationHost.TryGetDocumentSnapshot(
                identity.RelativePath,
                publication.ProjectSnapshot,
                out snapshot)
            || snapshot.ClientGeneration != clientGeneration
            || snapshot.EpochId != epochId
            || snapshot.AcceptedClientVersion != clientVersion
            || !DocumentIdentity.PlatformPathComparer.Equals(snapshot.DocumentPath, expectedSnapshot.DocumentPath)
            || snapshot.LastWorkspacePublicationIdentity != expectedSnapshot.LastWorkspacePublicationIdentity
            || snapshot.RoslynGeneration != expectedSnapshot.RoslynGeneration
            || snapshot.RoslynLspVersion != expectedSnapshot.RoslynLspVersion
            || snapshot.RoslynOverlayRevision != expectedSnapshot.RoslynOverlayRevision
            || !snapshot.HasCurrentAuthoritySnapshot
            || !snapshot.IsOpenInRoslyn
            || !snapshot.IsCurrentWorkspaceSource)
        {
            publication = null!;
            snapshot = default;
            return false;
        }

        return true;
    }

    private bool IsRoslynCorrelationCurrent(
        WorkspacePublication publication,
        DocumentSynchronizationDocumentSnapshot snapshot)
    {
        RoslynLanguageServerSnapshot roslynSnapshot = _roslynLanguageServerHost.GetSnapshot();
        return roslynSnapshot.RoslynGeneration == snapshot.RoslynGeneration
            && _roslynLanguageServerHost.IsProjectLoadCurrentFor(
                publication.WorkspaceIdentity,
                publication.Identity,
                roslynSnapshot);
    }

    private DocumentCompletionResolveResult SupersededResolve(
        DocumentCompletionResolveRequest request,
        DocumentSynchronizationDocumentSnapshot snapshot,
        WorkspacePublication publication,
        long workloadOperationId,
        CompletionResolveTimingState? timing,
        bool? cacheHit)
    {
        DocumentCompletionResolveResult result = DocumentCompletionResolveResult.Failure(
            DocumentCompletionResolveOutcome.Unavailable,
            request,
            snapshot,
            publication);

        WriteResolveEvent(
            "completion_resolve_request_superseded",
            request,
            snapshot,
            DocumentCompletionResolveOutcome.Unavailable.ToString(),
            workloadOperationId,
            timing,
            cacheHit,
            edit: null,
            expectedRoslynOverlayRevision: snapshot.RoslynOverlayRevision);
        WriteResolveEvent(
            "completion_resolve_request_completed",
            request,
            snapshot,
            DocumentCompletionResolveOutcome.Unavailable.ToString(),
            workloadOperationId,
            timing,
            cacheHit,
            edit: null);
        return result;
    }

    private DocumentCompletionResolveResult RejectResolve(
        DocumentCompletionResolveOutcome outcome,
        DocumentCompletionResolveRequest request,
        DocumentSynchronizationDocumentSnapshot? snapshot,
        WorkspacePublication? publication,
        long workloadOperationId,
        CompletionResolveTimingState? timing,
        string? documentPath = null,
        bool? cacheHit = null)
    {
        timing?.CompleteAdmissionValidation();
        DocumentCompletionResolveResult result = DocumentCompletionResolveResult.Failure(
            outcome,
            request,
            snapshot,
            publication,
            documentPath);
        WriteResolveEvent(
            "completion_resolve_request_rejected",
            request,
            snapshot,
            outcome.ToString(),
            workloadOperationId,
            timing,
            cacheHit,
            edit: null);
        WriteResolveEvent(
            "completion_resolve_request_completed",
            request,
            snapshot,
            outcome.ToString(),
            workloadOperationId,
            timing,
            cacheHit,
            edit: null);
        return result;
    }

    private void WriteResolveEvent(
        string eventName,
        DocumentCompletionResolveRequest? request,
        DocumentSynchronizationDocumentSnapshot? snapshot,
        string outcome,
        long? workloadOperationId,
        CompletionResolveTimingState? timing,
        bool? cacheHit,
        DocumentCompletionTextEdit? edit,
        long? expectedRoslynOverlayRevision = null)
    {
        if (!_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent(eventName);
            return;
        }

        WorkspacePublicationIdentity? publication = snapshot is DocumentSynchronizationDocumentSnapshot value
            ? value.LastWorkspacePublicationIdentity
            : null;
        int? editNewTextUtf8Bytes = edit is null
            ? null
            : System.Text.Encoding.UTF8.GetByteCount(edit.NewText);
        double? durationMs = timing?.GetTotalDurationMs();
        double? explicitWorkDurationMs = timing?.GetExplicitWorkDurationMs();
        double? unattributedHostDurationMs = durationMs is double total && explicitWorkDurationMs is double explicitDuration
            ? total - explicitDuration
            : null;

        _diagnosticLogging.WriteEvent(eventName, new
        {
            documentPath = snapshot?.DocumentPath ?? request?.DocumentPath,
            clientGeneration = request?.ClientGeneration,
            clientVersion = request?.ClientVersion,
            workspaceGeneration = publication?.WorkspaceGeneration,
            workspacePublicationVersion = publication?.PublicationVersion,
            roslynGeneration = snapshot?.RoslynGeneration,
            roslynDocumentVersion = snapshot?.RoslynLspVersion,
            roslynOverlayRevision = snapshot?.RoslynOverlayRevision,
            expectedRoslynOverlayRevision,
            workloadOperationId,
            outcome,
            editCount = edit is null ? 0 : 1,
            editNewTextUtf8Bytes,
            cacheHit,
            durationMs,
            admissionValidationDurationMs = timing?.AdmissionValidationDurationMs,
            preResolveRevalidationDurationMs = timing?.PreResolveRevalidationDurationMs,
            roslynResolveObservedDurationMs = timing?.RoslynResolveObservedDurationMs,
            postResolveRevalidationDurationMs = timing?.PostResolveRevalidationDurationMs,
            explicitWorkDurationMs,
            unattributedHostDurationMs,
            roslynResolveSenderCaptureDurationMs = timing?.RoslynTiming.SenderCaptureDurationMs,
            roslynResolveClientTotalDurationMs = timing?.RoslynTiming.ResolveClientTotalDurationMs,
            roslynResolveRpcDurationMs = timing?.RoslynTiming.ResolveRpcDurationMs,
            roslynResolveNormalizationDurationMs = timing?.RoslynTiming.ResolveNormalizationDurationMs,
            roslynResolvePostRpcValidationDurationMs = timing?.RoslynTiming.PostRpcGenerationValidationDurationMs,
            roslynResolveHostTotalDurationMs = timing?.RoslynTiming.HostTotalDurationMs,
        });
    }

    private DocumentCompletionResult Superseded(
        DocumentCompletionRequest request,
        DocumentSynchronizationDocumentSnapshot snapshot,
        WorkspacePublication publication,
        long workloadOperationId,
        CompletionTimingState? timing)
    {
        DocumentCompletionResult result = DocumentCompletionResult.Failure(
            DocumentCompletionOutcome.Unavailable,
            request,
            snapshot,
            publication);

        WriteEvent(
            "completion_request_superseded",
            request,
            snapshot,
            DocumentCompletionOutcome.Unavailable.ToString(),
            null,
            null,
            false,
            workloadOperationId,
            timing,
            expectedRoslynOverlayRevision: snapshot.RoslynOverlayRevision);

        WriteEvent(
            "completion_request_completed",
            request,
            snapshot,
            DocumentCompletionOutcome.Unavailable.ToString(),
            null,
            null,
            false,
            workloadOperationId,
            timing);
        return result;
    }

    private DocumentCompletionResult Reject(
        DocumentCompletionOutcome outcome,
        DocumentCompletionRequest request,
        DocumentSynchronizationDocumentSnapshot? snapshot,
        WorkspacePublication? publication,
        long workloadOperationId,
        CompletionTimingState? timing,
        string? documentPath = null)
    {
        timing?.CompleteAdmissionValidation();
        DocumentCompletionResult result = DocumentCompletionResult.Failure(
            outcome,
            request,
            snapshot,
            publication,
            documentPath);
        WriteEvent(
            "completion_request_rejected",
            request,
            snapshot,
            outcome.ToString(),
            null,
            null,
            false,
            workloadOperationId,
            timing);
        WriteEvent(
            "completion_request_completed",
            request,
            snapshot,
            outcome.ToString(),
            null,
            null,
            false,
            workloadOperationId,
            timing);
        return result;
    }

    private void WriteEvent(
        string eventName,
        DocumentCompletionRequest? request,
        DocumentSynchronizationDocumentSnapshot? snapshot,
        string completionOutcome,
        int? rawItemCount,
        int? returnedItemCount,
        bool isIncomplete,
        long? workloadOperationId,
        CompletionTimingState? timing,
        long? expectedRoslynOverlayRevision = null,
        int? commitSafeItemCount = null,
        CompletionCandidateSelectionStatistics? selectionStatistics = null,
        bool? candidateSelectionWasReduced = null)
    {
        if (!_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent(eventName);
            return;
        }

        WorkspacePublicationIdentity? publication = snapshot is DocumentSynchronizationDocumentSnapshot value
            ? value.LastWorkspacePublicationIdentity
            : null;
        double? durationMs = timing?.GetTotalDurationMs();
        double? explicitWorkDurationMs = timing?.GetExplicitWorkDurationMs();
        double? unattributedHostDurationMs = durationMs is double total && explicitWorkDurationMs is double explicitDuration
            ? total - explicitDuration
            : null;

        _diagnosticLogging.WriteEvent(eventName, new
        {
            documentPath = snapshot?.DocumentPath ?? request?.DocumentPath,
            clientGeneration = request?.ClientGeneration,
            clientVersion = request?.ClientVersion,
            line = request?.Line,
            character = request?.Character,
            workspaceGeneration = publication?.WorkspaceGeneration,
            workspacePublicationVersion = publication?.PublicationVersion,
            roslynGeneration = snapshot?.RoslynGeneration,
            roslynDocumentVersion = snapshot?.RoslynLspVersion,
            roslynOverlayRevision = snapshot?.RoslynOverlayRevision,
            expectedRoslynOverlayRevision,
            workloadOperationId,
            completionOutcome,
            rawItemCount,
            commitSafeItemCount,
            prefixMatchItemCount = selectionStatistics?.PrefixMatchCount,
            textualEligibleItemCount = selectionStatistics?.TextualEligibleCount,
            returnedItemCount,
            publishedItemCount = selectionStatistics?.PublishedCount,
            droppedByPrefixCount = selectionStatistics?.DroppedByPrefixCount,
            droppedByPublicationBudgetCount = selectionStatistics?.DroppedByPublicationBudgetCount,
            textualFilterApplied = selectionStatistics?.TextualFilterApplied,
            textualFallbackUsed = selectionStatistics?.TextualFallbackUsed,
            candidateSelectionWasReduced,
            prefixUtf16Length = request?.Prefix?.Length,
            preselectInputCount = selectionStatistics?.InputDistribution.PreselectCount,
            localInputCount = selectionStatistics?.InputDistribution.LocalCount,
            currentTypeInputCount = selectionStatistics?.InputDistribution.CurrentTypeCount,
            baseTypeDepth1InputCount = selectionStatistics?.InputDistribution.BaseTypeDepth1Count,
            baseTypeDepth2InputCount = selectionStatistics?.InputDistribution.BaseTypeDepth2Count,
            deepBaseTypeInputCount = selectionStatistics?.InputDistribution.DeepBaseTypeCount,
            otherUserCodeInputCount = selectionStatistics?.InputDistribution.OtherUserCodeCount,
            frameworkOrOtherInputCount = selectionStatistics?.InputDistribution.FrameworkOrOtherCount,
            unknownInputCount = selectionStatistics?.InputDistribution.UnknownCount,
            preselectEligibleCount = selectionStatistics?.EligibleDistribution.PreselectCount,
            localEligibleCount = selectionStatistics?.EligibleDistribution.LocalCount,
            currentTypeEligibleCount = selectionStatistics?.EligibleDistribution.CurrentTypeCount,
            baseTypeDepth1EligibleCount = selectionStatistics?.EligibleDistribution.BaseTypeDepth1Count,
            baseTypeDepth2EligibleCount = selectionStatistics?.EligibleDistribution.BaseTypeDepth2Count,
            deepBaseTypeEligibleCount = selectionStatistics?.EligibleDistribution.DeepBaseTypeCount,
            otherUserCodeEligibleCount = selectionStatistics?.EligibleDistribution.OtherUserCodeCount,
            frameworkOrOtherEligibleCount = selectionStatistics?.EligibleDistribution.FrameworkOrOtherCount,
            unknownEligibleCount = selectionStatistics?.EligibleDistribution.UnknownCount,
            preselectPublishedCount = selectionStatistics?.PublishedDistribution.PreselectCount,
            localPublishedCount = selectionStatistics?.PublishedDistribution.LocalCount,
            currentTypePublishedCount = selectionStatistics?.PublishedDistribution.CurrentTypeCount,
            baseTypeDepth1PublishedCount = selectionStatistics?.PublishedDistribution.BaseTypeDepth1Count,
            baseTypeDepth2PublishedCount = selectionStatistics?.PublishedDistribution.BaseTypeDepth2Count,
            deepBaseTypePublishedCount = selectionStatistics?.PublishedDistribution.DeepBaseTypeCount,
            otherUserCodePublishedCount = selectionStatistics?.PublishedDistribution.OtherUserCodeCount,
            frameworkOrOtherPublishedCount = selectionStatistics?.PublishedDistribution.FrameworkOrOtherCount,
            unknownPublishedCount = selectionStatistics?.PublishedDistribution.UnknownCount,
            isIncomplete,
            durationMs,
            admissionValidationDurationMs = timing?.AdmissionValidationDurationMs,
            startupReadinessWaitDurationMs = timing?.StartupReadinessWaitDurationMs,
            preCompletionRevalidationDurationMs = timing?.PreCompletionRevalidationDurationMs,
            roslynCompletionObservedDurationMs = timing?.RoslynCompletionObservedDurationMs,
            postCompletionRevalidationDurationMs = timing?.PostCompletionRevalidationDurationMs,
            itemProjectionDurationMs = timing?.ItemProjectionDurationMs,
            candidateSelectionDurationMs = timing?.CandidateSelectionDurationMs,
            explicitWorkDurationMs,
            unattributedHostDurationMs,
            roslynCompletionSenderCaptureDurationMs = timing?.RoslynTiming.SenderCaptureDurationMs,
            roslynCompletionClientTotalDurationMs = timing?.RoslynTiming.CompletionClientTotalDurationMs,
            roslynCompletionRpcDurationMs = timing?.RoslynTiming.CompletionRpcDurationMs,
            roslynCompletionNormalizationDurationMs = timing?.RoslynTiming.CompletionNormalizationDurationMs,
            roslynCompletionPostRpcValidationDurationMs = timing?.RoslynTiming.PostRpcGenerationValidationDurationMs,
            roslynCompletionHostTotalDurationMs = timing?.RoslynTiming.HostTotalDurationMs,
        });
    }

    private enum ImportResolveWarmupState
    {
        NotStarted,
        Starting,
        Running,
        Terminal,
    }

    private sealed record ImportResolveWarmupCandidate(
        long ClientGeneration,
        Guid EpochId,
        string DocumentPath,
        long ClientVersion,
        WorkspacePublicationIdentity WorkspacePublicationIdentity,
        long RoslynGeneration,
        int RoslynDocumentVersion,
        long RoslynOverlayRevision,
        RoslynCompletionResolvePayload Payload);

    private sealed class CompletionResolveTimingState
    {
        private readonly long _totalStarted;
        private long _admissionStarted;

        public CompletionResolveTimingState(long totalStarted)
        {
            _totalStarted = totalStarted;
        }

        public double? AdmissionValidationDurationMs { get; private set; }
        public double? PreResolveRevalidationDurationMs { get; private set; }
        public double? RoslynResolveObservedDurationMs { get; private set; }
        public double? PostResolveRevalidationDurationMs { get; private set; }
        public RoslynCompletionResolveTiming RoslynTiming { get; private set; }

        public void StartAdmissionValidation()
            => _admissionStarted = Stopwatch.GetTimestamp();

        public void CompleteAdmissionValidation()
        {
            if (AdmissionValidationDurationMs is null && _admissionStarted != 0)
                AdmissionValidationDurationMs = Elapsed(_admissionStarted);
        }

        public void SetPreResolveRevalidationDuration(long started)
            => PreResolveRevalidationDurationMs = Elapsed(started);

        public void SetRoslynResolveObservedDuration(long started)
            => RoslynResolveObservedDurationMs = Elapsed(started);

        public void SetRoslynResolveTiming(RoslynCompletionResolveTiming timing)
            => RoslynTiming = timing;

        public void SetPostResolveRevalidationDuration(long started)
            => PostResolveRevalidationDurationMs = Elapsed(started);

        public double GetTotalDurationMs()
            => Elapsed(_totalStarted);

        public double GetExplicitWorkDurationMs()
            => (AdmissionValidationDurationMs ?? 0)
                + (PreResolveRevalidationDurationMs ?? 0)
                + (RoslynResolveObservedDurationMs ?? 0)
                + (PostResolveRevalidationDurationMs ?? 0);

        private static double Elapsed(long started)
            => Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;
    }

    private sealed class CompletionTimingState
    {
        private readonly long _totalStarted;
        private long _admissionStarted;

        public CompletionTimingState(long totalStarted)
        {
            _totalStarted = totalStarted;
        }

        public double? AdmissionValidationDurationMs { get; private set; }
        public double? StartupReadinessWaitDurationMs { get; private set; }
        public double? PreCompletionRevalidationDurationMs { get; private set; }
        public double? RoslynCompletionObservedDurationMs { get; private set; }
        public double? PostCompletionRevalidationDurationMs { get; private set; }
        public double? ItemProjectionDurationMs { get; private set; }
        public double? CandidateSelectionDurationMs { get; private set; }
        public RoslynCompletionTiming RoslynTiming { get; private set; }

        public void StartAdmissionValidation()
            => _admissionStarted = Stopwatch.GetTimestamp();

        public void CompleteAdmissionValidation()
        {
            if (AdmissionValidationDurationMs is null && _admissionStarted != 0)
                AdmissionValidationDurationMs = Elapsed(_admissionStarted);
        }

        public void SetStartupReadinessWaitDuration(long started)
            => StartupReadinessWaitDurationMs = Elapsed(started);

        public void SetPreCompletionRevalidationDuration(long started)
            => PreCompletionRevalidationDurationMs = Elapsed(started);

        public void SetRoslynCompletionObservedDuration(long started)
            => RoslynCompletionObservedDurationMs = Elapsed(started);

        public void SetRoslynCompletionTiming(RoslynCompletionTiming timing)
            => RoslynTiming = timing;

        public void SetPostCompletionRevalidationDuration(long started)
            => PostCompletionRevalidationDurationMs = Elapsed(started);

        public void SetItemProjectionDuration(long started)
            => ItemProjectionDurationMs = Elapsed(started);

        public void SetCandidateSelectionDuration(long started)
            => CandidateSelectionDurationMs = Elapsed(started);

        public double GetTotalDurationMs()
            => Elapsed(_totalStarted);

        public double GetExplicitWorkDurationMs()
            => (AdmissionValidationDurationMs ?? 0)
                + (StartupReadinessWaitDurationMs ?? 0)
                + (PreCompletionRevalidationDurationMs ?? 0)
                + (RoslynCompletionObservedDurationMs ?? 0)
                + (PostCompletionRevalidationDurationMs ?? 0)
                + (ItemProjectionDurationMs ?? 0)
                + (CandidateSelectionDurationMs ?? 0);

        private static double Elapsed(long started)
            => Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;
    }

}
