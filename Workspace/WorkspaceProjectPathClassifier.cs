namespace SystemExplorer.CodeService;

internal static class WorkspaceProjectPathClassifier
{
    private const string SystemExplorerRelativeDirectory = "addons/system_explorer";
    private const string GodotProjectFile = "project.godot";

    private static readonly StringComparison PlatformPathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly StringComparer PlatformPathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static readonly HashSet<string> PrunedDirectoryNames = new(PlatformPathComparer)
    {
        ".godot",
        "bin",
        "obj",
        ".git",
        ".vs",
    };

    public static bool ShouldPruneDirectory(string relativePath, string directoryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        ArgumentException.ThrowIfNullOrEmpty(directoryName);

        return PrunedDirectoryNames.Contains(directoryName)
            || IsPathWithinPrunedTree(relativePath);
    }

    public static bool IsPathWithinPrunedTree(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        string normalizedRelativePath = NormalizeRelativePath(relativePath);
        if (normalizedRelativePath is "." or "")
        {
            return false;
        }

        string[] components = normalizedRelativePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string component in components)
        {
            if (PrunedDirectoryNames.Contains(component))
            {
                return true;
            }
        }

        return IsSystemExplorerPath(normalizedRelativePath);
    }

    public static WorkspaceProjectFileKind ClassifyFile(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        string normalizedRelativePath = NormalizeRelativePath(relativePath);
        if (IsPathWithinPrunedTree(normalizedRelativePath))
        {
            return WorkspaceProjectFileKind.Excluded;
        }

        string extension = Path.GetExtension(normalizedRelativePath);
        if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceProjectFileKind.CSharpSource;
        }

        if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceProjectFileKind.CSharpProject;
        }

        if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceProjectFileKind.Solution;
        }

        return WorkspaceProjectFileKind.Other;
    }

    public static bool IsWorkspaceMetadataFile(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        string normalizedRelativePath = NormalizeRelativePath(relativePath);
        return string.Equals(normalizedRelativePath, GodotProjectFile, PlatformPathComparison);
    }

    public static string NormalizeRelativePath(string relativePath)
        => relativePath.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool IsSystemExplorerPath(string normalizedRelativePath)
    {
        if (string.Equals(
                normalizedRelativePath,
                SystemExplorerRelativeDirectory,
                PlatformPathComparison))
        {
            return true;
        }

        string prefix = SystemExplorerRelativeDirectory + "/";
        return normalizedRelativePath.StartsWith(prefix, PlatformPathComparison);
    }
}

internal enum WorkspaceProjectFileKind
{
    Other,
    CSharpSource,
    CSharpProject,
    Solution,
    Excluded,
}
