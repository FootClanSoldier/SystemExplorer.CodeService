using System.Diagnostics;

namespace SystemExplorer.CodeService;

internal sealed class WorkspaceHost : IDisposable
{
    private static readonly TimeSpan FilesystemQuietPeriod = TimeSpan.FromMilliseconds(200);

    private readonly object _sync = new();
    private readonly WorkloadCoordinator _workloadCoordinator;
    private readonly WorkspaceProjectDiscovery _projectDiscovery;
    private readonly ProjectIndexHost _projectIndexHost;
    private readonly RoslynLanguageServerHost _roslynLanguageServerHost;
    private readonly DocumentSynchronizationHost _documentSynchronizationHost;
    private readonly DiagnosticLogging _diagnosticLogging;
    private readonly WorkspaceDirtyIntent _pendingDirtyIntent = new();

    private WorkspaceState _state = WorkspaceState.Uninitialized;
    private WorkspaceIdentity? _workspaceIdentity;
    private bool _initialProjectRootValidated;
    private WorkspacePublication? _workspacePublication;
    private WorkspaceFileChangeObserver? _changeObserver;
    private Timer? _debounceTimer;
    private Task? _activeRuntimeReconciliationTask;
    private long? _activeRuntimeOperationId;
    private string? _faultKind;
    private long _workspaceGeneration;
    private long _workspacePublicationVersion;
    private long _dirtyVersion;
    private long _lastDirtyTimestamp;
    private bool _runtimeReconciliationActive;
    private bool _requiresConservativeRevalidation;
    private bool _pendingOverflowLog;
    private bool _pendingObserverFaultLog;
    private bool _disposed;

    public WorkspaceHost(
        WorkloadCoordinator workloadCoordinator,
        RoslynLanguageServerHost roslynLanguageServerHost,
        DocumentSynchronizationHost documentSynchronizationHost,
        DiagnosticLogging diagnosticLogging,
        WorkspaceIdentity? startupWorkspaceIdentity = null)
    {
        _workloadCoordinator = workloadCoordinator
            ?? throw new ArgumentNullException(nameof(workloadCoordinator));
        _roslynLanguageServerHost = roslynLanguageServerHost
            ?? throw new ArgumentNullException(nameof(roslynLanguageServerHost));
        _documentSynchronizationHost = documentSynchronizationHost
            ?? throw new ArgumentNullException(nameof(documentSynchronizationHost));
        _diagnosticLogging = diagnosticLogging
            ?? throw new ArgumentNullException(nameof(diagnosticLogging));
        _workspaceIdentity = startupWorkspaceIdentity;
        _projectDiscovery = new WorkspaceProjectDiscovery();
        _projectIndexHost = new ProjectIndexHost(_diagnosticLogging);
    }

    public Task<WorkspaceInitializationResult> InitializeAsync(string? projectRoot)
    {
        WorkspaceIdentityCreationResult identityResult = WorkspaceIdentity.TryCreate(projectRoot);
        if (!identityResult.IsSuccess)
        {
            return Task.FromResult(
                WorkspaceInitializationResult.InvalidRequest(
                    GetStatusSnapshot(),
                    identityResult.ErrorMessage!));
        }

        return InitializeCoreAsync(
            identityResult.Identity!,
            WorkspaceInitializationSource.TransportRequest);
    }

    internal Task<WorkspaceInitializationResult> InitializeFromStartupAsync(
        WorkspaceIdentity workspaceIdentity)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);

        return InitializeCoreAsync(
            workspaceIdentity,
            WorkspaceInitializationSource.StartupProjectRoot);
    }

    private async Task<WorkspaceInitializationResult> InitializeCoreAsync(
        WorkspaceIdentity requestedIdentity,
        WorkspaceInitializationSource initializationSource)
    {
        WorkloadExecutionLease lease;
        long workspaceGeneration;
        ProjectIndexReconciliationHints initializationHints;
        ProjectIndexOperationTrigger operationTrigger;

        lock (_sync)
        {
            if (_disposed || _state == WorkspaceState.ShuttingDown)
            {
                return WorkspaceInitializationResult.Unavailable(CreateStatusSnapshotLocked());
            }

            if (_workspaceIdentity is not null)
            {
                if (!_workspaceIdentity.Equals(requestedIdentity))
                {
                    return WorkspaceInitializationResult.WorkspaceMismatch(
                        CreateStatusSnapshotLocked());
                }

                if (_state == WorkspaceState.Ready)
                {
                    return WorkspaceInitializationResult.Success(
                        CreateStatusSnapshotLocked(),
                        reusedExistingWorkspace: true);
                }

                if (_state is WorkspaceState.Initializing or WorkspaceState.Indexing)
                {
                    return WorkspaceInitializationResult.Busy(CreateStatusSnapshotLocked());
                }
            }

            if (!_initialProjectRootValidated)
            {
                WorkspaceInitialRootValidationResult rootValidation =
                    WorkspaceIdentity.ValidateInitialGodotProjectRoot(requestedIdentity);
                if (!rootValidation.IsSuccess)
                {
                    if (initializationSource == WorkspaceInitializationSource.StartupProjectRoot)
                    {
                        _state = WorkspaceState.Faulted;
                        _faultKind = WorkspaceFaultKinds.InvalidProjectRoot;
                    }

                    return WorkspaceInitializationResult.InvalidRequest(
                        CreateStatusSnapshotLocked(),
                        rootValidation.ErrorMessage!);
                }
            }

            WorkloadAdmissionResult admission = _workloadCoordinator.TryAdmitExclusive(
                WorkloadLane.WorkspaceConstruction);

            if (admission.Status == WorkloadAdmissionStatus.Busy)
            {
                return WorkspaceInitializationResult.Busy(CreateStatusSnapshotLocked());
            }

            if (admission.Status == WorkloadAdmissionStatus.ShuttingDown
                || admission.Lease is not WorkloadExecutionLease admittedLease)
            {
                return WorkspaceInitializationResult.Unavailable(CreateStatusSnapshotLocked());
            }

            lease = admittedLease;

            if (_workspaceIdentity is null)
            {
                _workspaceIdentity = requestedIdentity;
            }

            _initialProjectRootValidated = true;
            operationTrigger = _state == WorkspaceState.Faulted
                ? ProjectIndexOperationTrigger.ExplicitRetry
                : ProjectIndexOperationTrigger.Initialization;
            _state = WorkspaceState.Initializing;
            _faultKind = null;
            _pendingDirtyIntent.Clear();
            _dirtyVersion = 0;
            _lastDirtyTimestamp = 0;
            _pendingOverflowLog = false;
            _pendingObserverFaultLog = false;
            _runtimeReconciliationActive = false;
            _activeRuntimeReconciliationTask = null;
            _activeRuntimeOperationId = null;

            workspaceGeneration = ++_workspaceGeneration;
            _debounceTimer = CreateDebounceTimer(workspaceGeneration);
            initializationHints = _requiresConservativeRevalidation
                ? ProjectIndexReconciliationHints.FullSourceValidation
                : ProjectIndexReconciliationHints.None;
        }

        bool diagnosticsEnabled = _diagnosticLogging.IsEnabled;
        long initializationStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        double discoveryDurationMs = 0;
        double indexDurationMs = 0;
        double roslynReconcileDurationMs = 0;
        double indexRoslynParallelDurationMs = 0;
        double indexRoslynOverlapDurationMs = 0;
        double documentReplayDurationMs = 0;
        double publicationCommitDurationMs = 0;
        ProjectIndexOperationContext operationContext = new(
            operationTrigger,
            lease.OperationId,
            workspaceGeneration,
            DirtyVersion: null,
            DirtySignalCount: 0);

        if (diagnosticsEnabled)
        {
            _diagnosticLogging.WriteEvent(
                "workspace_initialization_started",
                new WorkspaceInitializationStartedDetails(
                    initializationSource,
                    operationTrigger,
                    lease.OperationId,
                    workspaceGeneration,
                    initializationHints.ForceFullSourceValidation,
                    initializationHints.ForcedFingerprintPathCount));
        }

        WorkspaceFileChangeObserver? newlyStartedObserver = null;
        try
        {
            newlyStartedObserver = WorkspaceFileChangeObserver.Start(
                requestedIdentity,
                workspaceGeneration,
                OnWorkspaceObservedChange);

            lock (_sync)
            {
                if (_disposed
                    || _state == WorkspaceState.ShuttingDown
                    || _workspaceGeneration != workspaceGeneration)
                {
                    return WorkspaceInitializationResult.Unavailable(CreateStatusSnapshotLocked());
                }

                _changeObserver = newlyStartedObserver;
                newlyStartedObserver = null;
            }

            _diagnosticLogging.WriteEvent("workspace_change_observer_started");

            long discoveryStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            WorkspaceProjectSnapshot candidateSnapshot = _projectDiscovery.Discover(
                requestedIdentity,
                lease.ServiceWorkShutdownToken);
            if (diagnosticsEnabled)
            {
                discoveryDurationMs = Stopwatch.GetElapsedTime(
                    discoveryStarted,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
            }

            bool useCurrentGenerationForRetry;
            WorkspacePublicationIdentity candidatePublicationIdentity;
            WorkspacePublication? previousPublication;
            lock (_sync)
            {
                if (_disposed
                    || _state == WorkspaceState.ShuttingDown
                    || _workspaceGeneration != workspaceGeneration)
                {
                    return WorkspaceInitializationResult.Unavailable(CreateStatusSnapshotLocked());
                }

                _state = WorkspaceState.Indexing;
                useCurrentGenerationForRetry = _requiresConservativeRevalidation
                    && _projectIndexHost.GetCurrentGenerationSnapshot() is not null;
                candidatePublicationIdentity = new WorkspacePublicationIdentity(
                    workspaceGeneration,
                    checked(_workspacePublicationVersion + 1));
                previousPublication = _workspacePublication;
            }

            bool requiresRoslynGenerationReplacement = initializationHints.ForceFullSourceValidation
                || (previousPublication is not null
                    && HasProjectTopologyChanged(previousPublication.ProjectSnapshot, candidateSnapshot));

            long indexRoslynParallelStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;

            Task<TimedOperationResult<RoslynProjectLoadResult>> roslynTask = MeasureOperationAsync(
                () => _roslynLanguageServerHost.ReconcileProjectLoadAsync(
                    candidateSnapshot,
                    candidatePublicationIdentity,
                    requiresRoslynGenerationReplacement,
                    lease.ServiceWorkShutdownToken),
                diagnosticsEnabled);

            Task<TimedOperationResult<ProjectIndexGeneration>> indexTask = MeasureOperationAsync(
                () => useCurrentGenerationForRetry
                    ? _projectIndexHost.ReconcileCurrentAsync(
                        candidateSnapshot,
                        initializationHints,
                        operationContext,
                        lease.ServiceWorkShutdownToken)
                    : _projectIndexHost.InitializeOrReconcileAsync(
                        candidateSnapshot,
                        initializationHints,
                        operationContext,
                        lease.ServiceWorkShutdownToken),
                diagnosticsEnabled);

            await Task.WhenAll(roslynTask, indexTask).ConfigureAwait(false);

            if (diagnosticsEnabled)
            {
                indexRoslynParallelDurationMs = Stopwatch.GetElapsedTime(
                    indexRoslynParallelStarted,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
            }

            TimedOperationResult<RoslynProjectLoadResult> timedRoslynResult =
                await roslynTask.ConfigureAwait(false);
            TimedOperationResult<ProjectIndexGeneration> timedIndexResult =
                await indexTask.ConfigureAwait(false);

            RoslynProjectLoadResult roslynResult = timedRoslynResult.Result;
            ProjectIndexGeneration projectIndexGeneration = timedIndexResult.Result;

            if (diagnosticsEnabled)
            {
                roslynReconcileDurationMs = timedRoslynResult.DurationMs;
                indexDurationMs = timedIndexResult.DurationMs;
                indexRoslynOverlapDurationMs = Math.Max(
                    0,
                    indexDurationMs
                        + roslynReconcileDurationMs
                        - indexRoslynParallelDurationMs);
            }

            long documentReplayStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            await _documentSynchronizationHost.ReconcileRoslynGenerationAsync(
                candidateSnapshot,
                candidatePublicationIdentity,
                roslynResult.Snapshot,
                roslynResult.ReusedExistingGeneration,
                lease,
                lease.ServiceWorkShutdownToken).ConfigureAwait(false);
            if (diagnosticsEnabled)
            {
                documentReplayDurationMs = Stopwatch.GetElapsedTime(
                    documentReplayStarted,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
            }

            long publicationCommitStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            RoslynLanguageServerSnapshot finalRoslynSnapshot = _roslynLanguageServerHost.GetSnapshot();
            ValidateRoslynPublicationCorrelation(
                requestedIdentity,
                candidatePublicationIdentity,
                finalRoslynSnapshot);

            WorkspacePublication candidatePublication = new(
                requestedIdentity,
                candidatePublicationIdentity,
                candidateSnapshot,
                projectIndexGeneration.GenerationId,
                finalRoslynSnapshot);

            WorkspaceStatusSnapshot readyStatus;
            lock (_sync)
            {
                if (_disposed
                    || _state == WorkspaceState.ShuttingDown
                    || _workspaceGeneration != workspaceGeneration)
                {
                    return WorkspaceInitializationResult.Unavailable(CreateStatusSnapshotLocked());
                }

                _workspacePublication = candidatePublication;
                _workspacePublicationVersion = candidatePublicationIdentity.PublicationVersion;
                _faultKind = null;
                _requiresConservativeRevalidation = false;
                _state = WorkspaceState.Ready;
                ArmPendingDirtyLocked();
                readyStatus = CreateStatusSnapshotLocked();
            }

            if (diagnosticsEnabled)
            {
                publicationCommitDurationMs = Stopwatch.GetElapsedTime(
                    publicationCommitStarted,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
            }

            WriteWorkspacePublicationCommitted(
                candidatePublication,
                roslynResult.ReusedExistingGeneration,
                requiresRoslynGenerationReplacement);

            if (diagnosticsEnabled)
            {
                double totalInitializationDurationMs = Stopwatch.GetElapsedTime(
                    initializationStarted,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
                double explicitWorkDurationMs = discoveryDurationMs
                    + indexRoslynParallelDurationMs
                    + documentReplayDurationMs
                    + publicationCommitDurationMs;
                double unattributedDurationMs = totalInitializationDurationMs - explicitWorkDurationMs;
                DiagnosticResourceSnapshot resources =
                    DiagnosticResourceSnapshot.CaptureIfEnabled(_diagnosticLogging);
                _diagnosticLogging.WriteEvent(
                    "workspace_ready",
                    new WorkspaceReadyDetails(
                        initializationSource,
                        operationTrigger,
                        lease.OperationId,
                        workspaceGeneration,
                        candidatePublicationIdentity.PublicationVersion,
                        projectIndexGeneration.GenerationId,
                        finalRoslynSnapshot.State,
                        finalRoslynSnapshot.RoslynGeneration,
                        candidateSnapshot.SourceFiles.Count,
                        candidateSnapshot.ProjectFiles.Count,
                        candidateSnapshot.SolutionFiles.Count,
                        discoveryDurationMs,
                        indexDurationMs,
                        roslynReconcileDurationMs,
                        indexRoslynParallelDurationMs,
                        indexRoslynOverlapDurationMs,
                        documentReplayDurationMs,
                        publicationCommitDurationMs,
                        explicitWorkDurationMs,
                        unattributedDurationMs,
                        totalInitializationDurationMs,
                        resources.WorkingSetBytes,
                        resources.ManagedMemoryBytes));
            }

            return WorkspaceInitializationResult.Success(
                readyStatus,
                reusedExistingWorkspace: false);
        }
        catch (OperationCanceledException)
            when (lease.ServiceWorkShutdownToken.IsCancellationRequested)
        {
            ObserverResources resourcesToRetire = default;
            bool writeLifecycleEvent = false;
            WorkspaceStatusSnapshot status;

            lock (_sync)
            {
                if (!_disposed && _state != WorkspaceState.ShuttingDown)
                {
                    _state = WorkspaceState.ShuttingDown;
                    _faultKind = null;
                    _pendingDirtyIntent.Clear();
                    resourcesToRetire = DetachObservationResourcesLocked(
                        invalidateGeneration: true);
                    writeLifecycleEvent = true;
                }

                status = CreateStatusSnapshotLocked();
            }

            RetireObservationResources(resourcesToRetire);
            if (writeLifecycleEvent)
            {
                _diagnosticLogging.WriteEvent("workspace_shutting_down");
            }

            return WorkspaceInitializationResult.Unavailable(status);
        }
        catch (Exception exception)
        {
            string faultKind = ClassifyFault(exception);
            ObserverResources resourcesToRetire = default;
            WorkspaceStatusSnapshot resultStatus;

            lock (_sync)
            {
                if (_disposed
                    || _state == WorkspaceState.ShuttingDown
                    || _workspaceGeneration != workspaceGeneration)
                {
                    return WorkspaceInitializationResult.Unavailable(CreateStatusSnapshotLocked());
                }

                _faultKind = faultKind;
                _state = WorkspaceState.Faulted;
                resourcesToRetire = DetachObservationResourcesLocked(invalidateGeneration: true);
                resultStatus = CreateStatusSnapshotLocked();
            }

            RetireObservationResources(resourcesToRetire);
            if (diagnosticsEnabled)
            {
                _diagnosticLogging.WriteFault(
                    "workspace_fault",
                    exception,
                    new WorkspaceInitializationFaultDetails(
                        initializationSource,
                        operationTrigger,
                        lease.OperationId,
                        workspaceGeneration,
                        Stopwatch.GetElapsedTime(
                            initializationStarted,
                            Stopwatch.GetTimestamp()).TotalMilliseconds));
            }
            else
            {
                _diagnosticLogging.WriteFault("workspace_fault", exception);
            }

            return WorkspaceInitializationResult.Faulted(resultStatus);
        }
        finally
        {
            newlyStartedObserver?.Dispose();
            lease.Retire();
        }
    }

    private static async Task<TimedOperationResult<TResult>> MeasureOperationAsync<TResult>(
        Func<Task<TResult>> operation,
        bool diagnosticsEnabled)
    {
        long started = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        TResult result = await operation().ConfigureAwait(false);
        double durationMs = diagnosticsEnabled
            ? Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds
            : 0;
        return new TimedOperationResult<TResult>(result, durationMs);
    }

    public WorkspaceStatusSnapshot GetStatusSnapshot()
    {
        lock (_sync)
        {
            return CreateStatusSnapshotLocked();
        }
    }

    public bool TryGetCurrentPublication(out WorkspacePublication publication)
    {
        lock (_sync)
        {
            if (!_disposed
                && _state == WorkspaceState.Ready
                && _workspacePublication is WorkspacePublication currentPublication)
            {
                publication = currentPublication;
                return true;
            }

            publication = null!;
            return false;
        }
    }

    public void BeginShutdown()
    {
        ObserverResources resourcesToRetire;
        bool writeLifecycleEvent;

        lock (_sync)
        {
            if (_disposed || _state == WorkspaceState.ShuttingDown)
            {
                return;
            }

            _state = WorkspaceState.ShuttingDown;
            _faultKind = null;
            _pendingDirtyIntent.Clear();
            resourcesToRetire = DetachObservationResourcesLocked(invalidateGeneration: true);
            writeLifecycleEvent = true;
        }

        RetireObservationResources(resourcesToRetire);
        _projectIndexHost.BeginShutdown();

        if (writeLifecycleEvent)
        {
            _diagnosticLogging.WriteEvent("workspace_shutting_down");
        }
    }

    public void Dispose()
    {
        ObserverResources resourcesToRetire;
        bool writeLifecycleEvent;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _state = WorkspaceState.ShuttingDown;
            _workspaceIdentity = null;
            _initialProjectRootValidated = false;
            _workspacePublication = null;
            _faultKind = null;
            _pendingDirtyIntent.Clear();
            resourcesToRetire = DetachObservationResourcesLocked(invalidateGeneration: true);
            writeLifecycleEvent = true;
        }

        RetireObservationResources(resourcesToRetire);
        _projectIndexHost.Dispose();

        if (writeLifecycleEvent)
        {
            _diagnosticLogging.WriteEvent("workspace_stopped");
        }
    }

    private Timer CreateDebounceTimer(long workspaceGeneration)
        => new(
            _ => OnDebounceTimer(workspaceGeneration),
            state: null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

    private void OnWorkspaceObservedChange(
        long workspaceGeneration,
        WorkspaceObservedChange change)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed
                    || _state is WorkspaceState.Faulted or WorkspaceState.ShuttingDown
                    || _workspaceGeneration != workspaceGeneration)
                {
                    return;
                }

                long dirtyVersion = unchecked(++_dirtyVersion);
                _lastDirtyTimestamp = Stopwatch.GetTimestamp();
                _pendingDirtyIntent.Mark(
                    dirtyVersion,
                    change.ForceFullSourceValidation,
                    change.ForceRoslynProjectReload,
                    change.SourceRelativePath,
                    change.SecondarySourceRelativePath);

                if (change.Incident == WorkspaceObserverIncident.Overflow)
                {
                    _pendingOverflowLog = true;
                }
                else if (change.Incident == WorkspaceObserverIncident.Fault)
                {
                    _pendingObserverFaultLog = true;
                }

                _debounceTimer?.Change(
                    FilesystemQuietPeriod,
                    Timeout.InfiniteTimeSpan);
            }
        }
        catch
        {
            // FileSystemWatcher callbacks are containment boundaries.
        }
    }

    private void OnDebounceTimer(long workspaceGeneration)
    {
        bool diagnosticsEnabled = _diagnosticLogging.IsEnabled;
        WorkloadExecutionLease? admittedLease = null;
        WorkspaceDirtyBatch batch = default;
        WorkspaceIdentity? workspaceIdentity = null;
        WorkspaceReconciliationCorrelationDetails? correlationDetails = null;
        bool startReconciliation = false;
        bool logOverflow = false;
        bool logObserverFault = false;

        try
        {
            lock (_sync)
            {
                if (_disposed
                    || _state is WorkspaceState.Faulted or WorkspaceState.ShuttingDown
                    || _workspaceGeneration != workspaceGeneration)
                {
                    return;
                }

                if (!_pendingDirtyIntent.IsDirty)
                {
                    return;
                }

                TimeSpan elapsed = _lastDirtyTimestamp == 0
                    ? FilesystemQuietPeriod
                    : Stopwatch.GetElapsedTime(_lastDirtyTimestamp, Stopwatch.GetTimestamp());

                if (elapsed < FilesystemQuietPeriod)
                {
                    _debounceTimer?.Change(
                        FilesystemQuietPeriod - elapsed,
                        Timeout.InfiniteTimeSpan);
                    return;
                }

                if (_state != WorkspaceState.Ready || _runtimeReconciliationActive)
                {
                    return;
                }

                WorkloadAdmissionResult admission = _workloadCoordinator.TryAdmitExclusive(
                    WorkloadLane.WorkspaceConstruction);

                if (admission.Status == WorkloadAdmissionStatus.Busy)
                {
                    _debounceTimer?.Change(
                        FilesystemQuietPeriod,
                        Timeout.InfiniteTimeSpan);
                    return;
                }

                if (admission.Status == WorkloadAdmissionStatus.ShuttingDown
                    || admission.Lease is not WorkloadExecutionLease lease)
                {
                    return;
                }

                admittedLease = lease;
                batch = _pendingDirtyIntent.CaptureAndReset();
                logOverflow = _pendingOverflowLog;
                logObserverFault = _pendingObserverFaultLog;
                _pendingOverflowLog = false;
                _pendingObserverFaultLog = false;
                workspaceIdentity = _workspaceIdentity
                    ?? throw new InvalidOperationException(
                        "runtime reconciliation lost the established workspace identity.");

                if (diagnosticsEnabled)
                {
                    correlationDetails = new WorkspaceReconciliationCorrelationDetails(
                        lease.OperationId,
                        workspaceGeneration,
                        batch.DirtyVersion,
                        batch.DirtySignalCount,
                        batch.ReconciliationHints.ForceFullSourceValidation,
                        batch.ReconciliationHints.ForcedFingerprintPathCount,
                        batch.ForceRoslynProjectReload);
                }

                _runtimeReconciliationActive = true;
                _activeRuntimeOperationId = lease.OperationId;
                _state = WorkspaceState.Indexing;
                Task task = RunRuntimeReconciliationAsync(
                    workspaceGeneration,
                    workspaceIdentity,
                    batch,
                    lease);
                _activeRuntimeReconciliationTask = task;
                startReconciliation = true;
            }

            if (logOverflow)
            {
                _diagnosticLogging.WriteEvent("workspace_change_observer_overflow");
            }

            if (logObserverFault)
            {
                _diagnosticLogging.WriteEvent("workspace_change_observer_fault");
            }

            if (startReconciliation)
            {
                if (correlationDetails is not null)
                {
                    _diagnosticLogging.WriteEvent(
                        "workspace_reconciliation_scheduled",
                        correlationDetails);
                    _diagnosticLogging.WriteEvent(
                        "workspace_reconciliation_started",
                        correlationDetails);
                }
                else
                {
                    _diagnosticLogging.WriteEvent("workspace_reconciliation_scheduled");
                    _diagnosticLogging.WriteEvent("workspace_reconciliation_started");
                }
            }
        }
        catch (Exception exception)
        {
            if (admittedLease is not null && !startReconciliation)
            {
                admittedLease.Retire();
            }

            if (correlationDetails is not null)
            {
                _diagnosticLogging.WriteFault(
                    "workspace_reconciliation_fault",
                    exception,
                    new WorkspaceReconciliationTerminalDetails(
                        correlationDetails.WorkloadOperationId,
                        correlationDetails.WorkspaceGeneration,
                        correlationDetails.DirtyVersion,
                        correlationDetails.DirtySignalCount,
                        correlationDetails.ForceFullSourceValidation,
                        correlationDetails.ForcedFingerprintPathCount,
                        correlationDetails.ForceRoslynProjectReload,
                        TotalDurationMs: 0,
                        PendingNewerDirty: false,
                        PendingNewerDirtySignalCount: 0));
            }
            else
            {
                _diagnosticLogging.WriteFault("workspace_reconciliation_fault", exception);
            }
        }
    }

    private async Task RunRuntimeReconciliationAsync(
        long workspaceGeneration,
        WorkspaceIdentity workspaceIdentity,
        WorkspaceDirtyBatch batch,
        WorkloadExecutionLease lease)
    {
        await Task.Yield();

        bool diagnosticsEnabled = _diagnosticLogging.IsEnabled;
        long totalStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        double discoveryDurationMs = 0;
        double indexDurationMs = 0;
        double roslynReconcileDurationMs = 0;
        double documentReplayDurationMs = 0;
        double publicationCommitDurationMs = 0;
        ProjectIndexOperationContext operationContext = new(
            ProjectIndexOperationTrigger.RuntimeFilesystem,
            lease.OperationId,
            workspaceGeneration,
            batch.DirtyVersion,
            batch.DirtySignalCount);

        try
        {
            WorkspacePublication previousPublication;
            lock (_sync)
            {
                previousPublication = _workspacePublication
                    ?? throw new InvalidOperationException("runtime reconciliation requires a current workspace publication.");
            }

            long discoveryStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            WorkspaceProjectSnapshot candidateSnapshot = _projectDiscovery.Discover(
                workspaceIdentity,
                lease.ServiceWorkShutdownToken);
            if (diagnosticsEnabled)
            {
                discoveryDurationMs = Stopwatch.GetElapsedTime(
                    discoveryStarted,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
            }

            long indexStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            ProjectIndexGeneration projectIndexGeneration = await _projectIndexHost.ReconcileCurrentAsync(
                candidateSnapshot,
                batch.ReconciliationHints,
                operationContext,
                lease.ServiceWorkShutdownToken).ConfigureAwait(false);
            if (diagnosticsEnabled)
            {
                indexDurationMs = Stopwatch.GetElapsedTime(
                    indexStarted,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
            }

            WorkspacePublicationIdentity candidatePublicationIdentity;
            lock (_sync)
            {
                if (_disposed
                    || _state == WorkspaceState.ShuttingDown
                    || _workspaceGeneration != workspaceGeneration)
                {
                    ClearRuntimeOperationOwnershipLocked(workspaceGeneration, lease.OperationId);
                    return;
                }

                candidatePublicationIdentity = new WorkspacePublicationIdentity(
                    workspaceGeneration,
                    checked(_workspacePublicationVersion + 1));
            }

            bool requiresRoslynGenerationReplacement = batch.ForceRoslynProjectReload
                || HasProjectTopologyChanged(previousPublication.ProjectSnapshot, candidateSnapshot);

            long roslynReconcileStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            RoslynProjectLoadResult roslynResult = await _roslynLanguageServerHost.ReconcileProjectLoadAsync(
                candidateSnapshot,
                candidatePublicationIdentity,
                requiresRoslynGenerationReplacement,
                lease.ServiceWorkShutdownToken).ConfigureAwait(false);
            if (diagnosticsEnabled)
            {
                roslynReconcileDurationMs = Stopwatch.GetElapsedTime(
                    roslynReconcileStarted,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
            }

            long documentReplayStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            await _documentSynchronizationHost.ReconcileRoslynGenerationAsync(
                candidateSnapshot,
                candidatePublicationIdentity,
                roslynResult.Snapshot,
                roslynResult.ReusedExistingGeneration,
                lease,
                lease.ServiceWorkShutdownToken).ConfigureAwait(false);
            if (diagnosticsEnabled)
            {
                documentReplayDurationMs = Stopwatch.GetElapsedTime(
                    documentReplayStarted,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
            }

            long publicationCommitStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            RoslynLanguageServerSnapshot finalRoslynSnapshot = _roslynLanguageServerHost.GetSnapshot();
            ValidateRoslynPublicationCorrelation(
                workspaceIdentity,
                candidatePublicationIdentity,
                finalRoslynSnapshot);

            WorkspacePublication candidatePublication = new(
                workspaceIdentity,
                candidatePublicationIdentity,
                candidateSnapshot,
                projectIndexGeneration.GenerationId,
                finalRoslynSnapshot);

            lock (_sync)
            {
                if (_disposed
                    || _state == WorkspaceState.ShuttingDown
                    || _workspaceGeneration != workspaceGeneration)
                {
                    ClearRuntimeOperationOwnershipLocked(workspaceGeneration, lease.OperationId);
                    return;
                }

                _workspacePublication = candidatePublication;
                _workspacePublicationVersion = candidatePublicationIdentity.PublicationVersion;
                _faultKind = null;
                _state = WorkspaceState.Ready;
                ClearRuntimeOperationOwnershipLocked(workspaceGeneration, lease.OperationId);
                ArmPendingDirtyLocked();
            }

            if (diagnosticsEnabled)
            {
                publicationCommitDurationMs = Stopwatch.GetElapsedTime(
                    publicationCommitStarted,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
            }

            WriteWorkspacePublicationCommitted(
                candidatePublication,
                roslynResult.ReusedExistingGeneration,
                requiresRoslynGenerationReplacement);

            if (diagnosticsEnabled)
            {
                double totalDurationMs = Stopwatch.GetElapsedTime(
                    totalStarted,
                    Stopwatch.GetTimestamp()).TotalMilliseconds;
                double explicitWorkDurationMs = discoveryDurationMs
                    + indexDurationMs
                    + roslynReconcileDurationMs
                    + documentReplayDurationMs
                    + publicationCommitDurationMs;
                double unattributedDurationMs = totalDurationMs - explicitWorkDurationMs;
                _diagnosticLogging.WriteEvent(
                    "workspace_reconciliation_completed",
                    new WorkspaceReconciliationCompletedDetails(
                        lease.OperationId,
                        workspaceGeneration,
                        batch.DirtyVersion,
                        batch.DirtySignalCount,
                        batch.ReconciliationHints.ForceFullSourceValidation,
                        batch.ReconciliationHints.ForcedFingerprintPathCount,
                        batch.ForceRoslynProjectReload,
                        candidatePublicationIdentity.PublicationVersion,
                        projectIndexGeneration.GenerationId,
                        finalRoslynSnapshot.State,
                        finalRoslynSnapshot.RoslynGeneration,
                        candidateSnapshot.SourceFiles.Count,
                        candidateSnapshot.ProjectFiles.Count,
                        candidateSnapshot.SolutionFiles.Count,
                        discoveryDurationMs,
                        indexDurationMs,
                        roslynReconcileDurationMs,
                        documentReplayDurationMs,
                        publicationCommitDurationMs,
                        explicitWorkDurationMs,
                        unattributedDurationMs,
                        totalDurationMs));
            }
            else
            {
                _diagnosticLogging.WriteEvent("workspace_reconciliation_completed");
            }
        }
        catch (OperationCanceledException)
            when (lease.ServiceWorkShutdownToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                ClearRuntimeOperationOwnershipLocked(workspaceGeneration, lease.OperationId);
            }
        }
        catch (ProjectIndexPublicationCanceledException)
        {
            lock (_sync)
            {
                ClearRuntimeOperationOwnershipLocked(workspaceGeneration, lease.OperationId);
            }
        }
        catch (Exception exception)
        {
            bool superseded = false;
            bool pendingNewerDirty = false;
            int pendingNewerDirtySignalCount = 0;
            ObserverResources resourcesToRetire = default;

            lock (_sync)
            {
                if (!_disposed
                    && _state != WorkspaceState.ShuttingDown
                    && _workspaceGeneration == workspaceGeneration)
                {
                    pendingNewerDirty = _pendingDirtyIntent.IsDirty;
                    pendingNewerDirtySignalCount = _pendingDirtyIntent.SignalCount;
                    superseded = pendingNewerDirty && IsSupersedableFilesystemException(exception);

                    if (superseded)
                    {
                        _faultKind = null;
                        _state = WorkspaceState.Ready;
                        ClearRuntimeOperationOwnershipLocked(workspaceGeneration, lease.OperationId);
                        ArmPendingDirtyLocked();
                    }
                    else
                    {
                        _faultKind = ClassifyFault(exception);
                        _state = WorkspaceState.Faulted;
                        _requiresConservativeRevalidation = true;
                        ClearRuntimeOperationOwnershipLocked(workspaceGeneration, lease.OperationId);
                        _pendingDirtyIntent.Clear();
                        resourcesToRetire = DetachObservationResourcesLocked(invalidateGeneration: true);
                    }
                }
                else
                {
                    ClearRuntimeOperationOwnershipLocked(workspaceGeneration, lease.OperationId);
                }
            }

            WorkspaceReconciliationTerminalDetails? terminalDetails = diagnosticsEnabled
                ? new WorkspaceReconciliationTerminalDetails(
                    lease.OperationId,
                    workspaceGeneration,
                    batch.DirtyVersion,
                    batch.DirtySignalCount,
                    batch.ReconciliationHints.ForceFullSourceValidation,
                    batch.ReconciliationHints.ForcedFingerprintPathCount,
                    batch.ForceRoslynProjectReload,
                    Stopwatch.GetElapsedTime(totalStarted, Stopwatch.GetTimestamp()).TotalMilliseconds,
                    pendingNewerDirty,
                    pendingNewerDirtySignalCount)
                : null;

            if (superseded)
            {
                if (terminalDetails is not null)
                    _diagnosticLogging.WriteEvent("workspace_reconciliation_superseded", terminalDetails);
                else
                    _diagnosticLogging.WriteEvent("workspace_reconciliation_superseded");
            }
            else
            {
                RetireObservationResources(resourcesToRetire);
                if (terminalDetails is not null)
                    _diagnosticLogging.WriteFault("workspace_reconciliation_fault", exception, terminalDetails);
                else
                    _diagnosticLogging.WriteFault("workspace_reconciliation_fault", exception);
            }
        }
        finally
        {
            lease.Retire();
        }
    }

    private void ClearRuntimeOperationOwnershipLocked(
        long workspaceGeneration,
        long operationId)
    {
        if (_workspaceGeneration != workspaceGeneration && _state != WorkspaceState.ShuttingDown)
        {
            return;
        }

        if (_activeRuntimeOperationId != operationId
            || !_runtimeReconciliationActive
            || _activeRuntimeReconciliationTask is null)
        {
            return;
        }

        _runtimeReconciliationActive = false;
        _activeRuntimeReconciliationTask = null;
        _activeRuntimeOperationId = null;
    }

    private void ArmPendingDirtyLocked()
    {
        if (_disposed
            || _state != WorkspaceState.Ready
            || _runtimeReconciliationActive
            || !_pendingDirtyIntent.IsDirty
            || _debounceTimer is null)
        {
            return;
        }

        TimeSpan elapsed = _lastDirtyTimestamp == 0
            ? FilesystemQuietPeriod
            : Stopwatch.GetElapsedTime(_lastDirtyTimestamp, Stopwatch.GetTimestamp());
        TimeSpan dueTime = elapsed >= FilesystemQuietPeriod
            ? TimeSpan.Zero
            : FilesystemQuietPeriod - elapsed;

        _debounceTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
    }

    private ObserverResources DetachObservationResourcesLocked(bool invalidateGeneration)
    {
        WorkspaceFileChangeObserver? observer = _changeObserver;
        Timer? timer = _debounceTimer;
        _changeObserver = null;
        _debounceTimer = null;
        _pendingOverflowLog = false;
        _pendingObserverFaultLog = false;

        if (invalidateGeneration)
        {
            _workspaceGeneration++;
        }

        return new ObserverResources(observer, timer);
    }

    private static void RetireObservationResources(ObserverResources resources)
    {
        resources.Observer?.Disable();
        resources.Timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        resources.Timer?.Dispose();
        resources.Observer?.Dispose();
    }

    private void ValidateRoslynPublicationCorrelation(
        WorkspaceIdentity workspaceIdentity,
        WorkspacePublicationIdentity publicationIdentity,
        RoslynLanguageServerSnapshot roslynSnapshot)
    {
        if (roslynSnapshot.State != RoslynLanguageServerState.ProjectLoaded)
        {
            return;
        }

        if (!_roslynLanguageServerHost.IsProjectLoadCurrentFor(
            workspaceIdentity,
            publicationIdentity,
            roslynSnapshot))
        {
            throw new InvalidOperationException(
                "Roslyn ProjectLoaded state could not be correlated exactly to the candidate workspace publication.");
        }
    }

    private void WriteWorkspacePublicationCommitted(
        WorkspacePublication publication,
        bool reusedRoslynGeneration,
        bool requiresRoslynGenerationReplacement)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            RoslynLanguageServerSnapshot roslyn = publication.RoslynSnapshot;
            RoslynProjectLoadPublication? load = roslyn.Publication;
            _diagnosticLogging.WriteEvent(
                "workspace_publication_committed",
                new
                {
                    workspaceGeneration = publication.Identity.WorkspaceGeneration,
                    workspacePublicationVersion = publication.Identity.PublicationVersion,
                    projectIndexGenerationId = publication.ProjectIndexGenerationId,
                    roslynState = roslyn.State.ToString(),
                    roslynGeneration = roslyn.RoslynGeneration,
                    processId = roslyn.ProcessIdentity?.ProcessId,
                    processStartTimeUtcTicks = roslyn.ProcessIdentity?.StartTimeUtcTicks,
                    loadKind = load?.LoadKind.ToString(),
                    loadTarget = load is null ? null : BoundWorkspaceDiagnosticText(load.LoadTargetRelativePath),
                    reusedRoslynGeneration,
                    requiresRoslynGenerationReplacement,
                });
        }
        else
        {
            _diagnosticLogging.WriteEvent("workspace_publication_committed");
        }
    }

    private static bool HasProjectTopologyChanged(
        WorkspaceProjectSnapshot previous,
        WorkspaceProjectSnapshot candidate)
        => !PathSetsEqual(previous.SourceFiles, candidate.SourceFiles)
            || !PathSetsEqual(previous.ProjectFiles, candidate.ProjectFiles)
            || !PathSetsEqual(previous.SolutionFiles, candidate.SolutionFiles);

    private static bool PathSetsEqual(IReadOnlyCollection<string> left, IReadOnlyCollection<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        StringComparer comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        return new HashSet<string>(left, comparer).SetEquals(right);
    }

    private static string BoundWorkspaceDiagnosticText(string value)
        => value.Length <= RoslynLanguageServerConstants.MaxDiagnosticTargetLength
            ? value
            : value[..RoslynLanguageServerConstants.MaxDiagnosticTargetLength];

    private WorkspaceStatusSnapshot CreateStatusSnapshotLocked()
    {
        int sourceFileCount = 0;
        int projectFileCount = 0;
        int solutionFileCount = 0;

        if (_state == WorkspaceState.Ready && _workspacePublication is not null)
        {
            sourceFileCount = _workspacePublication.ProjectSnapshot.SourceFiles.Count;
            projectFileCount = _workspacePublication.ProjectSnapshot.ProjectFiles.Count;
            solutionFileCount = _workspacePublication.ProjectSnapshot.SolutionFiles.Count;
        }

        return new WorkspaceStatusSnapshot(
            _state,
            _workspaceIdentity?.ProjectRoot,
            sourceFileCount,
            projectFileCount,
            solutionFileCount,
            _state == WorkspaceState.Faulted ? _faultKind : null);
    }

    private static bool IsSupersedableFilesystemException(Exception exception)
        => exception is IOException or InvalidDataException;

    private static string ClassifyFault(Exception exception)
        => exception switch
        {
            WorkspaceChangeObservationException => WorkspaceFaultKinds.ChangeObservationUnavailable,
            UnauthorizedAccessException => WorkspaceFaultKinds.UnauthorizedAccess,
            DirectoryNotFoundException => WorkspaceFaultKinds.InvalidProjectRoot,
            FileNotFoundException => WorkspaceFaultKinds.IoError,
            InvalidDataException => WorkspaceFaultKinds.InvalidProjectRoot,
            PathTooLongException => WorkspaceFaultKinds.PathError,
            ArgumentException => WorkspaceFaultKinds.PathError,
            NotSupportedException => WorkspaceFaultKinds.PathError,
            IOException => WorkspaceFaultKinds.IoError,
            _ => WorkspaceFaultKinds.DiscoveryError,
        };

    private readonly record struct TimedOperationResult<TResult>(
        TResult Result,
        double DurationMs);

    private readonly record struct ObserverResources(
        WorkspaceFileChangeObserver? Observer,
        Timer? Timer);
}

internal enum WorkspaceState
{
    Uninitialized,
    Initializing,
    Indexing,
    Ready,
    Faulted,
    ShuttingDown,
}

internal static class WorkspaceFaultKinds
{
    public const string InvalidProjectRoot = "InvalidProjectRoot";
    public const string UnauthorizedAccess = "UnauthorizedAccess";
    public const string PathError = "PathError";
    public const string IoError = "IoError";
    public const string DiscoveryError = "DiscoveryError";
    public const string ChangeObservationUnavailable = "ChangeObservationUnavailable";
}

internal enum WorkspaceInitializationOutcome
{
    Success,
    InvalidRequest,
    Busy,
    WorkspaceMismatch,
    Unavailable,
    Faulted,
}

internal readonly record struct WorkspaceInitializationResult(
    WorkspaceInitializationOutcome Outcome,
    WorkspaceStatusSnapshot Status,
    bool ReusedExistingWorkspace,
    string? ErrorMessage)
{
    public static WorkspaceInitializationResult Success(
        WorkspaceStatusSnapshot status,
        bool reusedExistingWorkspace)
        => new(
            WorkspaceInitializationOutcome.Success,
            status,
            reusedExistingWorkspace,
            null);

    public static WorkspaceInitializationResult InvalidRequest(
        WorkspaceStatusSnapshot status,
        string errorMessage)
        => new(
            WorkspaceInitializationOutcome.InvalidRequest,
            status,
            false,
            errorMessage);

    public static WorkspaceInitializationResult Busy(WorkspaceStatusSnapshot status)
        => new(WorkspaceInitializationOutcome.Busy, status, false, null);

    public static WorkspaceInitializationResult WorkspaceMismatch(WorkspaceStatusSnapshot status)
        => new(WorkspaceInitializationOutcome.WorkspaceMismatch, status, false, null);

    public static WorkspaceInitializationResult Unavailable(WorkspaceStatusSnapshot status)
        => new(WorkspaceInitializationOutcome.Unavailable, status, false, null);

    public static WorkspaceInitializationResult Faulted(WorkspaceStatusSnapshot status)
        => new(WorkspaceInitializationOutcome.Faulted, status, false, null);
}

internal readonly record struct WorkspaceStatusSnapshot(
    WorkspaceState State,
    string? ProjectRoot,
    int SourceFileCount,
    int ProjectFileCount,
    int SolutionFileCount,
    string? FaultKind);
