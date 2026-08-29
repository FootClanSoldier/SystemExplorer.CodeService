namespace SystemExplorer.CodeService;

internal sealed class WorkspaceProjectDiscovery
{
    public WorkspaceProjectSnapshot Discover(
        WorkspaceIdentity workspaceIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);

        cancellationToken.ThrowIfCancellationRequested();
        ValidateRootForDiscovery(workspaceIdentity);

        List<string> sourceFiles = new();
        List<string> projectFiles = new();
        List<string> solutionFiles = new();
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(workspaceIdentity.ProjectRoot);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string currentDirectory = pendingDirectories.Pop();

            foreach (string entryPath in Directory.EnumerateFileSystemEntries(currentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                FileAttributes attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    string relativeDirectory = GetNormalizedRelativePath(
                        workspaceIdentity.ProjectRoot,
                        entryPath);
                    string directoryName = Path.GetFileName(entryPath);

                    if (WorkspaceProjectPathClassifier.ShouldPruneDirectory(
                            relativeDirectory,
                            directoryName))
                    {
                        continue;
                    }

                    pendingDirectories.Push(entryPath);
                    continue;
                }

                string relativePath = GetNormalizedRelativePath(
                    workspaceIdentity.ProjectRoot,
                    entryPath);

                switch (WorkspaceProjectPathClassifier.ClassifyFile(relativePath))
                {
                    case WorkspaceProjectFileKind.CSharpSource:
                        sourceFiles.Add(relativePath);
                        break;
                    case WorkspaceProjectFileKind.CSharpProject:
                        projectFiles.Add(relativePath);
                        break;
                    case WorkspaceProjectFileKind.Solution:
                        solutionFiles.Add(relativePath);
                        break;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        sourceFiles.Sort(StringComparer.Ordinal);
        projectFiles.Sort(StringComparer.Ordinal);
        solutionFiles.Sort(StringComparer.Ordinal);

        return new WorkspaceProjectSnapshot(
            workspaceIdentity,
            sourceFiles,
            projectFiles,
            solutionFiles);
    }

    private static void ValidateRootForDiscovery(WorkspaceIdentity workspaceIdentity)
    {
        if (!Directory.Exists(workspaceIdentity.ProjectRoot))
        {
            throw new DirectoryNotFoundException(
                "the established workspace project root no longer exists.");
        }

        string projectFilePath = Path.Combine(workspaceIdentity.ProjectRoot, "project.godot");
        if (!File.Exists(projectFilePath))
        {
            throw new InvalidDataException(
                "the established workspace project root no longer contains project.godot.");
        }
    }

    private static string GetNormalizedRelativePath(string projectRoot, string path)
    {
        string relativePath = Path.GetRelativePath(projectRoot, path);

        if (Path.IsPathFullyQualified(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new IOException("project discovery produced a path outside the workspace root.");
        }

        return WorkspaceProjectPathClassifier.NormalizeRelativePath(relativePath);
    }
}
