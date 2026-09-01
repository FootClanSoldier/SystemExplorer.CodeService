namespace SystemExplorer.CodeService;

internal sealed class WorkloadCoordinator : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _serviceWorkShutdownSource = new();
    private readonly CancellationToken _serviceWorkShutdownToken;
    private readonly Dictionary<WorkloadLane, long> _activeOperations = new();
    private readonly TaskCompletionSource _drainCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _shutdownSignalCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lazy<Task> _retirementTask;

    private WorkloadCoordinatorState _state = WorkloadCoordinatorState.AdmissionOpen;
    private long _nextOperationId;

    public WorkloadCoordinator()
    {
        _serviceWorkShutdownToken = _serviceWorkShutdownSource.Token;
        _retirementTask = new Lazy<Task>(
            RetireCoreAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public CancellationToken ServiceWorkShutdownToken => _serviceWorkShutdownToken;

    public WorkloadAdmissionResult TryAdmitExclusive(WorkloadLane lane)
    {
        ValidateLane(lane);

        lock (_sync)
        {
            if (_state != WorkloadCoordinatorState.AdmissionOpen)
            {
                return WorkloadAdmissionResult.ShuttingDown();
            }

            if (_activeOperations.Keys.Any(activeLane => LanesConflict(activeLane, lane)))
            {
                return WorkloadAdmissionResult.Busy();
            }

            long operationId = checked(++_nextOperationId);
            WorkloadExecutionLease lease = new(
                this,
                lane,
                operationId,
                _serviceWorkShutdownToken);

            _activeOperations.Add(lane, operationId);
            return WorkloadAdmissionResult.Admitted(lease);
        }
    }

    public void BeginShutdown()
    {
        bool shouldSignalDrain;

        lock (_sync)
        {
            if (_state != WorkloadCoordinatorState.AdmissionOpen)
            {
                return;
            }

            _state = WorkloadCoordinatorState.ShuttingDown;
            shouldSignalDrain = _activeOperations.Count == 0;
        }

        if (shouldSignalDrain)
        {
            _drainCompletionSource.TrySetResult();
        }

        try
        {
            _serviceWorkShutdownSource.Cancel(throwOnFirstException: false);
        }
        finally
        {
            _shutdownSignalCompletionSource.TrySetResult();
        }
    }

    public Task RetireAsync()
        => _retirementTask.Value;

    public ValueTask DisposeAsync()
        => new(RetireAsync());

    internal void RetireLease(WorkloadLane lane, long operationId)
    {
        bool shouldSignalDrain = false;

        lock (_sync)
        {
            if (!_activeOperations.TryGetValue(lane, out long activeOperationId)
                || activeOperationId != operationId)
            {
                throw new InvalidOperationException(
                    "workload lease does not own the current active operation for its lane.");
            }

            _activeOperations.Remove(lane);

            if (_state != WorkloadCoordinatorState.AdmissionOpen
                && _activeOperations.Count == 0)
            {
                shouldSignalDrain = true;
            }
        }

        if (shouldSignalDrain)
        {
            _drainCompletionSource.TrySetResult();
        }
    }

    private async Task RetireCoreAsync()
    {
        Exception? retirementFailure = null;

        try
        {
            BeginShutdown();
        }
        catch (Exception exception)
        {
            retirementFailure = exception;
        }

        await _shutdownSignalCompletionSource.Task.ConfigureAwait(false);
        await _drainCompletionSource.Task.ConfigureAwait(false);

        lock (_sync)
        {
            if (_activeOperations.Count != 0)
            {
                throw new InvalidOperationException(
                    "workload retirement reached terminal disposal while active lanes remain.");
            }

            _state = WorkloadCoordinatorState.Retired;
        }

        try
        {
            _serviceWorkShutdownSource.Dispose();
        }
        catch (Exception exception)
        {
            retirementFailure = retirementFailure is null
                ? exception
                : new AggregateException(retirementFailure, exception);
        }

        if (retirementFailure is not null)
        {
            throw new InvalidOperationException(
                "workload coordinator retirement failed after admission was closed and drain completed.",
                retirementFailure);
        }
    }

    private static void ValidateLane(WorkloadLane lane)
    {
        if (lane is not WorkloadLane.WorkspaceConstruction
            and not WorkloadLane.DocumentSynchronization
            and not WorkloadLane.SemanticReadiness
            and not WorkloadLane.Completion)
        {
            throw new ArgumentOutOfRangeException(nameof(lane), lane, "unknown workload lane.");
        }
    }

    private static bool LanesConflict(WorkloadLane activeLane, WorkloadLane requestedLane)
        => (activeLane, requestedLane) switch
        {
            (WorkloadLane.WorkspaceConstruction, WorkloadLane.WorkspaceConstruction) => true,
            (WorkloadLane.WorkspaceConstruction, WorkloadLane.DocumentSynchronization) => true,
            (WorkloadLane.DocumentSynchronization, WorkloadLane.WorkspaceConstruction) => true,
            (WorkloadLane.DocumentSynchronization, WorkloadLane.DocumentSynchronization) => true,
            (WorkloadLane.WorkspaceConstruction, WorkloadLane.SemanticReadiness) => true,
            (WorkloadLane.SemanticReadiness, WorkloadLane.WorkspaceConstruction) => true,
            (WorkloadLane.DocumentSynchronization, WorkloadLane.SemanticReadiness) => true,
            (WorkloadLane.SemanticReadiness, WorkloadLane.DocumentSynchronization) => true,
            (WorkloadLane.SemanticReadiness, WorkloadLane.SemanticReadiness) => true,
            (WorkloadLane.WorkspaceConstruction, WorkloadLane.Completion) => true,
            (WorkloadLane.Completion, WorkloadLane.WorkspaceConstruction) => true,
            (WorkloadLane.DocumentSynchronization, WorkloadLane.Completion) => true,
            (WorkloadLane.Completion, WorkloadLane.DocumentSynchronization) => true,
            (WorkloadLane.SemanticReadiness, WorkloadLane.Completion) => true,
            (WorkloadLane.Completion, WorkloadLane.SemanticReadiness) => true,
            (WorkloadLane.Completion, WorkloadLane.Completion) => true,
            _ => throw new InvalidOperationException("unknown workload-lane conflict pair."),
        };

    private enum WorkloadCoordinatorState
    {
        AdmissionOpen,
        ShuttingDown,
        Retired,
    }
}

internal enum WorkloadLane
{
    WorkspaceConstruction,
    DocumentSynchronization,
    SemanticReadiness,
    Completion,
}

internal enum WorkloadAdmissionStatus
{
    Admitted,
    Busy,
    ShuttingDown,
}

internal readonly record struct WorkloadAdmissionResult(
    WorkloadAdmissionStatus Status,
    WorkloadExecutionLease? Lease)
{
    public static WorkloadAdmissionResult Admitted(WorkloadExecutionLease lease)
        => new(WorkloadAdmissionStatus.Admitted, lease);

    public static WorkloadAdmissionResult Busy()
        => new(WorkloadAdmissionStatus.Busy, null);

    public static WorkloadAdmissionResult ShuttingDown()
        => new(WorkloadAdmissionStatus.ShuttingDown, null);
}

internal sealed class WorkloadExecutionLease : IDisposable
{
    private readonly WorkloadCoordinator _owner;
    private int _retireState;

    internal WorkloadExecutionLease(
        WorkloadCoordinator owner,
        WorkloadLane lane,
        long operationId,
        CancellationToken serviceWorkShutdownToken)
    {
        _owner = owner;
        Lane = lane;
        OperationId = operationId;
        ServiceWorkShutdownToken = serviceWorkShutdownToken;
    }

    public WorkloadLane Lane { get; }

    public long OperationId { get; }

    public CancellationToken ServiceWorkShutdownToken { get; }

    public void Retire()
    {
        if (Interlocked.Exchange(ref _retireState, 1) != 0)
        {
            return;
        }

        _owner.RetireLease(Lane, OperationId);
    }

    public void Dispose()
        => Retire();
}
