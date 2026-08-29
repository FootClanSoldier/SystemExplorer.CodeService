namespace SystemExplorer.CodeService;

internal sealed class ProjectIndexReconciliationHints
{
    private readonly HashSet<string> _forcedFingerprintRelativePaths;

    public static ProjectIndexReconciliationHints None { get; } = new(
        forceFullSourceValidation: false,
        Array.Empty<string>());

    public static ProjectIndexReconciliationHints FullSourceValidation { get; } = new(
        forceFullSourceValidation: true,
        Array.Empty<string>());

    public ProjectIndexReconciliationHints(
        bool forceFullSourceValidation,
        IEnumerable<string> forcedFingerprintRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(forcedFingerprintRelativePaths);

        ForceFullSourceValidation = forceFullSourceValidation;
        _forcedFingerprintRelativePaths = new HashSet<string>(IndexPathValidation.SourcePathComparer);

        if (forceFullSourceValidation)
        {
            return;
        }

        foreach (string relativePath in forcedFingerprintRelativePaths)
        {
            IndexPathValidation.ValidateRelativeSourcePath(relativePath);
            _forcedFingerprintRelativePaths.Add(relativePath);
        }
    }

    public bool ForceFullSourceValidation { get; }

    public bool RequiresSourceValidation
        => ForceFullSourceValidation || _forcedFingerprintRelativePaths.Count != 0;

    public int ForcedFingerprintPathCount => _forcedFingerprintRelativePaths.Count;

    public bool RequiresFingerprint(string relativePath)
        => ForceFullSourceValidation || _forcedFingerprintRelativePaths.Contains(relativePath);
}
