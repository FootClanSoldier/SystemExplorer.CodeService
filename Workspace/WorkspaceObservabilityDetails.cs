namespace SystemExplorer.CodeService;

internal sealed record WorkspaceInitializationStartedDetails(
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    bool ForceFullSourceValidation,
    int ForcedFingerprintPathCount);

internal sealed record WorkspaceReadyDetails(
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
    double TotalInitializationDurationMs,
    long? WorkingSetBytes,
    long? ManagedMemoryBytes);

internal sealed record WorkspaceInitializationFaultDetails(
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
