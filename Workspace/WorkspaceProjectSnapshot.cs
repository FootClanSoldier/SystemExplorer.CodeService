using System.Collections.ObjectModel;

namespace SystemExplorer.CodeService;

internal sealed class WorkspaceProjectSnapshot
{
    public WorkspaceProjectSnapshot(
        WorkspaceIdentity workspaceIdentity,
        IReadOnlyList<string> sourceFiles,
        IReadOnlyList<string> projectFiles,
        IReadOnlyList<string> solutionFiles)
    {
        WorkspaceIdentity = workspaceIdentity ?? throw new ArgumentNullException(nameof(workspaceIdentity));
        SourceFiles = Freeze(sourceFiles, nameof(sourceFiles));
        ProjectFiles = Freeze(projectFiles, nameof(projectFiles));
        SolutionFiles = Freeze(solutionFiles, nameof(solutionFiles));
    }

    public WorkspaceIdentity WorkspaceIdentity { get; }

    public ReadOnlyCollection<string> SourceFiles { get; }

    public ReadOnlyCollection<string> ProjectFiles { get; }

    public ReadOnlyCollection<string> SolutionFiles { get; }

    private static ReadOnlyCollection<string> Freeze(
        IReadOnlyList<string> paths,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(paths, parameterName);

        string[] copy = new string[paths.Count];
        for (int index = 0; index < paths.Count; index++)
        {
            copy[index] = paths[index]
                ?? throw new ArgumentException("snapshot paths cannot contain null entries.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }
}
