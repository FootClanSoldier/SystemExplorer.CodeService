namespace SystemExplorer.CodeService;

internal enum StartupDocumentHintState
{
    NotSpecified,
    Accepted,
    Rejected,
}

internal sealed class StartupDocumentHint
{
    private StartupDocumentHint(
        StartupDocumentHintState state,
        string? documentPath,
        string? rejectionReason)
    {
        State = state;
        DocumentPath = documentPath;
        RejectionReason = rejectionReason;
    }

    public static StartupDocumentHint NotSpecified { get; } = new(
        StartupDocumentHintState.NotSpecified,
        documentPath: null,
        rejectionReason: null);

    public StartupDocumentHintState State { get; }

    public string? DocumentPath { get; }

    public string? RejectionReason { get; }

    public bool IsAccepted => State == StartupDocumentHintState.Accepted;

    public static StartupDocumentHint Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0 || string.IsNullOrWhiteSpace(value))
        {
            return Rejected("empty");
        }

        if (value.Length > DocumentSynchronizationLimits.MaxDocumentPathLength)
        {
            return Rejected("too_long");
        }

        if (!value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return Rejected("not_cs");
        }

        if (value[0] == '/')
        {
            return Rejected("leading_slash");
        }

        if (value[^1] == '/')
        {
            return Rejected("trailing_slash");
        }

        if (value.Contains('\\'))
        {
            return Rejected("backslash");
        }

        if (value.Contains(':'))
        {
            return Rejected("colon");
        }

        try
        {
            if (Path.IsPathFullyQualified(value) || Path.IsPathRooted(value))
            {
                return Rejected("rooted");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Rejected("rooted");
        }

        string[] segments = value.Split('/', StringSplitOptions.None);
        foreach (string segment in segments)
        {
            if (segment.Length == 0)
            {
                return Rejected("empty_segment");
            }

            if (string.Equals(segment, ".", StringComparison.Ordinal))
            {
                return Rejected("dot_segment");
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                return Rejected("parent_segment");
            }
        }

        return new StartupDocumentHint(
            StartupDocumentHintState.Accepted,
            string.Join("/", segments),
            rejectionReason: null);
    }

    public StartupDocumentHint RequireProjectRoot(bool hasUsableProjectRoot)
        => State == StartupDocumentHintState.Accepted && !hasUsableProjectRoot
            ? Rejected("project_root_missing")
            : this;

    private static StartupDocumentHint Rejected(string reason)
        => new(
            StartupDocumentHintState.Rejected,
            documentPath: null,
            rejectionReason: reason);
}
