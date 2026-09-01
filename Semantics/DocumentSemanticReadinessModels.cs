namespace SystemExplorer.CodeService;

internal sealed record DocumentSemanticReadinessRequest(
    int SchemaVersion,
    long ClientGeneration,
    Guid EpochId,
    string DocumentPath,
    long ClientVersion);

internal enum DocumentSemanticReadinessOutcome
{
    Success,
    AlreadyCurrent,
    InvalidRequest,
    VersionMismatch,
    Busy,
    WorkspaceUnavailable,
    RoslynUnavailable,
    SemanticUnavailable,
    StaleEpoch,
    EpochConflict,
    StaleVersion,
    DocumentNotSynchronized,
    DocumentNotOpen,
    DocumentNotInWorkspace,
    Unavailable,
}

internal readonly record struct DocumentSemanticCorrelationIdentity(
    string DocumentPath,
    WorkspacePublicationIdentity WorkspacePublicationIdentity,
    long RoslynGeneration,
    long RoslynOverlayRevision,
    int RoslynLspVersion);

internal readonly record struct DocumentSemanticReadinessResult(
    DocumentSemanticReadinessOutcome Outcome,
    long? ClientGeneration,
    Guid? EpochId,
    string? DocumentPath,
    long? AcceptedClientVersion,
    WorkspacePublicationIdentity? WorkspacePublicationIdentity,
    long? RoslynGeneration,
    int? RoslynDocumentVersion,
    long? RoslynOverlayRevision)
{
    public static DocumentSemanticReadinessResult Failure(
        DocumentSemanticReadinessOutcome outcome,
        DocumentSemanticReadinessRequest? request = null,
        DocumentSynchronizationDocumentSnapshot? snapshot = null,
        WorkspacePublication? publication = null,
        string? documentPath = null)
        => new(
            outcome,
            request?.ClientGeneration,
            request?.EpochId,
            documentPath ?? snapshot?.DocumentPath,
            snapshot?.AcceptedClientVersion,
            snapshot?.LastWorkspacePublicationIdentity ?? publication?.Identity,
            snapshot?.RoslynGeneration ?? publication?.RoslynSnapshot.RoslynGeneration,
            snapshot?.RoslynLspVersion,
            snapshot?.RoslynOverlayRevision);
}
