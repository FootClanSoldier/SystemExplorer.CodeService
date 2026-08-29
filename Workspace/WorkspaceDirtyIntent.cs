namespace SystemExplorer.CodeService;

internal sealed class WorkspaceDirtyIntent
{
    public const int MaxPendingDirtySourcePaths = 4096;

    private readonly HashSet<string> _sourcePaths = new(IndexPathValidation.SourcePathComparer);

    public bool IsDirty { get; private set; }

    public bool ForceFullSourceValidation { get; private set; }

    public long HighestDirtyVersion { get; private set; }

    // Number of accepted observer callbacks coalesced into the pending batch.
    // This is diagnostic metadata, not a unique-file count or event queue.
    public int SignalCount { get; private set; }

    public void Mark(
        long dirtyVersion,
        bool forceFullSourceValidation,
        string? sourceRelativePath,
        string? secondarySourceRelativePath)
    {
        IsDirty = true;
        HighestDirtyVersion = dirtyVersion;
        SignalCount = SignalCount == int.MaxValue ? int.MaxValue : SignalCount + 1;

        if (ForceFullSourceValidation || forceFullSourceValidation)
        {
            ForceFullSourceValidation = true;
            _sourcePaths.Clear();
            return;
        }

        AddSourcePath(sourceRelativePath);
        AddSourcePath(secondarySourceRelativePath);
    }

    public WorkspaceDirtyBatch CaptureAndReset()
    {
        if (!IsDirty)
        {
            throw new InvalidOperationException("cannot capture an empty workspace dirty intent.");
        }

        string[] sourcePaths = ForceFullSourceValidation
            ? Array.Empty<string>()
            : _sourcePaths.ToArray();

        WorkspaceDirtyBatch batch = new(
            HighestDirtyVersion,
            SignalCount,
            new ProjectIndexReconciliationHints(
                ForceFullSourceValidation,
                sourcePaths));

        Clear();
        return batch;
    }

    public void Clear()
    {
        IsDirty = false;
        ForceFullSourceValidation = false;
        HighestDirtyVersion = 0;
        SignalCount = 0;
        _sourcePaths.Clear();
    }

    private void AddSourcePath(string? relativePath)
    {
        if (relativePath is null || ForceFullSourceValidation)
        {
            return;
        }

        if (_sourcePaths.Contains(relativePath))
        {
            return;
        }

        if (_sourcePaths.Count >= MaxPendingDirtySourcePaths)
        {
            ForceFullSourceValidation = true;
            _sourcePaths.Clear();
            return;
        }

        _sourcePaths.Add(relativePath);
    }
}

internal readonly record struct WorkspaceDirtyBatch(
    long DirtyVersion,
    int DirtySignalCount,
    ProjectIndexReconciliationHints ReconciliationHints);
