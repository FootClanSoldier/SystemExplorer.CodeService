namespace SystemExplorer.CodeService;

internal sealed class CodeServiceHost : IAsyncDisposable
{
    private readonly GodotProcessLifetime _godotProcessLifetime;
    private readonly SessionCoordinator _sessionCoordinator;
    private readonly DiagnosticLogging _diagnosticLogging;
    private readonly WorkloadCoordinator _workloadCoordinator;
    private readonly RoslynLanguageServerHost _roslynLanguageServerHost;
    private readonly DocumentSynchronizationHost _documentSynchronizationHost;
    private readonly WorkspaceHost _workspaceHost;
    private readonly DocumentSemanticReadinessHost _documentSemanticReadinessHost;
    private readonly DocumentCompletionHost _documentCompletionHost;
    private readonly LocalTransportHost _localTransportHost;
    private readonly BootstrapReadinessWriter _bootstrapReadinessWriter;
    private readonly object _shutdownSync = new();
    private Task? _shutdownTask;
    private int _runState;

    private CodeServiceHost(
        GodotProcessLifetime godotProcessLifetime,
        SessionCoordinator sessionCoordinator,
        DiagnosticLogging diagnosticLogging,
        WorkloadCoordinator workloadCoordinator,
        RoslynLanguageServerHost roslynLanguageServerHost,
        DocumentSynchronizationHost documentSynchronizationHost,
        WorkspaceHost workspaceHost,
        DocumentSemanticReadinessHost documentSemanticReadinessHost,
        DocumentCompletionHost documentCompletionHost,
        LocalTransportHost localTransportHost,
        BootstrapReadinessWriter bootstrapReadinessWriter)
    {
        _godotProcessLifetime = godotProcessLifetime;
        _sessionCoordinator = sessionCoordinator;
        _diagnosticLogging = diagnosticLogging;
        _workloadCoordinator = workloadCoordinator;
        _roslynLanguageServerHost = roslynLanguageServerHost;
        _documentSynchronizationHost = documentSynchronizationHost;
        _workspaceHost = workspaceHost;
        _documentSemanticReadinessHost = documentSemanticReadinessHost;
        _documentCompletionHost = documentCompletionHost;
        _localTransportHost = localTransportHost;
        _bootstrapReadinessWriter = bootstrapReadinessWriter;
    }

    public static async Task<CodeServiceHostCreationResult> TryCreateAsync(
        CodeServiceStartupOptions startupOptions)
    {
        GodotProcessLifetimeAttachResult lifetimeResult =
            GodotProcessLifetime.TryAttach(startupOptions.GodotOwnerIdentity);

        if (!lifetimeResult.IsSuccess)
        {
            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.OwnerValidationFailure,
                lifetimeResult.ErrorMessage!);
        }

        GodotProcessLifetime godotProcessLifetime = lifetimeResult.Lifetime!;

        ServiceProcessIdentityCaptureResult serviceIdentityResult =
            ServiceProcessIdentity.TryCaptureCurrent();
        if (!serviceIdentityResult.IsSuccess)
        {
            try
            {
                godotProcessLifetime.Dispose();
            }
            catch
            {
                // The controlled service-identity startup failure remains authoritative.
            }

            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.SessionStartupFailure,
                serviceIdentityResult.ErrorMessage!);
        }

        ServiceProcessIdentity serviceProcessIdentity = serviceIdentityResult.Identity!.Value;
        DiagnosticLoggingCreationResult loggingResult = DiagnosticLogging.Create(
            startupOptions.DiagnosticLoggingEnabled,
            startupOptions.GodotOwnerIdentity,
            serviceProcessIdentity);
        DiagnosticLogging diagnosticLogging = loggingResult.Logging;

        diagnosticLogging.WriteEvent("owner_validated");

        RoslynLanguageServerRuntimeResolutionResult runtimeResolution =
            RoslynLanguageServerRuntimeResolver.Resolve(startupOptions.RoslynRuntime);

        if (runtimeResolution.Status == RoslynLanguageServerRuntimeResolutionStatus.Failure)
        {
            if (diagnosticLogging.IsEnabled)
            {
                diagnosticLogging.WriteEvent(
                    "roslyn_runtime_provisioning_fault",
                    new
                    {
                        platform = RoslynLanguageServerRuntimeResolver.GetCurrentPlatformName(),
                        processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                        distributionId = RoslynLanguageServerRuntime.DistributionId,
                        reason = runtimeResolution.ErrorMessage,
                    });
            }
            else
            {
                diagnosticLogging.WriteEvent("roslyn_runtime_provisioning_fault");
            }

            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);

            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();

            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.RoslynRuntimeProvisioningFailure,
                runtimeResolution.ErrorMessage!,
                diagnosticLogPath,
                loggingResult.WarningMessage);
        }

        if (runtimeResolution.Status == RoslynLanguageServerRuntimeResolutionStatus.UnsupportedPlatform)
        {
            if (diagnosticLogging.IsEnabled)
            {
                diagnosticLogging.WriteEvent(
                    "roslyn_runtime_unsupported_platform",
                    new
                    {
                        platform = RoslynLanguageServerRuntimeResolver.GetCurrentPlatformName(),
                        processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                        packagedRuntimeSupport = "win-x64",
                    });
            }
            else
            {
                diagnosticLogging.WriteEvent("roslyn_runtime_unsupported_platform");
            }
        }

        RoslynLanguageServerRuntime? resolvedRoslynRuntime = runtimeResolution.Runtime;
        if (resolvedRoslynRuntime is RoslynLanguageServerRuntime validatedRoslynRuntime)
        {
            if (diagnosticLogging.IsEnabled)
            {
                diagnosticLogging.WriteEvent(
                    "roslyn_runtime_validated",
                    new
                    {
                        runtimeSource = validatedRoslynRuntime.RuntimeSource.ToString(),
                        distributionId = validatedRoslynRuntime.RuntimeDistributionId,
                        platform = RoslynLanguageServerRuntimeResolver.GetCurrentPlatformName(),
                        processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                        languageServerDllSha256 = validatedRoslynRuntime.VerifiedLanguageServerDllSha256,
                        featuresDllSha256 = validatedRoslynRuntime.VerifiedFeaturesDllSha256,
                        languageServerProtocolDllSha256 = validatedRoslynRuntime.VerifiedLanguageServerProtocolDllSha256,
                        upstreamCommit = validatedRoslynRuntime.VerifiedUpstreamCommit,
                        semanticReusePatchSha256 = validatedRoslynRuntime.VerifiedSemanticReusePatchSha256,
                        semanticReuseSourceCommit = validatedRoslynRuntime.VerifiedSemanticReuseSourceCommit,
                        completionSemanticOriginPatchSha256 = validatedRoslynRuntime.VerifiedCompletionSemanticOriginPatchSha256,
                        provenance = "systemexplorer-private-patched-build",
                    });
            }
            else
            {
                diagnosticLogging.WriteEvent("roslyn_runtime_validated");
            }
        }

        SessionCoordinatorCreationResult sessionResult =
            SessionCoordinator.TryCreate(startupOptions.GodotOwnerIdentity);

        if (!sessionResult.IsSuccess)
        {
            if (sessionResult.LaunchAuthorityWasAcquired)
            {
                diagnosticLogging.WriteEvent("launch_authority_acquired");
                diagnosticLogging.WriteEvent("session_start_failed");
            }
            else
            {
                diagnosticLogging.WriteEvent("launch_authority_not_acquired");
            }

            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);
            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();

            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.SessionStartupFailure,
                sessionResult.ErrorMessage!,
                diagnosticLogPath,
                loggingResult.WarningMessage);
        }

        SessionCoordinator sessionCoordinator = sessionResult.Coordinator!;
        diagnosticLogging.WriteEvent("launch_authority_acquired");

        try
        {
            diagnosticLogging.BindSession(sessionCoordinator.Identity);
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("session_start_failed", exception);
            RetireSessionNoThrow(sessionCoordinator, diagnosticLogging, writeLifecycleEvents: false);
            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);
            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();

            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.SessionStartupFailure,
                $"session diagnostic binding failed: {ToSingleLine(exception.Message)}",
                diagnosticLogPath,
                loggingResult.WarningMessage);
        }

        diagnosticLogging.WriteEvent("session_created");

        diagnosticLogging.WriteEvent("launch_authority_scavenge_started");
        try
        {
            StaleLaunchAuthorityScavenger.Scavenge(startupOptions.GodotOwnerIdentity);
            diagnosticLogging.WriteEvent("launch_authority_scavenge_completed");
        }
        catch (Exception exception) when (IsNonFatalMaintenanceFailure(exception))
        {
            diagnosticLogging.WriteFault("launch_authority_scavenge_fault", exception);
        }

        CodeServiceVersionResolutionResult versionResult =
            CodeServiceProtocol.TryResolveServiceVersion();
        if (!versionResult.IsSuccess)
        {
            InvalidOperationException versionFailure = new(versionResult.ErrorMessage);
            diagnosticLogging.WriteFault("session_start_failed", versionFailure);
            diagnosticLogging.WriteEvent("service_stopping");
            RetireSessionNoThrow(sessionCoordinator, diagnosticLogging, writeLifecycleEvents: true);
            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);
            diagnosticLogging.WriteEvent("service_stopped");

            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();

            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.SessionStartupFailure,
                versionResult.ErrorMessage!,
                diagnosticLogPath,
                loggingResult.WarningMessage);
        }

        SessionProtocolContext protocolContext;
        try
        {
            protocolContext = new SessionProtocolContext(
                CodeServiceProtocol.ProtocolVersion,
                versionResult.ServiceVersion!,
                sessionCoordinator.Identity,
                startupOptions.GodotOwnerIdentity,
                serviceProcessIdentity,
                sessionCoordinator.Credentials);
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("session_start_failed", exception);
            diagnosticLogging.WriteEvent("service_stopping");
            RetireSessionNoThrow(sessionCoordinator, diagnosticLogging, writeLifecycleEvents: true);
            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);
            diagnosticLogging.WriteEvent("service_stopped");

            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();

            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.SessionStartupFailure,
                $"session protocol initialization failed: {ToSingleLine(exception.Message)}",
                diagnosticLogPath,
                loggingResult.WarningMessage);
        }

        diagnosticLogging.WriteEvent("service_started");

        WorkloadCoordinator workloadCoordinator;
        try
        {
            workloadCoordinator = new WorkloadCoordinator();
            diagnosticLogging.WriteEvent("workload_coordinator_started");
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("workload_fault", exception);
            diagnosticLogging.WriteFault("service_fault", exception);
            diagnosticLogging.WriteEvent("service_stopping");
            RetireSessionNoThrow(sessionCoordinator, diagnosticLogging, writeLifecycleEvents: true);
            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);
            diagnosticLogging.WriteEvent("service_stopped");

            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();

            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.SessionStartupFailure,
                $"workload coordinator initialization failed: {ToSingleLine(exception.Message)}",
                diagnosticLogPath,
                loggingResult.WarningMessage);
        }

        RoslynLanguageServerHost roslynLanguageServerHost = new(
            resolvedRoslynRuntime,
            versionResult.ServiceVersion!,
            diagnosticLogging);

        DocumentSynchronizationHost documentSynchronizationHost;
        try
        {
            documentSynchronizationHost = new DocumentSynchronizationHost(
                workloadCoordinator,
                roslynLanguageServerHost,
                diagnosticLogging);
            diagnosticLogging.WriteEvent("document_sync_host_started");
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("service_fault", exception);
            diagnosticLogging.WriteEvent("service_stopping");

            BeginWorkloadShutdownNoThrow(workloadCoordinator, diagnosticLogging);
            await RetireWorkloadNoThrowAsync(workloadCoordinator, diagnosticLogging)
                .ConfigureAwait(false);
            await RetireRoslynNoThrowAsync(roslynLanguageServerHost, diagnosticLogging)
                .ConfigureAwait(false);
            RetireSessionNoThrow(sessionCoordinator, diagnosticLogging, writeLifecycleEvents: true);
            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);
            diagnosticLogging.WriteEvent("service_stopped");

            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();

            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.SessionStartupFailure,
                $"document synchronization host initialization failed: {ToSingleLine(exception.Message)}",
                diagnosticLogPath,
                loggingResult.WarningMessage);
        }

        WorkspaceHost workspaceHost;
        try
        {
            workspaceHost = new WorkspaceHost(
                workloadCoordinator,
                roslynLanguageServerHost,
                documentSynchronizationHost,
                diagnosticLogging);
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("service_fault", exception);
            diagnosticLogging.WriteEvent("service_stopping");

            BeginDocumentShutdownNoThrow(documentSynchronizationHost, diagnosticLogging);
            BeginWorkloadShutdownNoThrow(workloadCoordinator, diagnosticLogging);
            await RetireWorkloadNoThrowAsync(workloadCoordinator, diagnosticLogging)
                .ConfigureAwait(false);
            await RetireRoslynNoThrowAsync(roslynLanguageServerHost, diagnosticLogging)
                .ConfigureAwait(false);
            DisposeDocumentNoThrow(documentSynchronizationHost, diagnosticLogging);
            RetireSessionNoThrow(sessionCoordinator, diagnosticLogging, writeLifecycleEvents: true);
            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);
            diagnosticLogging.WriteEvent("service_stopped");

            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();

            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.SessionStartupFailure,
                $"workspace host initialization failed: {ToSingleLine(exception.Message)}",
                diagnosticLogPath,
                loggingResult.WarningMessage);
        }

        DocumentSemanticReadinessHost documentSemanticReadinessHost;
        try
        {
            documentSemanticReadinessHost = new DocumentSemanticReadinessHost(
                workloadCoordinator, workspaceHost, documentSynchronizationHost, roslynLanguageServerHost, diagnosticLogging);
            diagnosticLogging.WriteEvent("semantic_readiness_host_started");
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("service_fault", exception);
            diagnosticLogging.WriteEvent("service_stopping");
            BeginDocumentShutdownNoThrow(documentSynchronizationHost, diagnosticLogging);
            BeginWorkspaceShutdownNoThrow(workspaceHost, diagnosticLogging);
            BeginWorkloadShutdownNoThrow(workloadCoordinator, diagnosticLogging);
            await RetireWorkloadNoThrowAsync(workloadCoordinator, diagnosticLogging).ConfigureAwait(false);
            await RetireRoslynNoThrowAsync(roslynLanguageServerHost, diagnosticLogging).ConfigureAwait(false);
            DisposeDocumentNoThrow(documentSynchronizationHost, diagnosticLogging);
            DisposeWorkspaceNoThrow(workspaceHost, diagnosticLogging);
            RetireSessionNoThrow(sessionCoordinator, diagnosticLogging, writeLifecycleEvents: true);
            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);
            diagnosticLogging.WriteEvent("service_stopped");
            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();
            return CodeServiceHostCreationResult.Failure(CodeServiceHostCreationFailureKind.SessionStartupFailure, $"semantic readiness host initialization failed: {ToSingleLine(exception.Message)}", diagnosticLogPath, loggingResult.WarningMessage);
        }

        DocumentCompletionHost documentCompletionHost;
        try
        {
            documentCompletionHost = new DocumentCompletionHost(
                workloadCoordinator,
                workspaceHost,
                documentSynchronizationHost,
                documentSemanticReadinessHost,
                roslynLanguageServerHost,
                diagnosticLogging);
            diagnosticLogging.WriteEvent("completion_host_started");
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("service_fault", exception);
            diagnosticLogging.WriteEvent("service_stopping");
            BeginSemanticShutdownNoThrow(documentSemanticReadinessHost, diagnosticLogging);
            BeginDocumentShutdownNoThrow(documentSynchronizationHost, diagnosticLogging);
            BeginWorkspaceShutdownNoThrow(workspaceHost, diagnosticLogging);
            BeginWorkloadShutdownNoThrow(workloadCoordinator, diagnosticLogging);
            await RetireWorkloadNoThrowAsync(workloadCoordinator, diagnosticLogging).ConfigureAwait(false);
            await RetireRoslynNoThrowAsync(roslynLanguageServerHost, diagnosticLogging).ConfigureAwait(false);
            DisposeSemanticNoThrow(documentSemanticReadinessHost, diagnosticLogging);
            DisposeDocumentNoThrow(documentSynchronizationHost, diagnosticLogging);
            DisposeWorkspaceNoThrow(workspaceHost, diagnosticLogging);
            RetireSessionNoThrow(sessionCoordinator, diagnosticLogging, writeLifecycleEvents: true);
            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);
            diagnosticLogging.WriteEvent("service_stopped");
            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();
            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.SessionStartupFailure,
                $"completion host initialization failed: {ToSingleLine(exception.Message)}",
                diagnosticLogPath,
                loggingResult.WarningMessage);
        }

        LocalTransportHost localTransportHost;
        try
        {
            localTransportHost = new LocalTransportHost(
                protocolContext,
                workspaceHost,
                documentSynchronizationHost,
                documentSemanticReadinessHost,
                documentCompletionHost);
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("transport_start_failed", exception);
            diagnosticLogging.WriteEvent("service_stopping");

            BeginCompletionShutdownNoThrow(documentCompletionHost, diagnosticLogging);
            BeginSemanticShutdownNoThrow(documentSemanticReadinessHost, diagnosticLogging);
            BeginDocumentShutdownNoThrow(documentSynchronizationHost, diagnosticLogging);
            BeginWorkspaceShutdownNoThrow(workspaceHost, diagnosticLogging);
            BeginWorkloadShutdownNoThrow(workloadCoordinator, diagnosticLogging);
            await RetireWorkloadNoThrowAsync(workloadCoordinator, diagnosticLogging)
                .ConfigureAwait(false);
            await RetireRoslynNoThrowAsync(roslynLanguageServerHost, diagnosticLogging)
                .ConfigureAwait(false);
            DisposeCompletionNoThrow(documentCompletionHost, diagnosticLogging);
            DisposeSemanticNoThrow(documentSemanticReadinessHost, diagnosticLogging);
            DisposeDocumentNoThrow(documentSynchronizationHost, diagnosticLogging);
            DisposeWorkspaceNoThrow(workspaceHost, diagnosticLogging);
            RetireSessionNoThrow(sessionCoordinator, diagnosticLogging, writeLifecycleEvents: true);
            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);
            diagnosticLogging.WriteEvent("service_stopped");

            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();

            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.TransportStartupFailure,
                $"local transport startup failed: {ToSingleLine(exception.Message)}",
                diagnosticLogPath,
                loggingResult.WarningMessage);
        }

        diagnosticLogging.WriteEvent("transport_starting");

        LocalTransportEndpoint endpoint;

        try
        {
            await localTransportHost.StartAsync().ConfigureAwait(false);

            endpoint = localTransportHost.BoundEndpoint
                ?? throw new InvalidOperationException(
                    "local transport startup completed without a verified bound endpoint.");

            diagnosticLogging.WriteTransportEvent("transport_bound", endpoint);
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("transport_start_failed", exception);
            diagnosticLogging.WriteEvent("service_stopping");

            BeginCompletionShutdownNoThrow(documentCompletionHost, diagnosticLogging);
            BeginSemanticShutdownNoThrow(documentSemanticReadinessHost, diagnosticLogging);
            BeginDocumentShutdownNoThrow(documentSynchronizationHost, diagnosticLogging);
            BeginWorkspaceShutdownNoThrow(workspaceHost, diagnosticLogging);
            BeginWorkloadShutdownNoThrow(workloadCoordinator, diagnosticLogging);
            await DisposeTransportNoThrowAsync(localTransportHost, diagnosticLogging)
                .ConfigureAwait(false);
            await RetireWorkloadNoThrowAsync(workloadCoordinator, diagnosticLogging)
                .ConfigureAwait(false);
            await RetireRoslynNoThrowAsync(roslynLanguageServerHost, diagnosticLogging)
                .ConfigureAwait(false);
            DisposeCompletionNoThrow(documentCompletionHost, diagnosticLogging);
            DisposeSemanticNoThrow(documentSemanticReadinessHost, diagnosticLogging);
            DisposeDocumentNoThrow(documentSynchronizationHost, diagnosticLogging);
            DisposeWorkspaceNoThrow(workspaceHost, diagnosticLogging);
            RetireSessionNoThrow(sessionCoordinator, diagnosticLogging, writeLifecycleEvents: true);
            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);
            diagnosticLogging.WriteEvent("service_stopped");

            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();

            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.TransportStartupFailure,
                $"local transport startup failed: {ToSingleLine(exception.Message)}",
                diagnosticLogPath,
                loggingResult.WarningMessage);
        }

        diagnosticLogging.WriteEvent("descriptor_publishing");

        try
        {
            SessionDescriptorPublicationResult publicationResult =
                sessionCoordinator.PublishDescriptor(
                    protocolContext.ProtocolVersion,
                    protocolContext.ServiceVersion,
                    serviceProcessIdentity,
                    endpoint);

            if (!publicationResult.IsSuccess)
            {
                throw new InvalidOperationException(publicationResult.ErrorMessage!);
            }

            diagnosticLogging.WriteEvent("descriptor_published");

            localTransportHost.EnableControlPlane();
            diagnosticLogging.WriteEvent("control_plane_ready");

            string descriptorPath = publicationResult.Registration!.DescriptorPath;
            BootstrapReadinessWriter bootstrapReadinessWriter = new(
                protocolContext,
                descriptorPath);

            return CodeServiceHostCreationResult.Success(
                new CodeServiceHost(
                    godotProcessLifetime,
                    sessionCoordinator,
                    diagnosticLogging,
                    workloadCoordinator,
                    roslynLanguageServerHost,
                    documentSynchronizationHost,
                    workspaceHost,
                    documentSemanticReadinessHost,
                    documentCompletionHost,
                    localTransportHost,
                    bootstrapReadinessWriter),
                diagnosticLogging.LogPath,
                loggingResult.WarningMessage);
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("session_start_failed", exception);
            diagnosticLogging.WriteEvent("service_stopping");
            BeginCompletionShutdownNoThrow(documentCompletionHost, diagnosticLogging);
            BeginSemanticShutdownNoThrow(documentSemanticReadinessHost, diagnosticLogging);
            BeginDocumentShutdownNoThrow(documentSynchronizationHost, diagnosticLogging);
            BeginWorkspaceShutdownNoThrow(workspaceHost, diagnosticLogging);
            BeginWorkloadShutdownNoThrow(workloadCoordinator, diagnosticLogging);
            diagnosticLogging.WriteEvent("transport_stopping");

            await StopAndDisposeTransportNoThrowAsync(localTransportHost, diagnosticLogging)
                .ConfigureAwait(false);
            await RetireWorkloadNoThrowAsync(workloadCoordinator, diagnosticLogging)
                .ConfigureAwait(false);
            await RetireRoslynNoThrowAsync(roslynLanguageServerHost, diagnosticLogging)
                .ConfigureAwait(false);
            DisposeCompletionNoThrow(documentCompletionHost, diagnosticLogging);
            DisposeSemanticNoThrow(documentSemanticReadinessHost, diagnosticLogging);
            DisposeDocumentNoThrow(documentSynchronizationHost, diagnosticLogging);
            DisposeWorkspaceNoThrow(workspaceHost, diagnosticLogging);

            RetireSessionNoThrow(sessionCoordinator, diagnosticLogging, writeLifecycleEvents: true);
            DisposeGodotLifetimeNoThrow(godotProcessLifetime, diagnosticLogging);
            diagnosticLogging.WriteEvent("service_stopped");

            string? diagnosticLogPath = diagnosticLogging.LogPath;
            diagnosticLogging.Flush();
            diagnosticLogging.Dispose();

            return CodeServiceHostCreationResult.Failure(
                CodeServiceHostCreationFailureKind.SessionStartupFailure,
                $"session publication/control-plane startup failed: {ToSingleLine(exception.Message)}",
                diagnosticLogPath,
                loggingResult.WarningMessage);
        }
    }

    public async Task RunAsync()
    {
        if (Interlocked.CompareExchange(ref _runState, 1, 0) != 0)
        {
            throw new InvalidOperationException("CodeServiceHost.RunAsync may only be executed once.");
        }

        ThrowIfShutdownStarted();

        using CancellationTokenSource ownerWaitCancellation = new();
        Task ownerExitTask = _godotProcessLifetime.WaitForOwnerExitAsync(ownerWaitCancellation.Token);
        Task transportCompletionTask = _localTransportHost.Completion;

        _diagnosticLogging.WriteEvent("host_running");

        if (!ownerExitTask.IsCompleted && !transportCompletionTask.IsCompleted)
        {
            BootstrapReadinessWriteResult readinessResult = _bootstrapReadinessWriter.TryWriteOnce();
            if (readinessResult.IsSuccess)
            {
                _diagnosticLogging.WriteEvent("bootstrap_ready_published");
            }
            else if (readinessResult.Exception is not null)
            {
                _diagnosticLogging.WriteFault(
                    "bootstrap_ready_write_failed",
                    readinessResult.Exception);
            }
            else
            {
                _diagnosticLogging.WriteEvent("bootstrap_ready_write_failed");
            }
        }

        await Task.WhenAny(ownerExitTask, transportCompletionTask).ConfigureAwait(false);

        if (ownerExitTask.IsCompleted)
        {
            try
            {
                await ownerExitTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _diagnosticLogging.WriteFault("service_fault", exception);
                await CleanupOwnedOwnerWaitAsync(
                    ownerWaitCancellation,
                    ownerExitTask,
                    ownerWaitAlreadyObserved: true).ConfigureAwait(false);
                await ShutdownAfterRuntimeFailureNoThrowAsync().ConfigureAwait(false);
                throw;
            }

            _diagnosticLogging.WriteEvent("owner_exit_observed");
            await EnsureShutdownAsync().ConfigureAwait(false);
            return;
        }

        InvalidOperationException transportFailure = new(
            "local transport stopped unexpectedly while the validated Godot owner was still alive.");

        _diagnosticLogging.WriteFault("transport_fault", transportFailure);
        _diagnosticLogging.WriteFault("service_fault", transportFailure);

        await CleanupOwnedOwnerWaitAsync(
            ownerWaitCancellation,
            ownerExitTask,
            ownerWaitAlreadyObserved: false).ConfigureAwait(false);
        await ShutdownAfterRuntimeFailureNoThrowAsync().ConfigureAwait(false);
        throw transportFailure;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await EnsureShutdownAsync().ConfigureAwait(false);
        }
        catch
        {
            // ShutdownCoreAsync records cleanup failures before diagnostics retire.
        }
    }

    private Task EnsureShutdownAsync()
    {
        lock (_shutdownSync)
        {
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    private async Task ShutdownCoreAsync()
    {
        Exception? shutdownFailure = null;

        _diagnosticLogging.WriteEvent("service_stopping");

        Exception? completionShutdownFailure = BeginCompletionShutdownNoThrow(_documentCompletionHost, _diagnosticLogging);
        if (completionShutdownFailure is not null) shutdownFailure = completionShutdownFailure;

        Exception? semanticShutdownFailure = BeginSemanticShutdownNoThrow(_documentSemanticReadinessHost, _diagnosticLogging);
        if (semanticShutdownFailure is not null) shutdownFailure = semanticShutdownFailure;

        Exception? documentShutdownFailure = BeginDocumentShutdownNoThrow(
            _documentSynchronizationHost,
            _diagnosticLogging);
        if (documentShutdownFailure is not null)
        {
            shutdownFailure = documentShutdownFailure;
        }

        Exception? workspaceShutdownFailure = BeginWorkspaceShutdownNoThrow(
            _workspaceHost,
            _diagnosticLogging);
        if (workspaceShutdownFailure is not null)
        {
            shutdownFailure = workspaceShutdownFailure;
        }

        Exception? workloadShutdownFailure = BeginWorkloadShutdownNoThrow(
            _workloadCoordinator,
            _diagnosticLogging);
        if (workloadShutdownFailure is not null)
        {
            shutdownFailure ??= workloadShutdownFailure;
        }

        _diagnosticLogging.WriteEvent("transport_stopping");

        try
        {
            await _localTransportHost.StopAsync().ConfigureAwait(false);
            _diagnosticLogging.WriteEvent("transport_stopped");
        }
        catch (Exception exception)
        {
            shutdownFailure ??= exception;
            _diagnosticLogging.WriteFault("transport_fault", exception);
            _diagnosticLogging.WriteFault("service_fault", exception);
        }

        try
        {
            await _localTransportHost.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            shutdownFailure ??= exception;
            _diagnosticLogging.WriteFault("transport_fault", exception);
            _diagnosticLogging.WriteFault("service_fault", exception);
        }

        Exception? workloadRetirementFailure = await RetireWorkloadNoThrowAsync(
            _workloadCoordinator,
            _diagnosticLogging).ConfigureAwait(false);
        if (workloadRetirementFailure is not null)
        {
            shutdownFailure ??= workloadRetirementFailure;
        }

        Exception? roslynRetirementFailure = await RetireRoslynNoThrowAsync(
            _roslynLanguageServerHost,
            _diagnosticLogging).ConfigureAwait(false);
        if (roslynRetirementFailure is not null)
        {
            shutdownFailure ??= roslynRetirementFailure;
        }

        Exception? completionRetirementFailure = DisposeCompletionNoThrow(_documentCompletionHost, _diagnosticLogging);
        if (completionRetirementFailure is not null) shutdownFailure ??= completionRetirementFailure;

        Exception? semanticRetirementFailure = DisposeSemanticNoThrow(_documentSemanticReadinessHost, _diagnosticLogging);
        if (semanticRetirementFailure is not null) shutdownFailure ??= semanticRetirementFailure;

        Exception? documentRetirementFailure = DisposeDocumentNoThrow(
            _documentSynchronizationHost,
            _diagnosticLogging);
        if (documentRetirementFailure is not null)
        {
            shutdownFailure ??= documentRetirementFailure;
        }

        Exception? workspaceRetirementFailure = DisposeWorkspaceNoThrow(
            _workspaceHost,
            _diagnosticLogging);
        if (workspaceRetirementFailure is not null)
        {
            shutdownFailure ??= workspaceRetirementFailure;
        }

        SessionCoordinatorRetirementResult sessionRetirement = RetireSessionNoThrow(
            _sessionCoordinator,
            _diagnosticLogging,
            writeLifecycleEvents: true);

        if (sessionRetirement.RetirementFailure is not null)
        {
            shutdownFailure ??= sessionRetirement.RetirementFailure;
        }

        try
        {
            _godotProcessLifetime.Dispose();
        }
        catch (Exception exception)
        {
            shutdownFailure ??= exception;
            _diagnosticLogging.WriteFault("service_fault", exception);
        }

        _diagnosticLogging.WriteEvent("service_stopped");
        _diagnosticLogging.Flush();
        _diagnosticLogging.Dispose();

        if (shutdownFailure is not null)
        {
            throw new InvalidOperationException("CodeService shutdown failed.", shutdownFailure);
        }
    }

    private async Task ShutdownAfterRuntimeFailureNoThrowAsync()
    {
        try
        {
            await EnsureShutdownAsync().ConfigureAwait(false);
        }
        catch
        {
            // ShutdownCoreAsync already recorded cleanup failures before diagnostics retired.
        }
    }

    private async Task CleanupOwnedOwnerWaitAsync(
        CancellationTokenSource ownerWaitCancellation,
        Task ownerExitTask,
        bool ownerWaitAlreadyObserved)
    {
        ownerWaitCancellation.Cancel();

        if (ownerWaitAlreadyObserved)
        {
            return;
        }

        try
        {
            await ownerExitTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ownerWaitCancellation.IsCancellationRequested)
        {
            // Cancellation retires only this abnormal owner-observation wait.
        }
        catch (Exception exception)
        {
            _diagnosticLogging.WriteFault("service_fault", exception);
        }
    }

    private void ThrowIfShutdownStarted()
    {
        lock (_shutdownSync)
        {
            ObjectDisposedException.ThrowIf(_shutdownTask is not null, this);
        }
    }

    private static Exception? BeginCompletionShutdownNoThrow(DocumentCompletionHost host, DiagnosticLogging diagnosticLogging)
    {
        try { host.BeginShutdown(); return null; }
        catch (Exception exception) { diagnosticLogging.WriteFault("service_fault", exception); return exception; }
    }

    private static Exception? DisposeCompletionNoThrow(DocumentCompletionHost host, DiagnosticLogging diagnosticLogging)
    {
        try { host.Dispose(); return null; }
        catch (Exception exception) { diagnosticLogging.WriteFault("service_fault", exception); return exception; }
    }

    private static Exception? BeginSemanticShutdownNoThrow(DocumentSemanticReadinessHost host, DiagnosticLogging diagnosticLogging)
    {
        try { host.BeginShutdown(); return null; }
        catch (Exception exception) { diagnosticLogging.WriteFault("service_fault", exception); return exception; }
    }

    private static Exception? DisposeSemanticNoThrow(DocumentSemanticReadinessHost host, DiagnosticLogging diagnosticLogging)
    {
        try { host.Dispose(); return null; }
        catch (Exception exception) { diagnosticLogging.WriteFault("service_fault", exception); return exception; }
    }

    private static Exception? BeginDocumentShutdownNoThrow(
        DocumentSynchronizationHost documentSynchronizationHost,
        DiagnosticLogging diagnosticLogging)
    {
        try
        {
            documentSynchronizationHost.BeginShutdown();
            return null;
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("service_fault", exception);
            return exception;
        }
    }

    private static Exception? DisposeDocumentNoThrow(
        DocumentSynchronizationHost documentSynchronizationHost,
        DiagnosticLogging diagnosticLogging)
    {
        try
        {
            documentSynchronizationHost.Dispose();
            return null;
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("service_fault", exception);
            return exception;
        }
    }

    private static Exception? BeginWorkspaceShutdownNoThrow(
        WorkspaceHost workspaceHost,
        DiagnosticLogging diagnosticLogging)
    {
        try
        {
            workspaceHost.BeginShutdown();
            return null;
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("service_fault", exception);
            return exception;
        }
    }

    private static Exception? DisposeWorkspaceNoThrow(
        WorkspaceHost workspaceHost,
        DiagnosticLogging diagnosticLogging)
    {
        try
        {
            workspaceHost.Dispose();
            return null;
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("service_fault", exception);
            return exception;
        }
    }

    private static Exception? BeginWorkloadShutdownNoThrow(
        WorkloadCoordinator workloadCoordinator,
        DiagnosticLogging diagnosticLogging)
    {
        Exception? shutdownFailure = null;

        try
        {
            workloadCoordinator.BeginShutdown();
        }
        catch (Exception exception)
        {
            shutdownFailure = exception;
            diagnosticLogging.WriteFault("workload_fault", exception);
            diagnosticLogging.WriteFault("service_fault", exception);
        }

        diagnosticLogging.WriteEvent("workload_admission_closed");
        return shutdownFailure;
    }

    private static async Task<Exception?> RetireWorkloadNoThrowAsync(
        WorkloadCoordinator workloadCoordinator,
        DiagnosticLogging diagnosticLogging)
    {
        try
        {
            await workloadCoordinator.RetireAsync().ConfigureAwait(false);
            diagnosticLogging.WriteEvent("workload_coordinator_stopped");
            return null;
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("workload_fault", exception);
            diagnosticLogging.WriteFault("service_fault", exception);
            return exception;
        }
    }

    private static async Task<Exception?> RetireRoslynNoThrowAsync(
        RoslynLanguageServerHost roslynLanguageServerHost,
        DiagnosticLogging diagnosticLogging)
    {
        try
        {
            await roslynLanguageServerHost.RetireAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("roslyn_fault", exception);
            diagnosticLogging.WriteFault("service_fault", exception);
            return exception;
        }
    }

    private static SessionCoordinatorRetirementResult RetireSessionNoThrow(
        SessionCoordinator sessionCoordinator,
        DiagnosticLogging diagnosticLogging,
        bool writeLifecycleEvents)
    {
        if (writeLifecycleEvents)
        {
            diagnosticLogging.WriteEvent("session_stopping");
            if (sessionCoordinator.HasPublishedDescriptor)
            {
                diagnosticLogging.WriteEvent("descriptor_removing");
            }
        }

        SessionCoordinatorRetirementResult retirementResult;

        try
        {
            retirementResult = sessionCoordinator.Retire();
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("service_fault", exception);
            return new SessionCoordinatorRetirementResult(
                SessionDescriptorRemovalResult.Failed(exception),
                exception,
                WasAlreadyRetired: false);
        }

        if (retirementResult.DescriptorRemoval.WasRemoved)
        {
            diagnosticLogging.WriteEvent("descriptor_removed");
        }
        else if (retirementResult.DescriptorRemoval.Status == SessionDescriptorRemovalStatus.Failed
            && retirementResult.DescriptorRemoval.Exception is not null)
        {
            diagnosticLogging.WriteFault(
                "descriptor_remove_failed",
                retirementResult.DescriptorRemoval.Exception);
        }

        if (retirementResult.RetirementFailure is not null)
        {
            diagnosticLogging.WriteFault("service_fault", retirementResult.RetirementFailure);
        }
        else if (writeLifecycleEvents && !retirementResult.WasAlreadyRetired)
        {
            diagnosticLogging.WriteEvent("session_stopped");
        }

        return retirementResult;
    }

    private static async Task DisposeTransportNoThrowAsync(
        LocalTransportHost localTransportHost,
        DiagnosticLogging diagnosticLogging)
    {
        try
        {
            await localTransportHost.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupException)
        {
            diagnosticLogging.WriteFault("transport_fault", cleanupException);
        }
    }

    private static async Task StopAndDisposeTransportNoThrowAsync(
        LocalTransportHost localTransportHost,
        DiagnosticLogging diagnosticLogging)
    {
        try
        {
            await localTransportHost.StopAsync().ConfigureAwait(false);
            diagnosticLogging.WriteEvent("transport_stopped");
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("transport_fault", exception);
        }

        await DisposeTransportNoThrowAsync(localTransportHost, diagnosticLogging)
            .ConfigureAwait(false);
    }

    private static void DisposeGodotLifetimeNoThrow(
        GodotProcessLifetime godotProcessLifetime,
        DiagnosticLogging diagnosticLogging)
    {
        try
        {
            godotProcessLifetime.Dispose();
        }
        catch (Exception exception)
        {
            diagnosticLogging.WriteFault("service_fault", exception);
        }
    }

    private static bool IsNonFatalMaintenanceFailure(Exception exception)
        => exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private static string ToSingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');
}

internal enum CodeServiceHostCreationFailureKind
{
    None,
    OwnerValidationFailure,
    SessionStartupFailure,
    TransportStartupFailure,
    RoslynRuntimeProvisioningFailure,
}

internal readonly record struct CodeServiceHostCreationResult(
    CodeServiceHost? Host,
    CodeServiceHostCreationFailureKind FailureKind,
    string? ErrorMessage,
    string? DiagnosticLogPath,
    string? DiagnosticLoggingWarning)
{
    public bool IsSuccess => Host is not null && FailureKind == CodeServiceHostCreationFailureKind.None;

    public static CodeServiceHostCreationResult Success(
        CodeServiceHost host,
        string? diagnosticLogPath,
        string? diagnosticLoggingWarning)
        => new(
            host,
            CodeServiceHostCreationFailureKind.None,
            null,
            diagnosticLogPath,
            diagnosticLoggingWarning);

    public static CodeServiceHostCreationResult Failure(
        CodeServiceHostCreationFailureKind failureKind,
        string errorMessage,
        string? diagnosticLogPath = null,
        string? diagnosticLoggingWarning = null)
        => new(
            null,
            failureKind,
            errorMessage,
            diagnosticLogPath,
            diagnosticLoggingWarning);
}
