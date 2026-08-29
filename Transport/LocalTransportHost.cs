using System.Globalization;
using System.Net;
using System.Security.Cryptography;
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
    private const long MaxRequestBodySizeBytes = 64 * 1024;
    private const int MaxWorkspaceInitializeBodySizeBytes = 8 * 1024;
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
        WorkspaceHost workspaceHost)
    {
        _protocolContext = protocolContext ?? throw new ArgumentNullException(nameof(protocolContext));
        _workspaceHost = workspaceHost ?? throw new ArgumentNullException(nameof(workspaceHost));
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
