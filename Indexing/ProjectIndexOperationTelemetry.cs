using System.Diagnostics;

namespace SystemExplorer.CodeService;

internal sealed class ProjectIndexOperationTelemetry
{
    private readonly DiagnosticLogging _diagnosticLogging;
    private readonly ProjectIndexOperationContext _context;
    private readonly long _totalStartedTimestamp;
    private bool _persistenceBreadcrumbWritten;
    private double? _totalDurationMs;
    private ProjectIndexOperationPhase _phase = ProjectIndexOperationPhase.Started;

    public ProjectIndexOperationTelemetry(
        DiagnosticLogging diagnosticLogging,
        ProjectIndexOperationContext context,
        ProjectIndexReconciliationHints reconciliationHints)
    {
        _diagnosticLogging = diagnosticLogging
            ?? throw new ArgumentNullException(nameof(diagnosticLogging));
        _context = context;
        ArgumentNullException.ThrowIfNull(reconciliationHints);

        ForceFullSourceValidation = reconciliationHints.ForceFullSourceValidation;
        ForcedFingerprintPathCount = reconciliationHints.ForcedFingerprintPathCount;
        CacheLoadStatus = ProjectIndexCacheLoadStatus.NotApplicable;
        _totalStartedTimestamp = Stopwatch.GetTimestamp();
    }

    public int SourceCount { get; set; }

    public int AddedSources { get; set; }

    public int DeletedSources { get; set; }

    public int MetadataChangedSources { get; set; }

    public int FingerprintedSources { get; private set; }

    public int ContentChangedExistingSources { get; private set; }

    public int AffectedShards { get; set; }

    public int BaselineShardLoadAttempts { get; private set; }

    public int InMemoryShardLoads { get; private set; }

    public int PersistentShardLoadAttempts { get; private set; }

    public int RebuiltShards { get; private set; }

    public int RewrittenShards { get; set; }

    public bool ForceFullSourceValidation { get; }

    public int ForcedFingerprintPathCount { get; }

    public ProjectIndexCacheLoadStatus CacheLoadStatus { get; set; }

    public bool CacheWriteAttempted { get; private set; }

    public bool CacheWriteSucceeded { get; private set; }

    public int PersistenceUnavailableIncidentCount { get; private set; }

    public int BaselineShardPersistenceUnavailableCount { get; private set; }

    public bool PublicationPersistenceUnavailable { get; private set; }

    public double InventoryDurationMs { get; private set; }

    public double CacheLoadDurationMs { get; private set; }

    public double FingerprintDurationMs { get; private set; }

    public double ShardLoadDurationMs { get; private set; }

    public double PublicationDurationMs { get; private set; }

    public ProjectIndexOperationPhase Phase => _phase;

    public ProjectIndexOperationContext Context => _context;

    public void StopTotalTimer()
    {
        _totalDurationMs ??= Stopwatch.GetElapsedTime(
            _totalStartedTimestamp,
            Stopwatch.GetTimestamp()).TotalMilliseconds;
    }

    public void SetPhase(ProjectIndexOperationPhase phase)
        => _phase = phase;

    public void RecordInventory(TimeSpan elapsed, int sourceCount)
    {
        InventoryDurationMs += elapsed.TotalMilliseconds;
        SourceCount = sourceCount;
    }

    public void RecordCacheLoad(TimeSpan elapsed, ProjectIndexCacheLoadStatus status)
    {
        CacheLoadDurationMs += elapsed.TotalMilliseconds;
        CacheLoadStatus = status;
    }

    public void AddCacheLoadDuration(TimeSpan elapsed)
        => CacheLoadDurationMs += elapsed.TotalMilliseconds;

    public void RecordFingerprint(TimeSpan elapsed)
    {
        FingerprintedSources = SaturatingIncrement(FingerprintedSources);
        FingerprintDurationMs += elapsed.TotalMilliseconds;
    }

    public void RecordContentChangedExisting()
        => ContentChangedExistingSources = SaturatingIncrement(ContentChangedExistingSources);

    public void RecordBaselineShardLoad(
        bool usedInMemoryShard,
        bool attemptedPersistentShard,
        TimeSpan elapsed)
    {
        BaselineShardLoadAttempts = SaturatingIncrement(BaselineShardLoadAttempts);
        if (usedInMemoryShard)
        {
            InMemoryShardLoads = SaturatingIncrement(InMemoryShardLoads);
        }

        if (attemptedPersistentShard)
        {
            PersistentShardLoadAttempts = SaturatingIncrement(PersistentShardLoadAttempts);
        }

        ShardLoadDurationMs += elapsed.TotalMilliseconds;
    }

    public void RecordRebuiltShard()
        => RebuiltShards = SaturatingIncrement(RebuiltShards);

    public void RecordPublication(TimeSpan elapsed, bool succeeded)
    {
        CacheWriteAttempted = true;
        CacheWriteSucceeded = succeeded;
        PublicationDurationMs += elapsed.TotalMilliseconds;
    }

    public void RecordPersistenceUnavailable(ProjectIndexPersistenceUnavailableStage stage)
    {
        PersistenceUnavailableIncidentCount = SaturatingIncrement(PersistenceUnavailableIncidentCount);

        if (stage == ProjectIndexPersistenceUnavailableStage.BaselineShardLoad)
        {
            BaselineShardPersistenceUnavailableCount =
                SaturatingIncrement(BaselineShardPersistenceUnavailableCount);
        }
        else if (stage == ProjectIndexPersistenceUnavailableStage.Publication)
        {
            PublicationPersistenceUnavailable = true;
        }

        if (_persistenceBreadcrumbWritten)
        {
            return;
        }

        _persistenceBreadcrumbWritten = true;
        _diagnosticLogging.WriteEvent(
            "index_persistence_unavailable",
            new IndexPersistenceUnavailableDetails(
                _context.Trigger,
                _context.WorkloadOperationId,
                _context.WorkspaceGeneration,
                _context.DirtyVersion,
                _context.DirtySignalCount,
                stage));
    }

    public IndexOperationCorrelationDetails CreateCorrelationDetails()
        => new(
            _context.Trigger,
            _context.WorkloadOperationId,
            _context.WorkspaceGeneration,
            _context.DirtyVersion,
            _context.DirtySignalCount,
            ForceFullSourceValidation,
            ForcedFingerprintPathCount);

    public IndexPreflightCompletedDetails CreatePreflightCompletedDetails()
        => new(
            _context.Trigger,
            _context.WorkloadOperationId,
            _context.WorkspaceGeneration,
            _context.DirtyVersion,
            _context.DirtySignalCount,
            ForceFullSourceValidation,
            ForcedFingerprintPathCount,
            SourceCount,
            CacheLoadStatus,
            InventoryDurationMs,
            CacheLoadDurationMs);

    public IndexCacheFormatIncompatibleDetails CreateCacheFormatIncompatibleDetails(
        int observedCacheFormatVersion,
        int expectedCacheFormatVersion)
        => new(
            _context.Trigger,
            _context.WorkloadOperationId,
            _context.WorkspaceGeneration,
            _context.DirtyVersion,
            _context.DirtySignalCount,
            observedCacheFormatVersion,
            expectedCacheFormatVersion);

    public IndexReconciliationPlannedDetails CreatePlanningDetails()
        => new(
            _context.Trigger,
            _context.WorkloadOperationId,
            _context.WorkspaceGeneration,
            _context.DirtyVersion,
            _context.DirtySignalCount,
            ForceFullSourceValidation,
            ForcedFingerprintPathCount,
            AddedSources,
            DeletedSources,
            MetadataChangedSources,
            AffectedShards);

    public IndexPublicationStartedDetails CreatePublicationStartedDetails(int rewrittenShards)
        => new(
            _context.Trigger,
            _context.WorkloadOperationId,
            _context.WorkspaceGeneration,
            _context.DirtyVersion,
            _context.DirtySignalCount,
            rewrittenShards);

    public IndexGenerationPublishedDetails CreateGenerationPublishedDetails(
        ProjectIndexGeneration generation,
        bool generationReused)
        => new(
            _context.Trigger,
            _context.WorkloadOperationId,
            _context.WorkspaceGeneration,
            _context.DirtyVersion,
            _context.DirtySignalCount,
            generation.GenerationId,
            generation.PersistenceState,
            generationReused);

    public ProjectIndexOperationSummaryDetails CreateTerminalDetails(
        ProjectIndexOperationOutcome outcome,
        ProjectIndexTerminalStatus terminalStatus,
        ProjectIndexGeneration? resultGeneration,
        bool? generationReused,
        DiagnosticResourceSnapshot resourceSnapshot = default)
        => new(
            _context.Trigger,
            _context.WorkloadOperationId,
            _context.WorkspaceGeneration,
            _context.DirtyVersion,
            _context.DirtySignalCount,
            ForceFullSourceValidation,
            ForcedFingerprintPathCount,
            SourceCount,
            AddedSources,
            DeletedSources,
            MetadataChangedSources,
            FingerprintedSources,
            ContentChangedExistingSources,
            AffectedShards,
            BaselineShardLoadAttempts,
            InMemoryShardLoads,
            PersistentShardLoadAttempts,
            RebuiltShards,
            RewrittenShards,
            generationReused,
            resultGeneration?.GenerationId,
            CacheLoadStatus,
            CacheWriteAttempted,
            CacheWriteSucceeded,
            resultGeneration?.PersistenceState,
            PersistenceUnavailableIncidentCount,
            BaselineShardPersistenceUnavailableCount,
            PublicationPersistenceUnavailable,
            outcome,
            terminalStatus,
            _phase,
            InventoryDurationMs,
            CacheLoadDurationMs,
            FingerprintDurationMs,
            ShardLoadDurationMs,
            PublicationDurationMs,
            _totalDurationMs
                ?? Stopwatch.GetElapsedTime(
                    _totalStartedTimestamp,
                    Stopwatch.GetTimestamp()).TotalMilliseconds,
            resourceSnapshot.WorkingSetBytes,
            resourceSnapshot.ManagedMemoryBytes);

    private static int SaturatingIncrement(int value)
        => value == int.MaxValue ? int.MaxValue : value + 1;
}

internal enum ProjectIndexOperationPhase
{
    Started,
    Preflight,
    Planning,
    ShardReconciliation,
    Fingerprinting,
    Publication,
    PublishingCurrentGeneration,
    Completed,
}

internal enum ProjectIndexOperationOutcome
{
    Unknown,
    ColdBuild,
    WarmReuse,
    Reconciled,
    GenerationReuse,
}

internal enum ProjectIndexTerminalStatus
{
    Succeeded,
    Canceled,
    Faulted,
}

internal enum ProjectIndexCacheLoadStatus
{
    NotApplicable,
    Valid,
    Miss,
    Invalid,
    IncompatibleFormat,
    PersistenceUnavailable,
}

internal enum ProjectIndexPersistenceUnavailableStage
{
    CacheAuthority,
    CacheLoad,
    CacheLocation,
    BaselineShardLoad,
    Publication,
}

internal sealed record IndexOperationCorrelationDetails(
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long? DirtyVersion,
    int DirtySignalCount,
    bool ForceFullSourceValidation,
    int ForcedFingerprintPathCount);

internal sealed record IndexPreflightCompletedDetails(
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long? DirtyVersion,
    int DirtySignalCount,
    bool ForceFullSourceValidation,
    int ForcedFingerprintPathCount,
    int SourceCount,
    ProjectIndexCacheLoadStatus CacheLoadStatus,
    double InventoryDurationMs,
    double CacheLoadDurationMs);

internal sealed record IndexCacheFormatIncompatibleDetails(
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long? DirtyVersion,
    int DirtySignalCount,
    int ObservedCacheFormatVersion,
    int ExpectedCacheFormatVersion);

internal sealed record IndexReconciliationPlannedDetails(
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long? DirtyVersion,
    int DirtySignalCount,
    bool ForceFullSourceValidation,
    int ForcedFingerprintPathCount,
    int AddedSources,
    int DeletedSources,
    int MetadataChangedSources,
    int AffectedShards);

internal sealed record IndexPublicationStartedDetails(
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long? DirtyVersion,
    int DirtySignalCount,
    int RewrittenShards);

internal sealed record IndexGenerationPublishedDetails(
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long? DirtyVersion,
    int DirtySignalCount,
    string ResultGenerationId,
    ProjectIndexPersistenceState PersistenceState,
    bool GenerationReused);

internal sealed record IndexPersistenceUnavailableDetails(
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long? DirtyVersion,
    int DirtySignalCount,
    ProjectIndexPersistenceUnavailableStage Stage);

internal sealed record ProjectIndexOperationSummaryDetails(
    ProjectIndexOperationTrigger Trigger,
    long WorkloadOperationId,
    long WorkspaceGeneration,
    long? DirtyVersion,
    int DirtySignalCount,
    bool ForceFullSourceValidation,
    int ForcedFingerprintPathCount,
    int SourceCount,
    int AddedSources,
    int DeletedSources,
    int MetadataChangedSources,
    int FingerprintedSources,
    int ContentChangedExistingSources,
    int AffectedShards,
    int BaselineShardLoadAttempts,
    int InMemoryShardLoads,
    int PersistentShardLoadAttempts,
    int RebuiltShards,
    int RewrittenShards,
    bool? GenerationReused,
    string? ResultGenerationId,
    ProjectIndexCacheLoadStatus CacheLoadStatus,
    bool CacheWriteAttempted,
    bool CacheWriteSucceeded,
    ProjectIndexPersistenceState? PersistenceState,
    int PersistenceUnavailableIncidentCount,
    int BaselineShardPersistenceUnavailableCount,
    bool PublicationPersistenceUnavailable,
    ProjectIndexOperationOutcome Outcome,
    ProjectIndexTerminalStatus TerminalStatus,
    ProjectIndexOperationPhase Phase,
    double InventoryDurationMs,
    double CacheLoadDurationMs,
    double FingerprintDurationMs,
    double ShardLoadDurationMs,
    double PublicationDurationMs,
    double TotalDurationMs,
    long? WorkingSetBytes,
    long? ManagedMemoryBytes);
