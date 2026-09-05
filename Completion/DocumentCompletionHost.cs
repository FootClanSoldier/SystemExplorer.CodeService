using System.Diagnostics;

namespace SystemExplorer.CodeService;

internal sealed class DocumentCompletionHost : IDisposable
{
    private readonly object _sync = new();
    private readonly WorkloadCoordinator _workloadCoordinator;
    private readonly WorkspaceHost _workspaceHost;
    private readonly DocumentSynchronizationHost _documentSynchronizationHost;
    private readonly RoslynLanguageServerHost _roslynLanguageServerHost;
    private readonly DiagnosticLogging _diagnosticLogging;
    private bool _shuttingDown;
    private bool _disposed;

    public DocumentCompletionHost(
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
            || request.Character > DocumentCompletionLimits.MaxCompletionCharacter)
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

        if (!_workspaceHost.TryGetCurrentPublication(out WorkspacePublication publication))
        {
            return Reject(DocumentCompletionOutcome.WorkspaceUnavailable, request, null, null, lease.OperationId, timing);
        }

        DocumentIdentityCreationResult identityResult = DocumentIdentity.TryCreate(
            request.DocumentPath,
            publication.WorkspaceIdentity,
            publication.ProjectSnapshot);
        if (!identityResult.IsSuccess)
        {
            return Reject(DocumentCompletionOutcome.InvalidRequest, request, null, publication, lease.OperationId, timing);
        }

        DocumentIdentity identity = identityResult.Identity!;
        if (!identityResult.IsCurrentWorkspaceSource)
        {
            return Reject(
                DocumentCompletionOutcome.DocumentNotInWorkspace,
                request,
                null,
                publication,
                lease.OperationId,
                timing,
                identity.RelativePath);
        }

        if (!_documentSynchronizationHost.TryGetCurrentAuthority(out DocumentClientAuthority authority))
        {
            return Reject(
                DocumentCompletionOutcome.DocumentNotSynchronized,
                request,
                null,
                publication,
                lease.OperationId,
                timing,
                identity.RelativePath);
        }

        if (request.ClientGeneration < authority.ClientGeneration)
        {
            return Reject(DocumentCompletionOutcome.StaleEpoch, request, null, publication, lease.OperationId, timing, identity.RelativePath);
        }

        if (request.ClientGeneration > authority.ClientGeneration)
        {
            return Reject(DocumentCompletionOutcome.DocumentNotSynchronized, request, null, publication, lease.OperationId, timing, identity.RelativePath);
        }

        if (request.EpochId != authority.EpochId)
        {
            return Reject(DocumentCompletionOutcome.EpochConflict, request, null, publication, lease.OperationId, timing, identity.RelativePath);
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
                lease.OperationId,
                timing,
                identity.RelativePath);
        }

        DocumentCompletionOutcome? synchronizedFailure = ValidateSynchronizedState(request, publication, initialSnapshot);
        if (synchronizedFailure is not null)
        {
            return Reject(synchronizedFailure.Value, request, initialSnapshot, publication, lease.OperationId, timing);
        }

        if (!IsRoslynCorrelationCurrent(publication, initialSnapshot))
        {
            return Reject(DocumentCompletionOutcome.RoslynUnavailable, request, initialSnapshot, publication, lease.OperationId, timing);
        }

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

        long preCompletionRevalidationStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        if (!TryRevalidateExactState(
                request,
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
                request,
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
        DocumentCompletionItem[] items = roslynResult.Items
            .Select(static item => new DocumentCompletionItem(
                item.DisplayText,
                item.InsertText,
                item.Kind,
                item.FilterText,
                item.SortText,
                item.Preselect,
                item.SemanticOrigin,
                item.InheritanceDepth))
            .ToArray();
        timing?.SetItemProjectionDuration(itemProjectionStarted);

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
            DocumentCompletionOutcome.Success.ToString(),
            roslynResult.RawItemCount,
            items.Length,
            roslynResult.IsIncomplete,
            lease.OperationId,
            timing);
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
            outcome.ToString(),
            null,
            null,
            false,
            workloadOperationId: null,
            timing: null);
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
            returnedItemCount,
            isIncomplete,
            durationMs,
            admissionValidationDurationMs = timing?.AdmissionValidationDurationMs,
            preCompletionRevalidationDurationMs = timing?.PreCompletionRevalidationDurationMs,
            roslynCompletionObservedDurationMs = timing?.RoslynCompletionObservedDurationMs,
            postCompletionRevalidationDurationMs = timing?.PostCompletionRevalidationDurationMs,
            itemProjectionDurationMs = timing?.ItemProjectionDurationMs,
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

    private sealed class CompletionTimingState
    {
        private readonly long _totalStarted;
        private long _admissionStarted;

        public CompletionTimingState(long totalStarted)
        {
            _totalStarted = totalStarted;
        }

        public double? AdmissionValidationDurationMs { get; private set; }
        public double? PreCompletionRevalidationDurationMs { get; private set; }
        public double? RoslynCompletionObservedDurationMs { get; private set; }
        public double? PostCompletionRevalidationDurationMs { get; private set; }
        public double? ItemProjectionDurationMs { get; private set; }
        public RoslynCompletionTiming RoslynTiming { get; private set; }

        public void StartAdmissionValidation()
            => _admissionStarted = Stopwatch.GetTimestamp();

        public void CompleteAdmissionValidation()
        {
            if (AdmissionValidationDurationMs is null && _admissionStarted != 0)
                AdmissionValidationDurationMs = Elapsed(_admissionStarted);
        }

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

        public double GetTotalDurationMs()
            => Elapsed(_totalStarted);

        public double GetExplicitWorkDurationMs()
            => (AdmissionValidationDurationMs ?? 0)
                + (PreCompletionRevalidationDurationMs ?? 0)
                + (RoslynCompletionObservedDurationMs ?? 0)
                + (PostCompletionRevalidationDurationMs ?? 0)
                + (ItemProjectionDurationMs ?? 0);

        private static double Elapsed(long started)
            => Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;
    }

}
