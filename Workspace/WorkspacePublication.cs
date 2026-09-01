namespace SystemExplorer.CodeService;

internal readonly record struct WorkspacePublicationIdentity(
    long WorkspaceGeneration,
    long PublicationVersion);

internal sealed class WorkspacePublication
{
    public WorkspacePublication(
        WorkspaceIdentity workspaceIdentity,
        WorkspacePublicationIdentity identity,
        WorkspaceProjectSnapshot projectSnapshot,
        string projectIndexGenerationId,
        RoslynLanguageServerSnapshot roslynSnapshot)
    {
        WorkspaceIdentity = workspaceIdentity ?? throw new ArgumentNullException(nameof(workspaceIdentity));
        Identity = identity;
        ProjectSnapshot = projectSnapshot ?? throw new ArgumentNullException(nameof(projectSnapshot));
        ProjectIndexGenerationId = string.IsNullOrWhiteSpace(projectIndexGenerationId)
            ? throw new ArgumentException("project index generation id is required.", nameof(projectIndexGenerationId))
            : projectIndexGenerationId;
        RoslynSnapshot = roslynSnapshot;
    }

    public WorkspaceIdentity WorkspaceIdentity { get; }
    public WorkspacePublicationIdentity Identity { get; }
    public WorkspaceProjectSnapshot ProjectSnapshot { get; }
    public string ProjectIndexGenerationId { get; }
    public RoslynLanguageServerSnapshot RoslynSnapshot { get; }
}
