namespace SystemExplorer.CodeService;

internal readonly record struct DocumentClientAuthority(
    long ClientGeneration,
    Guid EpochId);

internal sealed record DocumentEpochRequest(
    long ClientGeneration,
    Guid EpochId,
    IReadOnlyList<string> OpenDocumentPaths);

internal sealed record DocumentSnapshotRequest(
    long ClientGeneration,
    Guid EpochId,
    string DocumentPath,
    long ClientVersion,
    string Text,
    int TextUtf8ByteCount);

internal enum DocumentSynchronizationOutcome
{
    Success,
    AlreadyCurrent,
    InvalidRequest,
    VersionMismatch,
    Busy,
    WorkspaceUnavailable,
    RoslynUnavailable,
    StaleEpoch,
    EpochConflict,
    StaleVersion,
    VersionConflict,
    DocumentNotOpen,
    DocumentNotInWorkspace,
    CapacityExceeded,
    Unavailable,
}

internal readonly record struct DocumentEpochOperationResult(
    DocumentSynchronizationOutcome Outcome,
    long? ClientGeneration,
    Guid? EpochId,
    WorkspacePublicationIdentity? WorkspacePublicationIdentity,
    long? RoslynGeneration,
    int DeclaredOpenDocumentCount,
    int RetainedDocumentCount,
    int ClosedDocumentCount)
{
    public static DocumentEpochOperationResult Failure(
        DocumentSynchronizationOutcome outcome,
        DocumentEpochRequest? request = null,
        WorkspacePublication? publication = null)
        => new(
            outcome,
            request?.ClientGeneration,
            request?.EpochId,
            publication?.Identity,
            publication?.RoslynSnapshot.RoslynGeneration,
            0,
            0,
            0);
}

internal readonly record struct DocumentSnapshotOperationResult(
    DocumentSynchronizationOutcome Outcome,
    long? ClientGeneration,
    Guid? EpochId,
    string? DocumentPath,
    long? AcceptedClientVersion,
    WorkspacePublicationIdentity? WorkspacePublicationIdentity,
    long? RoslynGeneration,
    int? RoslynDocumentVersion)
{
    public static DocumentSnapshotOperationResult Failure(
        DocumentSynchronizationOutcome outcome,
        DocumentSnapshotRequest? request = null,
        WorkspacePublication? publication = null,
        string? documentPath = null,
        long? acceptedClientVersion = null,
        int? roslynDocumentVersion = null)
        => new(
            outcome,
            request?.ClientGeneration,
            request?.EpochId,
            documentPath,
            acceptedClientVersion,
            publication?.Identity,
            publication?.RoslynSnapshot.RoslynGeneration,
            roslynDocumentVersion);
}

internal readonly record struct DocumentSynchronizationDocumentSnapshot(
    string DocumentPath,
    long ClientGeneration,
    Guid EpochId,
    long AcceptedClientVersion,
    bool HasCurrentAuthoritySnapshot,
    WorkspacePublicationIdentity LastWorkspacePublicationIdentity,
    long RoslynGeneration,
    int RoslynLspVersion,
    long RoslynOverlayRevision,
    bool IsOpenInRoslyn,
    bool IsCurrentWorkspaceSource);

internal readonly record struct DocumentRoslynReplayResult(
    int ReplayDocumentCount,
    bool Completed,
    bool RoslynAvailable);
