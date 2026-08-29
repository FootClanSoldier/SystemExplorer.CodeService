namespace SystemExplorer.CodeService;

internal sealed class WorkspaceFileChangeObserver : IDisposable
{
    private readonly WorkspaceIdentity _workspaceIdentity;
    private readonly long _workspaceGeneration;
    private readonly Action<long, WorkspaceObservedChange> _changeSink;
    private readonly FileSystemWatcher _watcher;
    private bool _disposed;

    private WorkspaceFileChangeObserver(
        WorkspaceIdentity workspaceIdentity,
        long workspaceGeneration,
        Action<long, WorkspaceObservedChange> changeSink)
    {
        _workspaceIdentity = workspaceIdentity;
        _workspaceGeneration = workspaceGeneration;
        _changeSink = changeSink;
        _watcher = new FileSystemWatcher(workspaceIdentity.ProjectRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size,
            EnableRaisingEvents = false,
        };

        _watcher.Changed += OnChanged;
        _watcher.Created += OnCreated;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }

    public static WorkspaceFileChangeObserver Start(
        WorkspaceIdentity workspaceIdentity,
        long workspaceGeneration,
        Action<long, WorkspaceObservedChange> changeSink)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);
        ArgumentNullException.ThrowIfNull(changeSink);

        WorkspaceFileChangeObserver? observer = null;
        try
        {
            observer = new WorkspaceFileChangeObserver(
                workspaceIdentity,
                workspaceGeneration,
                changeSink);
            observer._watcher.EnableRaisingEvents = true;
            return observer;
        }
        catch (Exception exception)
        {
            observer?.Dispose();
            throw new WorkspaceChangeObservationException(
                "workspace filesystem observation could not be established.",
                exception);
        }
    }

    public void Disable()
    {
        try
        {
            _watcher.EnableRaisingEvents = false;
        }
        catch
        {
            // Retirement is best effort; generation guards contain stale callbacks.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Disable();

        try
        {
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnCreated;
            _watcher.Deleted -= OnDeleted;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
        }
        catch
        {
            // Observer retirement must not become a service failure.
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs)
    {
        try
        {
            WorkspaceObservedPath path = ClassifyPath(eventArgs.FullPath);
            if (path.IsPruned)
            {
                return;
            }

            if (!path.IsValid)
            {
                ReportConservative(WorkspaceObserverIncident.None);
                return;
            }

            WorkspaceProjectFileKind kind = WorkspaceProjectPathClassifier.ClassifyFile(path.RelativePath!);
            bool relevant = kind is WorkspaceProjectFileKind.CSharpSource
                or WorkspaceProjectFileKind.CSharpProject
                or WorkspaceProjectFileKind.Solution
                || WorkspaceProjectPathClassifier.IsWorkspaceMetadataFile(path.RelativePath!);

            if (!relevant)
            {
                return;
            }

            _changeSink(
                _workspaceGeneration,
                new WorkspaceObservedChange(
                    ForceFullSourceValidation: false,
                    SourceRelativePath: kind == WorkspaceProjectFileKind.CSharpSource
                        ? path.RelativePath
                        : null,
                    SecondarySourceRelativePath: null,
                    Incident: WorkspaceObserverIncident.None));
        }
        catch
        {
            ReportConservative(WorkspaceObserverIncident.None);
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs eventArgs)
        => OnStructuralPath(eventArgs.FullPath, secondaryFullPath: null);

    private void OnDeleted(object sender, FileSystemEventArgs eventArgs)
        => OnStructuralPath(eventArgs.FullPath, secondaryFullPath: null);

    private void OnRenamed(object sender, RenamedEventArgs eventArgs)
        => OnStructuralPath(eventArgs.FullPath, eventArgs.OldFullPath);

    private void OnError(object sender, ErrorEventArgs eventArgs)
    {
        try
        {
            Exception? exception = eventArgs.GetException();
            WorkspaceObserverIncident incident = exception is InternalBufferOverflowException
                ? WorkspaceObserverIncident.Overflow
                : WorkspaceObserverIncident.Fault;
            ReportConservative(incident);
        }
        catch
        {
            ReportConservative(WorkspaceObserverIncident.Fault);
        }
    }

    private void OnStructuralPath(string fullPath, string? secondaryFullPath)
    {
        try
        {
            WorkspaceObservedPath primary = ClassifyPath(fullPath);
            WorkspaceObservedPath secondary = secondaryFullPath is null
                ? WorkspaceObservedPath.None
                : ClassifyPath(secondaryFullPath);

            if ((!primary.IsValid && !primary.IsPruned)
                || (!secondary.IsValid && !secondary.IsPruned && secondaryFullPath is not null))
            {
                ReportConservative(WorkspaceObserverIncident.None);
                return;
            }

            bool primaryRelevant = primary.IsValid && !primary.IsPruned;
            bool secondaryRelevant = secondary.IsValid && !secondary.IsPruned;
            if (!primaryRelevant && !secondaryRelevant)
            {
                return;
            }

            string? primarySource = primaryRelevant
                && WorkspaceProjectPathClassifier.ClassifyFile(primary.RelativePath!)
                    == WorkspaceProjectFileKind.CSharpSource
                    ? primary.RelativePath
                    : null;

            string? secondarySource = secondaryRelevant
                && WorkspaceProjectPathClassifier.ClassifyFile(secondary.RelativePath!)
                    == WorkspaceProjectFileKind.CSharpSource
                    ? secondary.RelativePath
                    : null;

            _changeSink(
                _workspaceGeneration,
                new WorkspaceObservedChange(
                    ForceFullSourceValidation: false,
                    SourceRelativePath: primarySource,
                    SecondarySourceRelativePath: secondarySource,
                    Incident: WorkspaceObserverIncident.None));
        }
        catch
        {
            ReportConservative(WorkspaceObserverIncident.None);
        }
    }

    private WorkspaceObservedPath ClassifyPath(string fullPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return WorkspaceObservedPath.Invalid;
            }

            string normalizedFullPath = Path.GetFullPath(fullPath);
            string relativePath = WorkspaceProjectPathClassifier.NormalizeRelativePath(
                Path.GetRelativePath(_workspaceIdentity.ProjectRoot, normalizedFullPath));

            if (Path.IsPathFullyQualified(relativePath)
                || relativePath.Equals("..", StringComparison.Ordinal)
                || relativePath.StartsWith("../", StringComparison.Ordinal))
            {
                return WorkspaceObservedPath.Invalid;
            }

            if (WorkspaceProjectPathClassifier.IsPathWithinPrunedTree(relativePath))
            {
                return WorkspaceObservedPath.Pruned;
            }

            return WorkspaceObservedPath.Valid(relativePath);
        }
        catch
        {
            return WorkspaceObservedPath.Invalid;
        }
    }

    private void ReportConservative(WorkspaceObserverIncident incident)
    {
        try
        {
            _changeSink(
                _workspaceGeneration,
                new WorkspaceObservedChange(
                    ForceFullSourceValidation: true,
                    SourceRelativePath: null,
                    SecondarySourceRelativePath: null,
                    Incident: incident));
        }
        catch
        {
            // No exception may cross the FileSystemWatcher callback boundary.
        }
    }

    private readonly record struct WorkspaceObservedPath(
        bool IsValid,
        bool IsPruned,
        string? RelativePath)
    {
        public static WorkspaceObservedPath None => new(false, true, null);

        public static WorkspaceObservedPath Invalid => new(false, false, null);

        public static WorkspaceObservedPath Pruned => new(false, true, null);

        public static WorkspaceObservedPath Valid(string relativePath)
            => new(true, false, relativePath);
    }
}

internal readonly record struct WorkspaceObservedChange(
    bool ForceFullSourceValidation,
    string? SourceRelativePath,
    string? SecondarySourceRelativePath,
    WorkspaceObserverIncident Incident);

internal enum WorkspaceObserverIncident
{
    None,
    Overflow,
    Fault,
}

internal sealed class WorkspaceChangeObservationException : IOException
{
    public WorkspaceChangeObservationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
