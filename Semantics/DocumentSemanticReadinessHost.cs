using System.Diagnostics;

namespace SystemExplorer.CodeService;

internal sealed class DocumentSemanticReadinessHost : IDisposable
{
    private const int MaxProofCount = DocumentSynchronizationLimits.MaxTrackedOpenDocuments;
    private readonly object _sync = new();
    private readonly WorkloadCoordinator _workloadCoordinator;
    private readonly WorkspaceHost _workspaceHost;
    private readonly DocumentSynchronizationHost _documentSynchronizationHost;
    private readonly RoslynLanguageServerHost _roslynLanguageServerHost;
    private readonly DiagnosticLogging _diagnosticLogging;
    private readonly Dictionary<string, DocumentSemanticCorrelationIdentity> _proofs =
        new(DocumentIdentity.PlatformPathComparer);
    private StartupCompletionReadinessState _startupCompletionReadinessState =
        StartupCompletionReadinessState.NotStarted;
    private long? _startupCompletionReadinessRoslynGeneration;
    private StartupCompletionReadinessCandidate? _startupCompletionReadinessLatestCandidate;
    private TaskCompletionSource<StartupCompletionReadinessJoinResult>? _startupCompletionReadinessSignal;
    private bool _startupCompletionWarmupAttemptOwned;
    private StartupCompletionReadinessCandidate? _startupCompletionWarmupActiveCandidate;
    private Task? _startupCompletionWarmupTask;
    private CancellationTokenSource? _startupCompletionWarmupPreemptionSource;
    private bool _startupCompletionWarmupForegroundPreemptionRequested;
    private bool _startupCompletionWarmupForegroundOperationCompletedWithoutSatisfaction;
    private long? _startupCompletionForegroundSemanticReadinessRoslynGeneration;
    private bool _startupCompletionWarmupShutdownCancellationRequested;
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

    internal void ObserveDocumentForStartupCompletionReadiness(
        DocumentSnapshotOperationResult snapshotResult)
    {
        if (!TryCreateStartupCompletionReadinessCandidate(snapshotResult, out StartupCompletionReadinessCandidate candidate))
            return;

        AdoptStartupCompletionReadinessCandidate(candidate, "Snapshot");
    }

    internal async Task<StartupCompletionReadinessJoinResult> JoinStartupCompletionReadinessAsync(
        DocumentSemanticReadinessRequest request,
        WorkspacePublicationIdentity expectedPublicationIdentity,
        long expectedRoslynGeneration,
        int expectedRoslynLspVersion,
        long expectedRoslynOverlayRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryCreateStartupCompletionReadinessCandidate(
                request,
                expectedPublicationIdentity,
                expectedRoslynGeneration,
                expectedRoslynLspVersion,
                expectedRoslynOverlayRevision,
                out StartupCompletionReadinessCandidate candidate))
        {
            return new StartupCompletionReadinessJoinResult(
                StartupCompletionReadinessJoinOutcome.Unavailable,
                expectedRoslynGeneration);
        }

        StartupCompletionReadinessAdoption adoption = AdoptStartupCompletionReadinessCandidate(
            candidate,
            "Completion");
        if (adoption.ImmediateResult is StartupCompletionReadinessJoinResult immediateResult)
            return immediateResult;

        Task<StartupCompletionReadinessJoinResult> waitTask = adoption.WaitTask
            ?? throw new InvalidOperationException("unresolved startup completion readiness requires a generation-scoped signal.");

        long started = Stopwatch.GetTimestamp();
        _diagnosticLogging.WriteEvent(
            "completion_startup_readiness_join_started",
            CreateStartupCompletionReadinessCandidateDetails(candidate, "Completion"));

        try
        {
            StartupCompletionReadinessJoinResult result = await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            WriteStartupCompletionReadinessJoinCompleted(candidate, result.Outcome.ToString(), started);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteStartupCompletionReadinessJoinCompleted(candidate, "Canceled", started);
            throw;
        }
    }

    public async Task<DocumentSemanticReadinessResult> EnsureReadyAsync(
        DocumentSemanticReadinessRequest request,
        WorkloadExecutionLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Lane != WorkloadLane.SemanticReadiness)
            throw new InvalidOperationException("semantic readiness requires the semantic-readiness workload lane.");

        long? foregroundStartupRoslynGeneration = BeginStartupCompletionForegroundSemanticReadiness(request);

        try
        {
            DocumentSemanticReadinessResult result = await EnsureReadyCoreAsync(
                request,
                lease.OperationId,
                cancellationToken,
                cancellationToken).ConfigureAwait(false);

            if ((result.Outcome is DocumentSemanticReadinessOutcome.Success
                    or DocumentSemanticReadinessOutcome.AlreadyCurrent)
                && result.RoslynGeneration is long roslynGeneration
                && roslynGeneration > 0)
            {
                MarkStartupCompletionReadinessSatisfiedByForegroundSemanticReady(result, roslynGeneration);
            }
            else if (foregroundStartupRoslynGeneration is long foregroundGeneration)
            {
                ReleaseStartupCompletionReadinessWaitersAfterForegroundFailure(
                    foregroundGeneration,
                    result.Outcome.ToString());
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            if (foregroundStartupRoslynGeneration is long foregroundGeneration)
            {
                ReleaseStartupCompletionReadinessWaitersAfterForegroundFailure(
                    foregroundGeneration,
                    "Canceled");
            }
            throw;
        }
        catch (Exception)
        {
            if (foregroundStartupRoslynGeneration is long foregroundGeneration)
            {
                ReleaseStartupCompletionReadinessWaitersAfterForegroundFailure(
                    foregroundGeneration,
                    "Fault");
            }
            throw;
        }
        finally
        {
            EndStartupCompletionForegroundSemanticReadiness(foregroundStartupRoslynGeneration);
        }
    }

    private async Task<StartupCompletionGenerationWarmupResult> EstablishStartupCompletionGenerationReadinessAsync(
        StartupCompletionWarmupReservation reservation,
        WorkloadExecutionLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Lane != WorkloadLane.SemanticWarmup)
            throw new InvalidOperationException("startup completion generation warm-up requires the semantic-warmup workload lane.");

        cancellationToken.ThrowIfCancellationRequested();

        StartupCompletionGenerationWarmupOutcome preRpcValidation = TryCaptureStartupCompletionGenerationWarmupContext(
            reservation,
            out WorkspacePublication publication,
            out DocumentIdentity documentIdentity);
        if (preRpcValidation != StartupCompletionGenerationWarmupOutcome.Success)
        {
            return new StartupCompletionGenerationWarmupResult(
                preRpcValidation,
                RoslynOutcome: null,
                DiagnosticCount: null);
        }

        RoslynDocumentDiagnosticScope diagnosticScope = RoslynDocumentDiagnosticScope.CompilerSemanticOnly;
        long diagnosticStarted = Stopwatch.GetTimestamp();
        _diagnosticLogging.WriteEvent(
            "completion_startup_readiness_diagnostic_started",
            CreateStartupCompletionReadinessDiagnosticDetails(
                reservation.Candidate,
                lease.OperationId,
                diagnosticScope,
                roslynOutcome: "Started",
                diagnosticCount: null,
                durationMs: null));

        RoslynSemanticReadinessResult roslynResult;
        try
        {
            roslynResult = await _roslynLanguageServerHost.EstablishSemanticReadinessAsync(
                publication.WorkspaceIdentity,
                reservation.Candidate.WorkspacePublicationIdentity,
                reservation.Candidate.RoslynGeneration,
                documentIdentity,
                diagnosticScope,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _diagnosticLogging.WriteEvent(
                "completion_startup_readiness_diagnostic_completed",
                CreateStartupCompletionReadinessDiagnosticDetails(
                    reservation.Candidate,
                    lease.OperationId,
                    diagnosticScope,
                    roslynOutcome: "Canceled",
                    diagnosticCount: null,
                    durationMs: Stopwatch.GetElapsedTime(diagnosticStarted, Stopwatch.GetTimestamp()).TotalMilliseconds));
            throw;
        }

        double diagnosticDurationMs = Stopwatch.GetElapsedTime(
            diagnosticStarted,
            Stopwatch.GetTimestamp()).TotalMilliseconds;

        cancellationToken.ThrowIfCancellationRequested();

        StartupCompletionGenerationWarmupOutcome postRpcValidation =
            ValidateStartupCompletionGenerationEnvironment(reservation);

        StartupCompletionGenerationWarmupOutcome outcome = postRpcValidation != StartupCompletionGenerationWarmupOutcome.Success
            ? postRpcValidation
            : roslynResult.Outcome switch
            {
                RoslynSemanticReadinessOutcome.Success => StartupCompletionGenerationWarmupOutcome.Success,
                RoslynSemanticReadinessOutcome.SemanticUnavailable => StartupCompletionGenerationWarmupOutcome.SemanticUnavailable,
                RoslynSemanticReadinessOutcome.RoslynUnavailable => StartupCompletionGenerationWarmupOutcome.RoslynUnavailable,
                RoslynSemanticReadinessOutcome.Stale => StartupCompletionGenerationWarmupOutcome.EnvironmentChanged,
                _ => StartupCompletionGenerationWarmupOutcome.RoslynUnavailable,
            };

        _diagnosticLogging.WriteEvent(
            "completion_startup_readiness_diagnostic_completed",
            CreateStartupCompletionReadinessDiagnosticDetails(
                reservation.Candidate,
                lease.OperationId,
                diagnosticScope,
                roslynResult.Outcome.ToString(),
                roslynResult.DiagnosticCount,
                diagnosticDurationMs));

        return new StartupCompletionGenerationWarmupResult(
            outcome,
            roslynResult.Outcome,
            roslynResult.DiagnosticCount);
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
                RoslynDocumentDiagnosticScope.AllEnabledDocumentSources,
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
        TaskCompletionSource<StartupCompletionReadinessJoinResult>? readinessSignal;
        long? roslynGeneration;
        lock (_sync)
        {
            if (_disposed)
                return;

            _shuttingDown = true;
            if (_startupCompletionWarmupAttemptOwned)
            {
                _startupCompletionWarmupShutdownCancellationRequested = true;
                preemptionSource = _startupCompletionWarmupPreemptionSource;
            }
            else
            {
                preemptionSource = null;
            }

            readinessSignal = IsStartupCompletionReadinessUnresolvedLocked()
                ? _startupCompletionReadinessSignal
                : null;
            roslynGeneration = _startupCompletionReadinessRoslynGeneration;
        }

        CancelNoThrow(preemptionSource);
        if (readinessSignal is not null && roslynGeneration is long generation)
        {
            readinessSignal.TrySetResult(new StartupCompletionReadinessJoinResult(
                StartupCompletionReadinessJoinOutcome.Unavailable,
                generation));
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? preemptionSource;
        TaskCompletionSource<StartupCompletionReadinessJoinResult>? readinessSignal;
        long? roslynGeneration;
        bool disposePreemptionSource;
        lock (_sync)
        {
            if (_disposed)
                return;

            _shuttingDown = true;
            _proofs.Clear();
            _disposed = true;
            if (_startupCompletionWarmupAttemptOwned)
                _startupCompletionWarmupShutdownCancellationRequested = true;
            preemptionSource = _startupCompletionWarmupPreemptionSource;
            disposePreemptionSource = !_startupCompletionWarmupAttemptOwned;
            readinessSignal = _startupCompletionReadinessSignal;
            roslynGeneration = _startupCompletionReadinessRoslynGeneration;
        }

        CancelNoThrow(preemptionSource);
        if (readinessSignal is not null && roslynGeneration is long generation)
        {
            readinessSignal.TrySetResult(new StartupCompletionReadinessJoinResult(
                StartupCompletionReadinessJoinOutcome.Unavailable,
                generation));
        }

        if (disposePreemptionSource)
            preemptionSource?.Dispose();
    }

    private StartupCompletionGenerationWarmupOutcome TryCaptureStartupCompletionGenerationWarmupContext(
        StartupCompletionWarmupReservation reservation,
        out WorkspacePublication publication,
        out DocumentIdentity documentIdentity)
    {
        publication = null!;
        documentIdentity = null!;

        lock (_sync)
        {
            if (_disposed
                || _shuttingDown
                || !_startupCompletionWarmupAttemptOwned
                || !ReferenceEquals(_startupCompletionWarmupPreemptionSource, reservation.PreemptionSource)
                || !Equals(_startupCompletionWarmupActiveCandidate, reservation.Candidate)
                || _startupCompletionReadinessRoslynGeneration != reservation.Candidate.RoslynGeneration)
            {
                return StartupCompletionGenerationWarmupOutcome.EnvironmentChanged;
            }
        }

        if (!_workspaceHost.TryGetCurrentPublication(out publication)
            || publication.Identity != reservation.Candidate.WorkspacePublicationIdentity)
        {
            return StartupCompletionGenerationWarmupOutcome.EnvironmentChanged;
        }

        DocumentIdentityCreationResult identityResult = DocumentIdentity.TryCreate(
            reservation.Candidate.Request.DocumentPath,
            publication.WorkspaceIdentity,
            publication.ProjectSnapshot);
        if (!identityResult.IsSuccess || !identityResult.IsCurrentWorkspaceSource)
            return StartupCompletionGenerationWarmupOutcome.SeedUnavailable;

        documentIdentity = identityResult.Identity!;
        if (!_documentSynchronizationHost.TryGetDocumentSnapshot(
                documentIdentity.RelativePath,
                publication.ProjectSnapshot,
                out DocumentSynchronizationDocumentSnapshot currentSnapshot)
            || !currentSnapshot.HasCurrentAuthoritySnapshot
            || !currentSnapshot.IsCurrentWorkspaceSource
            || !currentSnapshot.IsOpenInRoslyn)
        {
            return StartupCompletionGenerationWarmupOutcome.SeedUnavailable;
        }

        if (currentSnapshot.LastWorkspacePublicationIdentity != reservation.Candidate.WorkspacePublicationIdentity
            || currentSnapshot.RoslynGeneration != reservation.Candidate.RoslynGeneration)
        {
            return StartupCompletionGenerationWarmupOutcome.EnvironmentChanged;
        }

        RoslynLanguageServerSnapshot roslynSnapshot = _roslynLanguageServerHost.GetSnapshot();
        if (roslynSnapshot.RoslynGeneration != reservation.Candidate.RoslynGeneration
            || !roslynSnapshot.IsProjectLoaded
            || !_roslynLanguageServerHost.IsProjectLoadCurrentFor(
                publication.WorkspaceIdentity,
                reservation.Candidate.WorkspacePublicationIdentity,
                roslynSnapshot))
        {
            return StartupCompletionGenerationWarmupOutcome.EnvironmentChanged;
        }

        return StartupCompletionGenerationWarmupOutcome.Success;
    }

    private StartupCompletionGenerationWarmupOutcome ValidateStartupCompletionGenerationEnvironment(
        StartupCompletionWarmupReservation reservation)
    {
        lock (_sync)
        {
            if (_disposed
                || _shuttingDown
                || !_startupCompletionWarmupAttemptOwned
                || !ReferenceEquals(_startupCompletionWarmupPreemptionSource, reservation.PreemptionSource)
                || !Equals(_startupCompletionWarmupActiveCandidate, reservation.Candidate))
            {
                return StartupCompletionGenerationWarmupOutcome.EnvironmentChanged;
            }
        }

        if (!_workspaceHost.TryGetCurrentPublication(out WorkspacePublication publication)
            || publication.Identity != reservation.Candidate.WorkspacePublicationIdentity)
        {
            return StartupCompletionGenerationWarmupOutcome.EnvironmentChanged;
        }

        RoslynLanguageServerSnapshot roslynSnapshot = _roslynLanguageServerHost.GetSnapshot();
        if (roslynSnapshot.RoslynGeneration != reservation.Candidate.RoslynGeneration
            || !roslynSnapshot.IsProjectLoaded
            || !_roslynLanguageServerHost.IsProjectLoadCurrentFor(
                publication.WorkspaceIdentity,
                reservation.Candidate.WorkspacePublicationIdentity,
                roslynSnapshot))
        {
            return StartupCompletionGenerationWarmupOutcome.EnvironmentChanged;
        }

        return StartupCompletionGenerationWarmupOutcome.Success;
    }

    private bool TryCreateStartupCompletionReadinessCandidate(
        DocumentSnapshotOperationResult snapshotResult,
        out StartupCompletionReadinessCandidate candidate)
    {
        candidate = null!;
        if (snapshotResult.Outcome is not (DocumentSynchronizationOutcome.Success or DocumentSynchronizationOutcome.AlreadyCurrent)
            || snapshotResult.ClientGeneration is not long clientGeneration
            || clientGeneration <= 0
            || snapshotResult.EpochId is not Guid epochId
            || epochId == Guid.Empty
            || string.IsNullOrWhiteSpace(snapshotResult.DocumentPath)
            || snapshotResult.AcceptedClientVersion is not long acceptedClientVersion
            || acceptedClientVersion <= 0
            || snapshotResult.WorkspacePublicationIdentity is not WorkspacePublicationIdentity publicationIdentity
            || snapshotResult.RoslynGeneration is not long roslynGeneration
            || roslynGeneration <= 0
            || snapshotResult.RoslynDocumentVersion is not int roslynDocumentVersion
            || roslynDocumentVersion <= 0)
        {
            return false;
        }

        DocumentSemanticReadinessRequest request = new(
            CodeServiceProtocol.SemanticReadinessSchemaVersion,
            clientGeneration,
            epochId,
            snapshotResult.DocumentPath!,
            acceptedClientVersion);

        return TryCreateStartupCompletionReadinessCandidate(
            request,
            publicationIdentity,
            roslynGeneration,
            roslynDocumentVersion,
            expectedRoslynOverlayRevision: null,
            out candidate);
    }

    private bool TryCreateStartupCompletionReadinessCandidate(
        DocumentSemanticReadinessRequest request,
        WorkspacePublicationIdentity expectedPublicationIdentity,
        long expectedRoslynGeneration,
        int expectedRoslynLspVersion,
        long expectedRoslynOverlayRevision,
        out StartupCompletionReadinessCandidate candidate)
        => TryCreateStartupCompletionReadinessCandidate(
            request,
            expectedPublicationIdentity,
            expectedRoslynGeneration,
            expectedRoslynLspVersion,
            (long?)expectedRoslynOverlayRevision,
            out candidate);

    private bool TryCreateStartupCompletionReadinessCandidate(
        DocumentSemanticReadinessRequest request,
        WorkspacePublicationIdentity expectedPublicationIdentity,
        long expectedRoslynGeneration,
        int expectedRoslynLspVersion,
        long? expectedRoslynOverlayRevision,
        out StartupCompletionReadinessCandidate candidate)
    {
        candidate = null!;
        if (request.SchemaVersion != CodeServiceProtocol.SemanticReadinessSchemaVersion
            || request.ClientGeneration <= 0
            || request.EpochId == Guid.Empty
            || request.ClientVersion <= 0
            || string.IsNullOrWhiteSpace(request.DocumentPath)
            || expectedRoslynGeneration <= 0
            || expectedRoslynLspVersion <= 0
            || expectedRoslynOverlayRevision is long invalidRevision && invalidRevision <= 0)
        {
            return false;
        }

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
                return false;
        }

        if (!_workspaceHost.TryGetCurrentPublication(out WorkspacePublication publication)
            || publication.Identity != expectedPublicationIdentity
            || !_documentSynchronizationHost.TryGetCurrentAuthority(out DocumentClientAuthority authority)
            || authority.ClientGeneration != request.ClientGeneration
            || authority.EpochId != request.EpochId
            || !_documentSynchronizationHost.TryGetDocumentSnapshot(
                request.DocumentPath,
                publication.ProjectSnapshot,
                out DocumentSynchronizationDocumentSnapshot snapshot)
            || snapshot.ClientGeneration != request.ClientGeneration
            || snapshot.EpochId != request.EpochId
            || snapshot.AcceptedClientVersion != request.ClientVersion
            || snapshot.LastWorkspacePublicationIdentity != expectedPublicationIdentity
            || snapshot.RoslynGeneration != expectedRoslynGeneration
            || snapshot.RoslynLspVersion != expectedRoslynLspVersion
            || (expectedRoslynOverlayRevision is long expectedRevision
                && snapshot.RoslynOverlayRevision != expectedRevision)
            || !snapshot.HasCurrentAuthoritySnapshot
            || !snapshot.IsCurrentWorkspaceSource
            || !snapshot.IsOpenInRoslyn
            || !IsRoslynCorrelationCurrent(publication, snapshot))
        {
            return false;
        }

        candidate = new StartupCompletionReadinessCandidate(
            request,
            expectedPublicationIdentity,
            expectedRoslynGeneration,
            expectedRoslynLspVersion,
            snapshot.RoslynOverlayRevision);
        return true;
    }

    private StartupCompletionReadinessAdoption AdoptStartupCompletionReadinessCandidate(
        StartupCompletionReadinessCandidate candidate,
        string source)
    {
        StartupCompletionReadinessGenerationReset? generationReset = null;
        StartupCompletionWarmupReservation? reservation = null;
        StartupCompletionReadinessJoinResult? immediateResult = null;
        Task<StartupCompletionReadinessJoinResult>? waitTask = null;
        bool candidateObserved = false;
        bool candidateReplaced = false;

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                immediateResult = new StartupCompletionReadinessJoinResult(
                    StartupCompletionReadinessJoinOutcome.Unavailable,
                    candidate.RoslynGeneration);
            }
            else
            {
                generationReset = ResetStartupCompletionReadinessGenerationLocked(candidate.RoslynGeneration);

                if (_startupCompletionReadinessState == StartupCompletionReadinessState.Satisfied)
                {
                    immediateResult = new StartupCompletionReadinessJoinResult(
                        StartupCompletionReadinessJoinOutcome.Satisfied,
                        candidate.RoslynGeneration);
                }
                else if (_startupCompletionReadinessState == StartupCompletionReadinessState.Degraded)
                {
                    immediateResult = new StartupCompletionReadinessJoinResult(
                        StartupCompletionReadinessJoinOutcome.Degraded,
                        candidate.RoslynGeneration);
                }
                else
                {
                    if (_startupCompletionReadinessLatestCandidate is null)
                    {
                        candidateObserved = true;
                    }
                    else if (!_startupCompletionReadinessLatestCandidate.Equals(candidate))
                    {
                        candidateReplaced = true;
                    }

                    _startupCompletionReadinessLatestCandidate = candidate;
                    reservation = TryReserveStartupCompletionWarmupLocked();
                    waitTask = _startupCompletionReadinessSignal?.Task;
                }
            }
        }

        PublishStartupCompletionReadinessGenerationReset(generationReset, "CandidateGenerationChanged");

        if (candidateObserved)
        {
            _diagnosticLogging.WriteEvent(
                "completion_startup_readiness_candidate_observed",
                CreateStartupCompletionReadinessCandidateDetails(candidate, source));
        }
        else if (candidateReplaced)
        {
            _diagnosticLogging.WriteEvent(
                "completion_startup_readiness_candidate_replaced",
                CreateStartupCompletionReadinessCandidateDetails(candidate, source));
        }

        if (reservation is not null)
            StartReservedStartupCompletionWarmup(reservation);

        return new StartupCompletionReadinessAdoption(waitTask, immediateResult);
    }

    private StartupCompletionWarmupReservation? TryReserveStartupCompletionWarmupLocked()
    {
        if (_disposed
            || _shuttingDown
            || _startupCompletionWarmupAttemptOwned
            || _startupCompletionReadinessState != StartupCompletionReadinessState.NotStarted
            || _startupCompletionReadinessLatestCandidate is not StartupCompletionReadinessCandidate candidate
            || _startupCompletionReadinessRoslynGeneration != candidate.RoslynGeneration
            || _startupCompletionForegroundSemanticReadinessRoslynGeneration == candidate.RoslynGeneration)
        {
            return null;
        }

        CancellationTokenSource preemptionSource = new();
        _startupCompletionWarmupAttemptOwned = true;
        _startupCompletionWarmupActiveCandidate = candidate;
        _startupCompletionWarmupPreemptionSource = preemptionSource;
        _startupCompletionWarmupForegroundPreemptionRequested = false;
        _startupCompletionWarmupForegroundOperationCompletedWithoutSatisfaction = false;
        _startupCompletionWarmupShutdownCancellationRequested = false;
        _startupCompletionReadinessState = StartupCompletionReadinessState.Starting;
        return new StartupCompletionWarmupReservation(candidate, preemptionSource);
    }

    private void StartReservedStartupCompletionWarmup(
        StartupCompletionWarmupReservation reservation)
    {
        WorkloadAdmissionResult admission;
        try
        {
            admission = _workloadCoordinator.TryAdmitExclusive(WorkloadLane.SemanticWarmup);
        }
        catch (Exception exception)
        {
            FinishStartupCompletionWarmupWithoutRun(
                reservation,
                "AdmissionFault",
                exception,
                shutdown: IsStartupCompletionReadinessShuttingDown());
            return;
        }

        if (admission.Status != WorkloadAdmissionStatus.Admitted
            || admission.Lease is not WorkloadExecutionLease lease)
        {
            FinishStartupCompletionWarmupWithoutRun(
                reservation,
                $"Admission{admission.Status}",
                fault: null,
                shutdown: admission.Status == WorkloadAdmissionStatus.ShuttingDown
                    || IsStartupCompletionReadinessShuttingDown());
            return;
        }

        lock (_sync)
        {
            if (_startupCompletionWarmupAttemptOwned
                && ReferenceEquals(_startupCompletionWarmupPreemptionSource, reservation.PreemptionSource)
                && Equals(_startupCompletionWarmupActiveCandidate, reservation.Candidate)
                && _startupCompletionReadinessRoslynGeneration == reservation.Candidate.RoslynGeneration
                && IsStartupCompletionReadinessUnresolvedLocked())
            {
                _startupCompletionReadinessState = StartupCompletionReadinessState.Running;
            }
        }

        _diagnosticLogging.WriteEvent(
            "completion_startup_readiness_attempt_started",
            CreateStartupCompletionReadinessAttemptDetails(
                reservation.Candidate,
                lease.OperationId,
                "Started"));

        Task warmupTask = RunStartupCompletionWarmupAsync(
            reservation,
            lease);
        lock (_sync)
        {
            if (_startupCompletionWarmupAttemptOwned
                && ReferenceEquals(_startupCompletionWarmupPreemptionSource, reservation.PreemptionSource)
                && Equals(_startupCompletionWarmupActiveCandidate, reservation.Candidate))
            {
                _startupCompletionWarmupTask = warmupTask;
            }
        }
    }

    private void FinishStartupCompletionWarmupWithoutRun(
        StartupCompletionWarmupReservation reservation,
        string reason,
        Exception? fault,
        bool shutdown)
    {
        TaskCompletionSource<StartupCompletionReadinessJoinResult>? signal = null;
        StartupCompletionReadinessJoinResult? signalResult = null;
        StartupCompletionWarmupReservation? nextReservation = null;
        bool degraded = false;

        lock (_sync)
        {
            if (_startupCompletionWarmupAttemptOwned
                && ReferenceEquals(_startupCompletionWarmupPreemptionSource, reservation.PreemptionSource)
                && Equals(_startupCompletionWarmupActiveCandidate, reservation.Candidate))
            {
                ClearStartupCompletionWarmupAttemptLocked();
                if (_startupCompletionReadinessRoslynGeneration == reservation.Candidate.RoslynGeneration
                    && IsStartupCompletionReadinessUnresolvedLocked())
                {
                    if (shutdown || _disposed || _shuttingDown)
                    {
                        signal = _startupCompletionReadinessSignal;
                        signalResult = new StartupCompletionReadinessJoinResult(
                            StartupCompletionReadinessJoinOutcome.Unavailable,
                            reservation.Candidate.RoslynGeneration);
                    }
                    else
                    {
                        _startupCompletionReadinessState = StartupCompletionReadinessState.Degraded;
                        _startupCompletionReadinessLatestCandidate = null;
                        signal = _startupCompletionReadinessSignal;
                        signalResult = new StartupCompletionReadinessJoinResult(
                            StartupCompletionReadinessJoinOutcome.Degraded,
                            reservation.Candidate.RoslynGeneration);
                        degraded = true;
                    }
                }
                else if (!shutdown && !_disposed && !_shuttingDown)
                {
                    nextReservation = TryReserveStartupCompletionWarmupLocked();
                }
            }
        }

        reservation.PreemptionSource.Dispose();

        if (fault is not null)
        {
            _diagnosticLogging.WriteFault(
                "completion_startup_readiness_attempt_fault",
                fault,
                CreateStartupCompletionReadinessCandidateDetails(reservation.Candidate, reason));
        }
        else
        {
            _diagnosticLogging.WriteEvent(
                degraded
                    ? "completion_startup_readiness_attempt_degraded"
                    : "completion_startup_readiness_attempt_unavailable",
                CreateStartupCompletionReadinessCandidateDetails(reservation.Candidate, reason));
        }

        if (signal is not null && signalResult is StartupCompletionReadinessJoinResult result)
            signal.TrySetResult(result);

        if (degraded)
        {
            WriteStartupCompletionReadinessGoalEvent(
                "completion_startup_readiness_goal_degraded",
                reservation.Candidate,
                reason,
                workloadOperationId: null);
        }

        if (nextReservation is not null)
        {
            _diagnosticLogging.WriteEvent(
                "completion_startup_readiness_attempt_rearmed",
                CreateStartupCompletionReadinessCandidateDetails(nextReservation.Candidate, "GenerationReplacement"));
            StartReservedStartupCompletionWarmup(nextReservation);
        }
    }

    private async Task RunStartupCompletionWarmupAsync(
        StartupCompletionWarmupReservation reservation,
        WorkloadExecutionLease lease)
    {
        await Task.Yield();

        long started = Stopwatch.GetTimestamp();
        StartupCompletionWarmupDisposition disposition = StartupCompletionWarmupDisposition.Degraded;
        string warmupOutcome = StartupCompletionGenerationWarmupOutcome.RoslynUnavailable.ToString();
        string? roslynOutcome = null;
        int? diagnosticCount = null;
        Exception? fault = null;
        long? replacementRoslynGeneration = null;

        try
        {
            using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                lease.ServiceWorkShutdownToken,
                reservation.PreemptionSource.Token);

            StartupCompletionGenerationWarmupResult result = await EstablishStartupCompletionGenerationReadinessAsync(
                reservation,
                lease,
                operationCancellation.Token).ConfigureAwait(false);
            warmupOutcome = result.Outcome.ToString();
            roslynOutcome = result.RoslynOutcome?.ToString();
            diagnosticCount = result.DiagnosticCount;

            if (result.Outcome == StartupCompletionGenerationWarmupOutcome.Success)
            {
                disposition = StartupCompletionWarmupDisposition.Satisfied;
            }
            else
            {
                if (IsStartupCompletionReadinessShuttingDown()
                    || lease.ServiceWorkShutdownToken.IsCancellationRequested)
                {
                    disposition = StartupCompletionWarmupDisposition.Shutdown;
                }
                else
                {
                    RoslynLanguageServerSnapshot roslynSnapshot = _roslynLanguageServerHost.GetSnapshot();
                    if (roslynSnapshot.RoslynGeneration > 0
                        && roslynSnapshot.RoslynGeneration != reservation.Candidate.RoslynGeneration)
                    {
                        disposition = StartupCompletionWarmupDisposition.GenerationChanged;
                        replacementRoslynGeneration = roslynSnapshot.RoslynGeneration;
                    }
                    else if (result.Outcome == StartupCompletionGenerationWarmupOutcome.SeedUnavailable)
                    {
                        disposition = StartupCompletionWarmupDisposition.SeedUnavailable;
                    }
                    else if (result.Outcome == StartupCompletionGenerationWarmupOutcome.EnvironmentChanged)
                    {
                        disposition = StartupCompletionWarmupDisposition.EnvironmentChanged;
                    }
                    else
                    {
                        disposition = StartupCompletionWarmupDisposition.Degraded;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (reservation.PreemptionSource.IsCancellationRequested)
        {
            bool shutdownCancellationRequested;
            bool foregroundPreemptionRequested;
            lock (_sync)
            {
                shutdownCancellationRequested = _startupCompletionWarmupShutdownCancellationRequested;
                foregroundPreemptionRequested = _startupCompletionWarmupForegroundPreemptionRequested;
            }

            if (shutdownCancellationRequested || lease.ServiceWorkShutdownToken.IsCancellationRequested)
            {
                warmupOutcome = "ServiceShutdown";
                disposition = StartupCompletionWarmupDisposition.Shutdown;
            }
            else
            {
                RoslynLanguageServerSnapshot roslynSnapshot = _roslynLanguageServerHost.GetSnapshot();
                if (roslynSnapshot.RoslynGeneration > 0
                    && roslynSnapshot.RoslynGeneration != reservation.Candidate.RoslynGeneration)
                {
                    warmupOutcome = "GenerationChanged";
                    disposition = StartupCompletionWarmupDisposition.GenerationChanged;
                    replacementRoslynGeneration = roslynSnapshot.RoslynGeneration;
                }
                else if (foregroundPreemptionRequested)
                {
                    warmupOutcome = "ForegroundPreempted";
                    disposition = StartupCompletionWarmupDisposition.ForegroundPreempted;
                }
                else
                {
                    warmupOutcome = "UnexpectedPreemption";
                    disposition = StartupCompletionWarmupDisposition.Degraded;
                }
            }
        }
        catch (OperationCanceledException) when (lease.ServiceWorkShutdownToken.IsCancellationRequested)
        {
            warmupOutcome = "ServiceShutdown";
            disposition = StartupCompletionWarmupDisposition.Shutdown;
        }
        catch (Exception exception)
        {
            fault = exception;
            warmupOutcome = "Fault";
            disposition = StartupCompletionWarmupDisposition.Degraded;
        }
        finally
        {
            double durationMs = Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;
            bool retirementSucceeded = true;
            try
            {
                lease.Retire();
            }
            catch (Exception retirementException)
            {
                retirementSucceeded = false;
                _diagnosticLogging.WriteFault(
                    "completion_startup_readiness_attempt_fault",
                    retirementException,
                    CreateStartupCompletionReadinessAttemptDetails(
                        reservation.Candidate,
                        lease.OperationId,
                        "RetirementFault"));
            }

            FinishStartupCompletionWarmupAfterRetirement(
                reservation,
                lease.OperationId,
                disposition,
                warmupOutcome,
                roslynOutcome,
                diagnosticCount,
                durationMs,
                fault,
                retirementSucceeded,
                replacementRoslynGeneration);
        }
    }

    private void FinishStartupCompletionWarmupAfterRetirement(
        StartupCompletionWarmupReservation reservation,
        long workloadOperationId,
        StartupCompletionWarmupDisposition disposition,
        string warmupOutcome,
        string? roslynOutcome,
        int? diagnosticCount,
        double durationMs,
        Exception? fault,
        bool retirementSucceeded,
        long? replacementRoslynGeneration)
    {
        TaskCompletionSource<StartupCompletionReadinessJoinResult>? terminalSignal = null;
        StartupCompletionReadinessJoinResult? terminalResult = null;
        StartupCompletionReadinessGenerationReset? generationReset = null;
        StartupCompletionWarmupReservation? nextReservation = null;
        string? rearmReason = null;
        bool goalSatisfied = false;
        bool goalDegraded = false;
        StartupCompletionReadinessCandidate? latestObservedCandidate = null;

        lock (_sync)
        {
            if (_startupCompletionReadinessLatestCandidate is StartupCompletionReadinessCandidate latestCandidate
                && latestCandidate.RoslynGeneration == reservation.Candidate.RoslynGeneration
                && !latestCandidate.Equals(reservation.Candidate))
            {
                latestObservedCandidate = latestCandidate;
            }

            if (_startupCompletionWarmupAttemptOwned
                && ReferenceEquals(_startupCompletionWarmupPreemptionSource, reservation.PreemptionSource)
                && Equals(_startupCompletionWarmupActiveCandidate, reservation.Candidate))
            {
                ClearStartupCompletionWarmupAttemptLocked();
            }

            if (!retirementSucceeded)
            {
                if (_startupCompletionReadinessRoslynGeneration is long currentGeneration
                    && IsStartupCompletionReadinessUnresolvedLocked())
                {
                    _startupCompletionReadinessState = StartupCompletionReadinessState.Degraded;
                    _startupCompletionReadinessLatestCandidate = null;
                    terminalSignal = _startupCompletionReadinessSignal;
                    terminalResult = new StartupCompletionReadinessJoinResult(
                        StartupCompletionReadinessJoinOutcome.Degraded,
                        currentGeneration);
                    goalDegraded = true;
                }
            }
            else
            {
                if (replacementRoslynGeneration is long replacementGeneration
                    && replacementGeneration > 0
                    && replacementGeneration != _startupCompletionReadinessRoslynGeneration)
                {
                    generationReset = ResetStartupCompletionReadinessGenerationLocked(replacementGeneration);
                }

                if (_startupCompletionReadinessRoslynGeneration == reservation.Candidate.RoslynGeneration)
                {
                    switch (disposition)
                    {
                        case StartupCompletionWarmupDisposition.Satisfied:
                            if (_startupCompletionReadinessState != StartupCompletionReadinessState.Satisfied)
                            {
                                _startupCompletionReadinessState = StartupCompletionReadinessState.Satisfied;
                                _startupCompletionReadinessLatestCandidate = null;
                                terminalSignal = _startupCompletionReadinessSignal;
                                terminalResult = new StartupCompletionReadinessJoinResult(
                                    StartupCompletionReadinessJoinOutcome.Satisfied,
                                    reservation.Candidate.RoslynGeneration);
                                goalSatisfied = true;
                            }
                            break;

                        case StartupCompletionWarmupDisposition.SeedUnavailable:
                        case StartupCompletionWarmupDisposition.EnvironmentChanged:
                            if (IsStartupCompletionReadinessUnresolvedLocked())
                            {
                                _startupCompletionReadinessState = StartupCompletionReadinessState.NotStarted;
                                if (_startupCompletionReadinessLatestCandidate is not StartupCompletionReadinessCandidate latest
                                    || latest.Equals(reservation.Candidate))
                                {
                                    _startupCompletionReadinessLatestCandidate = null;
                                }

                                if (_startupCompletionReadinessLatestCandidate is not null)
                                {
                                    nextReservation = TryReserveStartupCompletionWarmupLocked();
                                    rearmReason = disposition == StartupCompletionWarmupDisposition.SeedUnavailable
                                        ? "SeedUnavailableLatestCandidate"
                                        : "EnvironmentChangedLatestCandidate";
                                }
                            }
                            break;

                        case StartupCompletionWarmupDisposition.ForegroundPreempted:
                            if (IsStartupCompletionReadinessUnresolvedLocked())
                            {
                                _startupCompletionReadinessState = StartupCompletionReadinessState.NotStarted;
                                if (_startupCompletionWarmupForegroundOperationCompletedWithoutSatisfaction)
                                {
                                    terminalSignal = RotateStartupCompletionReadinessSignalLocked();
                                    terminalResult = new StartupCompletionReadinessJoinResult(
                                        StartupCompletionReadinessJoinOutcome.Unavailable,
                                        reservation.Candidate.RoslynGeneration);
                                    _startupCompletionWarmupForegroundOperationCompletedWithoutSatisfaction = false;
                                }
                            }
                            break;

                        case StartupCompletionWarmupDisposition.Degraded:
                            if (IsStartupCompletionReadinessUnresolvedLocked())
                            {
                                _startupCompletionReadinessState = StartupCompletionReadinessState.Degraded;
                                _startupCompletionReadinessLatestCandidate = null;
                                terminalSignal = _startupCompletionReadinessSignal;
                                terminalResult = new StartupCompletionReadinessJoinResult(
                                    StartupCompletionReadinessJoinOutcome.Degraded,
                                    reservation.Candidate.RoslynGeneration);
                                goalDegraded = true;
                            }
                            break;

                        case StartupCompletionWarmupDisposition.GenerationChanged:
                        case StartupCompletionWarmupDisposition.Shutdown:
                            break;

                        default:
                            throw new InvalidOperationException("unknown startup completion warm-up disposition.");
                    }
                }
                else if (!_disposed
                    && !_shuttingDown
                    && IsStartupCompletionReadinessUnresolvedLocked()
                    && _startupCompletionReadinessLatestCandidate is not null)
                {
                    nextReservation = TryReserveStartupCompletionWarmupLocked();
                    rearmReason = "GenerationReplacement";
                }
            }
        }

        reservation.PreemptionSource.Dispose();
        PublishStartupCompletionReadinessGenerationReset(generationReset, "WarmupObservedGenerationChange");

        bool overlayAdvancedDuringAttempt = latestObservedCandidate is StartupCompletionReadinessCandidate observedCandidate
            && observedCandidate.RoslynOverlayRevision != reservation.Candidate.RoslynOverlayRevision;

        if (fault is null)
        {
            _diagnosticLogging.WriteEvent(
                "completion_startup_readiness_attempt_completed",
                new
                {
                    workloadOperationId,
                    documentPath = reservation.Candidate.Request.DocumentPath,
                    clientGeneration = reservation.Candidate.Request.ClientGeneration,
                    clientVersion = reservation.Candidate.Request.ClientVersion,
                    workspaceGeneration = reservation.Candidate.WorkspacePublicationIdentity.WorkspaceGeneration,
                    workspacePublicationVersion = reservation.Candidate.WorkspacePublicationIdentity.PublicationVersion,
                    roslynGeneration = reservation.Candidate.RoslynGeneration,
                    roslynDocumentVersion = reservation.Candidate.RoslynLspVersion,
                    roslynOverlayRevision = reservation.Candidate.RoslynOverlayRevision,
                    seedClientVersion = reservation.Candidate.Request.ClientVersion,
                    seedRoslynDocumentVersion = reservation.Candidate.RoslynLspVersion,
                    seedRoslynOverlayRevision = reservation.Candidate.RoslynOverlayRevision,
                    latestObservedClientVersion = latestObservedCandidate?.Request.ClientVersion,
                    latestObservedRoslynDocumentVersion = latestObservedCandidate?.RoslynLspVersion,
                    latestObservedRoslynOverlayRevision = latestObservedCandidate?.RoslynOverlayRevision,
                    overlayAdvancedDuringAttempt,
                    warmupOutcome,
                    roslynOutcome,
                    diagnosticCount,
                    disposition = disposition.ToString(),
                    durationMs,
                });
        }
        else
        {
            _diagnosticLogging.WriteFault(
                "completion_startup_readiness_attempt_fault",
                fault,
                new
                {
                    workloadOperationId,
                    documentPath = reservation.Candidate.Request.DocumentPath,
                    clientGeneration = reservation.Candidate.Request.ClientGeneration,
                    clientVersion = reservation.Candidate.Request.ClientVersion,
                    workspaceGeneration = reservation.Candidate.WorkspacePublicationIdentity.WorkspaceGeneration,
                    workspacePublicationVersion = reservation.Candidate.WorkspacePublicationIdentity.PublicationVersion,
                    roslynGeneration = reservation.Candidate.RoslynGeneration,
                    roslynDocumentVersion = reservation.Candidate.RoslynLspVersion,
                    roslynOverlayRevision = reservation.Candidate.RoslynOverlayRevision,
                    seedClientVersion = reservation.Candidate.Request.ClientVersion,
                    seedRoslynDocumentVersion = reservation.Candidate.RoslynLspVersion,
                    seedRoslynOverlayRevision = reservation.Candidate.RoslynOverlayRevision,
                    latestObservedClientVersion = latestObservedCandidate?.Request.ClientVersion,
                    latestObservedRoslynDocumentVersion = latestObservedCandidate?.RoslynLspVersion,
                    latestObservedRoslynOverlayRevision = latestObservedCandidate?.RoslynOverlayRevision,
                    overlayAdvancedDuringAttempt,
                    warmupOutcome,
                    roslynOutcome,
                    diagnosticCount,
                    disposition = disposition.ToString(),
                    durationMs,
                });
        }

        if (terminalSignal is not null && terminalResult is StartupCompletionReadinessJoinResult result)
            terminalSignal.TrySetResult(result);

        if (goalSatisfied)
        {
            WriteStartupCompletionReadinessGoalEvent(
                "completion_startup_readiness_goal_satisfied",
                reservation.Candidate,
                warmupOutcome,
                workloadOperationId);
        }
        else if (goalDegraded)
        {
            WriteStartupCompletionReadinessGoalEvent(
                "completion_startup_readiness_goal_degraded",
                reservation.Candidate,
                retirementSucceeded ? warmupOutcome : "RetirementFault",
                workloadOperationId);
        }

        if (nextReservation is not null)
        {
            _diagnosticLogging.WriteEvent(
                "completion_startup_readiness_attempt_rearmed",
                CreateStartupCompletionReadinessCandidateDetails(
                    nextReservation.Candidate,
                    rearmReason ?? "LatestCandidate"));
            StartReservedStartupCompletionWarmup(nextReservation);
        }
    }

    private long? BeginStartupCompletionForegroundSemanticReadiness(
        DocumentSemanticReadinessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryGetCurrentSemanticRequestRoslynGeneration(request, out long foregroundRoslynGeneration))
            return null;

        CancellationTokenSource? preemptionSource = null;
        StartupCompletionReadinessCandidate? activeCandidate = null;
        lock (_sync)
        {
            if (_disposed || _shuttingDown)
                return null;

            _startupCompletionForegroundSemanticReadinessRoslynGeneration = foregroundRoslynGeneration;

            if (_startupCompletionWarmupAttemptOwned
                && _startupCompletionWarmupPreemptionSource is CancellationTokenSource activeSource
                && !activeSource.IsCancellationRequested
                && !_startupCompletionWarmupForegroundPreemptionRequested
                && _startupCompletionWarmupActiveCandidate is StartupCompletionReadinessCandidate candidate
                && candidate.RoslynGeneration == foregroundRoslynGeneration
                && _startupCompletionReadinessRoslynGeneration == foregroundRoslynGeneration)
            {
                _startupCompletionWarmupForegroundPreemptionRequested = true;
                _startupCompletionWarmupForegroundOperationCompletedWithoutSatisfaction = false;
                preemptionSource = activeSource;
                activeCandidate = candidate;
            }
        }

        if (preemptionSource is not null && activeCandidate is StartupCompletionReadinessCandidate candidateToPreempt)
        {
            CancelNoThrow(preemptionSource);
            _diagnosticLogging.WriteEvent(
                "completion_startup_readiness_foreground_preemption_requested",
                CreateStartupCompletionReadinessCandidateDetails(candidateToPreempt, "ForegroundSemanticReady"));
        }

        return foregroundRoslynGeneration;
    }

    private void EndStartupCompletionForegroundSemanticReadiness(long? roslynGeneration)
    {
        if (roslynGeneration is not long generation)
            return;

        lock (_sync)
        {
            if (_startupCompletionForegroundSemanticReadinessRoslynGeneration == generation)
                _startupCompletionForegroundSemanticReadinessRoslynGeneration = null;
        }
    }

    private bool TryGetCurrentSemanticRequestRoslynGeneration(
        DocumentSemanticReadinessRequest request,
        out long roslynGeneration)
    {
        roslynGeneration = 0;
        if (request.SchemaVersion != CodeServiceProtocol.SemanticReadinessSchemaVersion
            || request.ClientGeneration <= 0
            || request.EpochId == Guid.Empty
            || request.ClientVersion <= 0
            || string.IsNullOrWhiteSpace(request.DocumentPath))
        {
            return false;
        }

        if (!_workspaceHost.TryGetCurrentPublication(out WorkspacePublication publication))
            return false;

        DocumentIdentityCreationResult identityResult = DocumentIdentity.TryCreate(
            request.DocumentPath,
            publication.WorkspaceIdentity,
            publication.ProjectSnapshot);
        if (!identityResult.IsSuccess || !identityResult.IsCurrentWorkspaceSource)
            return false;

        DocumentIdentity identity = identityResult.Identity!;
        if (!_documentSynchronizationHost.TryGetCurrentAuthority(out DocumentClientAuthority authority)
            || authority.ClientGeneration != request.ClientGeneration
            || authority.EpochId != request.EpochId
            || !_documentSynchronizationHost.TryGetDocumentSnapshot(
                identity.RelativePath,
                publication.ProjectSnapshot,
                out DocumentSynchronizationDocumentSnapshot snapshot)
            || ValidateSynchronizedState(request, publication, snapshot) is not null
            || !IsRoslynCorrelationCurrent(publication, snapshot))
        {
            return false;
        }

        roslynGeneration = snapshot.RoslynGeneration;
        return roslynGeneration > 0;
    }

    private void ReleaseStartupCompletionReadinessWaitersAfterForegroundFailure(
        long roslynGeneration,
        string reason)
    {
        TaskCompletionSource<StartupCompletionReadinessJoinResult>? signal = null;
        lock (_sync)
        {
            if (_disposed
                || _shuttingDown
                || _startupCompletionReadinessRoslynGeneration != roslynGeneration
                || !IsStartupCompletionReadinessUnresolvedLocked())
            {
                return;
            }

            if (_startupCompletionWarmupAttemptOwned
                && _startupCompletionWarmupActiveCandidate is StartupCompletionReadinessCandidate activeCandidate
                && activeCandidate.RoslynGeneration == roslynGeneration)
            {
                _startupCompletionWarmupForegroundOperationCompletedWithoutSatisfaction = true;
                return;
            }

            _startupCompletionReadinessState = StartupCompletionReadinessState.NotStarted;
            signal = RotateStartupCompletionReadinessSignalLocked();
            _startupCompletionWarmupForegroundOperationCompletedWithoutSatisfaction = false;
        }

        signal?.TrySetResult(new StartupCompletionReadinessJoinResult(
            StartupCompletionReadinessJoinOutcome.Unavailable,
            roslynGeneration));
        _diagnosticLogging.WriteEvent(
            "completion_startup_readiness_foreground_unsatisfied",
            new
            {
                reason,
                roslynGeneration,
                state = StartupCompletionReadinessState.NotStarted.ToString(),
            });
    }

    private void MarkStartupCompletionReadinessSatisfiedByForegroundSemanticReady(
        DocumentSemanticReadinessResult semanticResult,
        long roslynGeneration)
    {
        StartupCompletionReadinessGenerationReset? generationReset;
        TaskCompletionSource<StartupCompletionReadinessJoinResult>? signal = null;
        bool stateChanged = false;

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
                return;

            generationReset = ResetStartupCompletionReadinessGenerationLocked(roslynGeneration);
            if (_startupCompletionReadinessState != StartupCompletionReadinessState.Satisfied)
            {
                _startupCompletionReadinessState = StartupCompletionReadinessState.Satisfied;
                _startupCompletionReadinessLatestCandidate = null;
                _startupCompletionWarmupForegroundOperationCompletedWithoutSatisfaction = false;
                signal = _startupCompletionReadinessSignal;
                stateChanged = true;
            }
        }

        PublishStartupCompletionReadinessGenerationReset(
            generationReset,
            "ForegroundSemanticReadyGenerationChanged");

        signal?.TrySetResult(new StartupCompletionReadinessJoinResult(
            StartupCompletionReadinessJoinOutcome.Satisfied,
            roslynGeneration));

        if (stateChanged)
        {
            WorkspacePublicationIdentity? publicationIdentity = semanticResult.WorkspacePublicationIdentity;
            _diagnosticLogging.WriteEvent(
                "completion_startup_readiness_goal_satisfied",
                new
                {
                    reason = "ForegroundSemanticReady",
                    documentPath = semanticResult.DocumentPath,
                    clientGeneration = semanticResult.ClientGeneration,
                    clientVersion = semanticResult.AcceptedClientVersion,
                    workspaceGeneration = publicationIdentity?.WorkspaceGeneration,
                    workspacePublicationVersion = publicationIdentity?.PublicationVersion,
                    roslynGeneration,
                    roslynDocumentVersion = semanticResult.RoslynDocumentVersion,
                    roslynOverlayRevision = semanticResult.RoslynOverlayRevision,
                    semanticOutcome = semanticResult.Outcome.ToString(),
                });
        }
    }

    private StartupCompletionReadinessGenerationReset? ResetStartupCompletionReadinessGenerationLocked(
        long roslynGeneration)
    {
        if (_startupCompletionReadinessRoslynGeneration == roslynGeneration)
            return null;

        long? previousGeneration = _startupCompletionReadinessRoslynGeneration;
        TaskCompletionSource<StartupCompletionReadinessJoinResult>? previousSignal = _startupCompletionReadinessSignal;
        CancellationTokenSource? preemptionSource = null;
        if (_startupCompletionWarmupAttemptOwned
            && _startupCompletionWarmupActiveCandidate is StartupCompletionReadinessCandidate activeCandidate
            && activeCandidate.RoslynGeneration != roslynGeneration)
        {
            preemptionSource = _startupCompletionWarmupPreemptionSource;
        }

        _startupCompletionReadinessRoslynGeneration = roslynGeneration;
        _startupCompletionReadinessState = StartupCompletionReadinessState.NotStarted;
        _startupCompletionReadinessLatestCandidate = null;
        _startupCompletionWarmupForegroundOperationCompletedWithoutSatisfaction = false;
        _startupCompletionReadinessSignal = CreateStartupCompletionReadinessSignal();

        return new StartupCompletionReadinessGenerationReset(
            previousGeneration,
            roslynGeneration,
            previousSignal,
            preemptionSource,
            _startupCompletionReadinessState);
    }

    private void PublishStartupCompletionReadinessGenerationReset(
        StartupCompletionReadinessGenerationReset? reset,
        string reason)
    {
        if (reset is not StartupCompletionReadinessGenerationReset value)
            return;

        if (value.PreviousSignal is not null && value.PreviousRoslynGeneration is long previousGeneration)
        {
            value.PreviousSignal.TrySetResult(new StartupCompletionReadinessJoinResult(
                StartupCompletionReadinessJoinOutcome.GenerationChanged,
                previousGeneration));
        }

        CancelNoThrow(value.PreemptionSource);

        if (value.PreviousRoslynGeneration is not null)
        {
            _diagnosticLogging.WriteEvent(
                "completion_startup_readiness_generation_reset",
                new
                {
                    reason,
                    previousRoslynGeneration = value.PreviousRoslynGeneration,
                    roslynGeneration = value.NewRoslynGeneration,
                    state = value.NewState.ToString(),
                });
        }
    }

    private TaskCompletionSource<StartupCompletionReadinessJoinResult>? RotateStartupCompletionReadinessSignalLocked()
    {
        TaskCompletionSource<StartupCompletionReadinessJoinResult>? previousSignal = _startupCompletionReadinessSignal;
        _startupCompletionReadinessSignal = CreateStartupCompletionReadinessSignal();
        return previousSignal;
    }

    private static TaskCompletionSource<StartupCompletionReadinessJoinResult> CreateStartupCompletionReadinessSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool IsStartupCompletionReadinessShuttingDown()
    {
        lock (_sync)
        {
            return _disposed || _shuttingDown;
        }
    }

    private bool IsStartupCompletionReadinessUnresolvedLocked()
        => _startupCompletionReadinessState is StartupCompletionReadinessState.NotStarted
            or StartupCompletionReadinessState.Starting
            or StartupCompletionReadinessState.Running;

    private void ClearStartupCompletionWarmupAttemptLocked()
    {
        _startupCompletionWarmupAttemptOwned = false;
        _startupCompletionWarmupActiveCandidate = null;
        _startupCompletionWarmupTask = null;
        _startupCompletionWarmupPreemptionSource = null;
        _startupCompletionWarmupForegroundPreemptionRequested = false;
        _startupCompletionWarmupShutdownCancellationRequested = false;
    }

    private void WriteStartupCompletionReadinessJoinCompleted(
        StartupCompletionReadinessCandidate candidate,
        string outcome,
        long started)
    {
        _diagnosticLogging.WriteEvent(
            "completion_startup_readiness_join_completed",
            new
            {
                documentPath = candidate.Request.DocumentPath,
                clientGeneration = candidate.Request.ClientGeneration,
                clientVersion = candidate.Request.ClientVersion,
                workspaceGeneration = candidate.WorkspacePublicationIdentity.WorkspaceGeneration,
                workspacePublicationVersion = candidate.WorkspacePublicationIdentity.PublicationVersion,
                roslynGeneration = candidate.RoslynGeneration,
                roslynDocumentVersion = candidate.RoslynLspVersion,
                roslynOverlayRevision = candidate.RoslynOverlayRevision,
                outcome,
                durationMs = Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds,
            });
    }

    private static object CreateStartupCompletionReadinessCandidateDetails(
        StartupCompletionReadinessCandidate candidate,
        string reason)
        => new
        {
            reason,
            documentPath = candidate.Request.DocumentPath,
            clientGeneration = candidate.Request.ClientGeneration,
            clientVersion = candidate.Request.ClientVersion,
            workspaceGeneration = candidate.WorkspacePublicationIdentity.WorkspaceGeneration,
            workspacePublicationVersion = candidate.WorkspacePublicationIdentity.PublicationVersion,
            roslynGeneration = candidate.RoslynGeneration,
            roslynDocumentVersion = candidate.RoslynLspVersion,
            roslynOverlayRevision = candidate.RoslynOverlayRevision,
        };

    private static object CreateStartupCompletionReadinessAttemptDetails(
        StartupCompletionReadinessCandidate candidate,
        long workloadOperationId,
        string reason)
        => new
        {
            workloadOperationId,
            reason,
            documentPath = candidate.Request.DocumentPath,
            clientGeneration = candidate.Request.ClientGeneration,
            clientVersion = candidate.Request.ClientVersion,
            workspaceGeneration = candidate.WorkspacePublicationIdentity.WorkspaceGeneration,
            workspacePublicationVersion = candidate.WorkspacePublicationIdentity.PublicationVersion,
            roslynGeneration = candidate.RoslynGeneration,
            roslynDocumentVersion = candidate.RoslynLspVersion,
            roslynOverlayRevision = candidate.RoslynOverlayRevision,
            seedClientVersion = candidate.Request.ClientVersion,
            seedRoslynDocumentVersion = candidate.RoslynLspVersion,
            seedRoslynOverlayRevision = candidate.RoslynOverlayRevision,
        };

    private static object CreateStartupCompletionReadinessDiagnosticDetails(
        StartupCompletionReadinessCandidate candidate,
        long workloadOperationId,
        RoslynDocumentDiagnosticScope diagnosticScope,
        string roslynOutcome,
        int? diagnosticCount,
        double? durationMs)
        => new
        {
            workloadOperationId,
            documentPath = candidate.Request.DocumentPath,
            workspaceGeneration = candidate.WorkspacePublicationIdentity.WorkspaceGeneration,
            workspacePublicationVersion = candidate.WorkspacePublicationIdentity.PublicationVersion,
            roslynGeneration = candidate.RoslynGeneration,
            seedClientVersion = candidate.Request.ClientVersion,
            seedRoslynDocumentVersion = candidate.RoslynLspVersion,
            seedRoslynOverlayRevision = candidate.RoslynOverlayRevision,
            diagnosticScope = diagnosticScope.ToString(),
            diagnosticIdentifier = RoslynLanguageServerConstants.GetDocumentDiagnosticIdentifier(diagnosticScope),
            roslynOutcome,
            diagnosticCount,
            durationMs,
        };

    private void WriteStartupCompletionReadinessGoalEvent(
        string eventName,
        StartupCompletionReadinessCandidate candidate,
        string reason,
        long? workloadOperationId)
    {
        _diagnosticLogging.WriteEvent(
            eventName,
            new
            {
                workloadOperationId,
                reason,
                documentPath = candidate.Request.DocumentPath,
                clientGeneration = candidate.Request.ClientGeneration,
                clientVersion = candidate.Request.ClientVersion,
                workspaceGeneration = candidate.WorkspacePublicationIdentity.WorkspaceGeneration,
                workspacePublicationVersion = candidate.WorkspacePublicationIdentity.PublicationVersion,
                roslynGeneration = candidate.RoslynGeneration,
                roslynDocumentVersion = candidate.RoslynLspVersion,
                roslynOverlayRevision = candidate.RoslynOverlayRevision,
            });
    }

    private readonly record struct StartupCompletionReadinessAdoption(
        Task<StartupCompletionReadinessJoinResult>? WaitTask,
        StartupCompletionReadinessJoinResult? ImmediateResult);

    private readonly record struct StartupCompletionGenerationWarmupResult(
        StartupCompletionGenerationWarmupOutcome Outcome,
        RoslynSemanticReadinessOutcome? RoslynOutcome,
        int? DiagnosticCount);

    private sealed record StartupCompletionWarmupReservation(
        StartupCompletionReadinessCandidate Candidate,
        CancellationTokenSource PreemptionSource);

    private readonly record struct StartupCompletionReadinessGenerationReset(
        long? PreviousRoslynGeneration,
        long NewRoslynGeneration,
        TaskCompletionSource<StartupCompletionReadinessJoinResult>? PreviousSignal,
        CancellationTokenSource? PreemptionSource,
        StartupCompletionReadinessState NewState);

    private enum StartupCompletionWarmupDisposition
    {
        Satisfied,
        SeedUnavailable,
        EnvironmentChanged,
        ForegroundPreempted,
        GenerationChanged,
        Degraded,
        Shutdown,
    }

    private enum StartupCompletionGenerationWarmupOutcome
    {
        Success,
        SeedUnavailable,
        EnvironmentChanged,
        SemanticUnavailable,
        RoslynUnavailable,
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
