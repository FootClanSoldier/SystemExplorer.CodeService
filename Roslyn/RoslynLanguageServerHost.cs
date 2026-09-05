using System.Diagnostics;
using StreamJsonRpc;

namespace SystemExplorer.CodeService;

internal enum RoslynLanguageServerState
{
    Disabled,
    Uninitialized,
    Starting,
    ProjectLoading,
    // ProjectLoaded means workspace/projectInitializationComplete was observed.
    // It is intentionally not a semantic-readiness guarantee.
    ProjectLoaded,
    Replacing,
    Faulted,
    ShuttingDown,
    Stopped,
}

internal sealed record RoslynProjectLoadPublication(
    string WorkspaceProjectRoot,
    WorkspacePublicationIdentity WorkspacePublicationIdentity,
    long RoslynGeneration,
    string LoadTargetRelativePath,
    RoslynProjectLoadKind LoadKind,
    RoslynProcessIdentity ProcessIdentity);

internal readonly record struct RoslynLanguageServerSnapshot(
    RoslynLanguageServerState State,
    long RoslynGeneration,
    RoslynProcessIdentity? ProcessIdentity,
    RoslynProjectLoadPublication? Publication,
    string? FaultKind)
{
    public bool IsProjectLoaded => State == RoslynLanguageServerState.ProjectLoaded;
}

internal readonly record struct RoslynProjectLoadResult(
    RoslynLanguageServerSnapshot Snapshot,
    bool ReusedExistingGeneration)
{
    public RoslynLanguageServerState State => Snapshot.State;
    public string? FaultKind => Snapshot.FaultKind;
    public bool IsProjectLoaded => Snapshot.IsProjectLoaded;
}

internal readonly record struct RoslynDocumentSendTiming(
    double? SenderCaptureDurationMs,
    double? NotificationAwaitDurationMs,
    double? PostSendGenerationValidationDurationMs,
    double? TotalDurationMs);

internal readonly record struct RoslynDocumentSendResult(
    bool IsSuccess,
    RoslynDocumentSendTiming Timing)
{
    public static RoslynDocumentSendResult Success(RoslynDocumentSendTiming timing = default)
        => new(true, timing);

    public static RoslynDocumentSendResult Unavailable(RoslynDocumentSendTiming timing = default)
        => new(false, timing);
}


internal enum RoslynSemanticReadinessOutcome
{
    Success,
    SemanticUnavailable,
    RoslynUnavailable,
    Stale,
}

internal readonly record struct RoslynSemanticReadinessTiming(
    double? SenderCaptureDurationMs,
    double? DiagnosticClientTotalDurationMs,
    double? DiagnosticRpcDurationMs,
    double? DiagnosticResponseInspectionDurationMs,
    double? PostRpcGenerationValidationDurationMs,
    double? HostTotalDurationMs);

internal readonly record struct RoslynSemanticReadinessResult(
    RoslynSemanticReadinessOutcome Outcome,
    int DiagnosticCount,
    long RoslynGeneration,
    RoslynProcessIdentity? ProcessIdentity,
    RoslynSemanticReadinessTiming Timing);


internal enum RoslynCompletionOutcome
{
    Success,
    CompletionUnavailable,
    RoslynUnavailable,
    Stale,
}

internal readonly record struct RoslynCompletionTiming(
    double? SenderCaptureDurationMs,
    double? CompletionClientTotalDurationMs,
    double? CompletionRpcDurationMs,
    double? CompletionNormalizationDurationMs,
    double? PostRpcGenerationValidationDurationMs,
    double? HostTotalDurationMs);

internal readonly record struct RoslynCompletionResult(
    RoslynCompletionOutcome Outcome,
    IReadOnlyList<RoslynCompletionItem> Items,
    bool IsIncomplete,
    int RawItemCount,
    long RoslynGeneration,
    RoslynProcessIdentity? ProcessIdentity,
    RoslynCompletionTiming Timing)
{
    public static RoslynCompletionResult Failure(
        RoslynCompletionOutcome outcome,
        long roslynGeneration,
        RoslynProcessIdentity? processIdentity = null,
        RoslynCompletionTiming timing = default)
        => new(outcome, [], false, 0, roslynGeneration, processIdentity, timing);
}

internal sealed class RoslynLanguageServerHost : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly RoslynLanguageServerRuntime? _runtime;
    private readonly string _serviceVersion;
    private readonly DiagnosticLogging _diagnosticLogging;
    private readonly Lazy<Task> _retirementTask;

    private RoslynLanguageServerState _state;
    private long _roslynGeneration;
    private RoslynLanguageServerProcess? _process;
    private RoslynLspClient? _client;
    private RoslynProjectLoadPublication? _publication;
    private string? _faultKind;
    private Task? _rpcObservationTask;
    private long? _rpcObservationRoslynGeneration;
    private Task? _generationCleanupTask;
    private long? _generationCleanupRoslynGeneration;
    private long? _intentionalRetirementGeneration;

    public RoslynLanguageServerHost(
        RoslynLanguageServerRuntime? runtime,
        string serviceVersion,
        DiagnosticLogging diagnosticLogging)
    {
        _runtime = runtime;
        _serviceVersion = string.IsNullOrWhiteSpace(serviceVersion)
            ? throw new ArgumentException("service version is required.", nameof(serviceVersion))
            : serviceVersion;
        _diagnosticLogging = diagnosticLogging
            ?? throw new ArgumentNullException(nameof(diagnosticLogging));
        _state = runtime is null
            ? RoslynLanguageServerState.Disabled
            : RoslynLanguageServerState.Uninitialized;
        _retirementTask = new Lazy<Task>(RetireCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public RoslynLanguageServerSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return CreateSnapshotLocked();
        }
    }

    public bool IsProjectLoadCurrentFor(
        WorkspaceIdentity workspaceIdentity,
        WorkspacePublicationIdentity publicationIdentity,
        RoslynLanguageServerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);
        if (!snapshot.IsProjectLoaded || snapshot.Publication is not RoslynProjectLoadPublication publication)
        {
            return false;
        }

        lock (_sync)
        {
            return _state == RoslynLanguageServerState.ProjectLoaded
                && _roslynGeneration == snapshot.RoslynGeneration
                && _process is not null
                && !_process.HasExited
                && snapshot.ProcessIdentity is RoslynProcessIdentity snapshotProcess
                && SameProcessIdentity(_process.Identity, snapshotProcess)
                && SameProcessIdentity(publication.ProcessIdentity, snapshotProcess)
                && WorkspaceRootsEqual(publication.WorkspaceProjectRoot, workspaceIdentity.ProjectRoot)
                && publication.WorkspacePublicationIdentity == publicationIdentity
                && _publication is not null
                && _publication.WorkspacePublicationIdentity == publicationIdentity
                && _publication.RoslynGeneration == snapshot.RoslynGeneration
                && SameProcessIdentity(_publication.ProcessIdentity, snapshotProcess);
        }
    }

    public Task<RoslynDocumentSendResult> OpenDocumentAsync(
        WorkspaceIdentity expectedWorkspaceIdentity,
        WorkspacePublicationIdentity expectedPublicationIdentity,
        long expectedRoslynGeneration,
        DocumentIdentity documentIdentity,
        int lspVersion,
        string text,
        CancellationToken cancellationToken)
        => SendDocumentNotificationAsync(
            expectedWorkspaceIdentity,
            expectedPublicationIdentity,
            expectedRoslynGeneration,
            documentIdentity,
            "didOpen",
            (client, absolutePath, token) => client.DidOpenAsync(absolutePath, lspVersion, text, token),
            cancellationToken);

    public Task<RoslynDocumentSendResult> ChangeDocumentAsync(
        WorkspaceIdentity expectedWorkspaceIdentity,
        WorkspacePublicationIdentity expectedPublicationIdentity,
        long expectedRoslynGeneration,
        DocumentIdentity documentIdentity,
        int lspVersion,
        string text,
        CancellationToken cancellationToken)
        => SendDocumentNotificationAsync(
            expectedWorkspaceIdentity,
            expectedPublicationIdentity,
            expectedRoslynGeneration,
            documentIdentity,
            "didChange",
            (client, absolutePath, token) => client.DidChangeFullAsync(absolutePath, lspVersion, text, token),
            cancellationToken);

    public Task<RoslynDocumentSendResult> CloseDocumentAsync(
        WorkspaceIdentity expectedWorkspaceIdentity,
        WorkspacePublicationIdentity expectedPublicationIdentity,
        long expectedRoslynGeneration,
        DocumentIdentity documentIdentity,
        CancellationToken cancellationToken)
        => SendDocumentNotificationAsync(
            expectedWorkspaceIdentity,
            expectedPublicationIdentity,
            expectedRoslynGeneration,
            documentIdentity,
            "didClose",
            static (client, absolutePath, token) => client.DidCloseAsync(absolutePath, token),
            cancellationToken);

    private async Task<RoslynDocumentSendResult> SendDocumentNotificationAsync(
        WorkspaceIdentity expectedWorkspaceIdentity,
        WorkspacePublicationIdentity expectedPublicationIdentity,
        long expectedRoslynGeneration,
        DocumentIdentity documentIdentity,
        string operation,
        Func<RoslynLspClient, string, CancellationToken, Task> sendAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedWorkspaceIdentity);
        ArgumentNullException.ThrowIfNull(documentIdentity);
        ArgumentNullException.ThrowIfNull(sendAsync);
        cancellationToken.ThrowIfCancellationRequested();

        bool diagnosticsEnabled = _diagnosticLogging.IsEnabled;
        long totalStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        long senderCaptureStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        string absolutePath = documentIdentity.GetAbsolutePath(expectedWorkspaceIdentity);
        RoslynLspClient client;
        RoslynLanguageServerProcess process;
        RoslynProcessIdentity processIdentity;

        lock (_sync)
        {
            if (!TryCaptureCurrentDocumentSenderLocked(
                    expectedWorkspaceIdentity,
                    expectedPublicationIdentity,
                    expectedRoslynGeneration,
                    out client,
                    out process,
                    out processIdentity))
            {
                double? senderCaptureDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, senderCaptureStarted);
                return RoslynDocumentSendResult.Unavailable(
                    CreateDocumentSendTiming(
                        diagnosticsEnabled,
                        totalStarted,
                        senderCaptureDurationMs,
                        null,
                        null));
            }
        }

        double? senderDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, senderCaptureStarted);
        long notificationStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            await sendAsync(client, absolutePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            double? notificationDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, notificationStarted);
            FailCurrentGenerationForDocumentSend(
                expectedPublicationIdentity,
                expectedRoslynGeneration,
                processIdentity,
                process,
                client,
                operation,
                documentIdentity.RelativePath,
                exception);
            return RoslynDocumentSendResult.Unavailable(
                CreateDocumentSendTiming(
                    diagnosticsEnabled,
                    totalStarted,
                    senderDurationMs,
                    notificationDurationMs,
                    null));
        }

        double? notificationAwaitDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, notificationStarted);
        long postSendValidationStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        lock (_sync)
        {
            if (!TryCaptureCurrentDocumentSenderLocked(
                    expectedWorkspaceIdentity,
                    expectedPublicationIdentity,
                    expectedRoslynGeneration,
                    out RoslynLspClient currentClient,
                    out RoslynLanguageServerProcess currentProcess,
                    out RoslynProcessIdentity currentProcessIdentity)
                || !ReferenceEquals(currentClient, client)
                || !ReferenceEquals(currentProcess, process)
                || !SameProcessIdentity(currentProcessIdentity, processIdentity))
            {
                double? postSendValidationDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, postSendValidationStarted);
                return RoslynDocumentSendResult.Unavailable(
                    CreateDocumentSendTiming(
                        diagnosticsEnabled,
                        totalStarted,
                        senderDurationMs,
                        notificationAwaitDurationMs,
                        postSendValidationDurationMs));
            }
        }

        double? postSendGenerationValidationDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, postSendValidationStarted);
        return RoslynDocumentSendResult.Success(
            CreateDocumentSendTiming(
                diagnosticsEnabled,
                totalStarted,
                senderDurationMs,
                notificationAwaitDurationMs,
                postSendGenerationValidationDurationMs));
    }

    public async Task<RoslynSemanticReadinessResult> EstablishSemanticReadinessAsync(
        WorkspaceIdentity expectedWorkspaceIdentity,
        WorkspacePublicationIdentity expectedPublicationIdentity,
        long expectedRoslynGeneration,
        DocumentIdentity documentIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedWorkspaceIdentity);
        ArgumentNullException.ThrowIfNull(documentIdentity);
        cancellationToken.ThrowIfCancellationRequested();

        bool diagnosticsEnabled = _diagnosticLogging.IsEnabled;
        long hostStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        long senderCaptureStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        string absolutePath = documentIdentity.GetAbsolutePath(expectedWorkspaceIdentity);
        RoslynLspClient client;
        RoslynLanguageServerProcess process;
        RoslynProcessIdentity processIdentity;

        lock (_sync)
        {
            if (!TryCaptureCurrentDocumentSenderLocked(expectedWorkspaceIdentity, expectedPublicationIdentity, expectedRoslynGeneration, out client, out process, out processIdentity))
            {
                return CreateSemanticReadinessResult(
                    RoslynSemanticReadinessOutcome.RoslynUnavailable,
                    0,
                    expectedRoslynGeneration,
                    null,
                    diagnosticsEnabled,
                    hostStarted,
                    GetElapsedMilliseconds(diagnosticsEnabled, senderCaptureStarted),
                    default,
                    null);
            }
        }

        if (!client.IsDiagnosticCapabilityAvailable)
        {
            return CreateSemanticReadinessResult(
                RoslynSemanticReadinessOutcome.SemanticUnavailable,
                0,
                expectedRoslynGeneration,
                processIdentity,
                diagnosticsEnabled,
                hostStarted,
                GetElapsedMilliseconds(diagnosticsEnabled, senderCaptureStarted),
                default,
                null);
        }

        double? senderCaptureDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, senderCaptureStarted);
        RoslynDiagnosticPullResult pullResult;
        try
        {
            pullResult = await client.PullDocumentDiagnosticsAsync(
                absolutePath,
                cancellationToken,
                diagnosticsEnabled).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            FailCurrentGenerationForDocumentSend(expectedPublicationIdentity, expectedRoslynGeneration, processIdentity, process, client, "semantic_readiness", documentIdentity.RelativePath, exception, RoslynProjectLoadFaultKinds.SemanticReadinessFailed);
            return CreateSemanticReadinessResult(
                RoslynSemanticReadinessOutcome.RoslynUnavailable,
                0,
                expectedRoslynGeneration,
                processIdentity,
                diagnosticsEnabled,
                hostStarted,
                senderCaptureDurationMs,
                default,
                null);
        }

        if (pullResult.Outcome == RoslynDiagnosticPullOutcome.Timeout)
        {
            return CreateSemanticReadinessResult(
                RoslynSemanticReadinessOutcome.SemanticUnavailable,
                0,
                expectedRoslynGeneration,
                processIdentity,
                diagnosticsEnabled,
                hostStarted,
                senderCaptureDurationMs,
                pullResult.Timing,
                null);
        }

        if (pullResult.Outcome == RoslynDiagnosticPullOutcome.Unavailable)
        {
            return CreateSemanticReadinessResult(
                RoslynSemanticReadinessOutcome.SemanticUnavailable,
                0,
                expectedRoslynGeneration,
                processIdentity,
                diagnosticsEnabled,
                hostStarted,
                senderCaptureDurationMs,
                pullResult.Timing,
                null);
        }

        long postRpcValidationStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        lock (_sync)
        {
            if (!TryCaptureCurrentDocumentSenderLocked(expectedWorkspaceIdentity, expectedPublicationIdentity, expectedRoslynGeneration, out RoslynLspClient currentClient, out RoslynLanguageServerProcess currentProcess, out RoslynProcessIdentity currentProcessIdentity)
                || !ReferenceEquals(client, currentClient)
                || !ReferenceEquals(process, currentProcess)
                || !SameProcessIdentity(processIdentity, currentProcessIdentity))
            {
                return CreateSemanticReadinessResult(
                    RoslynSemanticReadinessOutcome.Stale,
                    pullResult.DiagnosticCount,
                    expectedRoslynGeneration,
                    processIdentity,
                    diagnosticsEnabled,
                    hostStarted,
                    senderCaptureDurationMs,
                    pullResult.Timing,
                    GetElapsedMilliseconds(diagnosticsEnabled, postRpcValidationStarted));
            }
        }

        return CreateSemanticReadinessResult(
            RoslynSemanticReadinessOutcome.Success,
            pullResult.DiagnosticCount,
            expectedRoslynGeneration,
            processIdentity,
            diagnosticsEnabled,
            hostStarted,
            senderCaptureDurationMs,
            pullResult.Timing,
            GetElapsedMilliseconds(diagnosticsEnabled, postRpcValidationStarted));
    }

    public async Task<RoslynCompletionResult> CompleteAsync(
        WorkspaceIdentity expectedWorkspaceIdentity,
        WorkspacePublicationIdentity expectedPublicationIdentity,
        long expectedRoslynGeneration,
        DocumentIdentity documentIdentity,
        int line,
        int character,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedWorkspaceIdentity);
        ArgumentNullException.ThrowIfNull(documentIdentity);
        if (line < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(line), line, "completion line must be non-negative.");
        }
        if (character < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(character), character, "completion character must be non-negative.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        bool diagnosticsEnabled = _diagnosticLogging.IsEnabled;
        long hostStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        long senderCaptureStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        string absolutePath = documentIdentity.GetAbsolutePath(expectedWorkspaceIdentity);
        RoslynLspClient client;
        RoslynLanguageServerProcess process;
        RoslynProcessIdentity processIdentity;

        lock (_sync)
        {
            if (!TryCaptureCurrentDocumentSenderLocked(
                    expectedWorkspaceIdentity,
                    expectedPublicationIdentity,
                    expectedRoslynGeneration,
                    out client,
                    out process,
                    out processIdentity))
            {
                return RoslynCompletionResult.Failure(
                    RoslynCompletionOutcome.RoslynUnavailable,
                    expectedRoslynGeneration,
                    timing: CreateCompletionTiming(
                        diagnosticsEnabled,
                        hostStarted,
                        GetElapsedMilliseconds(diagnosticsEnabled, senderCaptureStarted),
                        default,
                        null));
            }
        }

        if (!client.IsCompletionCapabilityAvailable)
        {
            return RoslynCompletionResult.Failure(
                RoslynCompletionOutcome.CompletionUnavailable,
                expectedRoslynGeneration,
                processIdentity,
                CreateCompletionTiming(
                    diagnosticsEnabled,
                    hostStarted,
                    GetElapsedMilliseconds(diagnosticsEnabled, senderCaptureStarted),
                    default,
                    null));
        }

        double? senderCaptureDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, senderCaptureStarted);
        RoslynCompletionClientResult clientResult;
        try
        {
            clientResult = await client.CompletionAsync(
                absolutePath,
                line,
                character,
                cancellationToken,
                diagnosticsEnabled).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (IsGenerationBreakingCompletionFailure(exception, process))
            {
                FailCurrentGenerationForDocumentSend(
                    expectedPublicationIdentity,
                    expectedRoslynGeneration,
                    processIdentity,
                    process,
                    client,
                    "completion",
                    documentIdentity.RelativePath,
                    exception,
                    RoslynProjectLoadFaultKinds.CompletionFailed);
                return RoslynCompletionResult.Failure(
                    RoslynCompletionOutcome.RoslynUnavailable,
                    expectedRoslynGeneration,
                    processIdentity,
                    CreateCompletionTiming(
                        diagnosticsEnabled,
                        hostStarted,
                        senderCaptureDurationMs,
                        default,
                        null));
            }

            WriteCompletionRequestLocalFailure(
                exception,
                expectedPublicationIdentity,
                expectedRoslynGeneration,
                processIdentity,
                documentIdentity.RelativePath);
            return RoslynCompletionResult.Failure(
                RoslynCompletionOutcome.CompletionUnavailable,
                expectedRoslynGeneration,
                processIdentity,
                CreateCompletionTiming(
                    diagnosticsEnabled,
                    hostStarted,
                    senderCaptureDurationMs,
                    default,
                    null));
        }

        if (clientResult.Outcome != RoslynCompletionClientOutcome.Success)
        {
            return RoslynCompletionResult.Failure(
                RoslynCompletionOutcome.CompletionUnavailable,
                expectedRoslynGeneration,
                processIdentity,
                CreateCompletionTiming(
                    diagnosticsEnabled,
                    hostStarted,
                    senderCaptureDurationMs,
                    clientResult.Timing,
                    null));
        }

        long postRpcValidationStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        lock (_sync)
        {
            if (!TryCaptureCurrentDocumentSenderLocked(
                    expectedWorkspaceIdentity,
                    expectedPublicationIdentity,
                    expectedRoslynGeneration,
                    out RoslynLspClient currentClient,
                    out RoslynLanguageServerProcess currentProcess,
                    out RoslynProcessIdentity currentProcessIdentity)
                || !ReferenceEquals(client, currentClient)
                || !ReferenceEquals(process, currentProcess)
                || !SameProcessIdentity(processIdentity, currentProcessIdentity))
            {
                return RoslynCompletionResult.Failure(
                    RoslynCompletionOutcome.Stale,
                    expectedRoslynGeneration,
                    processIdentity,
                    CreateCompletionTiming(
                        diagnosticsEnabled,
                        hostStarted,
                        senderCaptureDurationMs,
                        clientResult.Timing,
                        GetElapsedMilliseconds(diagnosticsEnabled, postRpcValidationStarted)));
            }
        }

        RoslynCompletionTiming timing = CreateCompletionTiming(
            diagnosticsEnabled,
            hostStarted,
            senderCaptureDurationMs,
            clientResult.Timing,
            GetElapsedMilliseconds(diagnosticsEnabled, postRpcValidationStarted));

        return new RoslynCompletionResult(
            RoslynCompletionOutcome.Success,
            clientResult.Items,
            clientResult.IsIncomplete,
            clientResult.RawItemCount,
            expectedRoslynGeneration,
            processIdentity,
            timing);
    }

    private static RoslynDocumentSendTiming CreateDocumentSendTiming(
        bool diagnosticsEnabled,
        long totalStarted,
        double? senderCaptureDurationMs,
        double? notificationAwaitDurationMs,
        double? postSendGenerationValidationDurationMs)
        => new(
            senderCaptureDurationMs,
            notificationAwaitDurationMs,
            postSendGenerationValidationDurationMs,
            GetElapsedMilliseconds(diagnosticsEnabled, totalStarted));

    private static RoslynSemanticReadinessResult CreateSemanticReadinessResult(
        RoslynSemanticReadinessOutcome outcome,
        int diagnosticCount,
        long roslynGeneration,
        RoslynProcessIdentity? processIdentity,
        bool diagnosticsEnabled,
        long hostStarted,
        double? senderCaptureDurationMs,
        RoslynDiagnosticPullTiming clientTiming,
        double? postRpcGenerationValidationDurationMs)
        => new(
            outcome,
            diagnosticCount,
            roslynGeneration,
            processIdentity,
            new RoslynSemanticReadinessTiming(
                senderCaptureDurationMs,
                clientTiming.TotalDurationMs,
                clientTiming.RpcDurationMs,
                clientTiming.ResponseInspectionDurationMs,
                postRpcGenerationValidationDurationMs,
                GetElapsedMilliseconds(diagnosticsEnabled, hostStarted)));

    private static RoslynCompletionTiming CreateCompletionTiming(
        bool diagnosticsEnabled,
        long hostStarted,
        double? senderCaptureDurationMs,
        RoslynCompletionClientTiming clientTiming,
        double? postRpcGenerationValidationDurationMs)
        => new(
            senderCaptureDurationMs,
            clientTiming.TotalDurationMs,
            clientTiming.RpcDurationMs,
            clientTiming.NormalizationDurationMs,
            postRpcGenerationValidationDurationMs,
            GetElapsedMilliseconds(diagnosticsEnabled, hostStarted));

    private static double? GetElapsedMilliseconds(bool diagnosticsEnabled, long started)
        => diagnosticsEnabled
            ? Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds
            : null;

    private static bool IsGenerationBreakingCompletionFailure(
        Exception exception,
        RoslynLanguageServerProcess process)
        => process.HasExited
            || exception is ConnectionLostException
            || exception is EndOfStreamException
            || exception is IOException
            || exception is ObjectDisposedException;

    private void WriteCompletionRequestLocalFailure(
        Exception exception,
        WorkspacePublicationIdentity publicationIdentity,
        long roslynGeneration,
        RoslynProcessIdentity processIdentity,
        string documentPath)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent("roslyn_completion_request_local_failure", new
            {
                workspaceGeneration = publicationIdentity.WorkspaceGeneration,
                workspacePublicationVersion = publicationIdentity.PublicationVersion,
                roslynGeneration,
                processId = processIdentity.ProcessId,
                processStartTimeUtcTicks = processIdentity.StartTimeUtcTicks,
                documentPath = BoundDiagnosticText(documentPath),
                errorType = BoundDiagnosticText(exception.GetType().FullName ?? exception.GetType().Name),
            });
        }
        else
        {
            _diagnosticLogging.WriteEvent("roslyn_completion_request_local_failure");
        }
    }

    private bool TryCaptureCurrentDocumentSenderLocked(
        WorkspaceIdentity expectedWorkspaceIdentity,
        WorkspacePublicationIdentity expectedPublicationIdentity,
        long expectedRoslynGeneration,
        out RoslynLspClient client,
        out RoslynLanguageServerProcess process,
        out RoslynProcessIdentity processIdentity)
    {
        if (_state == RoslynLanguageServerState.ProjectLoaded
            && _roslynGeneration == expectedRoslynGeneration
            && _publication is RoslynProjectLoadPublication publication
            && publication.WorkspacePublicationIdentity == expectedPublicationIdentity
            && publication.RoslynGeneration == expectedRoslynGeneration
            && WorkspaceRootsEqual(publication.WorkspaceProjectRoot, expectedWorkspaceIdentity.ProjectRoot)
            && _process is RoslynLanguageServerProcess activeProcess
            && !activeProcess.HasExited
            && _client is RoslynLspClient activeClient
            && SameProcessIdentity(activeProcess.Identity, publication.ProcessIdentity))
        {
            client = activeClient;
            process = activeProcess;
            processIdentity = activeProcess.Identity;
            return true;
        }

        client = null!;
        process = null!;
        processIdentity = default;
        return false;
    }

    private void FailCurrentGenerationForDocumentSend(
        WorkspacePublicationIdentity expectedPublicationIdentity,
        long expectedRoslynGeneration,
        RoslynProcessIdentity expectedProcessIdentity,
        RoslynLanguageServerProcess process,
        RoslynLspClient client,
        string operation,
        string documentPath,
        Exception exception,
        string faultKind = RoslynProjectLoadFaultKinds.DocumentSynchronizationFailed)
    {
        bool faulted = false;

        lock (_sync)
        {
            if (_state == RoslynLanguageServerState.ProjectLoaded
                && _roslynGeneration == expectedRoslynGeneration
                && _publication is RoslynProjectLoadPublication publication
                && publication.WorkspacePublicationIdentity == expectedPublicationIdentity
                && SameProcessIdentity(publication.ProcessIdentity, expectedProcessIdentity)
                && ReferenceEquals(_process, process)
                && ReferenceEquals(_client, client))
            {
                _publication = null;
                _state = RoslynLanguageServerState.Faulted;
                _faultKind = faultKind;
                faulted = true;
            }
        }

        if (!faulted)
        {
            return;
        }

        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteFault("roslyn_document_send_fault", exception, new
            {
                workspaceGeneration = expectedPublicationIdentity.WorkspaceGeneration,
                workspacePublicationVersion = expectedPublicationIdentity.PublicationVersion,
                roslynGeneration = expectedRoslynGeneration,
                processId = expectedProcessIdentity.ProcessId,
                processStartTimeUtcTicks = expectedProcessIdentity.StartTimeUtcTicks,
                operation,
                documentPath = BoundDiagnosticText(documentPath),
                faultKind,
            });
        }
        else
        {
            _diagnosticLogging.WriteFault("roslyn_document_send_fault", exception);
        }

        StartFaultCleanup(expectedRoslynGeneration, process, client);
    }

    public async Task<RoslynProjectLoadResult> ReconcileProjectLoadAsync(
        WorkspaceProjectSnapshot snapshot,
        WorkspacePublicationIdentity publicationIdentity,
        bool requiresGenerationReplacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        if (_runtime is null)
        {
            return new RoslynProjectLoadResult(GetSnapshot(), ReusedExistingGeneration: false);
        }

        RoslynProjectLoadTargetSelectionResult selection = RoslynProjectLoadTarget.Select(snapshot);
        if (!selection.IsSuccess)
        {
            return await HandleSelectionFailureAsync(
                publicationIdentity,
                selection.FaultKind!,
                selection.ErrorMessage!).ConfigureAwait(false);
        }

        RoslynProjectLoadTarget target = selection.Target!;
        RoslynLanguageServerProcess? processToReplace = null;
        RoslynLspClient? clientToReplace = null;
        Task? rpcObservationToReplace = null;
        RoslynProcessIdentity? replacementIdentity = null;
        long roslynGeneration;
        bool replacement = false;

        lock (_sync)
        {
            switch (_state)
            {
                case RoslynLanguageServerState.ProjectLoaded:
                    bool sameTarget = IsReusableProjectLoadedLocked(snapshot, target);
                    if (sameTarget && !requiresGenerationReplacement)
                    {
                        RoslynProjectLoadPublication current = _publication!;
                        _publication = current with { WorkspacePublicationIdentity = publicationIdentity };
                        RoslynLanguageServerSnapshot reused = CreateSnapshotLocked();
                        WriteProjectLoadReused(publicationIdentity, reused, target);
                        return new RoslynProjectLoadResult(reused, ReusedExistingGeneration: true);
                    }

                    replacement = true;
                    _state = RoslynLanguageServerState.Replacing;
                    _publication = null;
                    _faultKind = null;
                    processToReplace = _process;
                    clientToReplace = _client;
                    rpcObservationToReplace = _rpcObservationTask;
                    replacementIdentity = processToReplace?.Identity;
                    _intentionalRetirementGeneration = _roslynGeneration;
                    roslynGeneration = _roslynGeneration;
                    break;

                case RoslynLanguageServerState.Faulted:
                    return new RoslynProjectLoadResult(CreateSnapshotLocked(), ReusedExistingGeneration: false);

                case RoslynLanguageServerState.ShuttingDown:
                case RoslynLanguageServerState.Stopped:
                case RoslynLanguageServerState.Disabled:
                    return new RoslynProjectLoadResult(CreateSnapshotLocked(), ReusedExistingGeneration: false);

                case RoslynLanguageServerState.Starting:
                case RoslynLanguageServerState.ProjectLoading:
                case RoslynLanguageServerState.Replacing:
                    throw new InvalidOperationException(
                        "Roslyn project reconciliation was entered concurrently despite exclusive WorkspaceConstruction ownership.");

                case RoslynLanguageServerState.Uninitialized:
                    roslynGeneration = checked(++_roslynGeneration);
                    _state = RoslynLanguageServerState.Starting;
                    _faultKind = null;
                    break;

                default:
                    throw new InvalidOperationException("unknown Roslyn Language Server state.");
            }
        }

        if (replacement)
        {
            WriteReplacementStarted(publicationIdentity, roslynGeneration, replacementIdentity, target);
            try
            {
                await RetireGenerationAsync(
                    roslynGeneration,
                    processToReplace,
                    clientToReplace,
                    rpcObservationToReplace,
                    attemptGracefulShutdown: true).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    if (_roslynGeneration == roslynGeneration)
                    {
                        _process = null;
                        _client = null;
                        _rpcObservationTask = null;
                        _rpcObservationRoslynGeneration = null;
                        _intentionalRetirementGeneration = null;
                        _state = RoslynLanguageServerState.Faulted;
                        _faultKind = RoslynProjectLoadFaultKinds.InitializationFailed;
                    }
                }

                WriteFault(exception, publicationIdentity, roslynGeneration, replacementIdentity, target,
                    RoslynProjectLoadFaultKinds.InitializationFailed);
                return new RoslynProjectLoadResult(GetSnapshot(), ReusedExistingGeneration: false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                if (_state is RoslynLanguageServerState.ShuttingDown or RoslynLanguageServerState.Stopped)
                {
                    throw new OperationCanceledException("Roslyn replacement lost service ownership.", cancellationToken);
                }

                if (_state != RoslynLanguageServerState.Replacing || _roslynGeneration != roslynGeneration)
                {
                    throw new InvalidOperationException("Roslyn replacement lost generation ownership after retirement.");
                }

                _process = null;
                _client = null;
                _rpcObservationTask = null;
                _rpcObservationRoslynGeneration = null;
                _intentionalRetirementGeneration = null;
                roslynGeneration = checked(++_roslynGeneration);
                _state = RoslynLanguageServerState.Starting;
            }
        }

        RoslynProjectLoadResult result = await StartAndLoadGenerationAsync(
            snapshot,
            publicationIdentity,
            target,
            roslynGeneration,
            cancellationToken).ConfigureAwait(false);

        if (replacement && result.IsProjectLoaded)
        {
            WriteReplacementCompleted(publicationIdentity, result.Snapshot, target);
        }

        return result;
    }

    public Task RetireAsync() => _retirementTask.Value;

    public ValueTask DisposeAsync() => new(RetireAsync());

    private async Task<RoslynProjectLoadResult> StartAndLoadGenerationAsync(
        WorkspaceProjectSnapshot snapshot,
        WorkspacePublicationIdentity publicationIdentity,
        RoslynProjectLoadTarget target,
        long roslynGeneration,
        CancellationToken cancellationToken)
    {
        bool diagnosticsEnabled = _diagnosticLogging.IsEnabled;
        long started = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
        double? processStartDurationMs = null;
        double? clientSetupDurationMs = null;
        double? lspInitializeDurationMs = null;
        double? projectOpenNotificationDurationMs = null;
        double? projectInitializationWaitDurationMs = null;
        double? publicationCommitDurationMs = null;
        WriteLifecycle("roslyn_initialization_started", publicationIdentity, roslynGeneration, null, target, null);

        RoslynLanguageServerProcess? process = null;
        RoslynLspClient? client = null;
        try
        {
            long processStartStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            process = await RoslynLanguageServerProcess.StartAsync(
                _runtime!, snapshot.WorkspaceIdentity, roslynGeneration, OnProcessExited, cancellationToken)
                .ConfigureAwait(false);
            processStartDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, processStartStarted);

            lock (_sync)
            {
                EnsureActiveStartingGenerationLocked(roslynGeneration);
                _process = process;
                _state = RoslynLanguageServerState.ProjectLoading;
            }

            WriteLifecycle("roslyn_process_started", publicationIdentity, roslynGeneration, process.Identity, target, null);
            if (process.HasExited)
            {
                throw new InvalidOperationException("Roslyn Language Server exited before LSP initialization could begin.");
            }

            long clientSetupStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            client = new RoslynLspClient(process, _serviceVersion);
            lock (_sync)
            {
                if (_roslynGeneration != roslynGeneration || _state != RoslynLanguageServerState.ProjectLoading)
                {
                    throw new InvalidOperationException("Roslyn generation changed while creating the LSP client.");
                }
                _client = client;
            }
            clientSetupDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, clientSetupStarted);

            long initializeStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            await client.InitializeAsync(snapshot.WorkspaceIdentity, cancellationToken).ConfigureAwait(false);
            lspInitializeDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, initializeStarted);
            WriteLifecycle("roslyn_project_loading", publicationIdentity, roslynGeneration, process.Identity, target, null);

            long projectOpenStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            await client.OpenProjectAsync(target, cancellationToken).ConfigureAwait(false);
            projectOpenNotificationDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, projectOpenStarted);

            long initializationWaitStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            await client.WaitForProjectInitializationAsync(cancellationToken).ConfigureAwait(false);
            projectInitializationWaitDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, initializationWaitStarted);
            cancellationToken.ThrowIfCancellationRequested();

            long publicationCommitStarted = diagnosticsEnabled ? Stopwatch.GetTimestamp() : 0;
            RoslynProjectLoadPublication publication = new(
                snapshot.WorkspaceIdentity.ProjectRoot,
                publicationIdentity,
                roslynGeneration,
                target.RelativePath,
                target.LoadKind,
                process.Identity);

            RoslynLanguageServerSnapshot hostSnapshot;
            lock (_sync)
            {
                if (_roslynGeneration != roslynGeneration
                    || _state != RoslynLanguageServerState.ProjectLoading
                    || process.HasExited)
                {
                    throw new InvalidOperationException(
                        "Roslyn project initialization completed after the active generation lost load ownership.");
                }

                _publication = publication;
                _faultKind = null;
                _state = RoslynLanguageServerState.ProjectLoaded;
                _rpcObservationRoslynGeneration = roslynGeneration;
                _rpcObservationTask = ObserveRpcCompletionAsync(roslynGeneration, client, process.Identity);
                hostSnapshot = CreateSnapshotLocked();
            }
            publicationCommitDurationMs = GetElapsedMilliseconds(diagnosticsEnabled, publicationCommitStarted);

            double? duration = GetElapsedMilliseconds(diagnosticsEnabled, started);
            WriteProjectLoaded(
                publicationIdentity,
                roslynGeneration,
                process.Identity,
                target,
                duration,
                processStartDurationMs,
                clientSetupDurationMs,
                lspInitializeDurationMs,
                projectOpenNotificationDurationMs,
                projectInitializationWaitDurationMs,
                publicationCommitDurationMs);
            return new RoslynProjectLoadResult(hostSnapshot, ReusedExistingGeneration: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkGenerationFault(roslynGeneration, RoslynProjectLoadFaultKinds.InitializationFailed);
            await CleanupOwnedFailedGenerationAsync(roslynGeneration, process, client).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            bool wroteFault = MarkGenerationFault(roslynGeneration, RoslynProjectLoadFaultKinds.InitializationFailed);
            if (wroteFault)
            {
                WriteFault(exception, publicationIdentity, roslynGeneration, process?.Identity, target,
                    RoslynProjectLoadFaultKinds.InitializationFailed);
            }

            try
            {
                await CleanupOwnedFailedGenerationAsync(roslynGeneration, process, client).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                WriteFault(cleanupException, publicationIdentity, roslynGeneration, process?.Identity, target,
                    RoslynProjectLoadFaultKinds.InitializationFailed);
            }

            return new RoslynProjectLoadResult(GetSnapshot(), ReusedExistingGeneration: false);
        }
    }

    private async Task<RoslynProjectLoadResult> HandleSelectionFailureAsync(
        WorkspacePublicationIdentity publicationIdentity,
        string faultKind,
        string errorMessage)
    {
        RoslynLanguageServerProcess? process = null;
        RoslynLspClient? client = null;
        Task? rpcObservationTask = null;
        long generation;
        RoslynProcessIdentity? processIdentity = null;
        bool shouldCleanup = false;
        bool writeFault = false;

        lock (_sync)
        {
            generation = _roslynGeneration;
            if (_state is RoslynLanguageServerState.ShuttingDown or RoslynLanguageServerState.Stopped)
            {
                return new RoslynProjectLoadResult(CreateSnapshotLocked(), false);
            }

            if (_state == RoslynLanguageServerState.Faulted)
            {
                return new RoslynProjectLoadResult(CreateSnapshotLocked(), false);
            }

            shouldCleanup = _process is not null || _client is not null;
            process = _process;
            client = _client;
            rpcObservationTask = _rpcObservationTask;
            processIdentity = process?.Identity;
            if (shouldCleanup)
            {
                _intentionalRetirementGeneration = generation;
            }
            _publication = null;
            _faultKind = faultKind;
            _state = RoslynLanguageServerState.Faulted;
            writeFault = true;
        }

        if (writeFault)
        {
            WriteFault(new InvalidOperationException(errorMessage), publicationIdentity, generation,
                processIdentity, null, faultKind);
        }

        if (shouldCleanup)
        {
            try
            {
                await RetireGenerationAsync(generation, process, client, rpcObservationTask,
                    attemptGracefulShutdown: true).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    if (_roslynGeneration == generation)
                    {
                        _process = null;
                        _client = null;
                        _rpcObservationTask = null;
                        _rpcObservationRoslynGeneration = null;
                        _intentionalRetirementGeneration = null;
                    }
                }
            }
        }

        return new RoslynProjectLoadResult(GetSnapshot(), false);
    }

    private async Task RetireCoreAsync()
    {
        RoslynLanguageServerProcess? process;
        RoslynLspClient? client;
        Task? rpcObservationTask;
        Task? generationCleanupTask;
        RoslynProcessIdentity? processIdentity;
        long generation;

        lock (_sync)
        {
            if (_state == RoslynLanguageServerState.Stopped) return;
            if (_state == RoslynLanguageServerState.Disabled)
            {
                _state = RoslynLanguageServerState.Stopped;
                return;
            }

            _state = RoslynLanguageServerState.ShuttingDown;
            _publication = null;
            process = _process;
            client = _client;
            rpcObservationTask = _rpcObservationTask;
            generationCleanupTask = _generationCleanupTask;
            processIdentity = process?.Identity;
            generation = _roslynGeneration;
            _intentionalRetirementGeneration = generation;
        }

        WriteStopping(generation, processIdentity);
        Exception? failure = null;
        try
        {
            if (generationCleanupTask is not null)
            {
                await generationCleanupTask.ConfigureAwait(false);
                if (rpcObservationTask is not null)
                {
                    await rpcObservationTask
                        .WaitAsync(RoslynLanguageServerConstants.ForcedExitTimeout)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                await RetireGenerationAsync(generation, process, client, rpcObservationTask, true).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            _diagnosticLogging.WriteFault("roslyn_fault", exception);
        }

        lock (_sync)
        {
            _process = null;
            _client = null;
            _publication = null;
            _faultKind = null;
            _rpcObservationTask = null;
            _rpcObservationRoslynGeneration = null;
            _generationCleanupTask = null;
            _generationCleanupRoslynGeneration = null;
            _intentionalRetirementGeneration = null;
            _state = RoslynLanguageServerState.Stopped;
        }

        WriteStopped(generation, processIdentity);
        if (failure is not null)
        {
            throw new InvalidOperationException("Roslyn Language Server retirement completed with cleanup failure.", failure);
        }
    }

    private async Task RetireGenerationAsync(
        long generation,
        RoslynLanguageServerProcess? process,
        RoslynLspClient? client,
        Task? rpcObservationTask,
        bool attemptGracefulShutdown)
    {
        Exception? failure = null;
        if (attemptGracefulShutdown && client is not null && process is not null && !process.HasExited)
        {
            try { await client.ShutdownAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) { failure = exception; }
        }

        if (process is not null)
        {
            try { await process.RetireAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) { failure = Combine(failure, exception); }
        }

        if (rpcObservationTask is not null)
        {
            try { await rpcObservationTask.WaitAsync(RoslynLanguageServerConstants.ForcedExitTimeout).ConfigureAwait(false); }
            catch (Exception exception) { failure = Combine(failure, exception); }
        }

        if (client is not null)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = Combine(failure, exception); }
        }

        if (failure is not null)
        {
            throw new InvalidOperationException($"Roslyn generation {generation} retirement failed.", failure);
        }
    }

    private Task CleanupOwnedFailedGenerationAsync(
        long generation,
        RoslynLanguageServerProcess? process,
        RoslynLspClient? client)
    {
        lock (_sync)
        {
            _intentionalRetirementGeneration = generation;
        }

        return CleanupCoreAsync();

        async Task CleanupCoreAsync()
        {
            try
            {
                await RetireGenerationAsync(generation, process, client, rpcObservationTask: null,
                    attemptGracefulShutdown: client?.IsInitialized == true && process?.HasExited == false)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    if (_roslynGeneration == generation)
                    {
                        if (_process is not null && process is not null && SameProcessIdentity(_process.Identity, process.Identity)) _process = null;
                        if (ReferenceEquals(_client, client)) _client = null;
                        _intentionalRetirementGeneration = null;
                    }
                }
            }
        }
    }

    private async Task ObserveRpcCompletionAsync(long generation, RoslynLspClient client, RoslynProcessIdentity processIdentity)
    {
        Exception? completionFailure = null;
        try { await client.Completion.ConfigureAwait(false); }
        catch (Exception exception) { completionFailure = exception; }

        WorkspacePublicationIdentity? publicationIdentity = null;
        RoslynLanguageServerProcess? processToCleanup = null;
        RoslynLspClient? clientToCleanup = null;
        bool writeFault = false;
        lock (_sync)
        {
            if (_rpcObservationRoslynGeneration != generation || _roslynGeneration != generation) return;
            if (_state is RoslynLanguageServerState.ShuttingDown or RoslynLanguageServerState.Stopped) return;
            if (_intentionalRetirementGeneration == generation || _state == RoslynLanguageServerState.Replacing) return;
            if (_state != RoslynLanguageServerState.Faulted)
            {
                publicationIdentity = _publication?.WorkspacePublicationIdentity;
                _publication = null;
                _state = RoslynLanguageServerState.Faulted;
                _faultKind = RoslynProjectLoadFaultKinds.InitializationFailed;
                processToCleanup = _process;
                clientToCleanup = _client;
                writeFault = true;
            }
        }

        if (writeFault)
        {
            WriteFault(completionFailure ?? new InvalidOperationException(
                "Roslyn Language Server JSON-RPC connection completed unexpectedly while the service was still active."),
                publicationIdentity, generation, processIdentity, null, RoslynProjectLoadFaultKinds.InitializationFailed);
            StartFaultCleanup(generation, processToCleanup, clientToCleanup);
        }
    }

    private void OnProcessExited(RoslynProcessExitObservation observation)
    {
        WorkspacePublicationIdentity? publicationIdentity = null;
        RoslynLanguageServerProcess? processToCleanup = null;
        RoslynLspClient? clientToCleanup = null;
        bool writeFault = false;
        lock (_sync)
        {
            long generation = observation.Identity.RoslynGeneration;
            if (generation != _roslynGeneration) return;
            if (_state is RoslynLanguageServerState.ShuttingDown or RoslynLanguageServerState.Stopped) return;
            if (observation.RetirementRequested || _intentionalRetirementGeneration == generation || _state == RoslynLanguageServerState.Replacing) return;
            if (_state != RoslynLanguageServerState.Faulted)
            {
                publicationIdentity = _publication?.WorkspacePublicationIdentity;
                _publication = null;
                _state = RoslynLanguageServerState.Faulted;
                _faultKind = RoslynProjectLoadFaultKinds.ProcessExited;
                processToCleanup = _process;
                clientToCleanup = _client;
                writeFault = true;
            }
        }

        if (writeFault)
        {
            WriteFault(new InvalidOperationException(
                $"Roslyn Language Server process exited unexpectedly with code {observation.ExitCode?.ToString() ?? "unknown"}."),
                publicationIdentity, observation.Identity.RoslynGeneration, observation.Identity, null,
                RoslynProjectLoadFaultKinds.ProcessExited);
            StartFaultCleanup(observation.Identity.RoslynGeneration, processToCleanup, clientToCleanup);
        }
    }

    private void StartFaultCleanup(
        long generation,
        RoslynLanguageServerProcess? process,
        RoslynLspClient? client)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_generationCleanupTask is not null
                && _generationCleanupRoslynGeneration == generation)
            {
                return;
            }

            _generationCleanupRoslynGeneration = generation;
            _generationCleanupTask = completion.Task;
        }

        _ = CompleteFaultCleanupAsync(completion, generation, process, client);
    }

    private async Task CompleteFaultCleanupAsync(
        TaskCompletionSource completion,
        long generation,
        RoslynLanguageServerProcess? process,
        RoslynLspClient? client)
    {
        try
        {
            await CleanupOwnedFailedGenerationAsync(generation, process, client).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _diagnosticLogging.WriteFault("roslyn_fault", exception);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private bool MarkGenerationFault(long generation, string faultKind)
    {
        lock (_sync)
        {
            if (_roslynGeneration != generation || _state is RoslynLanguageServerState.ShuttingDown or RoslynLanguageServerState.Stopped) return false;
            if (_state == RoslynLanguageServerState.Faulted) return false;
            _state = RoslynLanguageServerState.Faulted;
            _faultKind = faultKind;
            _publication = null;
            return true;
        }
    }

    private bool IsReusableProjectLoadedLocked(WorkspaceProjectSnapshot snapshot, RoslynProjectLoadTarget target)
        => _publication is not null
            && _process is not null
            && !_process.HasExited
            && SameProcessIdentity(_process.Identity, _publication.ProcessIdentity)
            && WorkspaceRootsEqual(_publication.WorkspaceProjectRoot, snapshot.WorkspaceIdentity.ProjectRoot)
            && _publication.LoadKind == target.LoadKind
            && PathsEqual(_publication.LoadTargetRelativePath, target.RelativePath);

    private RoslynLanguageServerSnapshot CreateSnapshotLocked()
        => new(_state, _roslynGeneration, _process?.Identity, _publication, _faultKind);

    private void EnsureActiveStartingGenerationLocked(long generation)
    {
        if (_roslynGeneration != generation || _state != RoslynLanguageServerState.Starting)
            throw new InvalidOperationException("Roslyn initialization lost active generation ownership.");
    }

    private void WriteProjectLoadReused(
        WorkspacePublicationIdentity publicationIdentity,
        RoslynLanguageServerSnapshot snapshot,
        RoslynProjectLoadTarget target)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent("roslyn_project_load_reused", new
            {
                workspaceGeneration = publicationIdentity.WorkspaceGeneration,
                workspacePublicationVersion = publicationIdentity.PublicationVersion,
                roslynGeneration = snapshot.RoslynGeneration,
                processId = snapshot.ProcessIdentity?.ProcessId,
                processStartTimeUtcTicks = snapshot.ProcessIdentity?.StartTimeUtcTicks,
                loadKind = target.LoadKind.ToString(),
                loadTarget = BoundDiagnosticText(target.RelativePath),
            });
        }
        else _diagnosticLogging.WriteEvent("roslyn_project_load_reused");
    }

    private void WriteReplacementStarted(WorkspacePublicationIdentity id, long generation, RoslynProcessIdentity? process, RoslynProjectLoadTarget target)
        => WriteLifecycle("roslyn_generation_replacement_started", id, generation, process, target, null);

    private void WriteReplacementCompleted(WorkspacePublicationIdentity id, RoslynLanguageServerSnapshot snapshot, RoslynProjectLoadTarget target)
        => WriteLifecycle("roslyn_generation_replacement_completed", id, snapshot.RoslynGeneration, snapshot.ProcessIdentity, target, null);

    private void WriteProjectLoaded(
        WorkspacePublicationIdentity id,
        long generation,
        RoslynProcessIdentity process,
        RoslynProjectLoadTarget target,
        double? durationMs,
        double? processStartDurationMs,
        double? clientSetupDurationMs,
        double? lspInitializeDurationMs,
        double? projectOpenNotificationDurationMs,
        double? projectInitializationWaitDurationMs,
        double? publicationCommitDurationMs)
    {
        if (!_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent("roslyn_project_loaded");
            return;
        }

        double explicitWorkDurationMs =
            (processStartDurationMs ?? 0)
            + (clientSetupDurationMs ?? 0)
            + (lspInitializeDurationMs ?? 0)
            + (projectOpenNotificationDurationMs ?? 0)
            + (projectInitializationWaitDurationMs ?? 0)
            + (publicationCommitDurationMs ?? 0);
        double? unattributedDurationMs = durationMs is double total
            ? total - explicitWorkDurationMs
            : null;

        _diagnosticLogging.WriteEvent("roslyn_project_loaded", new
        {
            workspaceGeneration = id.WorkspaceGeneration,
            workspacePublicationVersion = id.PublicationVersion,
            roslynGeneration = generation,
            processId = process.ProcessId,
            processStartTimeUtcTicks = process.StartTimeUtcTicks,
            loadKind = target.LoadKind.ToString(),
            loadTarget = BoundDiagnosticText(target.RelativePath),
            durationMs,
            processStartDurationMs,
            clientSetupDurationMs,
            lspInitializeDurationMs,
            projectOpenNotificationDurationMs,
            projectInitializationWaitDurationMs,
            publicationCommitDurationMs,
            explicitWorkDurationMs,
            unattributedDurationMs,
        });
    }

    private void WriteLifecycle(string eventName, WorkspacePublicationIdentity id, long generation, RoslynProcessIdentity? process, RoslynProjectLoadTarget target, double? durationMs)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteEvent(eventName, new
            {
                workspaceGeneration = id.WorkspaceGeneration,
                workspacePublicationVersion = id.PublicationVersion,
                roslynGeneration = generation,
                processId = process?.ProcessId,
                processStartTimeUtcTicks = process?.StartTimeUtcTicks,
                loadKind = target.LoadKind.ToString(),
                loadTarget = BoundDiagnosticText(target.RelativePath),
                durationMs,
            });
        }
        else _diagnosticLogging.WriteEvent(eventName);
    }

    private void WriteFault(Exception exception, WorkspacePublicationIdentity? id, long? generation, RoslynProcessIdentity? process, RoslynProjectLoadTarget? target, string faultKind)
    {
        if (_diagnosticLogging.IsEnabled)
        {
            _diagnosticLogging.WriteFault("roslyn_fault", exception, new
            {
                workspaceGeneration = id?.WorkspaceGeneration,
                workspacePublicationVersion = id?.PublicationVersion,
                roslynGeneration = generation,
                processId = process?.ProcessId,
                processStartTimeUtcTicks = process?.StartTimeUtcTicks,
                loadKind = target?.LoadKind.ToString(),
                loadTarget = target is null ? null : BoundDiagnosticText(target.RelativePath),
                faultKind,
            });
        }
        else _diagnosticLogging.WriteFault("roslyn_fault", exception);
    }

    private void WriteStopping(long generation, RoslynProcessIdentity? process)
    {
        if (_diagnosticLogging.IsEnabled) _diagnosticLogging.WriteEvent("roslyn_stopping", new { roslynGeneration = generation, processId = process?.ProcessId, processStartTimeUtcTicks = process?.StartTimeUtcTicks });
        else _diagnosticLogging.WriteEvent("roslyn_stopping");
    }

    private void WriteStopped(long generation, RoslynProcessIdentity? process)
    {
        if (_diagnosticLogging.IsEnabled) _diagnosticLogging.WriteEvent("roslyn_stopped", new { roslynGeneration = generation, processId = process?.ProcessId, processStartTimeUtcTicks = process?.StartTimeUtcTicks });
        else _diagnosticLogging.WriteEvent("roslyn_stopped");
    }

    private static Exception Combine(Exception? first, Exception second)
        => first is null ? second : new AggregateException(first, second);

    private static string BoundDiagnosticText(string value)
        => value.Length <= RoslynLanguageServerConstants.MaxDiagnosticTargetLength
            ? value
            : value[..RoslynLanguageServerConstants.MaxDiagnosticTargetLength];

    private static bool SameProcessIdentity(RoslynProcessIdentity left, RoslynProcessIdentity right)
        => left.ProcessId == right.ProcessId
            && left.StartTimeUtcTicks == right.StartTimeUtcTicks
            && left.RoslynGeneration == right.RoslynGeneration;

    private static bool WorkspaceRootsEqual(string left, string right) => PathsEqual(left, right);

    private static bool PathsEqual(string left, string right)
        => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
