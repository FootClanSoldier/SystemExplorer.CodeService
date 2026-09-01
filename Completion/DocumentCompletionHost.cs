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
        WriteEvent("completion_request_started", request, null, null, "Started", null, null, false, started);

        if (request.SchemaVersion != CodeServiceProtocol.CompletionSchemaVersion
            || request.ClientGeneration <= 0
            || request.EpochId == Guid.Empty
            || request.ClientVersion <= 0
            || string.IsNullOrWhiteSpace(request.DocumentPath)
            || request.Line < 0
            || request.Line > DocumentCompletionLimits.MaxCompletionLine
            || request.Character < 0
            || request.Character > DocumentCompletionLimits.MaxCompletionCharacter)
        {
            return Reject(DocumentCompletionOutcome.InvalidRequest, request, null, null, started);
        }

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                return Reject(DocumentCompletionOutcome.Unavailable, request, null, null, started);
            }
        }

        if (!_workspaceHost.TryGetCurrentPublication(out WorkspacePublication publication))
        {
            return Reject(DocumentCompletionOutcome.WorkspaceUnavailable, request, null, null, started);
        }

        DocumentIdentityCreationResult identityResult = DocumentIdentity.TryCreate(
            request.DocumentPath,
            publication.WorkspaceIdentity,
            publication.ProjectSnapshot);
        if (!identityResult.IsSuccess)
        {
            return Reject(DocumentCompletionOutcome.InvalidRequest, request, null, publication, started);
        }

        DocumentIdentity identity = identityResult.Identity!;
        if (!identityResult.IsCurrentWorkspaceSource)
        {
            return Reject(
                DocumentCompletionOutcome.DocumentNotInWorkspace,
                request,
                null,
                publication,
                started,
                identity.RelativePath);
        }

        if (!_documentSynchronizationHost.TryGetCurrentAuthority(out DocumentClientAuthority authority))
        {
            return Reject(
                DocumentCompletionOutcome.DocumentNotSynchronized,
                request,
                null,
                publication,
                started,
                identity.RelativePath);
        }

        if (request.ClientGeneration < authority.ClientGeneration)
        {
            return Reject(DocumentCompletionOutcome.StaleEpoch, request, null, publication, started, identity.RelativePath);
        }

        if (request.ClientGeneration > authority.ClientGeneration)
        {
            return Reject(DocumentCompletionOutcome.DocumentNotSynchronized, request, null, publication, started, identity.RelativePath);
        }

        if (request.EpochId != authority.EpochId)
        {
            return Reject(DocumentCompletionOutcome.EpochConflict, request, null, publication, started, identity.RelativePath);
        }

        if (!_documentSynchronizationHost.TryGetDocumentSnapshot(
                identity.RelativePath,
                publication.ProjectSnapshot,
                out DocumentSynchronizationDocumentSnapshot initialSnapshot))
        {
            return Reject(
                DocumentCompletionOutcome.DocumentNotSynchronized,
                request,
                null,
                publication,
                started,
                identity.RelativePath);
        }

        DocumentCompletionOutcome? synchronizedFailure = ValidateSynchronizedState(request, publication, initialSnapshot);
        if (synchronizedFailure is not null)
        {
            return Reject(synchronizedFailure.Value, request, initialSnapshot, publication, started);
        }

        if (!IsRoslynCorrelationCurrent(publication, initialSnapshot))
        {
            return Reject(DocumentCompletionOutcome.RoslynUnavailable, request, initialSnapshot, publication, started);
        }

        DocumentSemanticReadinessRequest semanticRequest = new(
            CodeServiceProtocol.SemanticReadinessSchemaVersion,
            request.ClientGeneration,
            request.EpochId,
            identity.RelativePath,
            request.ClientVersion);

        DocumentSemanticReadinessResult semanticResult = await _documentSemanticReadinessHost
            .EnsureReadyForCompletionAsync(semanticRequest, lease, cancellationToken)
            .ConfigureAwait(false);

        WriteEvent(
            "completion_semantic_ready",
            request,
            initialSnapshot,
            semanticResult.Outcome.ToString(),
            "Pending",
            null,
            null,
            false,
            started);

        if (semanticResult.Outcome is not DocumentSemanticReadinessOutcome.Success
            and not DocumentSemanticReadinessOutcome.AlreadyCurrent)
        {
            return Reject(
                MapSemanticFailure(semanticResult.Outcome),
                request,
                initialSnapshot,
                publication,
                started);
        }

        if (!TryRevalidateExactState(
                request,
                identity,
                publication.Identity,
                initialSnapshot,
                out WorkspacePublication semanticPublication,
                out DocumentSynchronizationDocumentSnapshot semanticSnapshot))
        {
            return Reject(DocumentCompletionOutcome.Unavailable, request, initialSnapshot, publication, started);
        }

        if (!IsRoslynCorrelationCurrent(semanticPublication, semanticSnapshot))
        {
            return Reject(DocumentCompletionOutcome.RoslynUnavailable, request, semanticSnapshot, semanticPublication, started);
        }

        RoslynCompletionResult roslynResult = await _roslynLanguageServerHost.CompleteAsync(
            semanticPublication.WorkspaceIdentity,
            semanticPublication.Identity,
            semanticSnapshot.RoslynGeneration,
            identity,
            request.Line,
            request.Character,
            cancellationToken).ConfigureAwait(false);

        WriteEvent(
            "completion_roslyn_completed",
            request,
            semanticSnapshot,
            semanticResult.Outcome.ToString(),
            roslynResult.Outcome.ToString(),
            roslynResult.RawItemCount,
            roslynResult.Items.Count,
            roslynResult.IsIncomplete,
            started);

        if (roslynResult.Outcome != RoslynCompletionOutcome.Success)
        {
            DocumentCompletionOutcome failure = roslynResult.Outcome switch
            {
                RoslynCompletionOutcome.RoslynUnavailable => DocumentCompletionOutcome.RoslynUnavailable,
                RoslynCompletionOutcome.CompletionUnavailable => DocumentCompletionOutcome.CompletionUnavailable,
                RoslynCompletionOutcome.Stale => DocumentCompletionOutcome.Unavailable,
                _ => DocumentCompletionOutcome.CompletionUnavailable,
            };
            return Reject(failure, request, semanticSnapshot, semanticPublication, started);
        }

        if (roslynResult.RoslynGeneration != semanticSnapshot.RoslynGeneration
            || !TryRevalidateExactState(
                request,
                identity,
                semanticPublication.Identity,
                semanticSnapshot,
                out WorkspacePublication completedPublication,
                out DocumentSynchronizationDocumentSnapshot completedSnapshot)
            || !IsRoslynCorrelationCurrent(completedPublication, completedSnapshot))
        {
            return Reject(DocumentCompletionOutcome.Unavailable, request, semanticSnapshot, semanticPublication, started);
        }

        DocumentCompletionItem[] items = roslynResult.Items
            .Select(static item => new DocumentCompletionItem(
                item.DisplayText,
                item.InsertText,
                item.Kind,
                item.FilterText,
                item.SortText,
                item.Preselect))
            .ToArray();

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
            items,
            roslynResult.IsIncomplete,
            roslynResult.RawItemCount);

        WriteEvent(
            "completion_request_completed",
            request,
            completedSnapshot,
            semanticResult.Outcome.ToString(),
            DocumentCompletionOutcome.Success.ToString(),
            roslynResult.RawItemCount,
            items.Length,
            roslynResult.IsIncomplete,
            started);
        return result;
    }

    public void RecordTransportRejection(
        DocumentCompletionOutcome outcome,
        DocumentCompletionRequest? request = null)
    {
        WriteEvent(
            "completion_request_rejected",
            request,
            null,
            null,
            outcome.ToString(),
            null,
            null,
            false,
            started: 0);
    }

    public void BeginShutdown()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _shuttingDown = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _shuttingDown = true;
            _disposed = true;
        }
    }

    private static DocumentCompletionOutcome? ValidateSynchronizedState(
        DocumentCompletionRequest request,
        WorkspacePublication publication,
        DocumentSynchronizationDocumentSnapshot snapshot)
    {
        if (snapshot.ClientGeneration != request.ClientGeneration)
        {
            return DocumentCompletionOutcome.StaleEpoch;
        }

        if (snapshot.EpochId != request.EpochId)
        {
            return DocumentCompletionOutcome.EpochConflict;
        }

        if (request.ClientVersion < snapshot.AcceptedClientVersion)
        {
            return DocumentCompletionOutcome.StaleVersion;
        }

        if (request.ClientVersion > snapshot.AcceptedClientVersion)
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
        DocumentCompletionRequest request,
        DocumentIdentity identity,
        WorkspacePublicationIdentity expectedPublicationIdentity,
        DocumentSynchronizationDocumentSnapshot expectedSnapshot,
        out WorkspacePublication publication,
        out DocumentSynchronizationDocumentSnapshot snapshot)
    {
        if (!_workspaceHost.TryGetCurrentPublication(out publication)
            || publication.Identity != expectedPublicationIdentity
            || !_documentSynchronizationHost.TryGetCurrentAuthority(out DocumentClientAuthority authority)
            || authority.ClientGeneration != request.ClientGeneration
            || authority.EpochId != request.EpochId
            || !_documentSynchronizationHost.TryGetDocumentSnapshot(
                identity.RelativePath,
                publication.ProjectSnapshot,
                out snapshot)
            || snapshot.ClientGeneration != request.ClientGeneration
            || snapshot.EpochId != request.EpochId
            || snapshot.AcceptedClientVersion != request.ClientVersion
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

    private DocumentCompletionResult Reject(
        DocumentCompletionOutcome outcome,
        DocumentCompletionRequest request,
        DocumentSynchronizationDocumentSnapshot? snapshot,
        WorkspacePublication? publication,
        long started,
        string? documentPath = null)
    {
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
            null,
            outcome.ToString(),
            null,
            null,
            false,
            started);
        WriteEvent(
            "completion_request_completed",
            request,
            snapshot,
            null,
            outcome.ToString(),
            null,
            null,
            false,
            started);
        return result;
    }

    private static DocumentCompletionOutcome MapSemanticFailure(DocumentSemanticReadinessOutcome outcome)
        => outcome switch
        {
            DocumentSemanticReadinessOutcome.InvalidRequest => DocumentCompletionOutcome.InvalidRequest,
            DocumentSemanticReadinessOutcome.VersionMismatch => DocumentCompletionOutcome.VersionMismatch,
            DocumentSemanticReadinessOutcome.Busy => DocumentCompletionOutcome.Busy,
            DocumentSemanticReadinessOutcome.WorkspaceUnavailable => DocumentCompletionOutcome.WorkspaceUnavailable,
            DocumentSemanticReadinessOutcome.RoslynUnavailable => DocumentCompletionOutcome.RoslynUnavailable,
            DocumentSemanticReadinessOutcome.SemanticUnavailable => DocumentCompletionOutcome.SemanticUnavailable,
            DocumentSemanticReadinessOutcome.StaleEpoch => DocumentCompletionOutcome.StaleEpoch,
            DocumentSemanticReadinessOutcome.EpochConflict => DocumentCompletionOutcome.EpochConflict,
            DocumentSemanticReadinessOutcome.StaleVersion => DocumentCompletionOutcome.StaleVersion,
            DocumentSemanticReadinessOutcome.DocumentNotSynchronized => DocumentCompletionOutcome.DocumentNotSynchronized,
            DocumentSemanticReadinessOutcome.DocumentNotOpen => DocumentCompletionOutcome.DocumentNotOpen,
            DocumentSemanticReadinessOutcome.DocumentNotInWorkspace => DocumentCompletionOutcome.DocumentNotInWorkspace,
            DocumentSemanticReadinessOutcome.Unavailable => DocumentCompletionOutcome.Unavailable,
            DocumentSemanticReadinessOutcome.Success or DocumentSemanticReadinessOutcome.AlreadyCurrent =>
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "successful semantic readiness cannot be mapped to a completion failure."),
            _ => DocumentCompletionOutcome.Unavailable,
        };

    private void WriteEvent(
        string eventName,
        DocumentCompletionRequest? request,
        DocumentSynchronizationDocumentSnapshot? snapshot,
        string? semanticOutcome,
        string completionOutcome,
        int? rawItemCount,
        int? returnedItemCount,
        bool isIncomplete,
        long started)
    {
        if (!_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent(eventName);
            return;
        }

        WorkspacePublicationIdentity? publication = snapshot is DocumentSynchronizationDocumentSnapshot value
            ? value.LastWorkspacePublicationIdentity
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
            semanticOutcome,
            completionOutcome,
            rawItemCount,
            returnedItemCount,
            isIncomplete,
            durationMs = started == 0
                ? (double?)null
                : Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds,
        });
    }
}
