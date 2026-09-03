namespace SystemExplorer.CodeService;

internal sealed record DocumentCompletionRequest(
    int SchemaVersion,
    long ClientGeneration,
    Guid EpochId,
    string DocumentPath,
    long ClientVersion,
    int Line,
    int Character);

internal sealed record DocumentCompletionItem(
    string DisplayText,
    string InsertText,
    int? Kind,
    string FilterText,
    string SortText,
    bool Preselect,
    CompletionSemanticOrigin SemanticOrigin,
    int? InheritanceDepth);

internal enum DocumentCompletionOutcome
{
    Success,
    InvalidRequest,
    VersionMismatch,
    Busy,
    WorkspaceUnavailable,
    RoslynUnavailable,
    SemanticUnavailable,
    CompletionUnavailable,
    StaleEpoch,
    EpochConflict,
    StaleVersion,
    DocumentNotSynchronized,
    DocumentNotOpen,
    DocumentNotInWorkspace,
    Unavailable,
}

internal readonly record struct DocumentCompletionResult(
    DocumentCompletionOutcome Outcome,
    long? ClientGeneration,
    Guid? EpochId,
    string? DocumentPath,
    long? AcceptedClientVersion,
    WorkspacePublicationIdentity? WorkspacePublicationIdentity,
    long? RoslynGeneration,
    int? RoslynDocumentVersion,
    long? RoslynOverlayRevision,
    IReadOnlyList<DocumentCompletionItem> Items,
    bool IsIncomplete,
    int RawItemCount)
{
    public static DocumentCompletionResult Failure(
        DocumentCompletionOutcome outcome,
        DocumentCompletionRequest? request = null,
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
            [],
            IsIncomplete: false,
            RawItemCount: 0);
}
