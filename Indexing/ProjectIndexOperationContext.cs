namespace SystemExplorer.CodeService;

internal readonly record struct ProjectIndexOperationContext(
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long? DirtyVersion,
    int DirtySignalCount);

internal enum ProjectIndexOperationTrigger
{
    Initialization,
    ExplicitRetry,
    RuntimeFilesystem,
}
