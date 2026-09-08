namespace SystemExplorer.CodeService;

internal readonly record struct StartupDocumentSeedResult(
    bool Succeeded,
    DocumentIdentity? DocumentIdentity,
    WorkspacePublicationIdentity WorkspacePublicationIdentity,
    long RoslynGeneration,
    int RoslynLspVersion,
    long RoslynOverlayRevision,
    int TextUtf8ByteCount,
    string? SkipReason)
{
    public static StartupDocumentSeedResult Success(
        DocumentIdentity documentIdentity,
        WorkspacePublicationIdentity workspacePublicationIdentity,
        long roslynGeneration,
        int roslynLspVersion,
        long roslynOverlayRevision,
        int textUtf8ByteCount)
        => new(
            true,
            documentIdentity,
            workspacePublicationIdentity,
            roslynGeneration,
            roslynLspVersion,
            roslynOverlayRevision,
            textUtf8ByteCount,
            null);

    public static StartupDocumentSeedResult Skipped(
        WorkspacePublicationIdentity workspacePublicationIdentity,
        long roslynGeneration,
        string reason)
        => new(
            false,
            null,
            workspacePublicationIdentity,
            roslynGeneration,
            0,
            0,
            0,
            reason);
}

internal readonly record struct StartupDocumentWarmupResult(
    bool Succeeded,
    DocumentIdentity DocumentIdentity,
    WorkspacePublicationIdentity WorkspacePublicationIdentity,
    long RoslynGeneration,
    int RoslynLspVersion,
    long RoslynOverlayRevision,
    int DiagnosticCount);
