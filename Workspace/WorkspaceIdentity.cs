namespace SystemExplorer.CodeService;

internal sealed class WorkspaceIdentity : IEquatable<WorkspaceIdentity>
{
    public const int MaxProjectRootLength = 4096;

    private static readonly StringComparer PlatformPathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private WorkspaceIdentity(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    public string ProjectRoot { get; }

    public static WorkspaceIdentityCreationResult TryCreate(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return WorkspaceIdentityCreationResult.Failure(
                "projectRoot must be a non-empty absolute filesystem path.");
        }

        if (projectRoot.Length > MaxProjectRootLength)
        {
            return WorkspaceIdentityCreationResult.Failure(
                $"projectRoot exceeds the maximum length of {MaxProjectRootLength} characters.");
        }

        try
        {
            if (!Path.IsPathFullyQualified(projectRoot))
            {
                return WorkspaceIdentityCreationResult.Failure(
                    "projectRoot must be a fully-qualified absolute filesystem path.");
            }

            string fullPath = Path.GetFullPath(projectRoot);
            string normalizedPath = Path.TrimEndingDirectorySeparator(fullPath);

            if (string.IsNullOrEmpty(normalizedPath)
                || normalizedPath.Length > MaxProjectRootLength)
            {
                return WorkspaceIdentityCreationResult.Failure(
                    "projectRoot could not be normalized to a bounded absolute path.");
            }

            return WorkspaceIdentityCreationResult.Success(new WorkspaceIdentity(normalizedPath));
        }
        catch (Exception exception) when (IsControlledPathException(exception))
        {
            return WorkspaceIdentityCreationResult.Failure(
                $"projectRoot is not a valid absolute filesystem path: {ToSingleLine(exception.Message)}");
        }
    }

    public static WorkspaceInitialRootValidationResult ValidateInitialGodotProjectRoot(
        WorkspaceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        try
        {
            if (!Directory.Exists(identity.ProjectRoot))
            {
                return WorkspaceInitialRootValidationResult.Failure(
                    "projectRoot must reference an existing directory.");
            }

            string projectFilePath = Path.Combine(identity.ProjectRoot, "project.godot");
            if (!File.Exists(projectFilePath))
            {
                return WorkspaceInitialRootValidationResult.Failure(
                    "projectRoot must contain project.godot directly in the project root.");
            }

            return WorkspaceInitialRootValidationResult.Success();
        }
        catch (Exception exception) when (IsControlledPathException(exception))
        {
            return WorkspaceInitialRootValidationResult.Failure(
                $"projectRoot could not be validated: {ToSingleLine(exception.Message)}");
        }
    }

    public bool Equals(WorkspaceIdentity? other)
        => other is not null
            && PlatformPathComparer.Equals(ProjectRoot, other.ProjectRoot);

    public override bool Equals(object? obj)
        => obj is WorkspaceIdentity other && Equals(other);

    public override int GetHashCode()
        => PlatformPathComparer.GetHashCode(ProjectRoot);

    public override string ToString()
        => ProjectRoot;

    private static bool IsControlledPathException(Exception exception)
        => exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException
            or UnauthorizedAccessException;

    private static string ToSingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');
}

internal readonly record struct WorkspaceIdentityCreationResult(
    WorkspaceIdentity? Identity,
    string? ErrorMessage)
{
    public bool IsSuccess => Identity is not null;

    public static WorkspaceIdentityCreationResult Success(WorkspaceIdentity identity)
        => new(identity, null);

    public static WorkspaceIdentityCreationResult Failure(string errorMessage)
        => new(null, errorMessage);
}

internal readonly record struct WorkspaceInitialRootValidationResult(
    bool IsSuccess,
    string? ErrorMessage)
{
    public static WorkspaceInitialRootValidationResult Success()
        => new(true, null);

    public static WorkspaceInitialRootValidationResult Failure(string errorMessage)
        => new(false, errorMessage);
}
