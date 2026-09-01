namespace SystemExplorer.CodeService;

internal sealed class DocumentIdentity : IEquatable<DocumentIdentity>
{
    public static readonly StringComparer PlatformPathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private DocumentIdentity(string relativePath)
    {
        RelativePath = relativePath;
    }

    public string RelativePath { get; }

    public static DocumentIdentityCreationResult TryCreate(
        string? wirePath,
        WorkspaceIdentity workspaceIdentity,
        WorkspaceProjectSnapshot projectSnapshot)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);
        ArgumentNullException.ThrowIfNull(projectSnapshot);

        if (string.IsNullOrWhiteSpace(wirePath)
            || wirePath.Length > DocumentSynchronizationLimits.MaxDocumentPathLength)
        {
            return DocumentIdentityCreationResult.Failure();
        }

        if (wirePath[0] == '/'
            || wirePath[^1] == '/'
            || wirePath.Contains('\\')
            || Path.IsPathFullyQualified(wirePath)
            || IsDriveQualified(wirePath))
        {
            return DocumentIdentityCreationResult.Failure();
        }

        string[] segments = wirePath.Split('/', StringSplitOptions.None);
        if (segments.Length == 0)
        {
            return DocumentIdentityCreationResult.Failure();
        }

        foreach (string segment in segments)
        {
            if (segment.Length == 0
                || string.Equals(segment, ".", StringComparison.Ordinal)
                || string.Equals(segment, "..", StringComparison.Ordinal))
            {
                return DocumentIdentityCreationResult.Failure();
            }
        }

        if (!string.Equals(Path.GetExtension(wirePath), ".cs", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentIdentityCreationResult.Failure();
        }

        try
        {
            string platformRelativePath = wirePath.Replace('/', Path.DirectorySeparatorChar);
            string absolutePath = Path.GetFullPath(
                Path.Combine(workspaceIdentity.ProjectRoot, platformRelativePath));
            string containmentRelativePath = Path.GetRelativePath(
                workspaceIdentity.ProjectRoot,
                absolutePath);

            if (Path.IsPathFullyQualified(containmentRelativePath)
                || string.Equals(containmentRelativePath, "..", StringComparison.Ordinal)
                || containmentRelativePath.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                || containmentRelativePath.StartsWith(
                    ".." + Path.AltDirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                return DocumentIdentityCreationResult.Failure();
            }

            string canonicalRelativePath = wirePath;
            bool isCurrentWorkspaceSource = false;
            foreach (string sourcePath in projectSnapshot.SourceFiles)
            {
                if (!PlatformPathComparer.Equals(sourcePath, wirePath))
                {
                    continue;
                }

                canonicalRelativePath = sourcePath;
                isCurrentWorkspaceSource = true;
                break;
            }

            return DocumentIdentityCreationResult.Success(
                new DocumentIdentity(canonicalRelativePath),
                isCurrentWorkspaceSource);
        }
        catch (Exception exception) when (IsControlledPathException(exception))
        {
            return DocumentIdentityCreationResult.Failure();
        }
    }

    public string GetAbsolutePath(WorkspaceIdentity workspaceIdentity)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);
        string platformRelativePath = RelativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(workspaceIdentity.ProjectRoot, platformRelativePath));
    }

    public bool Equals(DocumentIdentity? other)
        => other is not null && PlatformPathComparer.Equals(RelativePath, other.RelativePath);

    public override bool Equals(object? obj)
        => obj is DocumentIdentity other && Equals(other);

    public override int GetHashCode()
        => PlatformPathComparer.GetHashCode(RelativePath);

    public override string ToString()
        => RelativePath;

    private static bool IsDriveQualified(string value)
        => value.Length >= 2
            && char.IsAsciiLetter(value[0])
            && value[1] == ':';

    private static bool IsControlledPathException(Exception exception)
        => exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException
            or UnauthorizedAccessException;
}

internal readonly record struct DocumentIdentityCreationResult(
    DocumentIdentity? Identity,
    bool IsCurrentWorkspaceSource)
{
    public bool IsSuccess => Identity is not null;

    public static DocumentIdentityCreationResult Success(
        DocumentIdentity identity,
        bool isCurrentWorkspaceSource)
        => new(identity, isCurrentWorkspaceSource);

    public static DocumentIdentityCreationResult Failure()
        => new(null, false);
}
