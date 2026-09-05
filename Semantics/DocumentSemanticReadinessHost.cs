using System.Diagnostics;

namespace SystemExplorer.CodeService;

internal sealed class DocumentSemanticReadinessHost : IDisposable
{
    private const bool FirstDocumentSemanticWarmupExperimentEnabled = true;
    private const int MaxProofCount = DocumentSynchronizationLimits.MaxTrackedOpenDocuments;
    private readonly object _sync = new();
    private readonly WorkloadCoordinator _workloadCoordinator;
    private readonly WorkspaceHost _workspaceHost;
    private readonly DocumentSynchronizationHost _documentSynchronizationHost;
    private readonly RoslynLanguageServerHost _roslynLanguageServerHost;
    private readonly DiagnosticLogging _diagnosticLogging;
    private readonly Dictionary<string, DocumentSemanticCorrelationIdentity> _proofs =
        new(DocumentIdentity.PlatformPathComparer);
    private FirstDocumentSemanticWarmupState _firstDocumentSemanticWarmupState =
        FirstDocumentSemanticWarmupState.NotStarted;
    private Task? _firstDocumentSemanticWarmupTask;
    private CancellationTokenSource? _firstDocumentSemanticWarmupPreemptionSource;
    private bool _firstDocumentSemanticWarmupForegroundPreemptionRequested;
    private bool _firstDocumentSemanticWarmupShutdownCancellationRequested;
    private bool _shuttingDown;
    private bool _disposed;

    public DocumentSemanticReadinessHost(
        WorkloadCoordinator workloadCoordinator,
        WorkspaceHost workspaceHost,
        DocumentSynchronizationHost documentSynchronizationHost,
        RoslynLanguageServerHost roslynLanguageServerHost,
        DiagnosticLogging diagnosticLogging)
    {
        _workloadCoordinator = workloadCoordinator ?? throw new ArgumentNullException(nameof(workloadCoordinator));
        _workspaceHost = workspaceHost ?? throw new ArgumentNullException(nameof(workspaceHost));
        _documentSynchronizationHost = documentSynchronizationHost ?? throw new ArgumentNullException(nameof(documentSynchronizationHost));
        _roslynLanguageServerHost = roslynLanguageServerHost ?? throw new ArgumentNullException(nameof(roslynLanguageServerHost));
        _diagnosticLogging = diagnosticLogging ?? throw new ArgumentNullException(nameof(diagnosticLogging));
    }

    public WorkloadAdmissionResult TryAdmitTransportOperation()
    {
        lock (_sync)
        {
            if (_disposed || _shuttingDown)
                return WorkloadAdmissionResult.ShuttingDown();
        }
        return _workloadCoordinator.TryAdmitExclusive(WorkloadLane.SemanticReadiness);
    }

    internal void ObserveFirstDocumentForSemanticWarmup(
        DocumentSnapshotOperationResult snapshotResult)
    {
        if (!FirstDocumentSemanticWarmupExperimentEnabled
            || snapshotResult.Outcome != DocumentSynchronizationOutcome.Success
            || snapshotResult.RoslynDocumentVersion != 1
            || snapshotResult.ClientGeneration is not long clientGeneration
            || clientGeneration <= 0
            || snapshotResult.EpochId is not Guid epochId
            || epochId == Guid.Empty
            || string.IsNullOrWhiteSpace(snapshotResult.DocumentPath)
            || snapshotResult.AcceptedClientVersion is not long acceptedClientVersion
            || acceptedClientVersion <= 0
            || snapshotResult.WorkspacePublicationIdentity is not WorkspacePublicationIdentity publicationIdentity
            || snapshotResult.RoslynGeneration is not long roslynGeneration
            || roslynGeneration <= 0)
        {
            return;
        }

        CancellationTokenSource preemptionSource;
        lock (_sync)
        {
            if (_disposed
                || _shuttingDown
                || _firstDocumentSemanticWarmupState != FirstDocumentSemanticWarmupState.NotStarted)
            {
                return;
            }

            _firstDocumentSemanticWarmupState = FirstDocumentSemanticWarmupState.Starting;
            preemptionSource = new CancellationTokenSource();
            _firstDocumentSemanticWarmupPreemptionSource = preemptionSource;
        }

        WorkloadAdmissionResult admission;
        try
        {
            admission = _workloadCoordinator.TryAdmitExclusive(WorkloadLane.SemanticWarmup);
        }
        catch (Exception exception)
        {
            MarkFirstDocumentSemanticWarmupTerminal();
            _diagnosticLogging.WriteFault(
                "completion_first_document_semantic_warmup_experiment_fault",
                exception,
                CreateWarmupCandidateDetails(snapshotResult, "AdmissionFault"));
            return;
        }

        if (admission.Status != WorkloadAdmissionStatus.Admitted
            || admission.Lease is not WorkloadExecutionLease lease)
        {
            MarkFirstDocumentSemanticWarmupTerminal();
            _diagnosticLogging.WriteEvent(
                "completion_first_document_semantic_warmup_experiment_skipped",
                CreateWarmupCandidateDetails(snapshotResult, admission.Status.ToString()));
            return;
        }

        DocumentSemanticReadinessRequest request = new(
            CodeServiceProtocol.SemanticReadinessSchemaVersion,
            clientGeneration,
            epochId,
            snapshotResult.DocumentPath!,
            acceptedClientVersion);

        _diagnosticLogging.WriteEvent(
            "completion_first_document_semantic_warmup_experiment_started",
            new
            {
                workloadOperationId = lease.OperationId,
                documentPath = snapshotResult.DocumentPath,
                clientGeneration = snapshotResult.ClientGeneration,
                clientVersion = snapshotResult.AcceptedClientVersion,
                workspaceGeneration = publicationIdentity.WorkspaceGeneration,
                workspacePublicationVersion = publicationIdentity.PublicationVersion,
                roslynGeneration = snapshotResult.RoslynGeneration,
                roslynDocumentVersion = snapshotResult.RoslynDocumentVersion,
            });

        Task warmupTask;
        try
        {
            lock (_sync)
            {
                _firstDocumentSemanticWarmupState = FirstDocumentSemanticWarmupState.Running;
            }

            warmupTask = RunFirstDocumentSemanticWarmupAsync(
                request,
                snapshotResult,
                lease,
                preemptionSource);
        }
        catch (Exception exception)
        {
            MarkFirstDocumentSemanticWarmupTerminal();
            _diagnosticLogging.WriteFault(
                "completion_first_document_semantic_warmup_experiment_fault",
                exception,
                CreateWarmupCandidateDetails(snapshotResult, "StartFault"));
            lease.Retire();
            return;
        }

        lock (_sync)
        {
            _firstDocumentSemanticWarmupTask = warmupTask;
        }
    }

    public Task<DocumentSemanticReadinessResult> EnsureReadyAsync(
        DocumentSemanticReadinessRequest request,
        WorkloadExecutionLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Lane != WorkloadLane.SemanticReadiness)
            throw new InvalidOperationException("semantic readiness requires the semantic-readiness workload lane.");

        RequestFirstDocumentSemanticWarmupPreemption();

        return EnsureReadyCoreAsync(
            request,
            lease.OperationId,
            cancellationToken,
            cancellationToken);
    }

    internal Task<DocumentSemanticReadinessResult> EnsureReadyForStartupWarmupAsync(
        DocumentSemanticReadinessRequest request,
        WorkloadExecutionLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Lane != WorkloadLane.SemanticWarmup)
            throw new InvalidOperationException("startup semantic warm-up requires the semantic-warmup workload lane.");

        return EnsureReadyCoreAsync(
            request,
            lease.OperationId,
            cancellationToken,
            cancellationToken);
    }

    private async Task<DocumentSemanticReadinessResult> EnsureReadyCoreAsync(
        DocumentSemanticReadinessRequest request,
        long workloadOperationId,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool diagnosticsEnabled = _diagnosticLogging.IsEnabled;
        long started = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        SemanticReadinessTimingState? timing = diagnosticsEnabled
            ? new SemanticReadinessTimingState(started)
            : null;

        if (request.SchemaVersion != CodeServiceProtocol.SemanticReadinessSchemaVersion
            || request.ClientGeneration <= 0 || request.EpochId == Guid.Empty
            || request.ClientVersion <= 0 || string.IsNullOrWhiteSpace(request.DocumentPath))
            return Reject(DocumentSemanticReadinessOutcome.InvalidRequest, request, null, null, workloadOperationId, timing);

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
                return Reject(DocumentSemanticReadinessOutcome.Unavailable, request, null, null, workloadOperationId, timing);
        }

        if (!_workspaceHost.TryGetCurrentPublication(out WorkspacePublication publication))
            return Reject(DocumentSemanticReadinessOutcome.WorkspaceUnavailable, request, null, null, workloadOperationId, timing);

        DocumentIdentityCreationResult identityResult = DocumentIdentity.TryCreate(
            request.DocumentPath, publication.WorkspaceIdentity, publication.ProjectSnapshot);
        if (!identityResult.IsSuccess)
            return Reject(DocumentSemanticReadinessOutcome.InvalidRequest, request, null, publication, workloadOperationId, timing);
        DocumentIdentity identity = identityResult.Identity!;
        if (!identityResult.IsCurrentWorkspaceSource)
            return Reject(DocumentSemanticReadinessOutcome.DocumentNotInWorkspace, request, null, publication, workloadOperationId, timing, identity.RelativePath);

        if (!_documentSynchronizationHost.TryGetCurrentAuthority(out DocumentClientAuthority authority))
            return Reject(DocumentSemanticReadinessOutcome.DocumentNotSynchronized, request, null, publication, workloadOperationId, timing, identity.RelativePath);
        if (request.ClientGeneration < authority.ClientGeneration)
            return Reject(DocumentSemanticReadinessOutcome.StaleEpoch, request, null, publication, workloadOperationId, timing, identity.RelativePath);
        if (request.ClientGeneration > authority.ClientGeneration)
            return Reject(DocumentSemanticReadinessOutcome.DocumentNotSynchronized, request, null, publication, workloadOperationId, timing, identity.RelativePath);
        if (request.EpochId != authority.EpochId)
            return Reject(DocumentSemanticReadinessOutcome.EpochConflict, request, null, publication, workloadOperationId, timing, identity.RelativePath);

        if (!_documentSynchronizationHost.TryGetDocumentSnapshot(identity.RelativePath, publication.ProjectSnapshot, out DocumentSynchronizationDocumentSnapshot snapshot))
            return Reject(DocumentSemanticReadinessOutcome.DocumentNotSynchronized, request, null, publication, workloadOperationId, timing, identity.RelativePath);

        DocumentSemanticReadinessOutcome? admissionFailure = ValidateSynchronizedState(request, publication, snapshot);
        if (admissionFailure is not null)
            return Reject(admissionFailure.Value, request, snapshot, publication, workloadOperationId, timing);

        if (!IsRoslynCorrelationCurrent(publication, snapshot))
            return Reject(DocumentSemanticReadinessOutcome.RoslynUnavailable, request, snapshot, publication, workloadOperationId, timing);

        DocumentSemanticCorrelationIdentity correlation = ToCorrelation(snapshot);
        timing?.CompletePreProofValidation();

        long proofLookupStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        lock (_sync)
        {
            PruneStaleProofsLocked(publication.Identity, snapshot.RoslynGeneration, snapshot.RoslynOverlayRevision);
            if (_proofs.TryGetValue(snapshot.DocumentPath, out DocumentSemanticCorrelationIdentity proof) && proof == correlation)
            {
                if (!_documentSynchronizationHost.TryGetRoslynOverlayRevisionToken(
                        snapshot.RoslynOverlayRevision,
                        out CancellationToken proofRevisionToken)
                    || proofRevisionToken.IsCancellationRequested)
                {
                    timing?.SetProofLookupDuration(proofLookupStarted);
                    return Reject(DocumentSemanticReadinessOutcome.Unavailable, request, snapshot, publication, workloadOperationId, timing);
                }

                timing?.SetProofLookupDuration(proofLookupStarted);
                WriteEvent(
                    "semantic_readiness_proof_reused",
                    request,
                    snapshot,
                    "AlreadyCurrent",
                    workloadOperationId,
                    timing,
                    diagnosticCount: null);
                return SuccessResult(DocumentSemanticReadinessOutcome.AlreadyCurrent, request, snapshot);
            }
        }
        timing?.SetProofLookupDuration(proofLookupStarted);

        long overlaySetupStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        if (!_documentSynchronizationHost.TryGetRoslynOverlayRevisionToken(
                snapshot.RoslynOverlayRevision,
                out CancellationToken overlayRevisionToken))
        {
            timing?.SetOverlayCancellationSetupDuration(overlaySetupStarted);
            return Reject(DocumentSemanticReadinessOutcome.Unavailable, request, snapshot, publication, workloadOperationId, timing);
        }

        using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            overlayRevisionToken);
        timing?.SetOverlayCancellationSetupDuration(overlaySetupStarted);

        WriteEvent(
            "semantic_readiness_request_started",
            request,
            snapshot,
            "Started",
            workloadOperationId,
            timing,
            diagnosticCount: null);

        RoslynSemanticReadinessResult roslynResult;
        long roslynSemanticStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            roslynResult = await _roslynLanguageServerHost.EstablishSemanticReadinessAsync(
                publication.WorkspaceIdentity,
                publication.Identity,
                snapshot.RoslynGeneration,
                identity,
                operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            overlayRevisionToken.IsCancellationRequested
            && !callerCancellationToken.IsCancellationRequested)
        {
            timing?.SetRoslynSemanticObservedDuration(roslynSemanticStarted);
            return _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(snapshot.RoslynOverlayRevision)
                ? Superseded(request, snapshot, publication, workloadOperationId, timing)
                : Reject(DocumentSemanticReadinessOutcome.Unavailable, request, snapshot, publication, workloadOperationId, timing);
        }

        timing?.SetRoslynSemanticObservedDuration(roslynSemanticStarted);
        timing?.SetRoslynTiming(roslynResult.Timing);
        WriteEvent(
            "semantic_readiness_diagnostic_completed",
            request,
            snapshot,
            roslynResult.Outcome.ToString(),
            workloadOperationId,
            timing,
            roslynResult.DiagnosticCount);

        long postDiagnosticRevalidationStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        if (!callerCancellationToken.IsCancellationRequested
            && _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(snapshot.RoslynOverlayRevision))
        {
            timing?.SetPostDiagnosticRevalidationDuration(postDiagnosticRevalidationStarted);
            return Superseded(request, snapshot, publication, workloadOperationId, timing);
        }

        if (roslynResult.Outcome == RoslynSemanticReadinessOutcome.SemanticUnavailable)
        {
            timing?.SetPostDiagnosticRevalidationDuration(postDiagnosticRevalidationStarted);
            return Reject(DocumentSemanticReadinessOutcome.SemanticUnavailable, request, snapshot, publication, workloadOperationId, timing);
        }
        if (roslynResult.Outcome == RoslynSemanticReadinessOutcome.RoslynUnavailable)
        {
            timing?.SetPostDiagnosticRevalidationDuration(postDiagnosticRevalidationStarted);
            return Reject(DocumentSemanticReadinessOutcome.RoslynUnavailable, request, snapshot, publication, workloadOperationId, timing);
        }
        if (roslynResult.Outcome == RoslynSemanticReadinessOutcome.Stale)
        {
            timing?.SetPostDiagnosticRevalidationDuration(postDiagnosticRevalidationStarted);
            return Reject(DocumentSemanticReadinessOutcome.Unavailable, request, snapshot, publication, workloadOperationId, timing);
        }

        if (!_workspaceHost.TryGetCurrentPublication(out WorkspacePublication currentPublication)
            || currentPublication.Identity != publication.Identity
            || !_documentSynchronizationHost.TryGetCurrentAuthority(out DocumentClientAuthority currentAuthority)
            || currentAuthority.ClientGeneration != request.ClientGeneration
            || currentAuthority.EpochId != request.EpochId
            || !_documentSynchronizationHost.TryGetDocumentSnapshot(identity.RelativePath, currentPublication.ProjectSnapshot, out DocumentSynchronizationDocumentSnapshot currentSnapshot)
            || currentSnapshot.AcceptedClientVersion != request.ClientVersion
            || ToCorrelation(currentSnapshot) != correlation
            || !currentSnapshot.IsOpenInRoslyn
            || !currentSnapshot.IsCurrentWorkspaceSource)
        {
            timing?.SetPostDiagnosticRevalidationDuration(postDiagnosticRevalidationStarted);
            return !callerCancellationToken.IsCancellationRequested
                    && _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(snapshot.RoslynOverlayRevision)
                ? Superseded(request, snapshot, publication, workloadOperationId, timing)
                : Reject(DocumentSemanticReadinessOutcome.Unavailable, request, snapshot, publication, workloadOperationId, timing);
        }

        if (!IsRoslynCorrelationCurrent(currentPublication, currentSnapshot))
        {
            timing?.SetPostDiagnosticRevalidationDuration(postDiagnosticRevalidationStarted);
            return Reject(DocumentSemanticReadinessOutcome.RoslynUnavailable, request, currentSnapshot, currentPublication, workloadOperationId, timing);
        }

        if (!_documentSynchronizationHost.TryGetRoslynOverlayRevisionToken(
                correlation.RoslynOverlayRevision,
                out CancellationToken commitRevisionToken)
            || commitRevisionToken.IsCancellationRequested)
        {
            timing?.SetPostDiagnosticRevalidationDuration(postDiagnosticRevalidationStarted);
            return !callerCancellationToken.IsCancellationRequested
                    && _documentSynchronizationHost.IsRoslynOverlayRevisionSuperseded(correlation.RoslynOverlayRevision)
                ? Superseded(request, currentSnapshot, currentPublication, workloadOperationId, timing)
                : Reject(DocumentSemanticReadinessOutcome.Unavailable, request, currentSnapshot, currentPublication, workloadOperationId, timing);
        }
        timing?.SetPostDiagnosticRevalidationDuration(postDiagnosticRevalidationStarted);

        long proofCommitStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                timing?.SetProofCommitDuration(proofCommitStarted);
                return Reject(DocumentSemanticReadinessOutcome.Unavailable, request, currentSnapshot, currentPublication, workloadOperationId, timing);
            }

            _proofs[currentSnapshot.DocumentPath] = correlation;
            if (_proofs.Count > MaxProofCount)
                RemoveOldestDeterministicProofLocked(currentSnapshot.DocumentPath);
        }
        timing?.SetProofCommitDuration(proofCommitStarted);

        WriteEvent(
            "semantic_readiness_committed",
            request,
            currentSnapshot,
            "Success",
            workloadOperationId,
            timing,
            roslynResult.DiagnosticCount);
        return SuccessResult(DocumentSemanticReadinessOutcome.Success, request, currentSnapshot);
    }

    public void BeginShutdown()
    {
        CancellationTokenSource? preemptionSource;
        lock (_sync)
        {
            if (_disposed)
                return;

            _shuttingDown = true;
            if (IsFirstDocumentSemanticWarmupActiveLocked())
            {
                _firstDocumentSemanticWarmupShutdownCancellationRequested = true;
                preemptionSource = _firstDocumentSemanticWarmupPreemptionSource;
            }
            else
            {
                preemptionSource = null;
            }
        }

        CancelNoThrow(preemptionSource);
    }

    public void Dispose()
    {
        CancellationTokenSource? preemptionSource;
        lock (_sync)
        {
            if (_disposed)
                return;

            _shuttingDown = true;
            _proofs.Clear();
            _disposed = true;
            preemptionSource = _firstDocumentSemanticWarmupPreemptionSource;
        }

        preemptionSource?.Dispose();
    }

    private async Task RunFirstDocumentSemanticWarmupAsync(
        DocumentSemanticReadinessRequest request,
        DocumentSnapshotOperationResult snapshotResult,
        WorkloadExecutionLease lease,
        CancellationTokenSource preemptionSource)
    {
        await Task.Yield();

        long started = Stopwatch.GetTimestamp();
        string semanticOutcome = DocumentSemanticReadinessOutcome.Unavailable.ToString();
        Exception? fault = null;

        try
        {
            using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lease.ServiceWorkShutdownToken,
                preemptionSource.Token);

            DocumentSemanticReadinessResult result = await EnsureReadyForStartupWarmupAsync(
                request,
                lease,
                operationCancellation.Token).ConfigureAwait(false);
            semanticOutcome = result.Outcome.ToString();
        }
        catch (OperationCanceledException) when (preemptionSource.IsCancellationRequested)
        {
            bool shutdownCancellationRequested;
            lock (_sync)
            {
                shutdownCancellationRequested = _firstDocumentSemanticWarmupShutdownCancellationRequested;
            }

            semanticOutcome = shutdownCancellationRequested
                || lease.ServiceWorkShutdownToken.IsCancellationRequested
                ? "ServiceShutdown"
                : "ForegroundPreempted";
        }
        catch (OperationCanceledException) when (lease.ServiceWorkShutdownToken.IsCancellationRequested)
        {
            semanticOutcome = "ServiceShutdown";
        }
        catch (Exception exception)
        {
            fault = exception;
            semanticOutcome = "Fault";
        }
        finally
        {
            DiagnosticLogging diagnosticLogging = _diagnosticLogging;
            bool foregroundPreemptionRequested;
            lock (_sync)
            {
                foregroundPreemptionRequested = _firstDocumentSemanticWarmupForegroundPreemptionRequested;
                _firstDocumentSemanticWarmupState = FirstDocumentSemanticWarmupState.Terminal;
            }

            double durationMs = Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;
            if (fault is null)
            {
                diagnosticLogging.WriteEvent(
                    "completion_first_document_semantic_warmup_experiment_completed",
                    new
                    {
                        workloadOperationId = lease.OperationId,
                        documentPath = snapshotResult.DocumentPath,
                        clientVersion = snapshotResult.AcceptedClientVersion,
                        semanticOutcome,
                        durationMs,
                        foregroundPreemptionRequested,
                    });
            }
            else
            {
                diagnosticLogging.WriteFault(
                    "completion_first_document_semantic_warmup_experiment_fault",
                    fault,
                    new
                    {
                        workloadOperationId = lease.OperationId,
                        documentPath = snapshotResult.DocumentPath,
                        clientVersion = snapshotResult.AcceptedClientVersion,
                        semanticOutcome,
                        durationMs,
                        foregroundPreemptionRequested,
                    });
            }

            try
            {
                lease.Retire();
            }
            catch (Exception retirementException)
            {
                diagnosticLogging.WriteFault(
                    "completion_first_document_semantic_warmup_experiment_fault",
                    retirementException,
                    new
                    {
                        workloadOperationId = lease.OperationId,
                        documentPath = snapshotResult.DocumentPath,
                        clientVersion = snapshotResult.AcceptedClientVersion,
                        semanticOutcome = "RetirementFault",
                        durationMs,
                        foregroundPreemptionRequested,
                    });
            }
        }
    }

    private void RequestFirstDocumentSemanticWarmupPreemption()
    {
        CancellationTokenSource? preemptionSource = null;
        lock (_sync)
        {
            if (!IsFirstDocumentSemanticWarmupActiveLocked()
                || _firstDocumentSemanticWarmupPreemptionSource is not CancellationTokenSource activeSource
                || activeSource.IsCancellationRequested
                || _firstDocumentSemanticWarmupForegroundPreemptionRequested)
            {
                return;
            }

            _firstDocumentSemanticWarmupForegroundPreemptionRequested = true;
            preemptionSource = activeSource;
        }

        CancelNoThrow(preemptionSource);
        _diagnosticLogging.WriteEvent(
            "completion_first_document_semantic_warmup_experiment_preemption_requested");
    }

    private bool IsFirstDocumentSemanticWarmupActiveLocked()
        => _firstDocumentSemanticWarmupState is FirstDocumentSemanticWarmupState.Starting
            or FirstDocumentSemanticWarmupState.Running;

    private void MarkFirstDocumentSemanticWarmupTerminal()
    {
        lock (_sync)
        {
            _firstDocumentSemanticWarmupState = FirstDocumentSemanticWarmupState.Terminal;
        }
    }

    private static object CreateWarmupCandidateDetails(
        DocumentSnapshotOperationResult snapshotResult,
        string reason)
    {
        WorkspacePublicationIdentity? publicationIdentity = snapshotResult.WorkspacePublicationIdentity;
        return new
        {
            reason,
            documentPath = snapshotResult.DocumentPath,
            clientGeneration = snapshotResult.ClientGeneration,
            clientVersion = snapshotResult.AcceptedClientVersion,
            workspaceGeneration = publicationIdentity?.WorkspaceGeneration,
            workspacePublicationVersion = publicationIdentity?.PublicationVersion,
            roslynGeneration = snapshotResult.RoslynGeneration,
            roslynDocumentVersion = snapshotResult.RoslynDocumentVersion,
        };
    }

    private static void CancelNoThrow(CancellationTokenSource? cancellationSource)
    {
        if (cancellationSource is null)
            return;

        try
        {
            cancellationSource.Cancel(throwOnFirstException: false);
        }
        catch (Exception)
        {
            // Cancellation is best effort and must not turn foreground/shutdown signaling into a fault.
        }
    }

    private static DocumentSemanticReadinessOutcome? ValidateSynchronizedState(DocumentSemanticReadinessRequest request, WorkspacePublication publication, DocumentSynchronizationDocumentSnapshot snapshot)
    {
        if (snapshot.ClientGeneration != request.ClientGeneration) return DocumentSemanticReadinessOutcome.StaleEpoch;
        if (snapshot.EpochId != request.EpochId) return DocumentSemanticReadinessOutcome.EpochConflict;
        if (request.ClientVersion < snapshot.AcceptedClientVersion) return DocumentSemanticReadinessOutcome.StaleVersion;
        if (request.ClientVersion > snapshot.AcceptedClientVersion) return DocumentSemanticReadinessOutcome.DocumentNotSynchronized;
        if (!snapshot.HasCurrentAuthoritySnapshot) return DocumentSemanticReadinessOutcome.DocumentNotSynchronized;
        if (!snapshot.IsCurrentWorkspaceSource) return DocumentSemanticReadinessOutcome.DocumentNotInWorkspace;
        if (!snapshot.IsOpenInRoslyn || snapshot.RoslynGeneration <= 0 || snapshot.RoslynLspVersion <= 0) return DocumentSemanticReadinessOutcome.DocumentNotOpen;
        if (snapshot.LastWorkspacePublicationIdentity != publication.Identity) return DocumentSemanticReadinessOutcome.WorkspaceUnavailable;
        return null;
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

    private static DocumentSemanticCorrelationIdentity ToCorrelation(DocumentSynchronizationDocumentSnapshot snapshot)
        => new(snapshot.DocumentPath, snapshot.LastWorkspacePublicationIdentity, snapshot.RoslynGeneration, snapshot.RoslynOverlayRevision, snapshot.RoslynLspVersion);

    private DocumentSemanticReadinessResult Superseded(
        DocumentSemanticReadinessRequest request,
        DocumentSynchronizationDocumentSnapshot snapshot,
        WorkspacePublication publication,
        long workloadOperationId,
        SemanticReadinessTimingState? timing)
    {
        DocumentSemanticReadinessResult result = DocumentSemanticReadinessResult.Failure(
            DocumentSemanticReadinessOutcome.Unavailable,
            request,
            snapshot,
            publication);

        WriteEvent(
            "semantic_readiness_superseded",
            request,
            snapshot,
            DocumentSemanticReadinessOutcome.Unavailable.ToString(),
            workloadOperationId,
            timing,
            diagnosticCount: null,
            expectedRoslynOverlayRevision: snapshot.RoslynOverlayRevision);
        return result;
    }

    private DocumentSemanticReadinessResult Reject(
        DocumentSemanticReadinessOutcome outcome,
        DocumentSemanticReadinessRequest request,
        DocumentSynchronizationDocumentSnapshot? snapshot,
        WorkspacePublication? publication,
        long workloadOperationId,
        SemanticReadinessTimingState? timing,
        string? path = null)
    {
        timing?.CompletePreProofValidation();
        DocumentSemanticReadinessResult result = DocumentSemanticReadinessResult.Failure(outcome, request, snapshot, publication, path);
        WriteEvent(
            "semantic_readiness_rejected",
            request,
            snapshot,
            outcome.ToString(),
            workloadOperationId,
            timing,
            diagnosticCount: null);
        return result;
    }

    private static DocumentSemanticReadinessResult SuccessResult(DocumentSemanticReadinessOutcome outcome, DocumentSemanticReadinessRequest request, DocumentSynchronizationDocumentSnapshot snapshot)
        => new(outcome, request.ClientGeneration, request.EpochId, snapshot.DocumentPath, snapshot.AcceptedClientVersion, snapshot.LastWorkspacePublicationIdentity, snapshot.RoslynGeneration, snapshot.RoslynLspVersion, snapshot.RoslynOverlayRevision);

    private void PruneStaleProofsLocked(WorkspacePublicationIdentity publication, long roslynGeneration, long overlayRevision)
    {
        foreach (string key in _proofs.Where(pair => pair.Value.WorkspacePublicationIdentity != publication || pair.Value.RoslynGeneration != roslynGeneration || pair.Value.RoslynOverlayRevision != overlayRevision).Select(pair => pair.Key).ToArray())
            _proofs.Remove(key);
    }

    private void RemoveOldestDeterministicProofLocked(string preservePath)
    {
        string? victim = _proofs.Keys.Where(key => !DocumentIdentity.PlatformPathComparer.Equals(key, preservePath)).OrderBy(key => key, StringComparer.Ordinal).FirstOrDefault();
        if (victim is not null) _proofs.Remove(victim);
    }

    private void WriteEvent(
        string eventName,
        DocumentSemanticReadinessRequest request,
        DocumentSynchronizationDocumentSnapshot? snapshot,
        string outcome,
        long workloadOperationId,
        SemanticReadinessTimingState? timing,
        int? diagnosticCount,
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
        double durationMs = timing?.GetTotalDurationMs()
            ?? 0;
        double? explicitWorkDurationMs = timing?.GetExplicitWorkDurationMs();
        double? unattributedDurationMs = explicitWorkDurationMs is double explicitDuration
            ? durationMs - explicitDuration
            : null;

        _diagnosticLogging.WriteEvent(eventName, new
        {
            documentPath = snapshot?.DocumentPath ?? request.DocumentPath,
            clientGeneration = request.ClientGeneration,
            clientVersion = request.ClientVersion,
            workspaceGeneration = publication?.WorkspaceGeneration,
            workspacePublicationVersion = publication?.PublicationVersion,
            roslynGeneration = snapshot?.RoslynGeneration,
            roslynDocumentVersion = snapshot?.RoslynLspVersion,
            roslynOverlayRevision = snapshot?.RoslynOverlayRevision,
            expectedRoslynOverlayRevision,
            workloadOperationId,
            durationMs,
            preProofValidationDurationMs = timing?.PreProofValidationDurationMs,
            proofLookupDurationMs = timing?.ProofLookupDurationMs,
            overlayCancellationSetupDurationMs = timing?.OverlayCancellationSetupDurationMs,
            roslynSemanticObservedDurationMs = timing?.RoslynSemanticObservedDurationMs,
            roslynSemanticSenderCaptureDurationMs = timing?.RoslynSemanticTiming.SenderCaptureDurationMs,
            roslynSemanticDiagnosticClientTotalDurationMs = timing?.RoslynSemanticTiming.DiagnosticClientTotalDurationMs,
            roslynSemanticDiagnosticRpcDurationMs = timing?.RoslynSemanticTiming.DiagnosticRpcDurationMs,
            roslynSemanticDiagnosticResponseInspectionDurationMs = timing?.RoslynSemanticTiming.DiagnosticResponseInspectionDurationMs,
            roslynSemanticPostRpcValidationDurationMs = timing?.RoslynSemanticTiming.PostRpcGenerationValidationDurationMs,
            roslynSemanticHostTotalDurationMs = timing?.RoslynSemanticTiming.HostTotalDurationMs,
            postDiagnosticRevalidationDurationMs = timing?.PostDiagnosticRevalidationDurationMs,
            proofCommitDurationMs = timing?.ProofCommitDurationMs,
            explicitWorkDurationMs,
            unattributedDurationMs,
            diagnosticCount,
            outcome,
        });
    }

    private enum FirstDocumentSemanticWarmupState
    {
        NotStarted,
        Starting,
        Running,
        Terminal,
    }

    private sealed class SemanticReadinessTimingState
    {
        private readonly long _started;

        public SemanticReadinessTimingState(long started)
        {
            _started = started;
        }

        public double? PreProofValidationDurationMs { get; private set; }
        public double? ProofLookupDurationMs { get; private set; }
        public double? OverlayCancellationSetupDurationMs { get; private set; }
        public double? RoslynSemanticObservedDurationMs { get; private set; }
        public RoslynSemanticReadinessTiming RoslynSemanticTiming { get; private set; }
        public double? PostDiagnosticRevalidationDurationMs { get; private set; }
        public double? ProofCommitDurationMs { get; private set; }

        public void CompletePreProofValidation()
        {
            if (PreProofValidationDurationMs is null)
                PreProofValidationDurationMs = Elapsed(_started);
        }

        public void SetProofLookupDuration(long started)
            => ProofLookupDurationMs = Elapsed(started);

        public void SetOverlayCancellationSetupDuration(long started)
            => OverlayCancellationSetupDurationMs = Elapsed(started);

        public void SetRoslynSemanticObservedDuration(long started)
            => RoslynSemanticObservedDurationMs = Elapsed(started);

        public void SetRoslynTiming(RoslynSemanticReadinessTiming timing)
            => RoslynSemanticTiming = timing;

        public void SetPostDiagnosticRevalidationDuration(long started)
            => PostDiagnosticRevalidationDurationMs = Elapsed(started);

        public void SetProofCommitDuration(long started)
            => ProofCommitDurationMs = Elapsed(started);

        public double GetTotalDurationMs()
            => Elapsed(_started);

        public double GetExplicitWorkDurationMs()
            => (PreProofValidationDurationMs ?? 0)
                + (ProofLookupDurationMs ?? 0)
                + (OverlayCancellationSetupDurationMs ?? 0)
                + (RoslynSemanticObservedDurationMs ?? 0)
                + (PostDiagnosticRevalidationDurationMs ?? 0)
                + (ProofCommitDurationMs ?? 0);

        private static double Elapsed(long started)
            => Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;
    }
}
