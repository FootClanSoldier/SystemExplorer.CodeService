namespace SystemExplorer.CodeService;

internal enum RoslynProjectLoadKind
{
    Solution,
    Project,
}

internal static class RoslynProjectLoadFaultKinds
{
    public const string AmbiguousProjectGraph = "AmbiguousProjectGraph";
    public const string ProjectGraphMissing = "ProjectGraphMissing";
    public const string InvalidLoadTarget = "InvalidLoadTarget";
    public const string InitializationFailed = "InitializationFailed";
    public const string ProcessExited = "ProcessExited";
    public const string DocumentSynchronizationFailed = "DocumentSynchronizationFailed";
    public const string SemanticReadinessFailed = "SemanticReadinessFailed";
    public const string CompletionFailed = "CompletionFailed";
}

internal sealed class RoslynProjectLoadTarget
{
    private RoslynProjectLoadTarget(
        RoslynProjectLoadKind loadKind,
        string relativePath,
        string absolutePath)
    {
        LoadKind = loadKind;
        RelativePath = relativePath;
        AbsolutePath = absolutePath;
    }

    public RoslynProjectLoadKind LoadKind { get; }

    public string RelativePath { get; }

    public string AbsolutePath { get; }

    public static RoslynProjectLoadTargetSelectionResult Select(WorkspaceProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.SolutionFiles.Count > 1)
        {
            return RoslynProjectLoadTargetSelectionResult.Failure(
                RoslynProjectLoadFaultKinds.AmbiguousProjectGraph,
                "workspace discovery found multiple solution files; Roslyn project load is ambiguous.");
        }

        if (snapshot.SolutionFiles.Count == 1)
        {
            return CreateValidated(
                snapshot,
                RoslynProjectLoadKind.Solution,
                snapshot.SolutionFiles[0]);
        }

        if (snapshot.ProjectFiles.Count > 1)
        {
            return RoslynProjectLoadTargetSelectionResult.Failure(
                RoslynProjectLoadFaultKinds.AmbiguousProjectGraph,
                "workspace discovery found multiple project files without a solution; Roslyn project load is ambiguous.");
        }

        if (snapshot.ProjectFiles.Count == 1)
        {
            return CreateValidated(
                snapshot,
                RoslynProjectLoadKind.Project,
                snapshot.ProjectFiles[0]);
        }

        return RoslynProjectLoadTargetSelectionResult.Failure(
            RoslynProjectLoadFaultKinds.ProjectGraphMissing,
            "workspace discovery found no solution or C# project file for Roslyn project load.");
    }

    private static RoslynProjectLoadTargetSelectionResult CreateValidated(
        WorkspaceProjectSnapshot snapshot,
        RoslynProjectLoadKind loadKind,
        string relativePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
            {
                return InvalidTarget("workspace snapshot load target is not a bounded relative path.");
            }

            string platformRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            string absolutePath = Path.GetFullPath(
                Path.Combine(snapshot.WorkspaceIdentity.ProjectRoot, platformRelativePath));
            string containmentRelativePath = Path.GetRelativePath(
                snapshot.WorkspaceIdentity.ProjectRoot,
                absolutePath);

            if (Path.IsPathFullyQualified(containmentRelativePath)
                || containmentRelativePath.Equals("..", StringComparison.Ordinal)
                || containmentRelativePath.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                || containmentRelativePath.StartsWith(
                    ".." + Path.AltDirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                return InvalidTarget("Roslyn project load target resolves outside the established workspace root.");
            }

            if (!File.Exists(absolutePath))
            {
                return InvalidTarget("Roslyn project load target no longer exists.");
            }

            string extension = Path.GetExtension(absolutePath);
            bool extensionMatches = loadKind switch
            {
                RoslynProjectLoadKind.Solution
                    => extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase),
                RoslynProjectLoadKind.Project
                    => extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };

            if (!extensionMatches)
            {
                return InvalidTarget("Roslyn project load target extension does not match its selected load kind.");
            }

            return RoslynProjectLoadTargetSelectionResult.Success(
                new RoslynProjectLoadTarget(loadKind, relativePath, absolutePath));
        }
        catch (Exception exception) when (IsControlledPathException(exception))
        {
            return InvalidTarget(
                $"Roslyn project load target could not be validated: {ToSingleLine(exception.Message)}");
        }
    }

    private static RoslynProjectLoadTargetSelectionResult InvalidTarget(string errorMessage)
        => RoslynProjectLoadTargetSelectionResult.Failure(
            RoslynProjectLoadFaultKinds.InvalidLoadTarget,
            errorMessage);

    private static bool IsControlledPathException(Exception exception)
        => exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or IOException
            or UnauthorizedAccessException;

    private static string ToSingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');
}

internal readonly record struct RoslynProjectLoadTargetSelectionResult(
    RoslynProjectLoadTarget? Target,
    string? FaultKind,
    string? ErrorMessage)
{
    public bool IsSuccess => Target is not null;

    public static RoslynProjectLoadTargetSelectionResult Success(RoslynProjectLoadTarget target)
        => new(target, null, null);

    public static RoslynProjectLoadTargetSelectionResult Failure(
        string faultKind,
        string errorMessage)
        => new(null, faultKind, errorMessage);
}
