using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace SystemExplorer.CodeService;

internal sealed class LocalTransportHost : IAsyncDisposable
{
    private const int IngressConcurrencyLimit = 8;
    private const long MaxRequestBodySizeBytes = DocumentSynchronizationLimits.MaxSnapshotRequestBodySizeBytes;
    private const int MaxWorkspaceInitializeBodySizeBytes = 8 * 1024;
    private const int MaxSemanticReadinessBodySizeBytes = 8 * 1024;
    private const int MaxConcurrentConnections = 32;
    private const int MaxRequestHeaderCount = 32;
    private const int MaxRequestHeadersTotalSizeBytes = 16 * 1024;
    private const int MaxRequestLineSizeBytes = 4 * 1024;
    private const int MaxProtocolVersionHeaderLength = 16;
    private const int MaxSessionIdHeaderLength = 128;
    private const int MaxRequestIdHeaderLength = 64;
    private const string BearerPrefix = "Bearer ";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestHeadersTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(30);

    private readonly SessionProtocolContext _protocolContext;
    private readonly WorkspaceHost _workspaceHost;
    private readonly DocumentSynchronizationHost _documentSynchronizationHost;
    private readonly DocumentSemanticReadinessHost _documentSemanticReadinessHost;
    private readonly DocumentCompletionHost _documentCompletionHost;
    private readonly SemaphoreSlim _ingressGate = new(IngressConcurrencyLimit, IngressConcurrencyLimit);
    private readonly TaskCompletionSource _completionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WebApplication? _application;
    private CancellationTokenRegistration _applicationStoppedRegistration;
    private int _startState;
    private int _controlPlaneEnabled;
    private int _stopState;
    private int _disposeState;
    private int _ingressGateDisposeState;

    public LocalTransportHost(
        SessionProtocolContext protocolContext,
        WorkspaceHost workspaceHost,
        DocumentSynchronizationHost documentSynchronizationHost,
        DocumentSemanticReadinessHost documentSemanticReadinessHost,
        DocumentCompletionHost documentCompletionHost)
    {
        _protocolContext = protocolContext ?? throw new ArgumentNullException(nameof(protocolContext));
        _workspaceHost = workspaceHost ?? throw new ArgumentNullException(nameof(workspaceHost));
        _documentSynchronizationHost = documentSynchronizationHost
            ?? throw new ArgumentNullException(nameof(documentSynchronizationHost));
        _documentSemanticReadinessHost = documentSemanticReadinessHost
            ?? throw new ArgumentNullException(nameof(documentSemanticReadinessHost));
        _documentCompletionHost = documentCompletionHost
            ?? throw new ArgumentNullException(nameof(documentCompletionHost));
    }

    public LocalTransportEndpoint? BoundEndpoint { get; private set; }

    public Task Completion => _completionSource.Task;

    public async Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _startState, 1, 0) != 0)
        {
            throw new InvalidOperationException("local transport startup may only be attempted once.");
        }

        using CancellationTokenSource startupCancellation = new(StartupTimeout);

        try
        {
            WebApplication application = BuildApplication();
            _application = application;

            _applicationStoppedRegistration = application.Lifetime.ApplicationStopped.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                _completionSource);

            await application.StartAsync(startupCancellation.Token).ConfigureAwait(false);

            BoundEndpoint = ResolveBoundEndpoint(application);
        }
        catch (OperationCanceledException exception)
            when (startupCancellation.IsCancellationRequested)
        {
            await CleanupFailedStartupAsync().ConfigureAwait(false);
            throw new TimeoutException(
                $"local transport startup did not complete within {StartupTimeout.TotalSeconds:0} seconds.",
                exception);
        }
        catch
        {
            await CleanupFailedStartupAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void EnableControlPlane()
    {
        if (Volatile.Read(ref _startState) == 0
            || Volatile.Read(ref _stopState) != 0
            || _application is null
            || BoundEndpoint is null)
        {
            throw new InvalidOperationException(
                "authenticated control plane cannot be enabled before verified transport startup.");
        }

        if (Interlocked.CompareExchange(ref _controlPlaneEnabled, 1, 0) != 0)
        {
            return;
        }
    }

    public async Task StopAsync()
    {
        Volatile.Write(ref _controlPlaneEnabled, 0);

        if (Volatile.Read(ref _startState) == 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _stopState, 1, 0) != 0)
        {
            await _stopCompletionSource.Task.ConfigureAwait(false);
            return;
        }

        Exception? stopFailure = null;
        WebApplication? application = _application;

        try
        {
            if (application is not null)
            {
                using CancellationTokenSource shutdownCancellation = new(ShutdownTimeout);

                try
                {
                    await application.StopAsync(shutdownCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception)
                    when (shutdownCancellation.IsCancellationRequested)
                {
                    stopFailure = new TimeoutException(
                        $"local transport shutdown did not complete within {ShutdownTimeout.TotalSeconds:0} seconds.",
                        exception);
                }
                catch (Exception exception)
                {
                    stopFailure = exception;
                }
            }
        }
        finally
        {
            _applicationStoppedRegistration.Dispose();

            if (application is not null)
            {
                try
                {
                    await application.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    stopFailure ??= exception;
                }
            }

            _application = null;
            _completionSource.TrySetResult();
            DisposeIngressGate();
            _stopCompletionSource.TrySetResult();
        }

        if (stopFailure is not null)
        {
            throw new InvalidOperationException("local transport shutdown failed.", stopFailure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            DisposeIngressGate();
        }
    }

    private void DisposeIngressGate()
    {
        if (Interlocked.Exchange(ref _ingressGateDisposeState, 1) == 0)
        {
            _ingressGate.Dispose();
        }
    }

    private WebApplication BuildApplication()
    {
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(
            new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
            });

        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
        {
            options.Limits.MaxConcurrentConnections = MaxConcurrentConnections;
            options.Limits.RequestHeadersTimeout = RequestHeadersTimeout;
            options.Limits.KeepAliveTimeout = KeepAliveTimeout;
            options.Limits.MaxRequestBodySize = MaxRequestBodySizeBytes;
            options.Limits.MaxRequestHeaderCount = MaxRequestHeaderCount;
            options.Limits.MaxRequestHeadersTotalSize = MaxRequestHeadersTotalSizeBytes;
            options.Limits.MaxRequestLineSize = MaxRequestLineSizeBytes;

            options.Listen(IPAddress.Loopback, 0, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });

        WebApplication application = builder.Build();
        application.Run(HandleRequestAsync);
        return application;
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        bool admitted = false;

        try
        {
            if (context.RequestAborted.IsCancellationRequested)
            {
                AbortRequestNoThrow(context);
                return;
            }

            admitted = await _ingressGate
                .WaitAsync(0, context.RequestAborted)
                .ConfigureAwait(false);

            if (!admitted)
            {
                await CompleteZeroBodyResponseAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable).ConfigureAwait(false);
                return;
            }

            if (Volatile.Read(ref _controlPlaneEnabled) == 0)
            {
                await CompleteZeroBodyResponseAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable).ConfigureAwait(false);
                return;
            }

            string? requestPath = context.Request.Path.Value;
            if (string.Equals(
                    requestPath,
                    CodeServiceProtocol.HandshakePath,
                    StringComparison.Ordinal))
            {
                await HandleHandshakeAsync(context).ConfigureAwait(false);
                return;
            }

            if (string.Equals(
                    requestPath,
                    CodeServiceProtocol.WorkspaceInitializePath,
                    StringComparison.Ordinal))
            {
                await HandleWorkspaceInitializeAsync(context).ConfigureAwait(false);
                return;
            }

            if (string.Equals(
                    requestPath,
                    CodeServiceProtocol.WorkspaceStatusPath,
                    StringComparison.Ordinal))
            {
                await HandleWorkspaceStatusAsync(context).ConfigureAwait(false);
                return;
            }

            if (string.Equals(
                    requestPath,
                    CodeServiceProtocol.DocumentEpochPath,
                    StringComparison.Ordinal))
            {
                await HandleDocumentEpochAsync(context).ConfigureAwait(false);
                return;
            }

            if (string.Equals(
                    requestPath,
                    CodeServiceProtocol.DocumentSnapshotPath,
                    StringComparison.Ordinal))
            {
                await HandleDocumentSnapshotAsync(context).ConfigureAwait(false);
                return;
            }

            if (string.Equals(
                    requestPath,
                    CodeServiceProtocol.DocumentSemanticReadyPath,
                    StringComparison.Ordinal))
            {
                await HandleDocumentSemanticReadyAsync(context).ConfigureAwait(false);
                return;
            }

            if (string.Equals(
                    requestPath,
                    CodeServiceProtocol.CompletionPath,
                    StringComparison.Ordinal))
            {
                await HandleCompletionAsync(context).ConfigureAwait(false);
                return;
            }

            await CompleteZeroBodyResponseAsync(
                context,
                StatusCodes.Status503ServiceUnavailable).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            AbortRequestNoThrow(context);
        }
        catch (Exception)
        {
            AbortRequestNoThrow(context);
        }
        finally
        {
            if (admitted)
            {
                try
                {
                    _ingressGate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Forced transport teardown can retire the gate after the request is aborted.
                }
            }
        }
    }

    private async Task HandleHandshakeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await CompleteZeroBodyResponseAsync(
                context,
                StatusCodes.Status405MethodNotAllowed).ConfigureAwait(false);
            return;
        }

        if (!TryAuthenticate(context.Request))
        {
            await CompleteZeroBodyResponseAsync(
                context,
                StatusCodes.Status401Unauthorized).ConfigureAwait(false);
            return;
        }

        if (HasUnexpectedHandshakeBody(context.Request))
        {
            await CompleteHandshakeFailureAsync(
                context,
                StatusCodes.Status400BadRequest,
                CodeServiceProtocol.HandshakeInvalidRequestOutcome,
                requestId: null).ConfigureAwait(false);
            return;
        }

        if (!TryGetSingleHeader(
                context.Request,
                CodeServiceProtocol.ProtocolVersionHeaderName,
                MaxProtocolVersionHeaderLength,
                out string? protocolVersionValue)
            || !int.TryParse(
                protocolVersionValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int requestedProtocolVersion)
            || !TryGetSingleHeader(
                context.Request,
                CodeServiceProtocol.SessionIdHeaderName,
                MaxSessionIdHeaderLength,
                out string? requestedSessionId)
            || !TryGetSingleHeader(
                context.Request,
                CodeServiceProtocol.RequestIdHeaderName,
                MaxRequestIdHeaderLength,
                out string? requestIdValue)
            || !Guid.TryParseExact(requestIdValue, "D", out Guid requestId))
        {
            await CompleteHandshakeFailureAsync(
                context,
                StatusCodes.Status400BadRequest,
                CodeServiceProtocol.HandshakeInvalidRequestOutcome,
                requestId: null).ConfigureAwait(false);
            return;
        }

        string canonicalRequestId = requestId.ToString("D");

        if (requestedProtocolVersion != _protocolContext.ProtocolVersion)
        {
            await CompleteHandshakeFailureAsync(
                context,
                StatusCodes.Status409Conflict,
                CodeServiceProtocol.HandshakeVersionMismatchOutcome,
                canonicalRequestId).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(
                requestedSessionId,
                _protocolContext.SessionIdentity.SessionId,
                StringComparison.Ordinal))
        {
            await CompleteHandshakeFailureAsync(
                context,
                StatusCodes.Status400BadRequest,
                CodeServiceProtocol.HandshakeInvalidRequestOutcome,
                canonicalRequestId).ConfigureAwait(false);
            return;
        }

        LocalTransportEndpoint endpoint = BoundEndpoint
            ?? throw new InvalidOperationException(
                "control plane was enabled without a verified bound endpoint.");

        HandshakeResponse response = new(
            CodeServiceProtocol.HandshakeSchemaVersion,
            CodeServiceProtocol.HandshakeSuccessOutcome,
            canonicalRequestId,
            _protocolContext.ProtocolVersion,
            _protocolContext.ServiceVersion,
            _protocolContext.SessionIdentity.SessionId,
            _protocolContext.GodotOwnerIdentity.ProcessId,
            _protocolContext.GodotOwnerIdentity.StartTimeUtcTicks,
            _protocolContext.ServiceProcessIdentity.ProcessId,
            _protocolContext.ServiceProcessIdentity.StartTimeUtcTicks,
            endpoint.Scheme,
            endpoint.Address,
            endpoint.Port);

        await CompleteJsonResponseAsync(
            context,
            StatusCodes.Status200OK,
            response).ConfigureAwait(false);
    }

    private async Task HandleWorkspaceInitializeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await CompleteZeroBodyResponseAsync(
                context,
                StatusCodes.Status405MethodNotAllowed).ConfigureAwait(false);
            return;
        }

        if (!TryAuthenticate(context.Request))
        {
            await CompleteZeroBodyResponseAsync(
                context,
                StatusCodes.Status401Unauthorized).ConfigureAwait(false);
            return;
        }

        WorkspaceRequestHeaderValidationResult headerValidation =
            ValidateWorkspaceRequestHeaders(context.Request);
        if (!headerValidation.IsSuccess)
        {
            await CompleteWorkspaceHeaderFailureAsync(
                context,
                headerValidation).ConfigureAwait(false);
            return;
        }

        WorkspaceInitializeBodyParseResult bodyResult = await TryReadWorkspaceInitializeBodyAsync(
            context.Request,
            context.RequestAborted).ConfigureAwait(false);

        if (!bodyResult.IsSuccess)
        {
            bool versionMismatch =
                bodyResult.FailureKind == WorkspaceInitializeBodyFailureKind.VersionMismatch;

            await CompleteWorkspaceFailureAsync(
                context,
                versionMismatch
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status400BadRequest,
                versionMismatch
                    ? CodeServiceProtocol.WorkspaceVersionMismatchOutcome
                    : CodeServiceProtocol.WorkspaceInvalidRequestOutcome,
                headerValidation.RequestId,
                _workspaceHost.GetStatusSnapshot()).ConfigureAwait(false);
            return;
        }

        WorkspaceInitializationResult initializationResult =
            await _workspaceHost.InitializeAsync(bodyResult.ProjectRoot).ConfigureAwait(false);

        string outcome = GetWorkspaceInitializationOutcome(initializationResult.Outcome);
        int statusCode = GetWorkspaceInitializationStatusCode(initializationResult.Outcome);
        WorkspaceStatusSnapshot status = initializationResult.Status;

        WorkspaceInitializeResponse response = new(
            CodeServiceProtocol.WorkspaceSchemaVersion,
            outcome,
            headerValidation.RequestId,
            status.State.ToString(),
            status.ProjectRoot,
            initializationResult.ReusedExistingWorkspace,
            status.SourceFileCount,
            status.ProjectFileCount,
            status.SolutionFileCount,
            status.FaultKind);

        await CompleteJsonResponseAsync(context, statusCode, response).ConfigureAwait(false);
    }

    private async Task HandleWorkspaceStatusAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await CompleteZeroBodyResponseAsync(
                context,
                StatusCodes.Status405MethodNotAllowed).ConfigureAwait(false);
            return;
        }

        if (!TryAuthenticate(context.Request))
        {
            await CompleteZeroBodyResponseAsync(
                context,
                StatusCodes.Status401Unauthorized).ConfigureAwait(false);
            return;
        }

        WorkspaceRequestHeaderValidationResult headerValidation =
            ValidateWorkspaceRequestHeaders(context.Request);
        if (!headerValidation.IsSuccess)
        {
            await CompleteWorkspaceHeaderFailureAsync(
                context,
                headerValidation).ConfigureAwait(false);
            return;
        }

        if (HasUnexpectedHandshakeBody(context.Request))
        {
            await CompleteWorkspaceFailureAsync(
                context,
                StatusCodes.Status400BadRequest,
                CodeServiceProtocol.WorkspaceInvalidRequestOutcome,
                headerValidation.RequestId,
                _workspaceHost.GetStatusSnapshot()).ConfigureAwait(false);
            return;
        }

        WorkspaceStatusSnapshot status = _workspaceHost.GetStatusSnapshot();
        bool shuttingDown = status.State == WorkspaceState.ShuttingDown;
        WorkspaceStatusResponse response = new(
            CodeServiceProtocol.WorkspaceSchemaVersion,
            shuttingDown
                ? CodeServiceProtocol.WorkspaceUnavailableOutcome
                : CodeServiceProtocol.WorkspaceSuccessOutcome,
            headerValidation.RequestId,
            status.State.ToString(),
            status.ProjectRoot,
            status.SourceFileCount,
            status.ProjectFileCount,
            status.SolutionFileCount,
            status.FaultKind);

        await CompleteJsonResponseAsync(
            context,
            shuttingDown
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK,
            response).ConfigureAwait(false);
    }

    private async Task HandleDocumentEpochAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await CompleteZeroBodyResponseAsync(
                context,
                StatusCodes.Status405MethodNotAllowed).ConfigureAwait(false);
            return;
        }

        if (!TryAuthenticate(context.Request))
        {
            await CompleteZeroBodyResponseAsync(
                context,
                StatusCodes.Status401Unauthorized).ConfigureAwait(false);
            return;
        }

        WorkspaceRequestHeaderValidationResult headerValidation =
            ValidateWorkspaceRequestHeaders(context.Request);
        if (!headerValidation.IsSuccess)
        {
            DocumentSynchronizationOutcome outcome =
                headerValidation.FailureKind == WorkspaceRequestHeaderFailureKind.VersionMismatch
                    ? DocumentSynchronizationOutcome.VersionMismatch
                    : DocumentSynchronizationOutcome.InvalidRequest;
            await CompleteDocumentEpochResponseAsync(
                context,
                headerValidation.RequestId,
                DocumentEpochOperationResult.Failure(outcome)).ConfigureAwait(false);
            return;
        }

        TrySetEndpointRequestBodyLimit(
            context,
            DocumentSynchronizationLimits.MaxEpochRequestBodySizeBytes);

        DocumentEpochBodyParseResult bodyResult = await TryReadDocumentEpochBodyAsync(
            context.Request,
            context.RequestAborted).ConfigureAwait(false);
        if (!bodyResult.IsSuccess)
        {
            DocumentSynchronizationOutcome outcome = bodyResult.FailureKind switch
            {
                DocumentRequestBodyFailureKind.VersionMismatch => DocumentSynchronizationOutcome.VersionMismatch,
                DocumentRequestBodyFailureKind.CapacityExceeded => DocumentSynchronizationOutcome.CapacityExceeded,
                _ => DocumentSynchronizationOutcome.InvalidRequest,
            };

            await CompleteDocumentEpochResponseAsync(
                context,
                headerValidation.RequestId,
                DocumentEpochOperationResult.Failure(outcome, bodyResult.Request)).ConfigureAwait(false);
            return;
        }

        DocumentEpochRequest request = bodyResult.Request!;
        WorkloadAdmissionResult admission = _documentSynchronizationHost.TryAdmitTransportOperation();
        if (admission.Status != WorkloadAdmissionStatus.Admitted
            || admission.Lease is not WorkloadExecutionLease lease)
        {
            DocumentSynchronizationOutcome outcome = admission.Status == WorkloadAdmissionStatus.Busy
                ? DocumentSynchronizationOutcome.Busy
                : DocumentSynchronizationOutcome.Unavailable;
            await CompleteDocumentEpochResponseAsync(
                context,
                headerValidation.RequestId,
                DocumentEpochOperationResult.Failure(outcome, request)).ConfigureAwait(false);
            return;
        }

        try
        {
            if (!_workspaceHost.TryGetCurrentPublication(out WorkspacePublication publication))
            {
                await CompleteDocumentEpochResponseAsync(
                    context,
                    headerValidation.RequestId,
                    DocumentEpochOperationResult.Failure(
                        DocumentSynchronizationOutcome.WorkspaceUnavailable,
                        request)).ConfigureAwait(false);
                return;
            }

            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    context.RequestAborted,
                    lease.ServiceWorkShutdownToken);

            DocumentEpochOperationResult result;
            try
            {
                result = await _documentSynchronizationHost.ReconcileEpochAsync(
                    request,
                    publication,
                    lease,
                    linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (lease.ServiceWorkShutdownToken.IsCancellationRequested
                    && !context.RequestAborted.IsCancellationRequested)
            {
                result = DocumentEpochOperationResult.Failure(
                    DocumentSynchronizationOutcome.Unavailable,
                    request,
                    publication);
            }

            await CompleteDocumentEpochResponseAsync(
                context,
                headerValidation.RequestId,
                result).ConfigureAwait(false);
        }
        finally
        {
            lease.Retire();
        }
    }

    private async Task HandleDocumentSnapshotAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await CompleteZeroBodyResponseAsync(
                context,
                StatusCodes.Status405MethodNotAllowed).ConfigureAwait(false);
            return;
        }

        if (!TryAuthenticate(context.Request))
        {
            await CompleteZeroBodyResponseAsync(
                context,
                StatusCodes.Status401Unauthorized).ConfigureAwait(false);
            return;
        }

        WorkspaceRequestHeaderValidationResult headerValidation =
            ValidateWorkspaceRequestHeaders(context.Request);
        if (!headerValidation.IsSuccess)
        {
            DocumentSynchronizationOutcome outcome =
                headerValidation.FailureKind == WorkspaceRequestHeaderFailureKind.VersionMismatch
                    ? DocumentSynchronizationOutcome.VersionMismatch
                    : DocumentSynchronizationOutcome.InvalidRequest;
            await CompleteDocumentSnapshotResponseAsync(
                context,
                headerValidation.RequestId,
                DocumentSnapshotOperationResult.Failure(outcome)).ConfigureAwait(false);
            return;
        }

        TrySetEndpointRequestBodyLimit(
            context,
            DocumentSynchronizationLimits.MaxSnapshotRequestBodySizeBytes);

        DocumentSnapshotBodyParseResult bodyResult = await TryReadDocumentSnapshotBodyAsync(
            context.Request,
            context.RequestAborted).ConfigureAwait(false);
        if (!bodyResult.IsSuccess)
        {
            DocumentSynchronizationOutcome outcome = bodyResult.FailureKind switch
            {
                DocumentRequestBodyFailureKind.VersionMismatch => DocumentSynchronizationOutcome.VersionMismatch,
                DocumentRequestBodyFailureKind.CapacityExceeded => DocumentSynchronizationOutcome.CapacityExceeded,
                _ => DocumentSynchronizationOutcome.InvalidRequest,
            };

            await CompleteDocumentSnapshotResponseAsync(
                context,
                headerValidation.RequestId,
                DocumentSnapshotOperationResult.Failure(outcome, bodyResult.Request)).ConfigureAwait(false);
            return;
        }

        DocumentSnapshotRequest request = bodyResult.Request!;
        WorkloadAdmissionResult admission = _documentSynchronizationHost.TryAdmitTransportOperation();
        if (admission.Status != WorkloadAdmissionStatus.Admitted
            || admission.Lease is not WorkloadExecutionLease lease)
        {
            DocumentSynchronizationOutcome outcome = admission.Status == WorkloadAdmissionStatus.Busy
                ? DocumentSynchronizationOutcome.Busy
                : DocumentSynchronizationOutcome.Unavailable;
            await CompleteDocumentSnapshotResponseAsync(
                context,
                headerValidation.RequestId,
                DocumentSnapshotOperationResult.Failure(outcome, request)).ConfigureAwait(false);
            return;
        }

        try
        {
            if (!_workspaceHost.TryGetCurrentPublication(out WorkspacePublication publication))
            {
                await CompleteDocumentSnapshotResponseAsync(
                    context,
                    headerValidation.RequestId,
                    DocumentSnapshotOperationResult.Failure(
                        DocumentSynchronizationOutcome.WorkspaceUnavailable,
                        request)).ConfigureAwait(false);
                return;
            }

            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    context.RequestAborted,
                    lease.ServiceWorkShutdownToken);

            DocumentSnapshotOperationResult result;
            try
            {
                result = await _documentSynchronizationHost.SynchronizeSnapshotAsync(
                    request,
                    publication,
                    lease,
                    linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (lease.ServiceWorkShutdownToken.IsCancellationRequested
                    && !context.RequestAborted.IsCancellationRequested)
            {
                result = DocumentSnapshotOperationResult.Failure(
                    DocumentSynchronizationOutcome.Unavailable,
                    request,
                    publication);
            }

            await CompleteDocumentSnapshotResponseAsync(
                context,
                headerValidation.RequestId,
                result).ConfigureAwait(false);
        }
        finally
        {
            lease.Retire();
        }
    }

    private async Task HandleCompletionAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await CompleteZeroBodyResponseAsync(context, StatusCodes.Status405MethodNotAllowed).ConfigureAwait(false);
            return;
        }
        if (!TryAuthenticate(context.Request))
        {
            await CompleteZeroBodyResponseAsync(context, StatusCodes.Status401Unauthorized).ConfigureAwait(false);
            return;
        }

        WorkspaceRequestHeaderValidationResult headerValidation = ValidateWorkspaceRequestHeaders(context.Request);
        if (!headerValidation.IsSuccess)
        {
            DocumentCompletionOutcome outcome = headerValidation.FailureKind == WorkspaceRequestHeaderFailureKind.VersionMismatch
                ? DocumentCompletionOutcome.VersionMismatch
                : DocumentCompletionOutcome.InvalidRequest;
            _documentCompletionHost.RecordTransportRejection(outcome);
            await CompleteBoundedCompletionResponseAsync(
                context,
                headerValidation.RequestId,
                DocumentCompletionResult.Failure(outcome)).ConfigureAwait(false);
            return;
        }

        TrySetEndpointRequestBodyLimit(context, DocumentCompletionLimits.MaxRequestBodySizeBytes);
        CompletionBodyParseResult bodyResult = await TryReadCompletionBodyAsync(
            context.Request,
            context.RequestAborted).ConfigureAwait(false);
        if (!bodyResult.IsSuccess || bodyResult.Request is not DocumentCompletionRequest request)
        {
            DocumentCompletionOutcome outcome = bodyResult.FailureKind == DocumentRequestBodyFailureKind.VersionMismatch
                ? DocumentCompletionOutcome.VersionMismatch
                : DocumentCompletionOutcome.InvalidRequest;
            _documentCompletionHost.RecordTransportRejection(outcome, bodyResult.Request);
            await CompleteBoundedCompletionResponseAsync(
                context,
                headerValidation.RequestId,
                DocumentCompletionResult.Failure(outcome, bodyResult.Request)).ConfigureAwait(false);
            return;
        }

        WorkloadAdmissionResult admission = _documentCompletionHost.TryAdmitTransportOperation();
        if (admission.Status != WorkloadAdmissionStatus.Admitted || admission.Lease is not WorkloadExecutionLease lease)
        {
            DocumentCompletionOutcome outcome = admission.Status == WorkloadAdmissionStatus.Busy
                ? DocumentCompletionOutcome.Busy
                : DocumentCompletionOutcome.Unavailable;
            _documentCompletionHost.RecordTransportRejection(outcome, request);
            await CompleteBoundedCompletionResponseAsync(
                context,
                headerValidation.RequestId,
                DocumentCompletionResult.Failure(outcome, request)).ConfigureAwait(false);
            return;
        }

        try
        {
            using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                lease.ServiceWorkShutdownToken);
            DocumentCompletionResult result;
            try
            {
                result = await _documentCompletionHost.CompleteAsync(
                    request,
                    lease,
                    linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lease.ServiceWorkShutdownToken.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
            {
                result = DocumentCompletionResult.Failure(DocumentCompletionOutcome.Unavailable, request);
            }

            await CompleteBoundedCompletionResponseAsync(
                context,
                headerValidation.RequestId,
                result).ConfigureAwait(false);
        }
        finally
        {
            lease.Retire();
        }
    }

    private static async Task<CompletionBodyParseResult> TryReadCompletionBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        BoundedBodyReadResult readResult = await TryReadBoundedBodyAsync(
            request,
            DocumentCompletionLimits.MaxRequestBodySizeBytes,
            cancellationToken).ConfigureAwait(false);
        if (readResult.TooLarge || readResult.Body is null)
        {
            return CompletionBodyParseResult.Invalid();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                readResult.Body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return CompletionBodyParseResult.Invalid();
            }

            int schemaVersionCount = 0;
            int clientGenerationCount = 0;
            int epochIdCount = 0;
            int documentPathCount = 0;
            int clientVersionCount = 0;
            int lineCount = 0;
            int characterCount = 0;
            int schemaVersion = 0;
            long clientGeneration = 0;
            Guid epochId = default;
            string? documentPath = null;
            long clientVersion = 0;
            int line = 0;
            int character = 0;

            foreach (JsonProperty property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "schemaVersion":
                        schemaVersionCount++;
                        if (schemaVersionCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt32(out schemaVersion))
                        {
                            return CompletionBodyParseResult.Invalid();
                        }
                        break;

                    case "clientGeneration":
                        clientGenerationCount++;
                        if (clientGenerationCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt64(out clientGeneration)
                            || clientGeneration <= 0)
                        {
                            return CompletionBodyParseResult.Invalid();
                        }
                        break;

                    case "epochId":
                        epochIdCount++;
                        if (epochIdCount != 1
                            || property.Value.ValueKind != JsonValueKind.String
                            || !TryGetCanonicalGuid(property.Value.GetString(), out epochId)
                            || epochId == Guid.Empty)
                        {
                            return CompletionBodyParseResult.Invalid();
                        }
                        break;

                    case "documentPath":
                        documentPathCount++;
                        if (documentPathCount != 1
                            || property.Value.ValueKind != JsonValueKind.String)
                        {
                            return CompletionBodyParseResult.Invalid();
                        }
                        documentPath = property.Value.GetString();
                        if (string.IsNullOrWhiteSpace(documentPath)
                            || documentPath.Length > DocumentSynchronizationLimits.MaxDocumentPathLength)
                        {
                            return CompletionBodyParseResult.Invalid();
                        }
                        break;

                    case "clientVersion":
                        clientVersionCount++;
                        if (clientVersionCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt64(out clientVersion)
                            || clientVersion <= 0)
                        {
                            return CompletionBodyParseResult.Invalid();
                        }
                        break;

                    case "line":
                        lineCount++;
                        if (lineCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt32(out line)
                            || line < 0
                            || line > DocumentCompletionLimits.MaxCompletionLine)
                        {
                            return CompletionBodyParseResult.Invalid();
                        }
                        break;

                    case "character":
                        characterCount++;
                        if (characterCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt32(out character)
                            || character < 0
                            || character > DocumentCompletionLimits.MaxCompletionCharacter)
                        {
                            return CompletionBodyParseResult.Invalid();
                        }
                        break;

                    default:
                        return CompletionBodyParseResult.Invalid();
                }
            }

            if (schemaVersionCount != 1
                || clientGenerationCount != 1
                || epochIdCount != 1
                || documentPathCount != 1
                || clientVersionCount != 1
                || lineCount != 1
                || characterCount != 1
                || documentPath is null)
            {
                return CompletionBodyParseResult.Invalid();
            }

            DocumentCompletionRequest requestValue = new(
                schemaVersion,
                clientGeneration,
                epochId,
                documentPath,
                clientVersion,
                line,
                character);

            return schemaVersion == CodeServiceProtocol.CompletionSchemaVersion
                ? CompletionBodyParseResult.Success(requestValue)
                : CompletionBodyParseResult.VersionMismatch(requestValue);
        }
        catch (JsonException)
        {
            return CompletionBodyParseResult.Invalid();
        }
    }

    private async Task HandleDocumentSemanticReadyAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            await CompleteZeroBodyResponseAsync(context, StatusCodes.Status405MethodNotAllowed).ConfigureAwait(false);
            return;
        }
        if (!TryAuthenticate(context.Request))
        {
            await CompleteZeroBodyResponseAsync(context, StatusCodes.Status401Unauthorized).ConfigureAwait(false);
            return;
        }

        WorkspaceRequestHeaderValidationResult headerValidation = ValidateWorkspaceRequestHeaders(context.Request);
        if (!headerValidation.IsSuccess)
        {
            DocumentSemanticReadinessOutcome outcome = headerValidation.FailureKind == WorkspaceRequestHeaderFailureKind.VersionMismatch
                ? DocumentSemanticReadinessOutcome.VersionMismatch
                : DocumentSemanticReadinessOutcome.InvalidRequest;
            await CompleteSemanticReadinessResponseAsync(context, headerValidation.RequestId, DocumentSemanticReadinessResult.Failure(outcome)).ConfigureAwait(false);
            return;
        }

        TrySetEndpointRequestBodyLimit(context, MaxSemanticReadinessBodySizeBytes);
        DocumentSemanticReadinessRequest? request = await TryReadSemanticReadinessBodyAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        if (request is null)
        {
            await CompleteSemanticReadinessResponseAsync(context, headerValidation.RequestId, DocumentSemanticReadinessResult.Failure(DocumentSemanticReadinessOutcome.InvalidRequest)).ConfigureAwait(false);
            return;
        }
        if (request.SchemaVersion != CodeServiceProtocol.SemanticReadinessSchemaVersion)
        {
            await CompleteSemanticReadinessResponseAsync(context, headerValidation.RequestId, DocumentSemanticReadinessResult.Failure(DocumentSemanticReadinessOutcome.VersionMismatch, request)).ConfigureAwait(false);
            return;
        }

        WorkloadAdmissionResult admission = _documentSemanticReadinessHost.TryAdmitTransportOperation();
        if (admission.Status != WorkloadAdmissionStatus.Admitted || admission.Lease is not WorkloadExecutionLease lease)
        {
            DocumentSemanticReadinessOutcome outcome = admission.Status == WorkloadAdmissionStatus.Busy
                ? DocumentSemanticReadinessOutcome.Busy
                : DocumentSemanticReadinessOutcome.Unavailable;
            await CompleteSemanticReadinessResponseAsync(context, headerValidation.RequestId, DocumentSemanticReadinessResult.Failure(outcome, request)).ConfigureAwait(false);
            return;
        }

        try
        {
            using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lease.ServiceWorkShutdownToken);
            DocumentSemanticReadinessResult result;
            try
            {
                result = await _documentSemanticReadinessHost.EnsureReadyAsync(request, lease, linkedCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lease.ServiceWorkShutdownToken.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
            {
                result = DocumentSemanticReadinessResult.Failure(DocumentSemanticReadinessOutcome.Unavailable, request);
            }
            await CompleteSemanticReadinessResponseAsync(context, headerValidation.RequestId, result).ConfigureAwait(false);
        }
        finally
        {
            lease.Retire();
        }
    }

    private static async Task<DocumentSemanticReadinessRequest?> TryReadSemanticReadinessBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        BoundedBodyReadResult readResult = await TryReadBoundedBodyAsync(
            request,
            MaxSemanticReadinessBodySizeBytes,
            cancellationToken).ConfigureAwait(false);
        if (readResult.TooLarge || readResult.Body is null)
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                readResult.Body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            int schemaVersionCount = 0;
            int clientGenerationCount = 0;
            int epochIdCount = 0;
            int documentPathCount = 0;
            int clientVersionCount = 0;
            int schemaVersion = 0;
            long clientGeneration = 0;
            Guid epochId = default;
            string? documentPath = null;
            long clientVersion = 0;

            foreach (JsonProperty property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "schemaVersion":
                        schemaVersionCount++;
                        if (schemaVersionCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt32(out schemaVersion))
                        {
                            return null;
                        }
                        break;

                    case "clientGeneration":
                        clientGenerationCount++;
                        if (clientGenerationCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt64(out clientGeneration)
                            || clientGeneration <= 0)
                        {
                            return null;
                        }
                        break;

                    case "epochId":
                        epochIdCount++;
                        if (epochIdCount != 1
                            || property.Value.ValueKind != JsonValueKind.String
                            || !TryGetCanonicalGuid(property.Value.GetString(), out epochId)
                            || epochId == Guid.Empty)
                        {
                            return null;
                        }
                        break;

                    case "documentPath":
                        documentPathCount++;
                        if (documentPathCount != 1
                            || property.Value.ValueKind != JsonValueKind.String)
                        {
                            return null;
                        }

                        documentPath = property.Value.GetString();
                        if (string.IsNullOrWhiteSpace(documentPath)
                            || documentPath.Length > DocumentSynchronizationLimits.MaxDocumentPathLength)
                        {
                            return null;
                        }
                        break;

                    case "clientVersion":
                        clientVersionCount++;
                        if (clientVersionCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt64(out clientVersion)
                            || clientVersion <= 0)
                        {
                            return null;
                        }
                        break;

                    default:
                        return null;
                }
            }

            if (schemaVersionCount != 1
                || clientGenerationCount != 1
                || epochIdCount != 1
                || documentPathCount != 1
                || clientVersionCount != 1
                || documentPath is null)
            {
                return null;
            }

            return new DocumentSemanticReadinessRequest(
                schemaVersion,
                clientGeneration,
                epochId,
                documentPath,
                clientVersion);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private WorkspaceRequestHeaderValidationResult ValidateWorkspaceRequestHeaders(
        HttpRequest request)
    {
        if (!TryGetSingleHeader(
                request,
                CodeServiceProtocol.ProtocolVersionHeaderName,
                MaxProtocolVersionHeaderLength,
                out string? protocolVersionValue)
            || !int.TryParse(
                protocolVersionValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int requestedProtocolVersion)
            || !TryGetSingleHeader(
                request,
                CodeServiceProtocol.SessionIdHeaderName,
                MaxSessionIdHeaderLength,
                out string? requestedSessionId)
            || !TryGetSingleHeader(
                request,
                CodeServiceProtocol.RequestIdHeaderName,
                MaxRequestIdHeaderLength,
                out string? requestIdValue)
            || !Guid.TryParseExact(requestIdValue, "D", out Guid requestId))
        {
            return WorkspaceRequestHeaderValidationResult.InvalidRequest();
        }

        string canonicalRequestId = requestId.ToString("D");

        if (requestedProtocolVersion != _protocolContext.ProtocolVersion)
        {
            return WorkspaceRequestHeaderValidationResult.VersionMismatch(canonicalRequestId);
        }

        if (!string.Equals(
                requestedSessionId,
                _protocolContext.SessionIdentity.SessionId,
                StringComparison.Ordinal))
        {
            return WorkspaceRequestHeaderValidationResult.InvalidRequest(canonicalRequestId);
        }

        return WorkspaceRequestHeaderValidationResult.Success(canonicalRequestId);
    }

    private static async Task<WorkspaceInitializeBodyParseResult> TryReadWorkspaceInitializeBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaxWorkspaceInitializeBodySizeBytes)
        {
            return WorkspaceInitializeBodyParseResult.Invalid();
        }

        byte[] readBuffer = new byte[4096];
        using MemoryStream body = new();

        while (true)
        {
            int bytesRead = await request.Body
                .ReadAsync(readBuffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead == 0)
            {
                break;
            }

            if (body.Length + bytesRead > MaxWorkspaceInitializeBodySizeBytes)
            {
                return WorkspaceInitializeBodyParseResult.Invalid();
            }

            body.Write(readBuffer, 0, bytesRead);
        }

        if (body.Length == 0)
        {
            return WorkspaceInitializeBodyParseResult.Invalid();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                body.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return WorkspaceInitializeBodyParseResult.Invalid();
            }

            int schemaVersionCount = 0;
            int projectRootCount = 0;
            int schemaVersion = 0;
            string? projectRoot = null;

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "schemaVersion", StringComparison.Ordinal))
                {
                    schemaVersionCount++;
                    if (schemaVersionCount != 1
                        || property.Value.ValueKind != JsonValueKind.Number
                        || !property.Value.TryGetInt32(out schemaVersion))
                    {
                        return WorkspaceInitializeBodyParseResult.Invalid();
                    }

                    continue;
                }

                if (string.Equals(property.Name, "projectRoot", StringComparison.Ordinal))
                {
                    projectRootCount++;
                    if (projectRootCount != 1
                        || property.Value.ValueKind != JsonValueKind.String)
                    {
                        return WorkspaceInitializeBodyParseResult.Invalid();
                    }

                    projectRoot = property.Value.GetString();
                    if (projectRoot is null
                        || projectRoot.Length > WorkspaceIdentity.MaxProjectRootLength)
                    {
                        return WorkspaceInitializeBodyParseResult.Invalid();
                    }

                    continue;
                }

                return WorkspaceInitializeBodyParseResult.Invalid();
            }

            if (schemaVersionCount != 1 || projectRootCount != 1)
            {
                return WorkspaceInitializeBodyParseResult.Invalid();
            }

            if (schemaVersion != CodeServiceProtocol.WorkspaceSchemaVersion)
            {
                return WorkspaceInitializeBodyParseResult.VersionMismatch();
            }

            return WorkspaceInitializeBodyParseResult.Success(projectRoot!);
        }
        catch (JsonException)
        {
            return WorkspaceInitializeBodyParseResult.Invalid();
        }
    }

    private static async Task<DocumentEpochBodyParseResult> TryReadDocumentEpochBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        BoundedBodyReadResult readResult = await TryReadBoundedBodyAsync(
            request,
            DocumentSynchronizationLimits.MaxEpochRequestBodySizeBytes,
            cancellationToken).ConfigureAwait(false);
        if (readResult.TooLarge)
        {
            return DocumentEpochBodyParseResult.CapacityExceeded();
        }

        if (readResult.Body is null)
        {
            return DocumentEpochBodyParseResult.Invalid();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                readResult.Body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return DocumentEpochBodyParseResult.Invalid();
            }

            int schemaVersionCount = 0;
            int clientGenerationCount = 0;
            int epochIdCount = 0;
            int openDocumentPathsCount = 0;
            int schemaVersion = 0;
            long clientGeneration = 0;
            Guid epochId = default;
            List<string>? openDocumentPaths = null;

            foreach (JsonProperty property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "schemaVersion":
                        schemaVersionCount++;
                        if (schemaVersionCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt32(out schemaVersion))
                        {
                            return DocumentEpochBodyParseResult.Invalid();
                        }
                        break;

                    case "clientGeneration":
                        clientGenerationCount++;
                        if (clientGenerationCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt64(out clientGeneration)
                            || clientGeneration <= 0)
                        {
                            return DocumentEpochBodyParseResult.Invalid();
                        }
                        break;

                    case "epochId":
                        epochIdCount++;
                        if (epochIdCount != 1
                            || property.Value.ValueKind != JsonValueKind.String
                            || !TryGetCanonicalGuid(property.Value.GetString(), out epochId))
                        {
                            return DocumentEpochBodyParseResult.Invalid();
                        }
                        break;

                    case "openDocumentPaths":
                        openDocumentPathsCount++;
                        if (openDocumentPathsCount != 1
                            || property.Value.ValueKind != JsonValueKind.Array)
                        {
                            return DocumentEpochBodyParseResult.Invalid();
                        }

                        int pathCount = property.Value.GetArrayLength();
                        if (pathCount > DocumentSynchronizationLimits.MaxTrackedOpenDocuments)
                        {
                            return DocumentEpochBodyParseResult.CapacityExceeded();
                        }

                        openDocumentPaths = new List<string>(pathCount);
                        HashSet<string> duplicateGuard = new(DocumentIdentity.PlatformPathComparer);
                        foreach (JsonElement pathElement in property.Value.EnumerateArray())
                        {
                            if (pathElement.ValueKind != JsonValueKind.String)
                            {
                                return DocumentEpochBodyParseResult.Invalid();
                            }

                            string? path = pathElement.GetString();
                            if (string.IsNullOrEmpty(path)
                                || path.Length > DocumentSynchronizationLimits.MaxDocumentPathLength
                                || !duplicateGuard.Add(path))
                            {
                                return DocumentEpochBodyParseResult.Invalid();
                            }

                            openDocumentPaths.Add(path);
                        }
                        break;

                    default:
                        return DocumentEpochBodyParseResult.Invalid();
                }
            }

            if (schemaVersionCount != 1
                || clientGenerationCount != 1
                || epochIdCount != 1
                || openDocumentPathsCount != 1
                || openDocumentPaths is null)
            {
                return DocumentEpochBodyParseResult.Invalid();
            }

            DocumentEpochRequest requestValue = new(
                clientGeneration,
                epochId,
                openDocumentPaths.AsReadOnly());

            return schemaVersion == CodeServiceProtocol.DocumentSynchronizationSchemaVersion
                ? DocumentEpochBodyParseResult.Success(requestValue)
                : DocumentEpochBodyParseResult.VersionMismatch(requestValue);
        }
        catch (JsonException)
        {
            return DocumentEpochBodyParseResult.Invalid();
        }
    }

    private static async Task<DocumentSnapshotBodyParseResult> TryReadDocumentSnapshotBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        BoundedBodyReadResult readResult = await TryReadBoundedBodyAsync(
            request,
            DocumentSynchronizationLimits.MaxSnapshotRequestBodySizeBytes,
            cancellationToken).ConfigureAwait(false);
        if (readResult.TooLarge)
        {
            return DocumentSnapshotBodyParseResult.CapacityExceeded();
        }

        if (readResult.Body is null)
        {
            return DocumentSnapshotBodyParseResult.Invalid();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                readResult.Body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return DocumentSnapshotBodyParseResult.Invalid();
            }

            int schemaVersionCount = 0;
            int clientGenerationCount = 0;
            int epochIdCount = 0;
            int documentPathCount = 0;
            int clientVersionCount = 0;
            int textCount = 0;
            int schemaVersion = 0;
            long clientGeneration = 0;
            Guid epochId = default;
            string? documentPath = null;
            long clientVersion = 0;
            string? text = null;

            foreach (JsonProperty property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "schemaVersion":
                        schemaVersionCount++;
                        if (schemaVersionCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt32(out schemaVersion))
                        {
                            return DocumentSnapshotBodyParseResult.Invalid();
                        }
                        break;

                    case "clientGeneration":
                        clientGenerationCount++;
                        if (clientGenerationCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt64(out clientGeneration)
                            || clientGeneration <= 0)
                        {
                            return DocumentSnapshotBodyParseResult.Invalid();
                        }
                        break;

                    case "epochId":
                        epochIdCount++;
                        if (epochIdCount != 1
                            || property.Value.ValueKind != JsonValueKind.String
                            || !TryGetCanonicalGuid(property.Value.GetString(), out epochId))
                        {
                            return DocumentSnapshotBodyParseResult.Invalid();
                        }
                        break;

                    case "documentPath":
                        documentPathCount++;
                        if (documentPathCount != 1
                            || property.Value.ValueKind != JsonValueKind.String)
                        {
                            return DocumentSnapshotBodyParseResult.Invalid();
                        }

                        documentPath = property.Value.GetString();
                        if (string.IsNullOrEmpty(documentPath)
                            || documentPath.Length > DocumentSynchronizationLimits.MaxDocumentPathLength)
                        {
                            return DocumentSnapshotBodyParseResult.Invalid();
                        }
                        break;

                    case "clientVersion":
                        clientVersionCount++;
                        if (clientVersionCount != 1
                            || property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt64(out clientVersion)
                            || clientVersion <= 0)
                        {
                            return DocumentSnapshotBodyParseResult.Invalid();
                        }
                        break;

                    case "text":
                        textCount++;
                        if (textCount != 1 || property.Value.ValueKind != JsonValueKind.String)
                        {
                            return DocumentSnapshotBodyParseResult.Invalid();
                        }

                        text = property.Value.GetString();
                        if (text is null)
                        {
                            return DocumentSnapshotBodyParseResult.Invalid();
                        }
                        break;

                    default:
                        return DocumentSnapshotBodyParseResult.Invalid();
                }
            }

            if (schemaVersionCount != 1
                || clientGenerationCount != 1
                || epochIdCount != 1
                || documentPathCount != 1
                || clientVersionCount != 1
                || textCount != 1
                || documentPath is null
                || text is null)
            {
                return DocumentSnapshotBodyParseResult.Invalid();
            }

            int textUtf8ByteCount = Encoding.UTF8.GetByteCount(text);
            if (textUtf8ByteCount > DocumentSynchronizationLimits.MaxDocumentTextUtf8Bytes)
            {
                return DocumentSnapshotBodyParseResult.CapacityExceeded();
            }

            DocumentSnapshotRequest requestValue = new(
                clientGeneration,
                epochId,
                documentPath,
                clientVersion,
                text,
                textUtf8ByteCount);

            return schemaVersion == CodeServiceProtocol.DocumentSynchronizationSchemaVersion
                ? DocumentSnapshotBodyParseResult.Success(requestValue)
                : DocumentSnapshotBodyParseResult.VersionMismatch(requestValue);
        }
        catch (JsonException)
        {
            return DocumentSnapshotBodyParseResult.Invalid();
        }
    }

    private static async Task<BoundedBodyReadResult> TryReadBoundedBodyAsync(
        HttpRequest request,
        int maximumBodySizeBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is long contentLength
            && contentLength > maximumBodySizeBytes)
        {
            return BoundedBodyReadResult.Oversized();
        }

        using MemoryStream body = new();
        byte[] readBuffer = new byte[8192];

        try
        {
            while (true)
            {
                int bytesRead = await request.Body
                    .ReadAsync(readBuffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (body.Length + bytesRead > maximumBodySizeBytes)
                {
                    return BoundedBodyReadResult.Oversized();
                }

                body.Write(readBuffer, 0, bytesRead);
            }
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return BoundedBodyReadResult.Oversized();
        }

        return body.Length == 0
            ? BoundedBodyReadResult.Invalid()
            : BoundedBodyReadResult.Success(body.ToArray());
    }

    private static bool TryGetCanonicalGuid(string? value, out Guid guid)
    {
        if (value is not null
            && Guid.TryParseExact(value, "D", out guid)
            && string.Equals(value, guid.ToString("D"), StringComparison.Ordinal))
        {
            return true;
        }

        guid = default;
        return false;
    }

    private static void TrySetEndpointRequestBodyLimit(HttpContext context, long maximumBodySizeBytes)
    {
        IHttpMaxRequestBodySizeFeature? bodySizeFeature =
            context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is not null && !bodySizeFeature.IsReadOnly)
        {
            bodySizeFeature.MaxRequestBodySize = maximumBodySizeBytes;
        }
    }

    private static Task CompleteDocumentEpochResponseAsync(
        HttpContext context,
        string? requestId,
        DocumentEpochOperationResult result)
    {
        WorkspacePublicationIdentity? publicationIdentity = result.WorkspacePublicationIdentity;
        DocumentEpochResponse response = new(
            CodeServiceProtocol.DocumentSynchronizationSchemaVersion,
            GetDocumentOutcome(result.Outcome),
            requestId,
            result.ClientGeneration,
            result.EpochId?.ToString("D"),
            publicationIdentity?.WorkspaceGeneration,
            publicationIdentity?.PublicationVersion,
            result.RoslynGeneration,
            result.DeclaredOpenDocumentCount,
            result.RetainedDocumentCount,
            result.ClosedDocumentCount);

        return CompleteJsonResponseAsync(
            context,
            GetDocumentStatusCode(result.Outcome),
            response);
    }

    private static Task CompleteDocumentSnapshotResponseAsync(
        HttpContext context,
        string? requestId,
        DocumentSnapshotOperationResult result)
    {
        WorkspacePublicationIdentity? publicationIdentity = result.WorkspacePublicationIdentity;
        DocumentSnapshotResponse response = new(
            CodeServiceProtocol.DocumentSynchronizationSchemaVersion,
            GetDocumentOutcome(result.Outcome),
            requestId,
            result.ClientGeneration,
            result.EpochId?.ToString("D"),
            result.DocumentPath,
            result.AcceptedClientVersion,
            publicationIdentity?.WorkspaceGeneration,
            publicationIdentity?.PublicationVersion,
            result.RoslynGeneration,
            result.RoslynDocumentVersion);

        return CompleteJsonResponseAsync(
            context,
            GetDocumentStatusCode(result.Outcome),
            response);
    }

    private static async Task CompleteBoundedCompletionResponseAsync(
        HttpContext context,
        string? requestId,
        DocumentCompletionResult result)
    {
        DocumentCompletionResponse response = CreateCompletionResponse(requestId, result);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(response);
        int statusCode = GetCompletionStatusCode(result.Outcome);

        if (json.Length > DocumentCompletionLimits.MaxResponseBodySizeBytes)
        {
            WorkspacePublicationIdentity? publication = result.WorkspacePublicationIdentity;
            response = new DocumentCompletionResponse(
                CodeServiceProtocol.CompletionSchemaVersion,
                CodeServiceProtocol.CompletionUnavailableOutcome,
                requestId,
                result.ClientGeneration,
                result.EpochId?.ToString("D"),
                result.DocumentPath,
                result.AcceptedClientVersion,
                publication?.WorkspaceGeneration,
                publication?.PublicationVersion,
                result.RoslynGeneration,
                result.RoslynDocumentVersion,
                result.RoslynOverlayRevision,
                IsIncomplete: false,
                Items: []);
            json = JsonSerializer.SerializeToUtf8Bytes(response);
            statusCode = StatusCodes.Status503ServiceUnavailable;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = json.Length;
        await context.Response.Body.WriteAsync(json, context.RequestAborted).ConfigureAwait(false);
        await context.Response.CompleteAsync().ConfigureAwait(false);
    }

    private static DocumentCompletionResponse CreateCompletionResponse(
        string? requestId,
        DocumentCompletionResult result)
    {
        WorkspacePublicationIdentity? publication = result.WorkspacePublicationIdentity;
        DocumentCompletionResponseItem[] items = result.Items
            .Select(static item => new DocumentCompletionResponseItem(
                item.Kind,
                item.DisplayText,
                item.InsertText,
                item.FilterText,
                item.SortText,
                item.Preselect,
                CompletionSemanticOriginWire.ToWireValue(item.SemanticOrigin),
                item.InheritanceDepth))
            .ToArray();

        return new DocumentCompletionResponse(
            CodeServiceProtocol.CompletionSchemaVersion,
            GetCompletionOutcome(result.Outcome),
            requestId,
            result.ClientGeneration,
            result.EpochId?.ToString("D"),
            result.DocumentPath,
            result.AcceptedClientVersion,
            publication?.WorkspaceGeneration,
            publication?.PublicationVersion,
            result.RoslynGeneration,
            result.RoslynDocumentVersion,
            result.RoslynOverlayRevision,
            result.IsIncomplete,
            items);
    }

    private static string GetCompletionOutcome(DocumentCompletionOutcome outcome) => outcome switch
    {
        DocumentCompletionOutcome.Success => CodeServiceProtocol.DocumentSuccessOutcome,
        DocumentCompletionOutcome.InvalidRequest => CodeServiceProtocol.DocumentInvalidRequestOutcome,
        DocumentCompletionOutcome.VersionMismatch => CodeServiceProtocol.DocumentVersionMismatchOutcome,
        DocumentCompletionOutcome.Busy => CodeServiceProtocol.DocumentBusyOutcome,
        DocumentCompletionOutcome.WorkspaceUnavailable => CodeServiceProtocol.DocumentWorkspaceUnavailableOutcome,
        DocumentCompletionOutcome.RoslynUnavailable => CodeServiceProtocol.DocumentRoslynUnavailableOutcome,
        DocumentCompletionOutcome.SemanticUnavailable => CodeServiceProtocol.SemanticUnavailableOutcome,
        DocumentCompletionOutcome.CompletionUnavailable => CodeServiceProtocol.CompletionUnavailableOutcome,
        DocumentCompletionOutcome.StaleEpoch => CodeServiceProtocol.DocumentStaleEpochOutcome,
        DocumentCompletionOutcome.EpochConflict => CodeServiceProtocol.DocumentEpochConflictOutcome,
        DocumentCompletionOutcome.StaleVersion => CodeServiceProtocol.DocumentStaleVersionOutcome,
        DocumentCompletionOutcome.DocumentNotSynchronized => CodeServiceProtocol.DocumentNotSynchronizedOutcome,
        DocumentCompletionOutcome.DocumentNotOpen => CodeServiceProtocol.DocumentNotOpenOutcome,
        DocumentCompletionOutcome.DocumentNotInWorkspace => CodeServiceProtocol.DocumentNotInWorkspaceOutcome,
        DocumentCompletionOutcome.Unavailable => CodeServiceProtocol.DocumentUnavailableOutcome,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown document completion outcome."),
    };

    private static int GetCompletionStatusCode(DocumentCompletionOutcome outcome) => outcome switch
    {
        DocumentCompletionOutcome.Success => StatusCodes.Status200OK,
        DocumentCompletionOutcome.InvalidRequest => StatusCodes.Status400BadRequest,
        DocumentCompletionOutcome.VersionMismatch or DocumentCompletionOutcome.StaleEpoch or DocumentCompletionOutcome.EpochConflict
            or DocumentCompletionOutcome.StaleVersion or DocumentCompletionOutcome.DocumentNotSynchronized
            or DocumentCompletionOutcome.DocumentNotOpen or DocumentCompletionOutcome.DocumentNotInWorkspace => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status503ServiceUnavailable,
    };

    private static Task CompleteSemanticReadinessResponseAsync(HttpContext context, string? requestId, DocumentSemanticReadinessResult result)
    {
        WorkspacePublicationIdentity? publication = result.WorkspacePublicationIdentity;
        DocumentSemanticReadinessResponse response = new(
            CodeServiceProtocol.SemanticReadinessSchemaVersion, GetSemanticOutcome(result.Outcome), requestId, result.ClientGeneration, result.EpochId?.ToString("D"),
            result.DocumentPath, result.AcceptedClientVersion, publication?.WorkspaceGeneration, publication?.PublicationVersion, result.RoslynGeneration, result.RoslynDocumentVersion, result.RoslynOverlayRevision);
        return CompleteJsonResponseAsync(context, GetSemanticStatusCode(result.Outcome), response);
    }

    private static string GetSemanticOutcome(DocumentSemanticReadinessOutcome outcome) => outcome switch
    {
        DocumentSemanticReadinessOutcome.Success => CodeServiceProtocol.DocumentSuccessOutcome,
        DocumentSemanticReadinessOutcome.AlreadyCurrent => CodeServiceProtocol.DocumentAlreadyCurrentOutcome,
        DocumentSemanticReadinessOutcome.InvalidRequest => CodeServiceProtocol.DocumentInvalidRequestOutcome,
        DocumentSemanticReadinessOutcome.VersionMismatch => CodeServiceProtocol.DocumentVersionMismatchOutcome,
        DocumentSemanticReadinessOutcome.Busy => CodeServiceProtocol.DocumentBusyOutcome,
        DocumentSemanticReadinessOutcome.WorkspaceUnavailable => CodeServiceProtocol.DocumentWorkspaceUnavailableOutcome,
        DocumentSemanticReadinessOutcome.RoslynUnavailable => CodeServiceProtocol.DocumentRoslynUnavailableOutcome,
        DocumentSemanticReadinessOutcome.SemanticUnavailable => CodeServiceProtocol.SemanticUnavailableOutcome,
        DocumentSemanticReadinessOutcome.StaleEpoch => CodeServiceProtocol.DocumentStaleEpochOutcome,
        DocumentSemanticReadinessOutcome.EpochConflict => CodeServiceProtocol.DocumentEpochConflictOutcome,
        DocumentSemanticReadinessOutcome.StaleVersion => CodeServiceProtocol.DocumentStaleVersionOutcome,
        DocumentSemanticReadinessOutcome.DocumentNotSynchronized => CodeServiceProtocol.DocumentNotSynchronizedOutcome,
        DocumentSemanticReadinessOutcome.DocumentNotOpen => CodeServiceProtocol.DocumentNotOpenOutcome,
        DocumentSemanticReadinessOutcome.DocumentNotInWorkspace => CodeServiceProtocol.DocumentNotInWorkspaceOutcome,
        DocumentSemanticReadinessOutcome.Unavailable => CodeServiceProtocol.DocumentUnavailableOutcome,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown semantic readiness outcome."),
    };

    private static int GetSemanticStatusCode(DocumentSemanticReadinessOutcome outcome) => outcome switch
    {
        DocumentSemanticReadinessOutcome.Success or DocumentSemanticReadinessOutcome.AlreadyCurrent => StatusCodes.Status200OK,
        DocumentSemanticReadinessOutcome.InvalidRequest => StatusCodes.Status400BadRequest,
        DocumentSemanticReadinessOutcome.VersionMismatch or DocumentSemanticReadinessOutcome.StaleEpoch or DocumentSemanticReadinessOutcome.EpochConflict
            or DocumentSemanticReadinessOutcome.StaleVersion or DocumentSemanticReadinessOutcome.DocumentNotSynchronized
            or DocumentSemanticReadinessOutcome.DocumentNotOpen or DocumentSemanticReadinessOutcome.DocumentNotInWorkspace => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status503ServiceUnavailable,
    };

    private static string GetDocumentOutcome(DocumentSynchronizationOutcome outcome)
        => outcome switch
        {
            DocumentSynchronizationOutcome.Success => CodeServiceProtocol.DocumentSuccessOutcome,
            DocumentSynchronizationOutcome.AlreadyCurrent => CodeServiceProtocol.DocumentAlreadyCurrentOutcome,
            DocumentSynchronizationOutcome.InvalidRequest => CodeServiceProtocol.DocumentInvalidRequestOutcome,
            DocumentSynchronizationOutcome.VersionMismatch => CodeServiceProtocol.DocumentVersionMismatchOutcome,
            DocumentSynchronizationOutcome.Busy => CodeServiceProtocol.DocumentBusyOutcome,
            DocumentSynchronizationOutcome.WorkspaceUnavailable => CodeServiceProtocol.DocumentWorkspaceUnavailableOutcome,
            DocumentSynchronizationOutcome.RoslynUnavailable => CodeServiceProtocol.DocumentRoslynUnavailableOutcome,
            DocumentSynchronizationOutcome.StaleEpoch => CodeServiceProtocol.DocumentStaleEpochOutcome,
            DocumentSynchronizationOutcome.EpochConflict => CodeServiceProtocol.DocumentEpochConflictOutcome,
            DocumentSynchronizationOutcome.StaleVersion => CodeServiceProtocol.DocumentStaleVersionOutcome,
            DocumentSynchronizationOutcome.VersionConflict => CodeServiceProtocol.DocumentVersionConflictOutcome,
            DocumentSynchronizationOutcome.DocumentNotOpen => CodeServiceProtocol.DocumentNotOpenOutcome,
            DocumentSynchronizationOutcome.DocumentNotInWorkspace => CodeServiceProtocol.DocumentNotInWorkspaceOutcome,
            DocumentSynchronizationOutcome.CapacityExceeded => CodeServiceProtocol.DocumentCapacityExceededOutcome,
            DocumentSynchronizationOutcome.Unavailable => CodeServiceProtocol.DocumentUnavailableOutcome,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown document synchronization outcome."),
        };

    private static int GetDocumentStatusCode(DocumentSynchronizationOutcome outcome)
        => outcome switch
        {
            DocumentSynchronizationOutcome.Success => StatusCodes.Status200OK,
            DocumentSynchronizationOutcome.AlreadyCurrent => StatusCodes.Status200OK,
            DocumentSynchronizationOutcome.InvalidRequest => StatusCodes.Status400BadRequest,
            DocumentSynchronizationOutcome.VersionMismatch => StatusCodes.Status409Conflict,
            DocumentSynchronizationOutcome.StaleEpoch => StatusCodes.Status409Conflict,
            DocumentSynchronizationOutcome.EpochConflict => StatusCodes.Status409Conflict,
            DocumentSynchronizationOutcome.StaleVersion => StatusCodes.Status409Conflict,
            DocumentSynchronizationOutcome.VersionConflict => StatusCodes.Status409Conflict,
            DocumentSynchronizationOutcome.DocumentNotOpen => StatusCodes.Status409Conflict,
            DocumentSynchronizationOutcome.DocumentNotInWorkspace => StatusCodes.Status409Conflict,
            DocumentSynchronizationOutcome.CapacityExceeded => StatusCodes.Status413PayloadTooLarge,
            DocumentSynchronizationOutcome.Busy => StatusCodes.Status503ServiceUnavailable,
            DocumentSynchronizationOutcome.WorkspaceUnavailable => StatusCodes.Status503ServiceUnavailable,
            DocumentSynchronizationOutcome.RoslynUnavailable => StatusCodes.Status503ServiceUnavailable,
            DocumentSynchronizationOutcome.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown document synchronization outcome."),
        };

    private async Task CompleteWorkspaceHeaderFailureAsync(
        HttpContext context,
        WorkspaceRequestHeaderValidationResult validation)
    {
        WorkspaceStatusSnapshot status = _workspaceHost.GetStatusSnapshot();

        if (validation.FailureKind == WorkspaceRequestHeaderFailureKind.VersionMismatch)
        {
            await CompleteWorkspaceFailureAsync(
                context,
                StatusCodes.Status409Conflict,
                CodeServiceProtocol.WorkspaceVersionMismatchOutcome,
                validation.RequestId,
                status).ConfigureAwait(false);
            return;
        }

        await CompleteWorkspaceFailureAsync(
            context,
            StatusCodes.Status400BadRequest,
            CodeServiceProtocol.WorkspaceInvalidRequestOutcome,
            validation.RequestId,
            status).ConfigureAwait(false);
    }

    private static Task CompleteWorkspaceFailureAsync(
        HttpContext context,
        int statusCode,
        string outcome,
        string? requestId,
        WorkspaceStatusSnapshot status)
    {
        WorkspaceFailureResponse response = new(
            CodeServiceProtocol.WorkspaceSchemaVersion,
            outcome,
            requestId,
            status.State.ToString(),
            status.ProjectRoot,
            status.SourceFileCount,
            status.ProjectFileCount,
            status.SolutionFileCount,
            status.FaultKind);

        return CompleteJsonResponseAsync(context, statusCode, response);
    }

    private static string GetWorkspaceInitializationOutcome(WorkspaceInitializationOutcome outcome)
        => outcome switch
        {
            WorkspaceInitializationOutcome.Success => CodeServiceProtocol.WorkspaceSuccessOutcome,
            WorkspaceInitializationOutcome.InvalidRequest => CodeServiceProtocol.WorkspaceInvalidRequestOutcome,
            WorkspaceInitializationOutcome.Busy => CodeServiceProtocol.WorkspaceBusyOutcome,
            WorkspaceInitializationOutcome.WorkspaceMismatch => CodeServiceProtocol.WorkspaceMismatchOutcome,
            WorkspaceInitializationOutcome.Unavailable => CodeServiceProtocol.WorkspaceUnavailableOutcome,
            WorkspaceInitializationOutcome.Faulted => CodeServiceProtocol.WorkspaceFaultedOutcome,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown workspace outcome."),
        };

    private static int GetWorkspaceInitializationStatusCode(WorkspaceInitializationOutcome outcome)
        => outcome switch
        {
            WorkspaceInitializationOutcome.Success => StatusCodes.Status200OK,
            WorkspaceInitializationOutcome.InvalidRequest => StatusCodes.Status400BadRequest,
            WorkspaceInitializationOutcome.Busy => StatusCodes.Status503ServiceUnavailable,
            WorkspaceInitializationOutcome.WorkspaceMismatch => StatusCodes.Status409Conflict,
            WorkspaceInitializationOutcome.Unavailable => StatusCodes.Status503ServiceUnavailable,
            WorkspaceInitializationOutcome.Faulted => StatusCodes.Status500InternalServerError,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "unknown workspace outcome."),
        };

    private bool TryAuthenticate(HttpRequest request)
    {
        StringValues authorizationValues = request.Headers[HeaderNames.Authorization];
        if (authorizationValues.Count != 1)
        {
            return false;
        }

        string? authorization = authorizationValues[0];
        int expectedLength = BearerPrefix.Length + SessionCredentials.AuthenticationTokenBase64Length;

        if (authorization is null
            || authorization.Length != expectedLength
            || !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> encodedToken = authorization.AsSpan(BearerPrefix.Length);
        Span<byte> decodedToken = stackalloc byte[SessionCredentials.AuthenticationTokenByteCount];

        try
        {
            if (!Convert.TryFromBase64Chars(encodedToken, decodedToken, out int bytesWritten)
                || bytesWritten != SessionCredentials.AuthenticationTokenByteCount)
            {
                return false;
            }

            return _protocolContext.SessionCredentials.Matches(decodedToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decodedToken);
        }
    }

    private static bool HasUnexpectedHandshakeBody(HttpRequest request)
    {
        if (request.ContentLength is > 0)
        {
            return true;
        }

        StringValues transferEncodingValues = request.Headers[HeaderNames.TransferEncoding];
        return transferEncodingValues.Count != 0;
    }

    private static bool TryGetSingleHeader(
        HttpRequest request,
        string headerName,
        int maxLength,
        out string? value)
    {
        StringValues values = request.Headers[headerName];
        if (values.Count != 1)
        {
            value = null;
            return false;
        }

        value = values[0];
        return !string.IsNullOrEmpty(value)
            && value.Length <= maxLength
            && value.IndexOf(',') < 0;
    }

    private static Task CompleteHandshakeFailureAsync(
        HttpContext context,
        int statusCode,
        string outcome,
        string? requestId)
    {
        HandshakeFailureResponse response = new(
            CodeServiceProtocol.HandshakeSchemaVersion,
            outcome,
            requestId);

        return CompleteJsonResponseAsync(context, statusCode, response);
    }

    private static async Task CompleteJsonResponseAsync<T>(
        HttpContext context,
        int statusCode,
        T response)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(response);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = json.Length;

        await context.Response.Body
            .WriteAsync(json, context.RequestAborted)
            .ConfigureAwait(false);
        await context.Response.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task CompleteZeroBodyResponseAsync(
        HttpContext context,
        int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentLength = 0;
        await context.Response.CompleteAsync().ConfigureAwait(false);
    }

    private static void AbortRequestNoThrow(HttpContext context)
    {
        try
        {
            context.Abort();
        }
        catch (Exception)
        {
            // Request/connection teardown is best-effort and never faults the transport host.
        }
    }

    private static LocalTransportEndpoint ResolveBoundEndpoint(WebApplication application)
    {
        IServerAddressesFeature? addressesFeature =
            ((IApplicationBuilder)application).ServerFeatures.Get<IServerAddressesFeature>();

        if (addressesFeature is null || addressesFeature.Addresses.Count != 1)
        {
            throw new InvalidOperationException(
                "local transport did not expose exactly one bound server address.");
        }

        string address = addressesFeature.Addresses.Single();
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException(
                "local transport exposed an invalid bound server address.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.Equals(uri.Host, IPAddress.Loopback.ToString(), StringComparison.Ordinal)
            || uri.Port <= 0)
        {
            throw new InvalidOperationException(
                "local transport bound endpoint was not verified as http://127.0.0.1:<dynamic-port>.");
        }

        return new LocalTransportEndpoint(
            Uri.UriSchemeHttp,
            IPAddress.Loopback.ToString(),
            uri.Port);
    }

    private async Task CleanupFailedStartupAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original startup failure remains authoritative.
        }
    }
}


internal enum WorkspaceRequestHeaderFailureKind
{
    None,
    InvalidRequest,
    VersionMismatch,
}

internal readonly record struct WorkspaceRequestHeaderValidationResult(
    WorkspaceRequestHeaderFailureKind FailureKind,
    string? RequestId)
{
    public bool IsSuccess => FailureKind == WorkspaceRequestHeaderFailureKind.None;

    public static WorkspaceRequestHeaderValidationResult Success(string requestId)
        => new(WorkspaceRequestHeaderFailureKind.None, requestId);

    public static WorkspaceRequestHeaderValidationResult InvalidRequest(string? requestId = null)
        => new(WorkspaceRequestHeaderFailureKind.InvalidRequest, requestId);

    public static WorkspaceRequestHeaderValidationResult VersionMismatch(string requestId)
        => new(WorkspaceRequestHeaderFailureKind.VersionMismatch, requestId);
}

internal enum WorkspaceInitializeBodyFailureKind
{
    None,
    InvalidRequest,
    VersionMismatch,
}

internal readonly record struct WorkspaceInitializeBodyParseResult(
    WorkspaceInitializeBodyFailureKind FailureKind,
    string? ProjectRoot)
{
    public bool IsSuccess => FailureKind == WorkspaceInitializeBodyFailureKind.None;

    public static WorkspaceInitializeBodyParseResult Success(string projectRoot)
        => new(WorkspaceInitializeBodyFailureKind.None, projectRoot);

    public static WorkspaceInitializeBodyParseResult Invalid()
        => new(WorkspaceInitializeBodyFailureKind.InvalidRequest, null);

    public static WorkspaceInitializeBodyParseResult VersionMismatch()
        => new(WorkspaceInitializeBodyFailureKind.VersionMismatch, null);
}

internal enum DocumentRequestBodyFailureKind
{
    None,
    InvalidRequest,
    VersionMismatch,
    CapacityExceeded,
}

internal readonly record struct DocumentEpochBodyParseResult(
    DocumentRequestBodyFailureKind FailureKind,
    DocumentEpochRequest? Request)
{
    public bool IsSuccess => FailureKind == DocumentRequestBodyFailureKind.None && Request is not null;

    public static DocumentEpochBodyParseResult Success(DocumentEpochRequest request)
        => new(DocumentRequestBodyFailureKind.None, request);

    public static DocumentEpochBodyParseResult Invalid()
        => new(DocumentRequestBodyFailureKind.InvalidRequest, null);

    public static DocumentEpochBodyParseResult VersionMismatch(DocumentEpochRequest request)
        => new(DocumentRequestBodyFailureKind.VersionMismatch, request);

    public static DocumentEpochBodyParseResult CapacityExceeded()
        => new(DocumentRequestBodyFailureKind.CapacityExceeded, null);
}

internal readonly record struct DocumentSnapshotBodyParseResult(
    DocumentRequestBodyFailureKind FailureKind,
    DocumentSnapshotRequest? Request)
{
    public bool IsSuccess => FailureKind == DocumentRequestBodyFailureKind.None && Request is not null;

    public static DocumentSnapshotBodyParseResult Success(DocumentSnapshotRequest request)
        => new(DocumentRequestBodyFailureKind.None, request);

    public static DocumentSnapshotBodyParseResult Invalid()
        => new(DocumentRequestBodyFailureKind.InvalidRequest, null);

    public static DocumentSnapshotBodyParseResult VersionMismatch(DocumentSnapshotRequest request)
        => new(DocumentRequestBodyFailureKind.VersionMismatch, request);

    public static DocumentSnapshotBodyParseResult CapacityExceeded()
        => new(DocumentRequestBodyFailureKind.CapacityExceeded, null);
}

internal readonly record struct CompletionBodyParseResult(
    DocumentRequestBodyFailureKind FailureKind,
    DocumentCompletionRequest? Request)
{
    public bool IsSuccess => FailureKind == DocumentRequestBodyFailureKind.None && Request is not null;

    public static CompletionBodyParseResult Success(DocumentCompletionRequest request)
        => new(DocumentRequestBodyFailureKind.None, request);

    public static CompletionBodyParseResult Invalid()
        => new(DocumentRequestBodyFailureKind.InvalidRequest, null);

    public static CompletionBodyParseResult VersionMismatch(DocumentCompletionRequest request)
        => new(DocumentRequestBodyFailureKind.VersionMismatch, request);
}

internal readonly record struct BoundedBodyReadResult(
    byte[]? Body,
    bool TooLarge)
{
    public static BoundedBodyReadResult Success(byte[] body)
        => new(body, false);

    public static BoundedBodyReadResult Invalid()
        => new(null, false);

    public static BoundedBodyReadResult Oversized()
        => new(null, true);
}
