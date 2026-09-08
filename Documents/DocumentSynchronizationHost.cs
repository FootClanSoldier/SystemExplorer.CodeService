using System.Diagnostics;
using System.Text;

namespace SystemExplorer.CodeService;

internal sealed class DocumentSynchronizationHost : IDisposable
{
    private readonly object _sync = new();
    private readonly WorkloadCoordinator _workloadCoordinator;
    private readonly RoslynLanguageServerHost _roslynLanguageServerHost;
    private readonly DiagnosticLogging _diagnosticLogging;
    private readonly Dictionary<string, TrackedDocumentState> _documents =
        new(DocumentIdentity.PlatformPathComparer);

    private Dictionary<string, DocumentIdentity> _declaredOpenDocuments =
        new(DocumentIdentity.PlatformPathComparer);
    private DocumentClientAuthority? _authority;
    private long _totalTrackedSnapshotUtf8Bytes;
    private long _roslynOverlayRevision;
    private CancellationTokenSource? _roslynOverlayRevisionLifetimeSource = new();
    private bool _startupDocumentSeedAttempted;
    private bool _shuttingDown;
    private bool _disposed;

    public DocumentSynchronizationHost(
        WorkloadCoordinator workloadCoordinator,
        RoslynLanguageServerHost roslynLanguageServerHost,
        DiagnosticLogging diagnosticLogging)
    {
        _workloadCoordinator = workloadCoordinator
            ?? throw new ArgumentNullException(nameof(workloadCoordinator));
        _roslynLanguageServerHost = roslynLanguageServerHost
            ?? throw new ArgumentNullException(nameof(roslynLanguageServerHost));
        _diagnosticLogging = diagnosticLogging
            ?? throw new ArgumentNullException(nameof(diagnosticLogging));
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

        return _workloadCoordinator.TryAdmitExclusive(WorkloadLane.DocumentSynchronization);
    }

    internal async Task<StartupDocumentSeedResult> SeedStartupDocumentAsync(
        string startupDocumentPath,
        WorkspaceProjectSnapshot candidateSnapshot,
        WorkspacePublicationIdentity candidatePublicationIdentity,
        RoslynLanguageServerSnapshot roslynSnapshot,
        WorkloadExecutionLease workspaceConstructionLease,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(startupDocumentPath);
        ArgumentNullException.ThrowIfNull(candidateSnapshot);
        ValidateWorkspaceConstructionLease(workspaceConstructionLease);

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                WriteStartupDocumentSeedSkipped("service_shutting_down");
                return StartupDocumentSeedResult.Skipped(
                    candidatePublicationIdentity,
                    roslynSnapshot.RoslynGeneration,
                    "service_shutting_down");
            }

            if (_startupDocumentSeedAttempted)
            {
                WriteStartupDocumentSeedSkipped("already_attempted");
                return StartupDocumentSeedResult.Skipped(
                    candidatePublicationIdentity,
                    roslynSnapshot.RoslynGeneration,
                    "already_attempted");
            }

            _startupDocumentSeedAttempted = true;

            if (_authority is not null)
            {
                WriteStartupDocumentSeedSkipped("client_authority_already_present");
                return StartupDocumentSeedResult.Skipped(
                    candidatePublicationIdentity,
                    roslynSnapshot.RoslynGeneration,
                    "client_authority_already_present");
            }
        }

        long started = Stopwatch.GetTimestamp();
        _diagnosticLogging.WriteEvent("startup_document_seed_started");

        if (cancellationToken.IsCancellationRequested)
        {
            WriteStartupDocumentSeedSkipped("service_shutting_down");
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!roslynSnapshot.IsProjectLoaded
            || !_roslynLanguageServerHost.IsProjectLoadCurrentFor(
                candidateSnapshot.WorkspaceIdentity,
                candidatePublicationIdentity,
                roslynSnapshot))
        {
            return CompleteStartupDocumentSeedSkip(
                candidatePublicationIdentity,
                roslynSnapshot.RoslynGeneration,
                "roslyn_unavailable",
                started);
        }

        DocumentIdentityCreationResult identityResult = DocumentIdentity.TryCreate(
            startupDocumentPath,
            candidateSnapshot.WorkspaceIdentity,
            candidateSnapshot);
        if (!identityResult.IsSuccess)
        {
            return CompleteStartupDocumentSeedSkip(
                candidatePublicationIdentity,
                roslynSnapshot.RoslynGeneration,
                "invalid_wire_path",
                started);
        }

        DocumentIdentity identity = identityResult.Identity!;
        string absolutePath;
        FileAttributes attributes;
        try
        {
            absolutePath = identity.GetAbsolutePath(candidateSnapshot.WorkspaceIdentity);
            attributes = File.GetAttributes(absolutePath);
        }
        catch (FileNotFoundException)
        {
            return CompleteStartupDocumentSeedSkip(
                candidatePublicationIdentity,
                roslynSnapshot.RoslynGeneration,
                "target_missing",
                started);
        }
        catch (DirectoryNotFoundException)
        {
            return CompleteStartupDocumentSeedSkip(
                candidatePublicationIdentity,
                roslynSnapshot.RoslynGeneration,
                "target_missing",
                started);
        }
        catch (Exception exception) when (IsControlledStartupDocumentPathException(exception))
        {
            return CompleteStartupDocumentSeedSkip(
                candidatePublicationIdentity,
                roslynSnapshot.RoslynGeneration,
                "read_failed",
                started);
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return CompleteStartupDocumentSeedSkip(
                candidatePublicationIdentity,
                roslynSnapshot.RoslynGeneration,
                "target_not_file",
                started);
        }

        if (!identityResult.IsCurrentWorkspaceSource)
        {
            return CompleteStartupDocumentSeedSkip(
                candidatePublicationIdentity,
                roslynSnapshot.RoslynGeneration,
                "document_not_in_workspace",
                started);
        }

        StartupDocumentDiskReadResult readResult;
        try
        {
            readResult = await ReadStartupDocumentTextAsync(
                absolutePath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteStartupDocumentSeedSkipped("service_shutting_down");
            throw;
        }

        if (!readResult.Succeeded)
        {
            return CompleteStartupDocumentSeedSkip(
                candidatePublicationIdentity,
                roslynSnapshot.RoslynGeneration,
                readResult.FailureReason!,
                started);
        }

        string text = readResult.Text!;
        int textUtf8ByteCount = readResult.TextUtf8ByteCount;

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                return CompleteStartupDocumentSeedSkip(
                    candidatePublicationIdentity,
                    roslynSnapshot.RoslynGeneration,
                    "service_shutting_down",
                    started);
            }

            if (_authority is not null
                || _declaredOpenDocuments.ContainsKey(identity.RelativePath)
                || (_documents.TryGetValue(identity.RelativePath, out TrackedDocumentState? existingState)
                    && existingState.HasCurrentAuthoritySnapshot))
            {
                return CompleteStartupDocumentSeedSkip(
                    candidatePublicationIdentity,
                    roslynSnapshot.RoslynGeneration,
                    "client_authority_already_present",
                    started);
            }

            long candidateTotalBytes = _totalTrackedSnapshotUtf8Bytes + textUtf8ByteCount;
            if (candidateTotalBytes > DocumentSynchronizationLimits.MaxTotalTrackedSnapshotUtf8Bytes
                || (!_documents.ContainsKey(identity.RelativePath)
                    && _documents.Count >= DocumentSynchronizationLimits.MaxTrackedOpenDocuments))
            {
                return CompleteStartupDocumentSeedSkip(
                    candidatePublicationIdentity,
                    roslynSnapshot.RoslynGeneration,
                    "capacity_exceeded",
                    started);
            }
        }

        const int startupRoslynLspVersion = 1;
        RoslynDocumentSendResult sendResult = await _roslynLanguageServerHost.OpenDocumentAsync(
            candidateSnapshot.WorkspaceIdentity,
            candidatePublicationIdentity,
            roslynSnapshot.RoslynGeneration,
            identity,
            startupRoslynLspVersion,
            text,
            cancellationToken).ConfigureAwait(false);

        if (!sendResult.IsSuccess)
        {
            return CompleteStartupDocumentSeedSkip(
                candidatePublicationIdentity,
                roslynSnapshot.RoslynGeneration,
                "roslyn_unavailable",
                started);
        }

        CancellationTokenSource supersededOverlayLifetimeSource;
        long committedRoslynOverlayRevision;
        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                WriteStartupDocumentSeedSkipped("service_shutting_down");
                return StartupDocumentSeedResult.Skipped(
                    candidatePublicationIdentity,
                    roslynSnapshot.RoslynGeneration,
                    "service_shutting_down");
            }

            if (_authority is not null || _declaredOpenDocuments.ContainsKey(identity.RelativePath))
            {
                WriteStartupDocumentSeedSkipped("client_authority_already_present");
                return StartupDocumentSeedResult.Skipped(
                    candidatePublicationIdentity,
                    roslynSnapshot.RoslynGeneration,
                    "client_authority_already_present");
            }

            if (!_documents.TryGetValue(identity.RelativePath, out TrackedDocumentState? state))
            {
                state = new TrackedDocumentState(identity);
                _documents.Add(identity.RelativePath, state);
            }

            long nextTotal = _totalTrackedSnapshotUtf8Bytes
                - state.SnapshotUtf8ByteCount
                + textUtf8ByteCount;
            if (nextTotal > DocumentSynchronizationLimits.MaxTotalTrackedSnapshotUtf8Bytes)
            {
                throw new InvalidOperationException(
                    "startup document snapshot memory bound changed before commit despite WorkspaceConstruction ownership.");
            }

            supersededOverlayLifetimeSource = AdvanceRoslynOverlayRevisionLocked();
            _totalTrackedSnapshotUtf8Bytes = nextTotal;
            state.Identity = identity;
            state.LastAcceptedClientVersion = 0;
            state.HasCurrentAuthoritySnapshot = false;
            state.LastFullSnapshotText = text;
            state.SnapshotUtf8ByteCount = textUtf8ByteCount;
            state.IsOpenInRoslyn = true;
            state.RoslynGeneration = roslynSnapshot.RoslynGeneration;
            state.RoslynLspVersion = startupRoslynLspVersion;
            state.LastWorkspacePublicationIdentity = candidatePublicationIdentity;
            state.IsCurrentWorkspaceSource = true;
            committedRoslynOverlayRevision = _roslynOverlayRevision;
        }

        CancelAndDisposeRoslynOverlayRevisionSourceNoThrow(supersededOverlayLifetimeSource);

        _diagnosticLogging.WriteEvent(
            "startup_document_seeded",
            new
            {
                documentPath = identity.RelativePath,
                roslynGeneration = roslynSnapshot.RoslynGeneration,
                roslynLspVersion = startupRoslynLspVersion,
                roslynOverlayRevision = committedRoslynOverlayRevision,
                textUtf8ByteCount,
                durationMs = Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds,
            });

        return StartupDocumentSeedResult.Success(
            identity,
            candidatePublicationIdentity,
            roslynSnapshot.RoslynGeneration,
            startupRoslynLspVersion,
            committedRoslynOverlayRevision,
            textUtf8ByteCount);
    }

    public async Task<DocumentEpochOperationResult> ReconcileEpochAsync(
        DocumentEpochRequest request,
        WorkspacePublication publication,
        WorkloadExecutionLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publication);
        ValidateDocumentTransportLease(lease);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsRoslynUsableForPublication(publication))
        {
            return DocumentEpochOperationResult.Failure(
                DocumentSynchronizationOutcome.RoslynUnavailable,
                request,
                publication);
        }

        if (request.OpenDocumentPaths.Count > DocumentSynchronizationLimits.MaxTrackedOpenDocuments)
        {
            return DocumentEpochOperationResult.Failure(
                DocumentSynchronizationOutcome.CapacityExceeded,
                request,
                publication);
        }

        Dictionary<string, DocumentIdentity> candidateOpenDocuments =
            new(DocumentIdentity.PlatformPathComparer);

        foreach (string wirePath in request.OpenDocumentPaths)
        {
            DocumentIdentityCreationResult identityResult = DocumentIdentity.TryCreate(
                wirePath,
                publication.WorkspaceIdentity,
                publication.ProjectSnapshot);
            if (!identityResult.IsSuccess)
            {
                WriteEpochRejected(request, publication, DocumentSynchronizationOutcome.InvalidRequest);
                return DocumentEpochOperationResult.Failure(
                    DocumentSynchronizationOutcome.InvalidRequest,
                    request,
                    publication);
            }

            DocumentIdentity identity = identityResult.Identity!;
            if (!candidateOpenDocuments.TryAdd(identity.RelativePath, identity))
            {
                WriteEpochRejected(request, publication, DocumentSynchronizationOutcome.InvalidRequest);
                return DocumentEpochOperationResult.Failure(
                    DocumentSynchronizationOutcome.InvalidRequest,
                    request,
                    publication);
            }
        }

        DocumentClientAuthority requestedAuthority = new(request.ClientGeneration, request.EpochId);
        List<TrackedDocumentState> removedTrackedDocuments;
        int retainedDocumentCount;
        int closedDocumentCount;
        bool authorityTakeover;

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                return DocumentEpochOperationResult.Failure(
                    DocumentSynchronizationOutcome.Unavailable,
                    request,
                    publication);
            }

            if (_authority is DocumentClientAuthority currentAuthority)
            {
                if (requestedAuthority.ClientGeneration < currentAuthority.ClientGeneration)
                {
                    WriteEpochRejected(request, publication, DocumentSynchronizationOutcome.StaleEpoch);
                    return DocumentEpochOperationResult.Failure(
                        DocumentSynchronizationOutcome.StaleEpoch,
                        request,
                        publication);
                }

                if (requestedAuthority.ClientGeneration == currentAuthority.ClientGeneration
                    && requestedAuthority.EpochId != currentAuthority.EpochId)
                {
                    WriteEpochRejected(request, publication, DocumentSynchronizationOutcome.EpochConflict);
                    return DocumentEpochOperationResult.Failure(
                        DocumentSynchronizationOutcome.EpochConflict,
                        request,
                        publication);
                }
            }

            authorityTakeover = _authority is null
                || requestedAuthority.ClientGeneration > _authority.Value.ClientGeneration;

            retainedDocumentCount = CountIntersection(
                _declaredOpenDocuments.Keys,
                candidateOpenDocuments.Keys);
            closedDocumentCount = _declaredOpenDocuments.Count - retainedDocumentCount;

            if (!authorityTakeover
                && OpenSetsEqual(_declaredOpenDocuments, candidateOpenDocuments))
            {
                WriteEpochReconciled(
                    request,
                    publication,
                    DocumentSynchronizationOutcome.AlreadyCurrent,
                    candidateOpenDocuments.Count,
                    retainedDocumentCount,
                    0);
                return new DocumentEpochOperationResult(
                    DocumentSynchronizationOutcome.AlreadyCurrent,
                    request.ClientGeneration,
                    request.EpochId,
                    publication.Identity,
                    publication.RoslynSnapshot.RoslynGeneration,
                    candidateOpenDocuments.Count,
                    retainedDocumentCount,
                    0);
            }

            removedTrackedDocuments = _documents.Values
                .Where(state => !candidateOpenDocuments.ContainsKey(state.Identity.RelativePath))
                .ToList();

            if (authorityTakeover)
            {
                _authority = requestedAuthority;
                _declaredOpenDocuments = CloneOpenSet(candidateOpenDocuments);

                foreach (TrackedDocumentState state in _documents.Values)
                {
                    if (candidateOpenDocuments.TryGetValue(
                            state.Identity.RelativePath,
                            out DocumentIdentity? retainedIdentity))
                    {
                        state.Identity = retainedIdentity;
                    }

                    // A new client authority must explicitly re-establish every retained snapshot.
                    // Removed states are also made non-authoritative immediately because feature lanes
                    // may now overlap epoch reconciliation while Roslyn closes are still retiring.
                    state.LastAcceptedClientVersion = 0;
                    state.HasCurrentAuthoritySnapshot = false;
                }
            }
        }

        CancellationToken closeCancellationToken = authorityTakeover
            ? lease.ServiceWorkShutdownToken
            : cancellationToken;

        foreach (TrackedDocumentState state in removedTrackedDocuments)
        {
            closeCancellationToken.ThrowIfCancellationRequested();

            bool roslynCloseWasRequired = state.IsOpenInRoslyn
                && state.RoslynGeneration == publication.RoslynSnapshot.RoslynGeneration;
            RoslynDocumentSendTiming closeTiming = default;

            if (roslynCloseWasRequired)
            {
                RoslynDocumentSendResult closeResult = await _roslynLanguageServerHost.CloseDocumentAsync(
                    publication.WorkspaceIdentity,
                    publication.Identity,
                    publication.RoslynSnapshot.RoslynGeneration,
                    state.Identity,
                    closeCancellationToken).ConfigureAwait(false);

                closeTiming = closeResult.Timing;
                if (!closeResult.IsSuccess)
                {
                    MarkRoslynCorrelationsUnavailable(publication.RoslynSnapshot.RoslynGeneration);
                    if (authorityTakeover)
                    {
                        RemoveDocumentsNoLongerDeclared(candidateOpenDocuments);
                    }

                    WriteEpochRejected(request, publication, DocumentSynchronizationOutcome.RoslynUnavailable);
                    return DocumentEpochOperationResult.Failure(
                        DocumentSynchronizationOutcome.RoslynUnavailable,
                        request,
                        publication);
                }

                CancellationTokenSource supersededOverlayLifetimeSource;
                lock (_sync)
                {
                    supersededOverlayLifetimeSource = AdvanceRoslynOverlayRevisionLocked();
                    state.IsOpenInRoslyn = false;
                    state.RoslynGeneration = 0;
                    state.RoslynLspVersion = 0;
                }

                CancelAndDisposeRoslynOverlayRevisionSourceNoThrow(supersededOverlayLifetimeSource);
            }

            WriteDocumentClosed(
                request,
                publication,
                lease.OperationId,
                state.Identity.RelativePath,
                roslynCloseWasRequired,
                closeTiming);

            if (authorityTakeover)
            {
                RemoveTrackedDocument(state.Identity.RelativePath);
            }
        }

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                return DocumentEpochOperationResult.Failure(
                    DocumentSynchronizationOutcome.Unavailable,
                    request,
                    publication);
            }

            if (!authorityTakeover)
            {
                _declaredOpenDocuments = CloneOpenSet(candidateOpenDocuments);
                foreach (TrackedDocumentState removed in removedTrackedDocuments)
                {
                    RemoveTrackedDocumentLocked(removed.Identity.RelativePath);
                }
            }
        }

        WriteEpochReconciled(
            request,
            publication,
            DocumentSynchronizationOutcome.Success,
            candidateOpenDocuments.Count,
            retainedDocumentCount,
            closedDocumentCount);

        return new DocumentEpochOperationResult(
            DocumentSynchronizationOutcome.Success,
            request.ClientGeneration,
            request.EpochId,
            publication.Identity,
            publication.RoslynSnapshot.RoslynGeneration,
            candidateOpenDocuments.Count,
            retainedDocumentCount,
            closedDocumentCount);
    }

    public async Task<DocumentSnapshotOperationResult> SynchronizeSnapshotAsync(
        DocumentSnapshotRequest request,
        WorkspacePublication publication,
        WorkloadExecutionLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publication);
        ValidateDocumentTransportLease(lease);
        cancellationToken.ThrowIfCancellationRequested();

        DocumentSnapshotTimingState? timing = _diagnosticLogging.IsEnabled
            ? new DocumentSnapshotTimingState()
            : null;

        if (!IsRoslynUsableForPublication(publication))
        {
            return DocumentSnapshotOperationResult.Failure(
                DocumentSynchronizationOutcome.RoslynUnavailable,
                request,
                publication);
        }

        DocumentIdentityCreationResult identityResult = DocumentIdentity.TryCreate(
            request.DocumentPath,
            publication.WorkspaceIdentity,
            publication.ProjectSnapshot);
        if (!identityResult.IsSuccess)
        {
            WriteSnapshotRejected(request, publication, DocumentSynchronizationOutcome.InvalidRequest, null, null);
            return DocumentSnapshotOperationResult.Failure(
                DocumentSynchronizationOutcome.InvalidRequest,
                request,
                publication);
        }

        DocumentIdentity identity = identityResult.Identity!;
        if (request.TextUtf8ByteCount > DocumentSynchronizationLimits.MaxDocumentTextUtf8Bytes)
        {
            WriteSnapshotRejected(request, publication, DocumentSynchronizationOutcome.CapacityExceeded, identity.RelativePath, null);
            return DocumentSnapshotOperationResult.Failure(
                DocumentSynchronizationOutcome.CapacityExceeded,
                request,
                publication,
                identity.RelativePath);
        }

        TrackedDocumentState? existingState;
        long acceptedClientVersion;
        int? existingRoslynVersion;
        bool needsOpen;
        bool needsChange;
        int targetRoslynVersion;

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                return DocumentSnapshotOperationResult.Failure(
                    DocumentSynchronizationOutcome.Unavailable,
                    request,
                    publication,
                    identity.RelativePath);
            }

            if (_authority is not DocumentClientAuthority currentAuthority
                || request.ClientGeneration < currentAuthority.ClientGeneration
                || request.ClientGeneration > currentAuthority.ClientGeneration)
            {
                WriteSnapshotRejected(request, publication, DocumentSynchronizationOutcome.StaleEpoch, identity.RelativePath, null);
                return DocumentSnapshotOperationResult.Failure(
                    DocumentSynchronizationOutcome.StaleEpoch,
                    request,
                    publication,
                    identity.RelativePath);
            }

            if (request.EpochId != currentAuthority.EpochId)
            {
                WriteSnapshotRejected(request, publication, DocumentSynchronizationOutcome.EpochConflict, identity.RelativePath, null);
                return DocumentSnapshotOperationResult.Failure(
                    DocumentSynchronizationOutcome.EpochConflict,
                    request,
                    publication,
                    identity.RelativePath);
            }

            if (!_declaredOpenDocuments.TryGetValue(identity.RelativePath, out DocumentIdentity? declaredIdentity))
            {
                WriteSnapshotRejected(request, publication, DocumentSynchronizationOutcome.DocumentNotOpen, identity.RelativePath, null);
                return DocumentSnapshotOperationResult.Failure(
                    DocumentSynchronizationOutcome.DocumentNotOpen,
                    request,
                    publication,
                    identity.RelativePath);
            }

            if (!identityResult.IsCurrentWorkspaceSource)
            {
                WriteSnapshotRejected(request, publication, DocumentSynchronizationOutcome.DocumentNotInWorkspace, declaredIdentity.RelativePath, null);
                return DocumentSnapshotOperationResult.Failure(
                    DocumentSynchronizationOutcome.DocumentNotInWorkspace,
                    request,
                    publication,
                    declaredIdentity.RelativePath);
            }

            identity = identityResult.Identity!;
            _declaredOpenDocuments[identity.RelativePath] = identity;
            _documents.TryGetValue(identity.RelativePath, out existingState);

            acceptedClientVersion = existingState?.LastAcceptedClientVersion ?? 0;
            existingRoslynVersion = existingState is not null && existingState.IsOpenInRoslyn
                ? existingState.RoslynLspVersion
                : null;

            if (request.ClientVersion < acceptedClientVersion)
            {
                WriteSnapshotRejected(request, publication, DocumentSynchronizationOutcome.StaleVersion, identity.RelativePath, acceptedClientVersion);
                return DocumentSnapshotOperationResult.Failure(
                    DocumentSynchronizationOutcome.StaleVersion,
                    request,
                    publication,
                    identity.RelativePath,
                    acceptedClientVersion,
                    existingRoslynVersion);
            }

            if (request.ClientVersion == acceptedClientVersion && existingState is not null)
            {
                if (string.Equals(request.Text, existingState.LastFullSnapshotText, StringComparison.Ordinal))
                {
                    timing?.CompletePreRoslynValidation();
                    timing?.CompleteSnapshotPipeline();
                    WriteSnapshotAccepted(
                        request,
                        publication,
                        lease.OperationId,
                        DocumentSynchronizationOutcome.AlreadyCurrent,
                        identity.RelativePath,
                        acceptedClientVersion,
                        existingRoslynVersion,
                        _roslynOverlayRevision,
                        RoslynNotificationKind.None,
                        timing,
                        default);
                    return new DocumentSnapshotOperationResult(
                        DocumentSynchronizationOutcome.AlreadyCurrent,
                        request.ClientGeneration,
                        request.EpochId,
                        identity.RelativePath,
                        acceptedClientVersion,
                        publication.Identity,
                        publication.RoslynSnapshot.RoslynGeneration,
                        existingRoslynVersion);
                }

                WriteSnapshotRejected(request, publication, DocumentSynchronizationOutcome.VersionConflict, identity.RelativePath, acceptedClientVersion);
                return DocumentSnapshotOperationResult.Failure(
                    DocumentSynchronizationOutcome.VersionConflict,
                    request,
                    publication,
                    identity.RelativePath,
                    acceptedClientVersion,
                    existingRoslynVersion);
            }

            long candidateTotalBytes = _totalTrackedSnapshotUtf8Bytes
                - (existingState?.SnapshotUtf8ByteCount ?? 0)
                + request.TextUtf8ByteCount;
            if (candidateTotalBytes > DocumentSynchronizationLimits.MaxTotalTrackedSnapshotUtf8Bytes
                || (existingState is null
                    && _documents.Count >= DocumentSynchronizationLimits.MaxTrackedOpenDocuments))
            {
                WriteSnapshotRejected(request, publication, DocumentSynchronizationOutcome.CapacityExceeded, identity.RelativePath, acceptedClientVersion);
                return DocumentSnapshotOperationResult.Failure(
                    DocumentSynchronizationOutcome.CapacityExceeded,
                    request,
                    publication,
                    identity.RelativePath,
                    acceptedClientVersion,
                    existingRoslynVersion);
            }

            bool openInCurrentGeneration = existingState is not null
                && existingState.IsOpenInRoslyn
                && existingState.RoslynGeneration == publication.RoslynSnapshot.RoslynGeneration;
            bool textMatchesStored = existingState is not null
                && string.Equals(request.Text, existingState.LastFullSnapshotText, StringComparison.Ordinal);

            if (openInCurrentGeneration && textMatchesStored)
            {
                timing?.CompletePreRoslynValidation();
                long unchangedOverlayStateCommitStarted = timing?.StartPhase() ?? 0;
                CommitSnapshotLocked(
                    existingState!,
                    identity,
                    request,
                    publication.Identity,
                    existingState!.RoslynGeneration,
                    existingState.RoslynLspVersion,
                    isOpenInRoslyn: true);
                timing?.CompleteStateCommit(unchangedOverlayStateCommitStarted);
                timing?.CompleteSnapshotPipeline();

                WriteSnapshotAccepted(
                    request,
                    publication,
                    lease.OperationId,
                    DocumentSynchronizationOutcome.Success,
                    identity.RelativePath,
                    request.ClientVersion,
                    existingState.RoslynLspVersion,
                    _roslynOverlayRevision,
                    RoslynNotificationKind.None,
                    timing,
                    default);
                return new DocumentSnapshotOperationResult(
                    DocumentSynchronizationOutcome.Success,
                    request.ClientGeneration,
                    request.EpochId,
                    identity.RelativePath,
                    request.ClientVersion,
                    publication.Identity,
                    publication.RoslynSnapshot.RoslynGeneration,
                    existingState.RoslynLspVersion);
            }

            needsOpen = !openInCurrentGeneration;
            needsChange = openInCurrentGeneration && !textMatchesStored;
            if (needsOpen)
            {
                targetRoslynVersion = 1;
            }
            else
            {
                if (existingState!.RoslynLspVersion == int.MaxValue)
                {
                    WriteSnapshotRejected(request, publication, DocumentSynchronizationOutcome.Unavailable, identity.RelativePath, acceptedClientVersion);
                    return DocumentSnapshotOperationResult.Failure(
                        DocumentSynchronizationOutcome.Unavailable,
                        request,
                        publication,
                        identity.RelativePath,
                        acceptedClientVersion,
                        existingState.RoslynLspVersion);
                }

                targetRoslynVersion = checked(existingState.RoslynLspVersion + 1);
            }
        }

        timing?.CompletePreRoslynValidation();
        RoslynNotificationKind notificationKind = needsOpen
            ? RoslynNotificationKind.DidOpen
            : RoslynNotificationKind.DidChange;
        long roslynSendStarted = timing?.StartPhase() ?? 0;
        RoslynDocumentSendResult sendResult;
        if (needsOpen)
        {
            sendResult = await _roslynLanguageServerHost.OpenDocumentAsync(
                publication.WorkspaceIdentity,
                publication.Identity,
                publication.RoslynSnapshot.RoslynGeneration,
                identity,
                targetRoslynVersion,
                request.Text,
                cancellationToken).ConfigureAwait(false);
        }
        else if (needsChange)
        {
            sendResult = await _roslynLanguageServerHost.ChangeDocumentAsync(
                publication.WorkspaceIdentity,
                publication.Identity,
                publication.RoslynSnapshot.RoslynGeneration,
                identity,
                targetRoslynVersion,
                request.Text,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException("document synchronization reached a send boundary without an open or change operation.");
        }

        timing?.CompleteRoslynSend(roslynSendStarted);

        if (!sendResult.IsSuccess)
        {
            MarkRoslynCorrelationsUnavailable(publication.RoslynSnapshot.RoslynGeneration);
            WriteSnapshotRejected(request, publication, DocumentSynchronizationOutcome.RoslynUnavailable, identity.RelativePath, acceptedClientVersion);
            return DocumentSnapshotOperationResult.Failure(
                DocumentSynchronizationOutcome.RoslynUnavailable,
                request,
                publication,
                identity.RelativePath,
                acceptedClientVersion,
                existingRoslynVersion);
        }

        CancellationTokenSource supersededOverlayLifetimeSource;
        long committedRoslynOverlayRevision;
        long stateCommitStarted = timing?.StartPhase() ?? 0;
        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                return DocumentSnapshotOperationResult.Failure(
                    DocumentSynchronizationOutcome.Unavailable,
                    request,
                    publication,
                    identity.RelativePath,
                    acceptedClientVersion,
                    existingRoslynVersion);
            }

            if (!_documents.TryGetValue(identity.RelativePath, out TrackedDocumentState? state))
            {
                state = new TrackedDocumentState(identity);
                _documents.Add(identity.RelativePath, state);
            }

            supersededOverlayLifetimeSource = AdvanceRoslynOverlayRevisionLocked();
            CommitSnapshotLocked(
                state,
                identity,
                request,
                publication.Identity,
                publication.RoslynSnapshot.RoslynGeneration,
                targetRoslynVersion,
                isOpenInRoslyn: true);
            committedRoslynOverlayRevision = _roslynOverlayRevision;
        }

        timing?.CompleteStateCommit(stateCommitStarted);

        long overlayCancellationStarted = timing?.StartPhase() ?? 0;
        CancelAndDisposeRoslynOverlayRevisionSourceNoThrow(supersededOverlayLifetimeSource);
        timing?.CompleteOverlayCancellationSignal(overlayCancellationStarted);
        timing?.CompleteSnapshotPipeline();

        WriteSnapshotAccepted(
            request,
            publication,
            lease.OperationId,
            DocumentSynchronizationOutcome.Success,
            identity.RelativePath,
            request.ClientVersion,
            targetRoslynVersion,
            committedRoslynOverlayRevision,
            notificationKind,
            timing,
            sendResult.Timing);

        return new DocumentSnapshotOperationResult(
            DocumentSynchronizationOutcome.Success,
            request.ClientGeneration,
            request.EpochId,
            identity.RelativePath,
            request.ClientVersion,
            publication.Identity,
            publication.RoslynSnapshot.RoslynGeneration,
            targetRoslynVersion);
    }

    public async Task<DocumentRoslynReplayResult> ReconcileRoslynGenerationAsync(
        WorkspaceProjectSnapshot candidateSnapshot,
        WorkspacePublicationIdentity candidatePublicationIdentity,
        RoslynLanguageServerSnapshot roslynSnapshot,
        bool reusedExistingGeneration,
        WorkloadExecutionLease workspaceConstructionLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidateSnapshot);
        ValidateWorkspaceConstructionLease(workspaceConstructionLease);
        cancellationToken.ThrowIfCancellationRequested();

        bool diagnosticsEnabled = _diagnosticLogging.IsEnabled;
        long started = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        List<TrackedDocumentState> replayCandidates = new();

        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                return new DocumentRoslynReplayResult(0, Completed: false, RoslynAvailable: false);
            }

            ReconcileCanonicalPathsAndMembershipLocked(candidateSnapshot);

            if (!roslynSnapshot.IsProjectLoaded
                || !_roslynLanguageServerHost.IsProjectLoadCurrentFor(
                    candidateSnapshot.WorkspaceIdentity,
                    candidatePublicationIdentity,
                    roslynSnapshot))
            {
                MarkAllRoslynCorrelationsUnavailableLocked();
                return new DocumentRoslynReplayResult(0, Completed: true, RoslynAvailable: false);
            }

            if (reusedExistingGeneration)
            {
                foreach (TrackedDocumentState state in _documents.Values)
                {
                    if (!state.IsCurrentWorkspaceSource)
                    {
                        state.IsOpenInRoslyn = false;
                        continue;
                    }

                    if (state.IsOpenInRoslyn
                        && state.RoslynGeneration == roslynSnapshot.RoslynGeneration)
                    {
                        state.LastWorkspacePublicationIdentity = candidatePublicationIdentity;
                    }
                }

                return new DocumentRoslynReplayResult(0, Completed: true, RoslynAvailable: true);
            }

            foreach (TrackedDocumentState state in _documents.Values)
            {
                if (state.IsCurrentWorkspaceSource
                    && _declaredOpenDocuments.ContainsKey(state.Identity.RelativePath))
                {
                    replayCandidates.Add(state);
                }
                else
                {
                    state.IsOpenInRoslyn = false;
                    state.RoslynGeneration = 0;
                    state.RoslynLspVersion = 0;
                }
            }
        }

        WriteReplayStarted(candidatePublicationIdentity, roslynSnapshot.RoslynGeneration, replayCandidates.Count);

        int replayed = 0;
        foreach (TrackedDocumentState state in replayCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RoslynDocumentSendResult sendResult = await _roslynLanguageServerHost.OpenDocumentAsync(
                candidateSnapshot.WorkspaceIdentity,
                candidatePublicationIdentity,
                roslynSnapshot.RoslynGeneration,
                state.Identity,
                lspVersion: 1,
                state.LastFullSnapshotText,
                cancellationToken).ConfigureAwait(false);

            if (!sendResult.IsSuccess)
            {
                MarkRoslynCorrelationsUnavailable(roslynSnapshot.RoslynGeneration);
                WriteReplayFault(candidatePublicationIdentity, roslynSnapshot.RoslynGeneration, replayCandidates.Count, replayed);
                return new DocumentRoslynReplayResult(replayed, Completed: false, RoslynAvailable: false);
            }

            CancellationTokenSource supersededOverlayLifetimeSource;
            lock (_sync)
            {
                if (_disposed || _shuttingDown)
                {
                    return new DocumentRoslynReplayResult(replayed, Completed: false, RoslynAvailable: false);
                }

                supersededOverlayLifetimeSource = AdvanceRoslynOverlayRevisionLocked();
                state.IsOpenInRoslyn = true;
                state.RoslynGeneration = roslynSnapshot.RoslynGeneration;
                state.RoslynLspVersion = 1;
                state.LastWorkspacePublicationIdentity = candidatePublicationIdentity;
            }

            CancelAndDisposeRoslynOverlayRevisionSourceNoThrow(supersededOverlayLifetimeSource);
            replayed++;
        }

        WriteReplayCompleted(
            candidatePublicationIdentity,
            roslynSnapshot.RoslynGeneration,
            replayed,
            diagnosticsEnabled
                ? Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds
                : null);

        return new DocumentRoslynReplayResult(replayed, Completed: true, RoslynAvailable: true);
    }

    public bool TryGetDocumentSnapshot(
        string documentPath,
        WorkspaceProjectSnapshot currentProjectSnapshot,
        out DocumentSynchronizationDocumentSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentPath);
        ArgumentNullException.ThrowIfNull(currentProjectSnapshot);

        lock (_sync)
        {
            if (_authority is not DocumentClientAuthority authority
                || !_documents.TryGetValue(documentPath, out TrackedDocumentState? state))
            {
                snapshot = default;
                return false;
            }

            bool isCurrentSource = currentProjectSnapshot.SourceFiles.Any(
                source => DocumentIdentity.PlatformPathComparer.Equals(source, state.Identity.RelativePath));

            snapshot = new DocumentSynchronizationDocumentSnapshot(
                state.Identity.RelativePath,
                authority.ClientGeneration,
                authority.EpochId,
                state.LastAcceptedClientVersion,
                state.HasCurrentAuthoritySnapshot,
                state.LastWorkspacePublicationIdentity,
                state.RoslynGeneration,
                state.RoslynLspVersion,
                _roslynOverlayRevision,
                state.IsOpenInRoslyn,
                isCurrentSource);
            return true;
        }
    }

    public bool TryGetRoslynOverlayRevisionToken(
        long expectedRevision,
        out CancellationToken token)
    {
        lock (_sync)
        {
            if (_disposed
                || _shuttingDown
                || expectedRevision != _roslynOverlayRevision
                || _roslynOverlayRevisionLifetimeSource is null)
            {
                token = default;
                return false;
            }

            token = _roslynOverlayRevisionLifetimeSource.Token;
            return true;
        }
    }

    public bool IsRoslynOverlayRevisionSuperseded(long expectedRevision)
    {
        lock (_sync)
        {
            return !_disposed
                && !_shuttingDown
                && expectedRevision != _roslynOverlayRevision;
        }
    }

    public bool TryGetCurrentAuthority(out DocumentClientAuthority authority)
    {
        lock (_sync)
        {
            if (!_disposed && !_shuttingDown && _authority is DocumentClientAuthority current)
            {
                authority = current;
                return true;
            }

            authority = default;
            return false;
        }
    }

    private CancellationTokenSource AdvanceRoslynOverlayRevisionLocked()
    {
        if (_roslynOverlayRevision == long.MaxValue)
        {
            MarkAllRoslynCorrelationsUnavailableLocked();
            throw new InvalidOperationException("Roslyn overlay revision overflowed; semantic correlation can no longer be represented safely.");
        }

        CancellationTokenSource supersededSource = _roslynOverlayRevisionLifetimeSource
            ?? throw new InvalidOperationException("Roslyn overlay revision lifetime is unavailable while document synchronization is active.");

        _roslynOverlayRevision = checked(_roslynOverlayRevision + 1);
        _roslynOverlayRevisionLifetimeSource = new CancellationTokenSource();
        return supersededSource;
    }

    public void BeginShutdown()
    {
        CancellationTokenSource? overlayLifetimeSource;
        lock (_sync)
        {
            if (_disposed || _shuttingDown)
            {
                return;
            }

            _shuttingDown = true;
            overlayLifetimeSource = _roslynOverlayRevisionLifetimeSource;
            _roslynOverlayRevisionLifetimeSource = null;
        }

        CancelAndDisposeRoslynOverlayRevisionSourceNoThrow(overlayLifetimeSource);
        _diagnosticLogging.WriteEvent("document_sync_shutting_down");
    }

    public void Dispose()
    {
        CancellationTokenSource? overlayLifetimeSource;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shuttingDown = true;
            overlayLifetimeSource = _roslynOverlayRevisionLifetimeSource;
            _roslynOverlayRevisionLifetimeSource = null;
            _authority = null;
            _declaredOpenDocuments.Clear();
            _documents.Clear();
            _totalTrackedSnapshotUtf8Bytes = 0;
        }

        CancelAndDisposeRoslynOverlayRevisionSourceNoThrow(overlayLifetimeSource);
        _diagnosticLogging.WriteEvent("document_sync_stopped");
    }

    private void CancelAndDisposeRoslynOverlayRevisionSourceNoThrow(
        CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel(throwOnFirstException: false);
        }
        catch (Exception exception)
        {
            _diagnosticLogging.WriteFault("document_sync_overlay_cancellation_fault", exception);
        }

        try
        {
            source.Dispose();
        }
        catch (Exception exception)
        {
            _diagnosticLogging.WriteFault("document_sync_overlay_cancellation_dispose_fault", exception);
        }
    }

    private bool IsRoslynUsableForPublication(WorkspacePublication publication)
        => publication.RoslynSnapshot.IsProjectLoaded
            && _roslynLanguageServerHost.IsProjectLoadCurrentFor(
                publication.WorkspaceIdentity,
                publication.Identity,
                publication.RoslynSnapshot);

    private void CommitSnapshotLocked(
        TrackedDocumentState state,
        DocumentIdentity identity,
        DocumentSnapshotRequest request,
        WorkspacePublicationIdentity publicationIdentity,
        long roslynGeneration,
        int roslynLspVersion,
        bool isOpenInRoslyn)
    {
        long nextTotal = _totalTrackedSnapshotUtf8Bytes
            - state.SnapshotUtf8ByteCount
            + request.TextUtf8ByteCount;
        if (nextTotal > DocumentSynchronizationLimits.MaxTotalTrackedSnapshotUtf8Bytes)
        {
            throw new InvalidOperationException("document snapshot memory bound changed before commit despite exclusive document workload ownership.");
        }

        _totalTrackedSnapshotUtf8Bytes = nextTotal;
        state.Identity = identity;
        state.LastAcceptedClientVersion = request.ClientVersion;
        state.HasCurrentAuthoritySnapshot = true;
        state.LastFullSnapshotText = request.Text;
        state.SnapshotUtf8ByteCount = request.TextUtf8ByteCount;
        state.IsOpenInRoslyn = isOpenInRoslyn;
        state.RoslynGeneration = roslynGeneration;
        state.RoslynLspVersion = roslynLspVersion;
        state.LastWorkspacePublicationIdentity = publicationIdentity;
        state.IsCurrentWorkspaceSource = true;
    }

    private void ReconcileCanonicalPathsAndMembershipLocked(WorkspaceProjectSnapshot candidateSnapshot)
    {
        foreach (KeyValuePair<string, DocumentIdentity> open in _declaredOpenDocuments.ToArray())
        {
            DocumentIdentityCreationResult identityResult = DocumentIdentity.TryCreate(
                open.Value.RelativePath,
                candidateSnapshot.WorkspaceIdentity,
                candidateSnapshot);
            if (identityResult.IsSuccess)
            {
                _declaredOpenDocuments[open.Key] = identityResult.Identity!;
            }
        }

        foreach (TrackedDocumentState state in _documents.Values)
        {
            DocumentIdentityCreationResult identityResult = DocumentIdentity.TryCreate(
                state.Identity.RelativePath,
                candidateSnapshot.WorkspaceIdentity,
                candidateSnapshot);
            if (!identityResult.IsSuccess)
            {
                state.IsCurrentWorkspaceSource = false;
                state.IsOpenInRoslyn = false;
                state.RoslynGeneration = 0;
                state.RoslynLspVersion = 0;
                continue;
            }

            state.Identity = identityResult.Identity!;
            state.IsCurrentWorkspaceSource = identityResult.IsCurrentWorkspaceSource;
        }
    }

    private void MarkRoslynCorrelationsUnavailable(long roslynGeneration)
    {
        lock (_sync)
        {
            foreach (TrackedDocumentState state in _documents.Values)
            {
                if (state.RoslynGeneration != roslynGeneration)
                {
                    continue;
                }

                state.IsOpenInRoslyn = false;
                state.RoslynGeneration = 0;
                state.RoslynLspVersion = 0;
            }
        }
    }

    private void MarkAllRoslynCorrelationsUnavailableLocked()
    {
        foreach (TrackedDocumentState state in _documents.Values)
        {
            state.IsOpenInRoslyn = false;
            state.RoslynGeneration = 0;
            state.RoslynLspVersion = 0;
        }
    }

    private void RemoveDocumentsNoLongerDeclared(Dictionary<string, DocumentIdentity> declared)
    {
        lock (_sync)
        {
            foreach (string path in _documents.Keys.Where(path => !declared.ContainsKey(path)).ToArray())
            {
                RemoveTrackedDocumentLocked(path);
            }
        }
    }

    private void RemoveTrackedDocument(string relativePath)
    {
        lock (_sync)
        {
            RemoveTrackedDocumentLocked(relativePath);
        }
    }

    private void RemoveTrackedDocumentLocked(string relativePath)
    {
        if (!_documents.Remove(relativePath, out TrackedDocumentState? removed))
        {
            return;
        }

        _totalTrackedSnapshotUtf8Bytes -= removed.SnapshotUtf8ByteCount;
        if (_totalTrackedSnapshotUtf8Bytes < 0)
        {
            throw new InvalidOperationException("document snapshot memory accounting became negative.");
        }
    }

    private static Dictionary<string, DocumentIdentity> CloneOpenSet(
        Dictionary<string, DocumentIdentity> source)
        => new(source, DocumentIdentity.PlatformPathComparer);

    private static bool OpenSetsEqual(
        Dictionary<string, DocumentIdentity> left,
        Dictionary<string, DocumentIdentity> right)
        => left.Count == right.Count
            && left.Keys.All(right.ContainsKey);

    private static int CountIntersection(
        IEnumerable<string> left,
        IEnumerable<string> right)
    {
        HashSet<string> rightSet = new(right, DocumentIdentity.PlatformPathComparer);
        int count = 0;
        foreach (string value in left)
        {
            if (rightSet.Contains(value))
            {
                count++;
            }
        }

        return count;
    }

    private StartupDocumentSeedResult CompleteStartupDocumentSeedSkip(
        WorkspacePublicationIdentity candidatePublicationIdentity,
        long roslynGeneration,
        string reason,
        long started)
    {
        _diagnosticLogging.WriteEvent(
            "startup_document_seed_skipped",
            new
            {
                reason,
                durationMs = Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds,
            });
        return StartupDocumentSeedResult.Skipped(
            candidatePublicationIdentity,
            roslynGeneration,
            reason);
    }

    private void WriteStartupDocumentSeedSkipped(string reason)
        => _diagnosticLogging.WriteEvent(
            "startup_document_seed_skipped",
            new
            {
                reason,
            });

    private static async Task<StartupDocumentDiskReadResult> ReadStartupDocumentTextAsync(
        string absolutePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] prefix = new byte[4];
            int prefixLength = 0;
            while (prefixLength < prefix.Length)
            {
                int read = await stream.ReadAsync(
                    prefix.AsMemory(prefixLength, prefix.Length - prefixLength),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                prefixLength += read;
            }

            StartupDocumentTextEncoding textEncoding = DetectStartupDocumentEncoding(
                prefix.AsSpan(0, prefixLength));
            long maxEncodedBytes = checked(
                ((long)DocumentSynchronizationLimits.MaxDocumentTextUtf8Bytes
                    * textEncoding.MaxEncodedBytesPerUtf8Byte)
                + textEncoding.PreambleLength);
            if (stream.Length > maxEncodedBytes)
            {
                return StartupDocumentDiskReadResult.Failure("document_too_large");
            }

            stream.Position = textEncoding.PreambleLength;
            using StreamReader reader = new(
                stream,
                textEncoding.Encoding,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: false);

            StringBuilder builder = new();
            char[] buffer = new char[4096];
            while (true)
            {
                int charsRead = await reader.ReadAsync(
                    buffer.AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                if (charsRead == 0)
                {
                    break;
                }

                if (builder.Length > DocumentSynchronizationLimits.MaxDocumentTextUtf8Bytes - charsRead)
                {
                    return StartupDocumentDiskReadResult.Failure("document_too_large");
                }

                builder.Append(buffer, 0, charsRead);
            }

            string text = builder.ToString();
            int textUtf8ByteCount;
            try
            {
                textUtf8ByteCount = StrictUtf8Encoding.GetByteCount(text);
            }
            catch (EncoderFallbackException)
            {
                return StartupDocumentDiskReadResult.Failure("decode_failed");
            }

            if (textUtf8ByteCount > DocumentSynchronizationLimits.MaxDocumentTextUtf8Bytes)
            {
                return StartupDocumentDiskReadResult.Failure("document_too_large");
            }

            return StartupDocumentDiskReadResult.Success(text, textUtf8ByteCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DecoderFallbackException)
        {
            return StartupDocumentDiskReadResult.Failure("decode_failed");
        }
        catch (FileNotFoundException)
        {
            return StartupDocumentDiskReadResult.Failure("target_missing");
        }
        catch (DirectoryNotFoundException)
        {
            return StartupDocumentDiskReadResult.Failure("target_missing");
        }
        catch (Exception exception) when (IsControlledStartupDocumentReadException(exception))
        {
            return StartupDocumentDiskReadResult.Failure("read_failed");
        }
    }

    private static StartupDocumentTextEncoding DetectStartupDocumentEncoding(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 4
            && prefix[0] == 0x00
            && prefix[1] == 0x00
            && prefix[2] == 0xFE
            && prefix[3] == 0xFF)
        {
            return new StartupDocumentTextEncoding(
                new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true),
                PreambleLength: 4,
                MaxEncodedBytesPerUtf8Byte: 4);
        }

        if (prefix.Length >= 4
            && prefix[0] == 0xFF
            && prefix[1] == 0xFE
            && prefix[2] == 0x00
            && prefix[3] == 0x00)
        {
            return new StartupDocumentTextEncoding(
                new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true),
                PreambleLength: 4,
                MaxEncodedBytesPerUtf8Byte: 4);
        }

        if (prefix.Length >= 3
            && prefix[0] == 0xEF
            && prefix[1] == 0xBB
            && prefix[2] == 0xBF)
        {
            return new StartupDocumentTextEncoding(
                StrictUtf8Encoding,
                PreambleLength: 3,
                MaxEncodedBytesPerUtf8Byte: 1);
        }

        if (prefix.Length >= 2
            && prefix[0] == 0xFE
            && prefix[1] == 0xFF)
        {
            return new StartupDocumentTextEncoding(
                new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true),
                PreambleLength: 2,
                MaxEncodedBytesPerUtf8Byte: 2);
        }

        if (prefix.Length >= 2
            && prefix[0] == 0xFF
            && prefix[1] == 0xFE)
        {
            return new StartupDocumentTextEncoding(
                new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true),
                PreambleLength: 2,
                MaxEncodedBytesPerUtf8Byte: 2);
        }

        return new StartupDocumentTextEncoding(
            StrictUtf8Encoding,
            PreambleLength: 0,
            MaxEncodedBytesPerUtf8Byte: 1);
    }

    private static bool IsControlledStartupDocumentPathException(Exception exception)
        => exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException
            or UnauthorizedAccessException;

    private static bool IsControlledStartupDocumentReadException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException;

    private static readonly UTF8Encoding StrictUtf8Encoding = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly record struct StartupDocumentTextEncoding(
        Encoding Encoding,
        int PreambleLength,
        int MaxEncodedBytesPerUtf8Byte);

    private readonly record struct StartupDocumentDiskReadResult(
        bool Succeeded,
        string? Text,
        int TextUtf8ByteCount,
        string? FailureReason)
    {
        public static StartupDocumentDiskReadResult Success(string text, int textUtf8ByteCount)
            => new(true, text, textUtf8ByteCount, null);

        public static StartupDocumentDiskReadResult Failure(string reason)
            => new(false, null, 0, reason);
    }

    private static void ValidateDocumentTransportLease(WorkloadExecutionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Lane != WorkloadLane.DocumentSynchronization)
        {
            throw new InvalidOperationException("document transport operation requires DocumentSynchronization workload ownership.");
        }
    }

    private static void ValidateWorkspaceConstructionLease(WorkloadExecutionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Lane != WorkloadLane.WorkspaceConstruction)
        {
            throw new InvalidOperationException("document Roslyn generation reconciliation requires existing WorkspaceConstruction ownership.");
        }
    }

    private void WriteEpochReconciled(
        DocumentEpochRequest request,
        WorkspacePublication publication,
        DocumentSynchronizationOutcome outcome,
        int declaredCount,
        int retainedCount,
        int closedCount)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent("document_sync_epoch_reconciled", new
            {
                clientGeneration = request.ClientGeneration,
                epochId = request.EpochId.ToString("D"),
                workspaceGeneration = publication.Identity.WorkspaceGeneration,
                workspacePublicationVersion = publication.Identity.PublicationVersion,
                roslynGeneration = publication.RoslynSnapshot.RoslynGeneration,
                outcome = outcome.ToString(),
                declaredOpenDocumentCount = declaredCount,
                retainedDocumentCount = retainedCount,
                closedDocumentCount = closedCount,
            });
        }
        else
        {
            _diagnosticLogging.WriteEvent("document_sync_epoch_reconciled");
        }
    }

    private void WriteEpochRejected(
        DocumentEpochRequest request,
        WorkspacePublication publication,
        DocumentSynchronizationOutcome outcome)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent("document_sync_epoch_rejected", new
            {
                clientGeneration = request.ClientGeneration,
                epochId = request.EpochId.ToString("D"),
                workspaceGeneration = publication.Identity.WorkspaceGeneration,
                workspacePublicationVersion = publication.Identity.PublicationVersion,
                roslynGeneration = publication.RoslynSnapshot.RoslynGeneration,
                outcome = outcome.ToString(),
            });
        }
        else
        {
            _diagnosticLogging.WriteEvent("document_sync_epoch_rejected");
        }
    }

    private void WriteDocumentClosed(
        DocumentEpochRequest request,
        WorkspacePublication publication,
        long workloadOperationId,
        string documentPath,
        bool roslynNotificationWasRequired,
        RoslynDocumentSendTiming roslynTiming)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent("document_sync_document_closed", new
            {
                clientGeneration = request.ClientGeneration,
                epochId = request.EpochId.ToString("D"),
                documentPath = BoundDocumentPath(documentPath),
                workspaceGeneration = publication.Identity.WorkspaceGeneration,
                workspacePublicationVersion = publication.Identity.PublicationVersion,
                roslynGeneration = publication.RoslynSnapshot.RoslynGeneration,
                roslynNotificationWasRequired,
                workloadOperationId,
                roslynSenderCaptureDurationMs = roslynTiming.SenderCaptureDurationMs,
                roslynNotificationAwaitDurationMs = roslynTiming.NotificationAwaitDurationMs,
                roslynPostSendGenerationValidationDurationMs = roslynTiming.PostSendGenerationValidationDurationMs,
                roslynDocumentSendTotalDurationMs = roslynTiming.TotalDurationMs,
            });
        }
        else
        {
            _diagnosticLogging.WriteEvent("document_sync_document_closed");
        }
    }

    private void WriteSnapshotAccepted(
        DocumentSnapshotRequest request,
        WorkspacePublication publication,
        long workloadOperationId,
        DocumentSynchronizationOutcome outcome,
        string documentPath,
        long acceptedClientVersion,
        int? roslynDocumentVersion,
        long roslynOverlayRevision,
        RoslynNotificationKind roslynNotificationKind,
        DocumentSnapshotTimingState? timing,
        RoslynDocumentSendTiming roslynTiming)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent("document_sync_snapshot_accepted", new
            {
                clientGeneration = request.ClientGeneration,
                epochId = request.EpochId.ToString("D"),
                documentPath = BoundDocumentPath(documentPath),
                clientVersion = acceptedClientVersion,
                snapshotUtf8Bytes = request.TextUtf8ByteCount,
                workspaceGeneration = publication.Identity.WorkspaceGeneration,
                workspacePublicationVersion = publication.Identity.PublicationVersion,
                roslynGeneration = publication.RoslynSnapshot.RoslynGeneration,
                roslynDocumentVersion,
                roslynOverlayRevision,
                outcome = outcome.ToString(),
                workloadOperationId,
                roslynNotificationKind = roslynNotificationKind.ToString(),
                preRoslynValidationDurationMs = timing?.PreRoslynValidationDurationMs,
                roslynSendObservedDurationMs = timing?.RoslynSendObservedDurationMs,
                stateCommitDurationMs = timing?.StateCommitDurationMs,
                overlayCancellationSignalDurationMs = timing?.OverlayCancellationSignalDurationMs,
                snapshotPipelineDurationMs = timing?.SnapshotPipelineDurationMs,
                roslynSenderCaptureDurationMs = roslynTiming.SenderCaptureDurationMs,
                roslynNotificationAwaitDurationMs = roslynTiming.NotificationAwaitDurationMs,
                roslynPostSendGenerationValidationDurationMs = roslynTiming.PostSendGenerationValidationDurationMs,
                roslynDocumentSendTotalDurationMs = roslynTiming.TotalDurationMs,
            });
        }
        else
        {
            _diagnosticLogging.WriteEvent("document_sync_snapshot_accepted");
        }
    }

    private void WriteSnapshotRejected(
        DocumentSnapshotRequest request,
        WorkspacePublication publication,
        DocumentSynchronizationOutcome outcome,
        string? documentPath,
        long? acceptedClientVersion)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent("document_sync_snapshot_rejected", new
            {
                clientGeneration = request.ClientGeneration,
                epochId = request.EpochId.ToString("D"),
                documentPath = documentPath is null ? null : BoundDocumentPath(documentPath),
                clientVersion = request.ClientVersion,
                acceptedClientVersion,
                snapshotUtf8Bytes = request.TextUtf8ByteCount,
                workspaceGeneration = publication.Identity.WorkspaceGeneration,
                workspacePublicationVersion = publication.Identity.PublicationVersion,
                roslynGeneration = publication.RoslynSnapshot.RoslynGeneration,
                outcome = outcome.ToString(),
            });
        }
        else
        {
            _diagnosticLogging.WriteEvent("document_sync_snapshot_rejected");
        }
    }

    private void WriteReplayStarted(
        WorkspacePublicationIdentity publicationIdentity,
        long roslynGeneration,
        int replayDocumentCount)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent("document_sync_generation_replay_started", new
            {
                workspaceGeneration = publicationIdentity.WorkspaceGeneration,
                workspacePublicationVersion = publicationIdentity.PublicationVersion,
                roslynGeneration,
                replayDocumentCount,
            });
        }
        else
        {
            _diagnosticLogging.WriteEvent("document_sync_generation_replay_started");
        }
    }

    private void WriteReplayCompleted(
        WorkspacePublicationIdentity publicationIdentity,
        long roslynGeneration,
        int replayDocumentCount,
        double? durationMs)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent("document_sync_generation_replay_completed", new
            {
                workspaceGeneration = publicationIdentity.WorkspaceGeneration,
                workspacePublicationVersion = publicationIdentity.PublicationVersion,
                roslynGeneration,
                replayDocumentCount,
                durationMs,
            });
        }
        else
        {
            _diagnosticLogging.WriteEvent("document_sync_generation_replay_completed");
        }
    }

    private void WriteReplayFault(
        WorkspacePublicationIdentity publicationIdentity,
        long roslynGeneration,
        int replayDocumentCount,
        int replayedDocumentCount)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent("document_sync_generation_replay_fault", new
            {
                workspaceGeneration = publicationIdentity.WorkspaceGeneration,
                workspacePublicationVersion = publicationIdentity.PublicationVersion,
                roslynGeneration,
                replayDocumentCount,
                replayedDocumentCount,
            });
        }
        else
        {
            _diagnosticLogging.WriteEvent("document_sync_generation_replay_fault");
        }
    }

    private static string BoundDocumentPath(string path)
        => path.Length <= DocumentSynchronizationLimits.MaxDocumentPathLength
            ? path
            : path[..DocumentSynchronizationLimits.MaxDocumentPathLength];

    private enum RoslynNotificationKind
    {
        None = 0,
        DidOpen = 1,
        DidChange = 2,
    }

    private sealed class DocumentSnapshotTimingState
    {
        private readonly long _started = Stopwatch.GetTimestamp();

        public double? PreRoslynValidationDurationMs { get; private set; }

        public double? RoslynSendObservedDurationMs { get; private set; }

        public double? StateCommitDurationMs { get; private set; }

        public double? OverlayCancellationSignalDurationMs { get; private set; }

        public double? SnapshotPipelineDurationMs { get; private set; }

        public long StartPhase() => Stopwatch.GetTimestamp();

        public void CompletePreRoslynValidation()
            => PreRoslynValidationDurationMs ??= Stopwatch.GetElapsedTime(_started, Stopwatch.GetTimestamp()).TotalMilliseconds;

        public void CompleteRoslynSend(long started)
            => RoslynSendObservedDurationMs = Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;

        public void CompleteStateCommit(long started)
            => StateCommitDurationMs = Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;

        public void CompleteOverlayCancellationSignal(long started)
            => OverlayCancellationSignalDurationMs = Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;

        public void CompleteSnapshotPipeline()
            => SnapshotPipelineDurationMs = Stopwatch.GetElapsedTime(_started, Stopwatch.GetTimestamp()).TotalMilliseconds;
    }

    private sealed class TrackedDocumentState
    {
        public TrackedDocumentState(DocumentIdentity identity)
        {
            Identity = identity;
        }

        public DocumentIdentity Identity { get; set; }
        public long LastAcceptedClientVersion { get; set; }
        public bool HasCurrentAuthoritySnapshot { get; set; }
        public string LastFullSnapshotText { get; set; } = string.Empty;
        public int SnapshotUtf8ByteCount { get; set; }
        public bool IsOpenInRoslyn { get; set; }
        public long RoslynGeneration { get; set; }
        public int RoslynLspVersion { get; set; }
        public WorkspacePublicationIdentity LastWorkspacePublicationIdentity { get; set; }
        public bool IsCurrentWorkspaceSource { get; set; }
    }
}
