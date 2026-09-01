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

    public Task<DocumentSemanticReadinessResult> EnsureReadyAsync(
        DocumentSemanticReadinessRequest request,
        WorkloadExecutionLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Lane != WorkloadLane.SemanticReadiness)
            throw new InvalidOperationException("semantic readiness requires the semantic-readiness workload lane.");

        return EnsureReadyCoreAsync(request, cancellationToken);
    }

    internal Task<DocumentSemanticReadinessResult> EnsureReadyForCompletionAsync(
        DocumentSemanticReadinessRequest request,
        WorkloadExecutionLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Lane != WorkloadLane.Completion)
            throw new InvalidOperationException("completion-owned semantic readiness requires the completion workload lane.");

        return EnsureReadyCoreAsync(request, cancellationToken);
    }

    private async Task<DocumentSemanticReadinessResult> EnsureReadyCoreAsync(
        DocumentSemanticReadinessRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool diagnosticsEnabled = _diagnosticLogging.IsEnabled;
        long started = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;

        if (request.SchemaVersion != CodeServiceProtocol.SemanticReadinessSchemaVersion
            || request.ClientGeneration <= 0 || request.EpochId == Guid.Empty
            || request.ClientVersion <= 0 || string.IsNullOrWhiteSpace(request.DocumentPath))
            return Reject(DocumentSemanticReadinessOutcome.InvalidRequest, request, null, null, started);

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
                return Reject(DocumentSemanticReadinessOutcome.Unavailable, request, null, null, started);
        }

        if (!_workspaceHost.TryGetCurrentPublication(out WorkspacePublication publication))
            return Reject(DocumentSemanticReadinessOutcome.WorkspaceUnavailable, request, null, null, started);

        DocumentIdentityCreationResult identityResult = DocumentIdentity.TryCreate(
            request.DocumentPath, publication.WorkspaceIdentity, publication.ProjectSnapshot);
        if (!identityResult.IsSuccess)
            return Reject(DocumentSemanticReadinessOutcome.InvalidRequest, request, null, publication, started);
        DocumentIdentity identity = identityResult.Identity!;
        if (!identityResult.IsCurrentWorkspaceSource)
            return Reject(DocumentSemanticReadinessOutcome.DocumentNotInWorkspace, request, null, publication, started, identity.RelativePath);

        if (!_documentSynchronizationHost.TryGetCurrentAuthority(out DocumentClientAuthority authority))
            return Reject(DocumentSemanticReadinessOutcome.DocumentNotSynchronized, request, null, publication, started, identity.RelativePath);
        if (request.ClientGeneration < authority.ClientGeneration)
            return Reject(DocumentSemanticReadinessOutcome.StaleEpoch, request, null, publication, started, identity.RelativePath);
        if (request.ClientGeneration > authority.ClientGeneration)
            return Reject(DocumentSemanticReadinessOutcome.DocumentNotSynchronized, request, null, publication, started, identity.RelativePath);
        if (request.EpochId != authority.EpochId)
            return Reject(DocumentSemanticReadinessOutcome.EpochConflict, request, null, publication, started, identity.RelativePath);

        if (!_documentSynchronizationHost.TryGetDocumentSnapshot(identity.RelativePath, publication.ProjectSnapshot, out DocumentSynchronizationDocumentSnapshot snapshot))
            return Reject(DocumentSemanticReadinessOutcome.DocumentNotSynchronized, request, null, publication, started, identity.RelativePath);

        DocumentSemanticReadinessOutcome? admissionFailure = ValidateSynchronizedState(request, publication, snapshot);
        if (admissionFailure is not null)
            return Reject(admissionFailure.Value, request, snapshot, publication, started);

        if (!IsRoslynCorrelationCurrent(publication, snapshot))
            return Reject(DocumentSemanticReadinessOutcome.RoslynUnavailable, request, snapshot, publication, started);

        DocumentSemanticCorrelationIdentity correlation = ToCorrelation(snapshot);
        lock (_sync)
        {
            PruneStaleProofsLocked(publication.Identity, snapshot.RoslynGeneration, snapshot.RoslynOverlayRevision);
            if (_proofs.TryGetValue(snapshot.DocumentPath, out DocumentSemanticCorrelationIdentity proof) && proof == correlation)
            {
                WriteEvent("semantic_readiness_proof_reused", request, snapshot, "AlreadyCurrent", started, null);
                return SuccessResult(DocumentSemanticReadinessOutcome.AlreadyCurrent, request, snapshot);
            }
        }

        WriteEvent("semantic_readiness_request_started", request, snapshot, "Started", started, null);
        RoslynSemanticReadinessResult roslynResult = await _roslynLanguageServerHost.EstablishSemanticReadinessAsync(
            publication.WorkspaceIdentity, publication.Identity, snapshot.RoslynGeneration, identity, cancellationToken).ConfigureAwait(false);
        WriteEvent("semantic_readiness_diagnostic_completed", request, snapshot, roslynResult.Outcome.ToString(), started, roslynResult.DiagnosticCount);

        if (roslynResult.Outcome == RoslynSemanticReadinessOutcome.SemanticUnavailable)
            return Reject(DocumentSemanticReadinessOutcome.SemanticUnavailable, request, snapshot, publication, started);
        if (roslynResult.Outcome == RoslynSemanticReadinessOutcome.RoslynUnavailable)
            return Reject(DocumentSemanticReadinessOutcome.RoslynUnavailable, request, snapshot, publication, started);
        if (roslynResult.Outcome == RoslynSemanticReadinessOutcome.Stale)
            return Reject(DocumentSemanticReadinessOutcome.Unavailable, request, snapshot, publication, started);

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
            return Reject(DocumentSemanticReadinessOutcome.Unavailable, request, snapshot, publication, started);
        }

        if (!IsRoslynCorrelationCurrent(currentPublication, currentSnapshot))
            return Reject(DocumentSemanticReadinessOutcome.RoslynUnavailable, request, currentSnapshot, currentPublication, started);

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
                return Reject(DocumentSemanticReadinessOutcome.Unavailable, request, currentSnapshot, currentPublication, started);
            _proofs[currentSnapshot.DocumentPath] = correlation;
            if (_proofs.Count > MaxProofCount)
                RemoveOldestDeterministicProofLocked(currentSnapshot.DocumentPath);
        }

        WriteEvent("semantic_readiness_committed", request, currentSnapshot, "Success", started, roslynResult.DiagnosticCount);
        return SuccessResult(DocumentSemanticReadinessOutcome.Success, request, currentSnapshot);
    }

    public void BeginShutdown() { lock (_sync) { if (!_disposed) _shuttingDown = true; } }
    public void Dispose() { lock (_sync) { _shuttingDown = true; _proofs.Clear(); _disposed = true; } }

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

    private DocumentSemanticReadinessResult Reject(DocumentSemanticReadinessOutcome outcome, DocumentSemanticReadinessRequest request, DocumentSynchronizationDocumentSnapshot? snapshot, WorkspacePublication? publication, long started, string? path = null)
    {
        DocumentSemanticReadinessResult result = DocumentSemanticReadinessResult.Failure(outcome, request, snapshot, publication, path);
        WriteEvent("semantic_readiness_rejected", request, snapshot, outcome.ToString(), started, null);
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

    private void WriteEvent(string eventName, DocumentSemanticReadinessRequest request, DocumentSynchronizationDocumentSnapshot? snapshot, string outcome, long started, int? diagnosticCount)
    {
        if (!_diagnosticLogging.IsEnabled) { _diagnosticLogging.WriteEvent(eventName); return; }
        WorkspacePublicationIdentity? publication = snapshot is DocumentSynchronizationDocumentSnapshot value
            ? value.LastWorkspacePublicationIdentity
            : null;
        _diagnosticLogging.WriteEvent(eventName, new {
            documentPath = snapshot?.DocumentPath ?? request.DocumentPath,
            clientGeneration = request.ClientGeneration,
            clientVersion = request.ClientVersion,
            workspaceGeneration = publication?.WorkspaceGeneration,
            workspacePublicationVersion = publication?.PublicationVersion,
            roslynGeneration = snapshot?.RoslynGeneration,
            roslynDocumentVersion = snapshot?.RoslynLspVersion,
            roslynOverlayRevision = snapshot?.RoslynOverlayRevision,
            durationMs = started == 0 ? (double?)null : Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds,
            diagnosticCount,
            outcome,
        });
    }
}
