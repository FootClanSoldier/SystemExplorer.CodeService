namespace SystemExplorer.CodeService;

internal enum WorkspaceInitializationSource
{
    TransportRequest,
    StartupProjectRoot,
}

internal sealed record StartupWorkspaceInitializationCompletedDetails(
    WorkspaceInitializationOutcome Outcome,
    WorkspaceState WorkspaceState,
    string? FaultKind,
    bool ReusedExistingWorkspace);

internal sealed record StartupWorkspaceInitializationFaultDetails(
    WorkspaceState WorkspaceState);

internal sealed record WorkspaceInitializationStartedDetails(
    WorkspaceInitializationSource InitializationSource,
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    bool ForceFullSourceValidation,
    int ForcedFingerprintPathCount);

internal sealed record WorkspaceReadyDetails(
    WorkspaceInitializationSource InitializationSource,
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long WorkspacePublicationVersion,
    string ProjectIndexGenerationId,
    RoslynLanguageServerState RoslynState,
    long RoslynGeneration,
    int SourceFileCount,
    int ProjectFileCount,
    int SolutionFileCount,
    double DiscoveryDurationMs,
    double IndexDurationMs,
    double RoslynReconcileDurationMs,
    double IndexRoslynParallelDurationMs,
    double IndexRoslynOverlapDurationMs,
    double DocumentReplayDurationMs,
    double PublicationCommitDurationMs,
    double ExplicitWorkDurationMs,
    double UnattributedDurationMs,
    double TotalInitializationDurationMs,
    long? WorkingSetBytes,
    long? ManagedMemoryBytes);

internal sealed record WorkspaceInitializationFaultDetails(
    WorkspaceInitializationSource InitializationSource,
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    double TotalInitializationDurationMs);

internal sealed record WorkspaceReconciliationCorrelationDetails(
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long DirtyVersion,
    int DirtySignalCount,
    bool ForceFullSourceValidation,
    int ForcedFingerprintPathCount,
    bool ForceRoslynProjectReload);

internal sealed record WorkspaceReconciliationCompletedDetails(
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long DirtyVersion,
    int DirtySignalCount,
    bool ForceFullSourceValidation,
    int ForcedFingerprintPathCount,
    bool ForceRoslynProjectReload,
    long WorkspacePublicationVersion,
    string ProjectIndexGenerationId,
    RoslynLanguageServerState RoslynState,
    long RoslynGeneration,
    int SourceFileCount,
    int ProjectFileCount,
    int SolutionFileCount,
    double DiscoveryDurationMs,
    double IndexDurationMs,
    double RoslynReconcileDurationMs,
    double DocumentReplayDurationMs,
    double PublicationCommitDurationMs,
    double ExplicitWorkDurationMs,
    double UnattributedDurationMs,
    double TotalDurationMs);

internal sealed record WorkspaceReconciliationTerminalDetails(
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long DirtyVersion,
    int DirtySignalCount,
    bool ForceFullSourceValidation,
    int ForcedFingerprintPathCount,
    bool ForceRoslynProjectReload,
    double TotalDurationMs,
    bool PendingNewerDirty,
    int PendingNewerDirtySignalCount);
