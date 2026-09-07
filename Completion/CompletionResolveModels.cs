namespace SystemExplorer.CodeService;

internal sealed record DocumentCompletionResolveRequest(
    int SchemaVersion,
    long ClientGeneration,
    Guid EpochId,
    string DocumentPath,
    long ClientVersion,
    Guid CompletionHandle);

internal readonly record struct DocumentCompletionTextPosition(
    int Line,
    int Character);

internal readonly record struct DocumentCompletionTextRange(
    DocumentCompletionTextPosition Start,
    DocumentCompletionTextPosition End);

internal sealed record DocumentCompletionTextEdit(
    DocumentCompletionTextRange Range,
    string NewText);

internal enum DocumentCompletionResolveOutcome
{
    Success,
    InvalidRequest,
    VersionMismatch,
    Busy,
    WorkspaceUnavailable,
    RoslynUnavailable,
    CompletionUnavailable,
    CompletionExpired,
    StaleEpoch,
    EpochConflict,
    StaleVersion,
    DocumentNotSynchronized,
    DocumentNotOpen,
    DocumentNotInWorkspace,
    Unavailable,
}

internal readonly record struct DocumentCompletionResolveResult(
    DocumentCompletionResolveOutcome Outcome,
    long? ClientGeneration,
    Guid? EpochId,
    string? DocumentPath,
    long? AcceptedClientVersion,
    WorkspacePublicationIdentity? WorkspacePublicationIdentity,
    long? RoslynGeneration,
    int? RoslynDocumentVersion,
    long? RoslynOverlayRevision,
    IReadOnlyList<DocumentCompletionTextEdit> Edits)
{
    public static DocumentCompletionResolveResult Success(
        DocumentCompletionResolveRequest request,
        DocumentSynchronizationDocumentSnapshot snapshot,
        DocumentCompletionTextEdit edit)
        => new(
            DocumentCompletionResolveOutcome.Success,
            request.ClientGeneration,
            request.EpochId,
            snapshot.DocumentPath,
            snapshot.AcceptedClientVersion,
            snapshot.LastWorkspacePublicationIdentity,
            snapshot.RoslynGeneration,
            snapshot.RoslynLspVersion,
            snapshot.RoslynOverlayRevision,
            [edit]);

    public static DocumentCompletionResolveResult Failure(
        DocumentCompletionResolveOutcome outcome,
        DocumentCompletionResolveRequest? request = null,
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
            snapshot?.RoslynOverlayRevision,
            []);
}
