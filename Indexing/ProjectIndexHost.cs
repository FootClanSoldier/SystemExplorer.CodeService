using System.Diagnostics;

namespace SystemExplorer.CodeService;

internal sealed class ProjectIndexHost : IDisposable
{
    private readonly object _sync = new();
    private readonly IndexCacheStore _cacheStore;
    private readonly IndexCacheGarbageCollector _garbageCollector;
    private readonly DiagnosticLogging _diagnosticLogging;

    private ProjectIndexHostState _state = ProjectIndexHostState.Uninitialized;
    private ProjectIndexGeneration? _currentGeneration;
    private WorkspaceCacheAccess? _cacheAccess;
    private WorkspaceIdentity? _cacheWorkspaceIdentity;
    private bool _cacheAccessEstablishing;
    private long _reconciliationGeneration;
    private bool _disposed;

    public ProjectIndexHost(DiagnosticLogging diagnosticLogging)
    {
        _diagnosticLogging = diagnosticLogging
            ?? throw new ArgumentNullException(nameof(diagnosticLogging));
        _cacheStore = new IndexCacheStore();
        _garbageCollector = new IndexCacheGarbageCollector(_cacheStore);
    }

    public async Task<ProjectIndexGeneration> InitializeOrReconcileAsync(
        WorkspaceProjectSnapshot projectSnapshot,
        ProjectIndexReconciliationHints reconciliationHints,
        ProjectIndexOperationContext operationContext,
        CancellationToken serviceWorkShutdownToken)
    {
        ArgumentNullException.ThrowIfNull(projectSnapshot);
        ArgumentNullException.ThrowIfNull(reconciliationHints);

        ProjectIndexOperationTelemetry? telemetry = CreateTelemetry(
            operationContext,
            reconciliationHints);
        ProjectIndexOperationOutcome outcome = ProjectIndexOperationOutcome.Unknown;
        long reconciliationGeneration = BeginReconciliation();

        telemetry?.SetPhase(ProjectIndexOperationPhase.Preflight);
        WriteEventIfEnabled("index_preflight_started", telemetry?.CreateCorrelationDetails());

        try
        {
            serviceWorkShutdownToken.ThrowIfCancellationRequested();

            long inventoryStarted = telemetry is null ? 0 : Stopwatch.GetTimestamp();
            SourceInventory inventory = BuildSourceInventory(
                projectSnapshot,
                serviceWorkShutdownToken);
            if (telemetry is not null)
            {
                telemetry.RecordInventory(
                    Stopwatch.GetElapsedTime(inventoryStarted, Stopwatch.GetTimestamp()),
                    inventory.Sources.Count);
            }

            WorkspaceCacheAccess? cacheAccess = null;
            IndexCacheLoadResult cacheLoad = IndexCacheLoadResult.PersistenceUnavailable();

            try
            {
                cacheAccess = GetOrCreateCacheAccess(projectSnapshot.WorkspaceIdentity);

                long cacheLoadStarted = telemetry is null ? 0 : Stopwatch.GetTimestamp();
                try
                {
                    if (cacheAccess.CanReadPersistentCache)
                    {
                        cacheLoad = await _cacheStore.TryLoadCurrentAsync(
                            cacheAccess,
                            projectSnapshot.WorkspaceIdentity,
                            serviceWorkShutdownToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (telemetry is not null)
                    {
                        telemetry.AddCacheLoadDuration(
                            Stopwatch.GetElapsedTime(cacheLoadStarted, Stopwatch.GetTimestamp()));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                cacheAccess = null;
                cacheLoad = IndexCacheLoadResult.PersistenceUnavailable();
            }

            if (telemetry is not null)
            {
                telemetry.CacheLoadStatus = MapCacheLoadStatus(cacheLoad.Status);
                if (cacheLoad.Status == IndexCacheLoadStatus.PersistenceUnavailable)
                {
                    telemetry.RecordPersistenceUnavailable(
                        cacheAccess is null
                            ? ProjectIndexPersistenceUnavailableStage.CacheLocation
                            : cacheAccess.CoordinationMode == CacheCoordinationMode.Unavailable
                                ? ProjectIndexPersistenceUnavailableStage.CacheAuthority
                                : ProjectIndexPersistenceUnavailableStage.CacheLoad);
                }

                _diagnosticLogging.WriteEvent(
                    "index_preflight_completed",
                    telemetry.CreatePreflightCompletedDetails());
            }

            if (cacheLoad.Status == IndexCacheLoadStatus.Valid
                && cacheLoad.Manifest is IndexManifest warmManifest)
            {
                ProjectIndexGeneration persistentBaseline = CreateGeneration(
                    projectSnapshot.WorkspaceIdentity,
                    warmManifest,
                    cacheAccess,
                    changedShards: null,
                    baselineGeneration: null,
                    ProjectIndexPersistenceState.Persisted);

                if (IsExactMetadataMatch(warmManifest, inventory)
                    && !reconciliationHints.RequiresSourceValidation)
                {
                    outcome = ProjectIndexOperationOutcome.WarmReuse;
                    telemetry?.SetPhase(ProjectIndexOperationPhase.PublishingCurrentGeneration);
                    PublishCurrentGeneration(reconciliationGeneration, persistentBaseline);
                    if (cacheAccess is not null && cacheAccess.CanCollectGarbage)
                    {
                        await RunCacheGarbageCollectionAsync(
                            cacheAccess,
                            warmManifest,
                            IndexCacheGarbageCollectionTrigger.WarmInitialization,
                            serviceWorkShutdownToken).ConfigureAwait(false);
                    }

                    telemetry?.StopTotalTimer();
                    WriteEventIfEnabled("index_cache_warm", telemetry?.CreateCorrelationDetails());
                    WriteCompletedTelemetry(
                        telemetry,
                        outcome,
                        persistentBaseline,
                        generationReused: true);
                    return persistentBaseline;
                }

                outcome = ProjectIndexOperationOutcome.Reconciled;
                telemetry?.SetPhase(ProjectIndexOperationPhase.Planning);
                WriteEventIfEnabled(
                    "index_reconciliation_started",
                    telemetry?.CreateCorrelationDetails());

                ProjectIndexGeneration reconciled = await ReconcileAgainstBaselineAsync(
                    projectSnapshot.WorkspaceIdentity,
                    inventory,
                    persistentBaseline,
                    cacheAccess,
                    reconciliationHints,
                    telemetry,
                    serviceWorkShutdownToken).ConfigureAwait(false);

                bool generationReused = ReferenceEquals(reconciled, persistentBaseline);
                outcome = generationReused
                    ? ProjectIndexOperationOutcome.GenerationReuse
                    : ProjectIndexOperationOutcome.Reconciled;

                telemetry?.SetPhase(ProjectIndexOperationPhase.PublishingCurrentGeneration);
                PublishCurrentGeneration(reconciliationGeneration, reconciled);
                if (generationReused
                    && reconciled.PersistenceState == ProjectIndexPersistenceState.Persisted
                    && cacheAccess is not null
                    && cacheAccess.CanCollectGarbage)
                {
                    await RunCacheGarbageCollectionAsync(
                        cacheAccess,
                        reconciled.Manifest,
                        IndexCacheGarbageCollectionTrigger.WarmInitialization,
                        serviceWorkShutdownToken).ConfigureAwait(false);
                }

                telemetry?.StopTotalTimer();
                if (!generationReused)
                {
                    WriteGenerationPublished(telemetry, reconciled, generationReused: false);
                }

                WriteCompletedTelemetry(
                    telemetry,
                    outcome,
                    reconciled,
                    generationReused);
                return reconciled;
            }

            switch (cacheLoad.Status)
            {
                case IndexCacheLoadStatus.Miss:
                    WriteEventIfEnabled("index_cache_miss", telemetry?.CreateCorrelationDetails());
                    break;
                case IndexCacheLoadStatus.Invalid:
                    WriteEventIfEnabled("index_cache_invalid", telemetry?.CreateCorrelationDetails());
                    break;
                case IndexCacheLoadStatus.IncompatibleFormat:
                    if (cacheLoad.ObservedCacheFormatVersion is int observedCacheFormatVersion)
                    {
                        WriteEventIfEnabled(
                            "index_cache_format_incompatible",
                            telemetry?.CreateCacheFormatIncompatibleDetails(
                                observedCacheFormatVersion,
                                IndexCacheFormat.CacheFormatVersion));
                    }
                    break;
                case IndexCacheLoadStatus.PersistenceUnavailable:
                    // The operation-local telemetry writes at most one sparse persistence breadcrumb.
                    break;
            }

            outcome = ProjectIndexOperationOutcome.ColdBuild;
            telemetry?.SetPhase(ProjectIndexOperationPhase.Planning);
            WriteEventIfEnabled(
                "index_reconciliation_started",
                telemetry?.CreateCorrelationDetails());

            ProjectIndexGeneration coldGeneration = await BuildColdGenerationAsync(
                projectSnapshot.WorkspaceIdentity,
                inventory,
                cacheAccess,
                telemetry,
                serviceWorkShutdownToken).ConfigureAwait(false);

            telemetry?.SetPhase(ProjectIndexOperationPhase.PublishingCurrentGeneration);
            PublishCurrentGeneration(reconciliationGeneration, coldGeneration);
            telemetry?.StopTotalTimer();
            WriteGenerationPublished(telemetry, coldGeneration, generationReused: false);
            WriteCompletedTelemetry(
                telemetry,
                outcome,
                coldGeneration,
                generationReused: false);
            return coldGeneration;
        }
        catch (ProjectIndexPublicationCanceledException)
        {
            WriteCanceledTelemetry(telemetry, outcome);
            throw;
        }
        catch (OperationCanceledException)
        {
            WriteCanceledTelemetry(telemetry, outcome);
            throw;
        }
        catch (Exception exception)
        {
            MarkInitialReconciliationFault(reconciliationGeneration);
            WriteFaultTelemetry(telemetry, outcome, exception);
            throw;
        }
    }

    public async Task<ProjectIndexGeneration> ReconcileCurrentAsync(
        WorkspaceProjectSnapshot projectSnapshot,
        ProjectIndexReconciliationHints reconciliationHints,
        ProjectIndexOperationContext operationContext,
        CancellationToken serviceWorkShutdownToken)
    {
        ArgumentNullException.ThrowIfNull(projectSnapshot);
        ArgumentNullException.ThrowIfNull(reconciliationHints);

        ProjectIndexOperationTelemetry? telemetry = CreateTelemetry(
            operationContext,
            reconciliationHints);
        ProjectIndexOperationOutcome outcome = ProjectIndexOperationOutcome.Unknown;
        ProjectIndexGeneration baselineGeneration;
        long reconciliationGeneration;

        lock (_sync)
        {
            if (_disposed || _state == ProjectIndexHostState.ShuttingDown)
            {
                throw new ProjectIndexPublicationCanceledException();
            }

            baselineGeneration = _currentGeneration
                ?? throw new InvalidOperationException(
                    "runtime index reconciliation requires an existing current generation.");

            if (!baselineGeneration.WorkspaceIdentity.Equals(projectSnapshot.WorkspaceIdentity))
            {
                throw new InvalidOperationException(
                    "runtime index reconciliation workspace identity does not match the current generation.");
            }

            _state = ProjectIndexHostState.Reconciling;
            reconciliationGeneration = ++_reconciliationGeneration;
        }

        telemetry?.SetPhase(ProjectIndexOperationPhase.Preflight);
        WriteEventIfEnabled("index_preflight_started", telemetry?.CreateCorrelationDetails());

        try
        {
            serviceWorkShutdownToken.ThrowIfCancellationRequested();

            long inventoryStarted = telemetry is null ? 0 : Stopwatch.GetTimestamp();
            SourceInventory inventory = BuildSourceInventory(
                projectSnapshot,
                serviceWorkShutdownToken);
            if (telemetry is not null)
            {
                telemetry.RecordInventory(
                    Stopwatch.GetElapsedTime(inventoryStarted, Stopwatch.GetTimestamp()),
                    inventory.Sources.Count);
                _diagnosticLogging.WriteEvent(
                    "index_preflight_completed",
                    telemetry.CreatePreflightCompletedDetails());
            }

            telemetry?.SetPhase(ProjectIndexOperationPhase.Planning);
            WriteEventIfEnabled(
                "index_reconciliation_started",
                telemetry?.CreateCorrelationDetails());

            if (IsExactMetadataMatch(baselineGeneration.Manifest, inventory)
                && !reconciliationHints.RequiresSourceValidation)
            {
                outcome = ProjectIndexOperationOutcome.GenerationReuse;
                telemetry?.SetPhase(ProjectIndexOperationPhase.PublishingCurrentGeneration);
                PublishCurrentGeneration(reconciliationGeneration, baselineGeneration);
                telemetry?.StopTotalTimer();
                WriteCompletedTelemetry(
                    telemetry,
                    outcome,
                    baselineGeneration,
                    generationReused: true);
                return baselineGeneration;
            }

            outcome = ProjectIndexOperationOutcome.Reconciled;
            WorkspaceCacheAccess? cacheAccess = null;
            try
            {
                cacheAccess = GetOrCreateCacheAccess(projectSnapshot.WorkspaceIdentity);
            }
            catch (ProjectIndexPublicationCanceledException)
            {
                throw;
            }
            catch
            {
                telemetry?.RecordPersistenceUnavailable(
                    ProjectIndexPersistenceUnavailableStage.CacheLocation);
            }

            if (cacheAccess is not null && !cacheAccess.CanWritePersistentCache)
            {
                telemetry?.RecordPersistenceUnavailable(
                    cacheAccess.CoordinationMode == CacheCoordinationMode.Unavailable
                        ? ProjectIndexPersistenceUnavailableStage.CacheAuthority
                        : ProjectIndexPersistenceUnavailableStage.CacheLoad);
            }

            ProjectIndexGeneration candidate = await ReconcileAgainstBaselineAsync(
                projectSnapshot.WorkspaceIdentity,
                inventory,
                baselineGeneration,
                cacheAccess,
                reconciliationHints,
                telemetry,
                serviceWorkShutdownToken).ConfigureAwait(false);

            bool generationReused = ReferenceEquals(candidate, baselineGeneration);
            outcome = generationReused
                ? ProjectIndexOperationOutcome.GenerationReuse
                : ProjectIndexOperationOutcome.Reconciled;

            telemetry?.SetPhase(ProjectIndexOperationPhase.PublishingCurrentGeneration);
            PublishCurrentGeneration(reconciliationGeneration, candidate);
            telemetry?.StopTotalTimer();
            if (!generationReused)
            {
                WriteGenerationPublished(telemetry, candidate, generationReused: false);
            }

            WriteCompletedTelemetry(
                telemetry,
                outcome,
                candidate,
                generationReused);
            return candidate;
        }
        catch (ProjectIndexPublicationCanceledException)
        {
            RestorePreviousGenerationAfterRuntimeFailure(
                reconciliationGeneration,
                baselineGeneration);
            WriteCanceledTelemetry(telemetry, outcome);
            throw;
        }
        catch (OperationCanceledException)
        {
            RestorePreviousGenerationAfterRuntimeFailure(
                reconciliationGeneration,
                baselineGeneration);
            WriteCanceledTelemetry(telemetry, outcome);
            throw;
        }
        catch (Exception exception)
        {
            RestorePreviousGenerationAfterRuntimeFailure(
                reconciliationGeneration,
                baselineGeneration);
            WriteFaultTelemetry(telemetry, outcome, exception);
            throw;
        }
    }

    public ProjectIndexGeneration? GetCurrentGenerationSnapshot()
    {
        lock (_sync)
        {
            return _currentGeneration;
        }
    }

    public void BeginShutdown()
    {
        lock (_sync)
        {
            if (_disposed || _state == ProjectIndexHostState.ShuttingDown)
            {
                return;
            }

            _state = ProjectIndexHostState.ShuttingDown;
            _reconciliationGeneration++;
        }
    }

    public void Dispose()
    {
        WorkspaceCacheAccess? cacheAccess;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _state = ProjectIndexHostState.ShuttingDown;
            _currentGeneration = null;
            cacheAccess = _cacheAccess;
            _cacheAccess = null;
            _cacheWorkspaceIdentity = null;
            _reconciliationGeneration++;
        }

        if (cacheAccess is not null)
        {
            try
            {
                cacheAccess.Dispose();
            }
            catch (Exception exception)
            {
                _diagnosticLogging.WriteFault(
                    "index_cache_authority_release_fault",
                    exception);
            }
        }
    }

    private long BeginReconciliation()
    {
        lock (_sync)
        {
            if (_disposed || _state == ProjectIndexHostState.ShuttingDown)
            {
                throw new ProjectIndexPublicationCanceledException();
            }

            _state = ProjectIndexHostState.Reconciling;
            return ++_reconciliationGeneration;
        }
    }

    private async Task<ProjectIndexGeneration> BuildColdGenerationAsync(
        WorkspaceIdentity workspaceIdentity,
        SourceInventory inventory,
        WorkspaceCacheAccess? cacheAccess,
        ProjectIndexOperationTelemetry? telemetry,
        CancellationToken cancellationToken)
    {
        string generationId = IndexCacheStore.CreateGenerationId();
        Dictionary<string, SourceFileMetadata> metadataByPath = CloneMetadata(inventory);
        Dictionary<int, List<IndexShardRecord>> recordsByShard = new();

        if (telemetry is not null)
        {
            telemetry.AddedSources = inventory.Sources.Count;
            telemetry.AffectedShards = inventory.DistinctShardCount;
            _diagnosticLogging.WriteEvent(
                "index_reconciliation_planned",
                telemetry.CreatePlanningDetails());
            telemetry.SetPhase(ProjectIndexOperationPhase.Fingerprinting);
        }

        foreach (SourceFileMetadata source in inventory.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceFingerprintReadResult fingerprint = await ComputeFingerprintAsync(
                workspaceIdentity,
                source.RelativePath,
                telemetry,
                cancellationToken).ConfigureAwait(false);

            metadataByPath[source.RelativePath] = fingerprint.Metadata;
            if (!recordsByShard.TryGetValue(source.ShardId, out List<IndexShardRecord>? records))
            {
                records = new List<IndexShardRecord>();
                recordsByShard.Add(source.ShardId, records);
            }

            records.Add(new IndexShardRecord(
                source.RelativePath,
                fingerprint.ContentHashSha256));
        }

        Dictionary<int, IndexShard> changedShards = new();
        List<IndexManifestShardReference> shardReferences = new();

        foreach ((int shardId, List<IndexShardRecord> records) in recordsByShard.OrderBy(static pair => pair.Key))
        {
            IndexShard shard = new(shardId, generationId, records);
            changedShards.Add(shardId, shard);
            shardReferences.Add(new IndexManifestShardReference(
                shardId,
                IndexCacheStore.GetShardFileName(shardId, generationId),
                shard.Records.Count));
        }

        if (telemetry is not null)
        {
            telemetry.RewrittenShards = changedShards.Count;
        }

        IndexManifest manifest = CreateManifest(
            workspaceIdentity,
            cacheAccess?.Location,
            generationId,
            metadataByPath.Values,
            shardReferences);

        ProjectIndexPersistenceState persistenceState = ProjectIndexPersistenceState.MemoryOnly;
        if (cacheAccess is not null && cacheAccess.CanWritePersistentCache)
        {
            telemetry?.SetPhase(ProjectIndexOperationPhase.Publication);
            if (telemetry is not null)
            {
                _diagnosticLogging.WriteEvent(
                    "index_publication_started",
                    telemetry.CreatePublicationStartedDetails(changedShards.Count));
            }

            long publicationStarted = telemetry is null ? 0 : Stopwatch.GetTimestamp();
            bool publicationPersisted = false;
            try
            {
                IndexCachePublicationResult publication = await _cacheStore.TryPublishAsync(
                    cacheAccess,
                    manifest,
                    changedShards,
                    cancellationToken).ConfigureAwait(false);

                publicationPersisted = publication.IsPersisted;
                telemetry?.RecordPublication(
                    telemetry is null
                        ? TimeSpan.Zero
                        : Stopwatch.GetElapsedTime(publicationStarted, Stopwatch.GetTimestamp()),
                    publicationPersisted);

                if (publicationPersisted)
                {
                    persistenceState = ProjectIndexPersistenceState.Persisted;
                }
                else
                {
                    telemetry?.RecordPersistenceUnavailable(
                        ProjectIndexPersistenceUnavailableStage.Publication);
                }
            }
            catch
            {
                if (telemetry is not null)
                {
                    telemetry.RecordPublication(
                        Stopwatch.GetElapsedTime(publicationStarted, Stopwatch.GetTimestamp()),
                        succeeded: false);
                }

                throw;
            }

            if (publicationPersisted && cacheAccess.CanCollectGarbage)
            {
                await RunCacheGarbageCollectionAsync(
                    cacheAccess,
                    manifest,
                    IndexCacheGarbageCollectionTrigger.PostPublication,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return CreateGeneration(
            workspaceIdentity,
            manifest,
            cacheAccess,
            changedShards,
            baselineGeneration: null,
            persistenceState);
    }

    private async Task<ProjectIndexGeneration> ReconcileAgainstBaselineAsync(
        WorkspaceIdentity workspaceIdentity,
        SourceInventory inventory,
        ProjectIndexGeneration baselineGeneration,
        WorkspaceCacheAccess? cacheAccess,
        ProjectIndexReconciliationHints reconciliationHints,
        ProjectIndexOperationTelemetry? telemetry,
        CancellationToken cancellationToken)
    {
        IndexManifest baselineManifest = baselineGeneration.Manifest;
        Dictionary<string, SourceFileMetadata> currentMetadata = CloneMetadata(inventory);
        Dictionary<string, IndexManifestSource> previousSources = baselineManifest.Sources.ToDictionary(
            static source => source.RelativePath,
            IndexPathValidation.SourcePathComparer);
        Dictionary<string, SourceFileMetadata> currentSources = inventory.Sources.ToDictionary(
            static source => source.RelativePath,
            IndexPathValidation.SourcePathComparer);
        Dictionary<int, IndexManifestShardReference> candidateShardReferences = baselineManifest.Shards.ToDictionary(
            static shard => shard.ShardId);
        Dictionary<int, ShardMutationPlan> plans = new();

        int addedSources = 0;
        int metadataChangedSources = 0;
        foreach (SourceFileMetadata currentSource in inventory.Sources)
        {
            if (!previousSources.TryGetValue(currentSource.RelativePath, out IndexManifestSource previousSource))
            {
                addedSources = SaturatingIncrement(addedSources);
                GetPlan(plans, currentSource.ShardId).Added.Add(currentSource.RelativePath);
                continue;
            }

            bool metadataChanged = previousSource.Length != currentSource.Length
                || previousSource.LastWriteTimeUtcTicks != currentSource.LastWriteTimeUtcTicks
                || previousSource.ShardId != currentSource.ShardId;

            if (metadataChanged)
            {
                metadataChangedSources = SaturatingIncrement(metadataChangedSources);
            }

            if (metadataChanged || reconciliationHints.RequiresFingerprint(currentSource.RelativePath))
            {
                GetPlan(plans, previousSource.ShardId)
                    .PotentiallyModified.Add(currentSource.RelativePath);
            }
        }

        int deletedSources = 0;
        foreach (IndexManifestSource previousSource in baselineManifest.Sources)
        {
            if (!currentSources.ContainsKey(previousSource.RelativePath))
            {
                deletedSources = SaturatingIncrement(deletedSources);
                GetPlan(plans, previousSource.ShardId).Deleted.Add(previousSource.RelativePath);
            }
        }

        if (telemetry is not null)
        {
            telemetry.AddedSources = addedSources;
            telemetry.DeletedSources = deletedSources;
            telemetry.MetadataChangedSources = metadataChangedSources;
            telemetry.AffectedShards = plans.Count;
            _diagnosticLogging.WriteEvent(
                "index_reconciliation_planned",
                telemetry.CreatePlanningDetails());
            telemetry.SetPhase(ProjectIndexOperationPhase.ShardReconciliation);
        }

        Dictionary<int, IndexShard> changedShards = new();
        string? candidateGenerationId = null;

        foreach ((int shardId, ShardMutationPlan plan) in plans.OrderBy(static pair => pair.Key))
        {
            telemetry?.SetPhase(ProjectIndexOperationPhase.ShardReconciliation);
            cancellationToken.ThrowIfCancellationRequested();
            List<SourceFileMetadata> currentShardSources = currentMetadata.Values
                .Where(source => source.ShardId == shardId)
                .OrderBy(static source => source.RelativePath, StringComparer.Ordinal)
                .ToList();

            if (currentShardSources.Count == 0)
            {
                candidateShardReferences.Remove(shardId);
                continue;
            }

            Dictionary<string, IndexShardRecord> records;
            bool localRebuild = false;

            if (baselineGeneration.ShardCatalog.TryGetValue(
                    shardId,
                    out ProjectIndexShardSnapshot? baselineShardSnapshot))
            {
                bool usedInMemoryShard = baselineShardSnapshot.InMemoryShard is not null;
                bool attemptedPersistentShard = !usedInMemoryShard
                    && baselineShardSnapshot.PersistentPath is not null;
                long shardLoadStarted = telemetry is null ? 0 : Stopwatch.GetTimestamp();
                IndexShardLoadResult shardLoad;
                try
                {
                    shardLoad = await LoadBaselineShardAsync(
                        cacheAccess,
                        baselineManifest,
                        baselineShardSnapshot,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (telemetry is not null)
                    {
                        telemetry.RecordBaselineShardLoad(
                            usedInMemoryShard,
                            attemptedPersistentShard,
                            Stopwatch.GetElapsedTime(shardLoadStarted, Stopwatch.GetTimestamp()));
                    }
                }

                if (shardLoad.Status == IndexShardLoadStatus.Valid
                    && shardLoad.Shard is IndexShard loadedShard)
                {
                    records = loadedShard.Records.ToDictionary(
                        static record => record.RelativePath,
                        IndexPathValidation.SourcePathComparer);
                }
                else
                {
                    records = new Dictionary<string, IndexShardRecord>(IndexPathValidation.SourcePathComparer);
                    localRebuild = true;

                    if (shardLoad.Status == IndexShardLoadStatus.PersistenceUnavailable)
                    {
                        telemetry?.RecordPersistenceUnavailable(
                            ProjectIndexPersistenceUnavailableStage.BaselineShardLoad);
                    }
                }
            }
            else
            {
                records = new Dictionary<string, IndexShardRecord>(IndexPathValidation.SourcePathComparer);
                localRebuild = true;
            }

            if (!localRebuild
                && plan.PotentiallyModified.Any(path => !records.ContainsKey(path)))
            {
                localRebuild = true;
            }

            bool shardContentChanged = localRebuild;

            if (localRebuild)
            {
                telemetry?.RecordRebuiltShard();
                records.Clear();

                foreach (SourceFileMetadata source in currentShardSources)
                {
                    SourceFingerprintReadResult fingerprint = await ComputeFingerprintAsync(
                        workspaceIdentity,
                        source.RelativePath,
                        telemetry,
                        cancellationToken).ConfigureAwait(false);
                    currentMetadata[source.RelativePath] = fingerprint.Metadata;
                    records[source.RelativePath] = new IndexShardRecord(
                        source.RelativePath,
                        fingerprint.ContentHashSha256);
                }
            }
            else
            {
                foreach (string deletedPath in plan.Deleted)
                {
                    shardContentChanged |= records.Remove(deletedPath);
                }

                foreach (string addedPath in plan.Added)
                {
                    SourceFingerprintReadResult fingerprint = await ComputeFingerprintAsync(
                        workspaceIdentity,
                        addedPath,
                        telemetry,
                        cancellationToken).ConfigureAwait(false);
                    currentMetadata[addedPath] = fingerprint.Metadata;
                    records[addedPath] = new IndexShardRecord(
                        addedPath,
                        fingerprint.ContentHashSha256);
                    shardContentChanged = true;
                }

                foreach (string modifiedPath in plan.PotentiallyModified)
                {
                    IndexShardRecord previousRecord = records[modifiedPath];
                    SourceFingerprintReadResult fingerprint = await ComputeFingerprintAsync(
                        workspaceIdentity,
                        modifiedPath,
                        telemetry,
                        cancellationToken).ConfigureAwait(false);
                    currentMetadata[modifiedPath] = fingerprint.Metadata;

                    if (!string.Equals(
                            previousRecord.ContentHashSha256,
                            fingerprint.ContentHashSha256,
                            StringComparison.Ordinal))
                    {
                        telemetry?.RecordContentChangedExisting();
                        records[modifiedPath] = new IndexShardRecord(
                            modifiedPath,
                            fingerprint.ContentHashSha256);
                        shardContentChanged = true;
                    }
                }
            }

            if (shardContentChanged)
            {
                candidateGenerationId ??= IndexCacheStore.CreateGenerationId();
                IndexShard changedShard = new(
                    shardId,
                    candidateGenerationId,
                    records.Values.ToArray());
                changedShards[shardId] = changedShard;
                candidateShardReferences[shardId] = new IndexManifestShardReference(
                    shardId,
                    IndexCacheStore.GetShardFileName(shardId, candidateGenerationId),
                    changedShard.Records.Count);
            }
        }

        if (telemetry is not null)
        {
            telemetry.RewrittenShards = changedShards.Count;
        }

        if (changedShards.Count == 0
            && IsExactManifestMetadataMatch(baselineManifest, currentMetadata.Values)
            && AreShardReferencesExactMatch(baselineManifest, candidateShardReferences.Values))
        {
            return baselineGeneration;
        }

        candidateGenerationId ??= IndexCacheStore.CreateGenerationId();
        IndexManifest candidateManifest = CreateManifest(
            workspaceIdentity,
            cacheAccess?.Location,
            candidateGenerationId,
            currentMetadata.Values,
            candidateShardReferences.Values);

        ProjectIndexPersistenceState persistenceState = ProjectIndexPersistenceState.MemoryOnly;
        if (cacheAccess is not null && cacheAccess.CanWritePersistentCache)
        {
            Dictionary<int, IndexShard> publicationShards = CreatePublicationShardSet(
                candidateManifest,
                changedShards,
                baselineGeneration);

            telemetry?.SetPhase(ProjectIndexOperationPhase.Publication);
            if (telemetry is not null)
            {
                _diagnosticLogging.WriteEvent(
                    "index_publication_started",
                    telemetry.CreatePublicationStartedDetails(changedShards.Count));
            }

            long publicationStarted = telemetry is null ? 0 : Stopwatch.GetTimestamp();
            bool publicationPersisted = false;
            try
            {
                IndexCachePublicationResult publication = await _cacheStore.TryPublishAsync(
                    cacheAccess,
                    candidateManifest,
                    publicationShards,
                    cancellationToken).ConfigureAwait(false);

                publicationPersisted = publication.IsPersisted;
                telemetry?.RecordPublication(
                    telemetry is null
                        ? TimeSpan.Zero
                        : Stopwatch.GetElapsedTime(publicationStarted, Stopwatch.GetTimestamp()),
                    publicationPersisted);

                if (publicationPersisted)
                {
                    persistenceState = ProjectIndexPersistenceState.Persisted;
                }
                else
                {
                    telemetry?.RecordPersistenceUnavailable(
                        ProjectIndexPersistenceUnavailableStage.Publication);
                }
            }
            catch
            {
                if (telemetry is not null)
                {
                    telemetry.RecordPublication(
                        Stopwatch.GetElapsedTime(publicationStarted, Stopwatch.GetTimestamp()),
                        succeeded: false);
                }

                throw;
            }

            if (publicationPersisted && cacheAccess.CanCollectGarbage)
            {
                await RunCacheGarbageCollectionAsync(
                    cacheAccess,
                    candidateManifest,
                    IndexCacheGarbageCollectionTrigger.PostPublication,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return CreateGeneration(
            workspaceIdentity,
            candidateManifest,
            cacheAccess,
            changedShards,
            baselineGeneration,
            persistenceState);
    }

    private async Task<SourceFingerprintReadResult> ComputeFingerprintAsync(
        WorkspaceIdentity workspaceIdentity,
        string relativePath,
        ProjectIndexOperationTelemetry? telemetry,
        CancellationToken cancellationToken)
    {
        long started = 0;
        if (telemetry is not null)
        {
            telemetry.SetPhase(ProjectIndexOperationPhase.Fingerprinting);
            started = Stopwatch.GetTimestamp();
        }

        try
        {
            return await SourceFileFingerprint.ComputeStableAsync(
                workspaceIdentity,
                relativePath,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (telemetry is not null)
            {
                telemetry.RecordFingerprint(
                    Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()));
            }
        }
    }
    private ProjectIndexOperationTelemetry? CreateTelemetry(
        ProjectIndexOperationContext operationContext,
        ProjectIndexReconciliationHints reconciliationHints)
        => _diagnosticLogging.IsEnabled
            ? new ProjectIndexOperationTelemetry(
                _diagnosticLogging,
                operationContext,
                reconciliationHints)
            : null;

    private void WriteEventIfEnabled<TDetails>(string eventName, TDetails? details)
        where TDetails : class
    {
        if (details is not null)
        {
            _diagnosticLogging.WriteEvent(eventName, details);
        }
    }

    private void WriteGenerationPublished(
        ProjectIndexOperationTelemetry? telemetry,
        ProjectIndexGeneration generation,
        bool generationReused)
    {
        if (telemetry is null)
        {
            return;
        }

        _diagnosticLogging.WriteEvent(
            "index_generation_published",
            telemetry.CreateGenerationPublishedDetails(generation, generationReused));
    }

    private void WriteCompletedTelemetry(
        ProjectIndexOperationTelemetry? telemetry,
        ProjectIndexOperationOutcome outcome,
        ProjectIndexGeneration resultGeneration,
        bool generationReused)
    {
        if (telemetry is null)
        {
            return;
        }

        telemetry.SetPhase(ProjectIndexOperationPhase.Completed);
        DiagnosticResourceSnapshot resources =
            DiagnosticResourceSnapshot.CaptureIfEnabled(_diagnosticLogging);
        _diagnosticLogging.WriteEvent(
            "index_reconciliation_completed",
            telemetry.CreateTerminalDetails(
                outcome,
                ProjectIndexTerminalStatus.Succeeded,
                resultGeneration,
                generationReused,
                resources));
    }

    private void WriteCanceledTelemetry(
        ProjectIndexOperationTelemetry? telemetry,
        ProjectIndexOperationOutcome outcome)
    {
        if (telemetry is null)
        {
            return;
        }

        telemetry.StopTotalTimer();
        _diagnosticLogging.WriteEvent(
            "index_reconciliation_canceled",
            telemetry.CreateTerminalDetails(
                outcome,
                ProjectIndexTerminalStatus.Canceled,
                resultGeneration: null,
                generationReused: null));
    }

    private void WriteFaultTelemetry(
        ProjectIndexOperationTelemetry? telemetry,
        ProjectIndexOperationOutcome outcome,
        Exception exception)
    {
        if (telemetry is null)
        {
            _diagnosticLogging.WriteFault("index_fault", exception);
            return;
        }

        telemetry.StopTotalTimer();
        _diagnosticLogging.WriteFault(
            "index_fault",
            exception,
            telemetry.CreateTerminalDetails(
                outcome,
                ProjectIndexTerminalStatus.Faulted,
                resultGeneration: null,
                generationReused: null));
    }

    private static ProjectIndexCacheLoadStatus MapCacheLoadStatus(IndexCacheLoadStatus status)
        => status switch
        {
            IndexCacheLoadStatus.Valid => ProjectIndexCacheLoadStatus.Valid,
            IndexCacheLoadStatus.Miss => ProjectIndexCacheLoadStatus.Miss,
            IndexCacheLoadStatus.Invalid => ProjectIndexCacheLoadStatus.Invalid,
            IndexCacheLoadStatus.IncompatibleFormat => ProjectIndexCacheLoadStatus.IncompatibleFormat,
            IndexCacheLoadStatus.PersistenceUnavailable =>
                ProjectIndexCacheLoadStatus.PersistenceUnavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "unknown cache load status."),
        };

    private static int SaturatingIncrement(int value)
        => value == int.MaxValue ? int.MaxValue : value + 1;

    private async Task<IndexShardLoadResult> LoadBaselineShardAsync(
        WorkspaceCacheAccess? cacheAccess,
        IndexManifest baselineManifest,
        ProjectIndexShardSnapshot baselineShardSnapshot,
        CancellationToken cancellationToken)
    {
        if (baselineShardSnapshot.InMemoryShard is IndexShard inMemoryShard)
        {
            return IndexShardLoadResult.Valid(
                inMemoryShard,
                baselineShardSnapshot.PersistentPath ?? baselineShardSnapshot.Reference.FileName);
        }

        if (baselineShardSnapshot.PersistentPath is string persistentPath)
        {
            if (cacheAccess is null || !cacheAccess.CanReadPersistentCache)
            {
                return IndexShardLoadResult.PersistenceUnavailable();
            }

            return await _cacheStore.TryLoadShardByPersistentPathAsync(
                cacheAccess,
                baselineManifest,
                baselineShardSnapshot.Reference,
                persistentPath,
                cancellationToken).ConfigureAwait(false);
        }

        return IndexShardLoadResult.Invalid();
    }

    private static Dictionary<int, IndexShard> CreatePublicationShardSet(
        IndexManifest candidateManifest,
        IReadOnlyDictionary<int, IndexShard> changedShards,
        ProjectIndexGeneration baselineGeneration)
    {
        Dictionary<int, IndexShard> publicationShards = changedShards.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value);

        foreach (IndexManifestShardReference reference in candidateManifest.Shards)
        {
            if (publicationShards.ContainsKey(reference.ShardId))
            {
                continue;
            }

            if (baselineGeneration.ShardCatalog.TryGetValue(
                    reference.ShardId,
                    out ProjectIndexShardSnapshot? baselineShard)
                && baselineShard.Reference == reference
                && baselineShard.InMemoryShard is IndexShard inMemoryShard)
            {
                publicationShards.Add(reference.ShardId, inMemoryShard);
            }
        }

        return publicationShards;
    }

    private static SourceInventory BuildSourceInventory(
        WorkspaceProjectSnapshot projectSnapshot,
        CancellationToken cancellationToken)
    {
        if (projectSnapshot.SourceFiles.Count > IndexCacheFormat.MaxSourceEntries)
        {
            throw new InvalidDataException("workspace source count exceeds the cache format limit.");
        }

        List<SourceFileMetadata> sources = new(projectSnapshot.SourceFiles.Count);
        HashSet<string> paths = new(IndexPathValidation.SourcePathComparer);
        ulong shardMask = 0;

        foreach (string relativePath in projectSnapshot.SourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IndexPathValidation.ValidateRelativeSourcePath(relativePath);
            if (!paths.Add(relativePath))
            {
                throw new InvalidDataException(
                    "workspace discovery contains duplicate platform-equivalent source paths.");
            }

            SourceFileMetadata metadata = SourceFileFingerprint.ReadMetadata(
                projectSnapshot.WorkspaceIdentity,
                relativePath);
            sources.Add(metadata);
            shardMask |= 1UL << metadata.ShardId;
        }

        sources.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return new SourceInventory(sources, System.Numerics.BitOperations.PopCount(shardMask));
    }

    private static bool IsExactMetadataMatch(
        IndexManifest manifest,
        SourceInventory inventory)
    {
        if (manifest.CacheFormatVersion != IndexCacheFormat.CacheFormatVersion
            || manifest.ShardPartitionerVersion != IndexCacheFormat.ShardPartitionerVersion
            || manifest.ShardCount != IndexCacheFormat.FixedShardCount
            || manifest.Sources.Count != inventory.Sources.Count)
        {
            return false;
        }

        Dictionary<string, IndexManifestSource> manifestSources = manifest.Sources.ToDictionary(
            static source => source.RelativePath,
            IndexPathValidation.SourcePathComparer);

        foreach (SourceFileMetadata source in inventory.Sources)
        {
            if (!manifestSources.TryGetValue(source.RelativePath, out IndexManifestSource cached)
                || cached.Length != source.Length
                || cached.LastWriteTimeUtcTicks != source.LastWriteTimeUtcTicks
                || cached.ShardId != source.ShardId)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExactManifestMetadataMatch(
        IndexManifest manifest,
        IEnumerable<SourceFileMetadata> metadata)
    {
        SourceFileMetadata[] current = metadata
            .OrderBy(static source => source.RelativePath, StringComparer.Ordinal)
            .ToArray();

        if (manifest.Sources.Count != current.Length)
        {
            return false;
        }

        for (int index = 0; index < current.Length; index++)
        {
            IndexManifestSource previous = manifest.Sources[index];
            SourceFileMetadata source = current[index];
            if (!IndexPathValidation.SourcePathComparer.Equals(
                    previous.RelativePath,
                    source.RelativePath)
                || previous.Length != source.Length
                || previous.LastWriteTimeUtcTicks != source.LastWriteTimeUtcTicks
                || previous.ShardId != source.ShardId)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreShardReferencesExactMatch(
        IndexManifest manifest,
        IEnumerable<IndexManifestShardReference> references)
    {
        IndexManifestShardReference[] current = references
            .OrderBy(static shard => shard.ShardId)
            .ToArray();

        return manifest.Shards.SequenceEqual(current);
    }

    private static IndexManifest CreateManifest(
        WorkspaceIdentity workspaceIdentity,
        WorkspaceCacheLocation? cacheLocation,
        string generationId,
        IEnumerable<SourceFileMetadata> sources,
        IEnumerable<IndexManifestShardReference> shardReferences)
    {
        string workspaceKey = cacheLocation?.WorkspaceKey
            ?? IndexCachePathResolver.ComputeWorkspaceKey(workspaceIdentity);

        IndexManifestSource[] manifestSources = sources
            .Select(static source => new IndexManifestSource(
                source.RelativePath,
                source.LastWriteTimeUtcTicks,
                source.Length,
                source.ShardId))
            .OrderBy(static source => source.RelativePath, StringComparer.Ordinal)
            .ToArray();

        IndexManifestShardReference[] manifestShards = shardReferences
            .OrderBy(static shard => shard.ShardId)
            .ToArray();

        return new IndexManifest(
            generationId,
            workspaceIdentity.ProjectRoot,
            workspaceKey,
            manifestSources,
            manifestShards);
    }

    private ProjectIndexGeneration CreateGeneration(
        WorkspaceIdentity workspaceIdentity,
        IndexManifest manifest,
        WorkspaceCacheAccess? cacheAccess,
        IReadOnlyDictionary<int, IndexShard>? changedShards,
        ProjectIndexGeneration? baselineGeneration,
        ProjectIndexPersistenceState persistenceState)
    {
        Dictionary<int, ProjectIndexShardSnapshot> catalog = new();

        foreach (IndexManifestShardReference shardReference in manifest.Shards)
        {
            if (persistenceState == ProjectIndexPersistenceState.Persisted)
            {
                if (cacheAccess is null || !cacheAccess.CanReadPersistentCache)
                {
                    throw new InvalidOperationException(
                        "persisted index generation requires readable workspace cache access.");
                }

                string persistentPath = _cacheStore.ResolveShardPath(
                    cacheAccess,
                    shardReference);
                catalog.Add(
                    shardReference.ShardId,
                    new ProjectIndexShardSnapshot(
                        shardReference,
                        persistentPath,
                        inMemoryShard: null));
                continue;
            }

            if (changedShards is not null
                && changedShards.TryGetValue(shardReference.ShardId, out IndexShard? changedShard))
            {
                catalog.Add(
                    shardReference.ShardId,
                    new ProjectIndexShardSnapshot(
                        shardReference,
                        persistentPath: null,
                        changedShard));
                continue;
            }

            if (baselineGeneration is not null
                && baselineGeneration.ShardCatalog.TryGetValue(
                    shardReference.ShardId,
                    out ProjectIndexShardSnapshot? baselineShard)
                && baselineShard.Reference == shardReference)
            {
                catalog.Add(shardReference.ShardId, baselineShard);
                continue;
            }

            throw new InvalidOperationException(
                "index generation lost both reusable and newly built shard state.");
        }

        return new ProjectIndexGeneration(
            manifest.GenerationId,
            workspaceIdentity,
            manifest,
            catalog,
            persistenceState);
    }

    private WorkspaceCacheAccess GetOrCreateCacheAccess(WorkspaceIdentity workspaceIdentity)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);

        lock (_sync)
        {
            if (_cacheAccess is not null)
            {
                if (_cacheWorkspaceIdentity is null
                    || !_cacheWorkspaceIdentity.Equals(workspaceIdentity))
                {
                    throw new InvalidOperationException(
                        "project index host cache access is already bound to a different workspace identity.");
                }

                return _cacheAccess;
            }

            if (_disposed || _state == ProjectIndexHostState.ShuttingDown)
            {
                throw new ProjectIndexPublicationCanceledException();
            }

            if (_cacheAccessEstablishing)
            {
                throw new InvalidOperationException(
                    "project index host cache access establishment is already in progress.");
            }

            _cacheAccessEstablishing = true;
        }

        WorkspaceCacheAccess? candidate = null;
        WorkspaceCacheAccess? result = null;
        bool installed = false;

        try
        {
            WorkspaceCacheLocation location = IndexCachePathResolver.ResolveWorkspaceCache(
                workspaceIdentity);
            candidate = WorkspaceCacheAccess.Create(location);

            lock (_sync)
            {
                if (_disposed || _state == ProjectIndexHostState.ShuttingDown)
                {
                    throw new ProjectIndexPublicationCanceledException();
                }

                if (_cacheAccess is not null)
                {
                    if (_cacheWorkspaceIdentity is null
                        || !_cacheWorkspaceIdentity.Equals(workspaceIdentity))
                    {
                        throw new InvalidOperationException(
                            "project index host cache access is already bound to a different workspace identity.");
                    }

                    result = _cacheAccess;
                }
                else
                {
                    _cacheWorkspaceIdentity = workspaceIdentity;
                    _cacheAccess = candidate;
                    result = candidate;
                    candidate = null;
                    installed = true;
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _cacheAccessEstablishing = false;
            }

            if (candidate is not null)
            {
                try
                {
                    candidate.Dispose();
                }
                catch
                {
                    // A non-installed candidate must never turn cache-access arbitration into index failure.
                }
            }
        }

        if (installed)
        {
            WriteCacheAuthorityState(result!);
        }

        return result!;
    }

    private void WriteCacheAuthorityState(WorkspaceCacheAccess cacheAccess)
    {
        if (!_diagnosticLogging.IsEnabled)
        {
            return;
        }

        if (cacheAccess.CoordinationMode == CacheCoordinationMode.CoordinatedExclusive)
        {
            _diagnosticLogging.WriteEvent(
                "index_cache_authority_acquired",
                new IndexCacheAuthorityDetails(
                    cacheAccess.CoordinationMode.ToString(),
                    Reason: null));
        }
        else if (cacheAccess.CoordinationMode == CacheCoordinationMode.Unavailable)
        {
            _diagnosticLogging.WriteEvent(
                "index_cache_authority_unavailable",
                new IndexCacheAuthorityDetails(
                    cacheAccess.CoordinationMode.ToString(),
                    cacheAccess.UnavailabilityReason));
        }
    }

    private async Task RunCacheGarbageCollectionAsync(
        WorkspaceCacheAccess cacheAccess,
        IndexManifest trustedCurrentManifest,
        IndexCacheGarbageCollectionTrigger trigger,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        IndexCacheGarbageCollectionResult result;

        try
        {
            result = await _garbageCollector.TryCollectAsync(
                cacheAccess,
                trustedCurrentManifest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_diagnosticLogging.IsEnabled)
            {
                IndexCacheGarbageCollectionDetails failedDetails = new(
                    trigger,
                    IndexCacheGarbageCollectionStatus.SkippedPersistenceUnavailable,
                    trustedCurrentManifest.GenerationId,
                    ManifestEntriesExamined: 0,
                    ManifestsDeleted: 0,
                    ShardEntriesExamined: 0,
                    ShardsDeleted: 0,
                    TempEntriesExamined: 0,
                    TempFilesDeleted: 0,
                    DeleteFailures: 1,
                    Truncated: true,
                    DurationMs: Stopwatch.GetElapsedTime(
                        started,
                        Stopwatch.GetTimestamp()).TotalMilliseconds);
                _diagnosticLogging.WriteFault(
                    "index_cache_gc_skipped",
                    exception,
                    failedDetails);
            }

            return;
        }

        if (!_diagnosticLogging.IsEnabled)
        {
            return;
        }

        IndexCacheGarbageCollectionDetails details = new(
            trigger,
            result.Status,
            trustedCurrentManifest.GenerationId,
            result.ManifestEntriesExamined,
            result.ManifestsDeleted,
            result.ShardEntriesExamined,
            result.ShardsDeleted,
            result.TempEntriesExamined,
            result.TempFilesDeleted,
            result.DeleteFailures,
            result.Truncated,
            result.DurationMs);

        _diagnosticLogging.WriteEvent(
            result.Status == IndexCacheGarbageCollectionStatus.Completed
                ? "index_cache_gc_completed"
                : "index_cache_gc_skipped",
            details);
    }

    private void PublishCurrentGeneration(
        long reconciliationGeneration,
        ProjectIndexGeneration generation)
    {
        lock (_sync)
        {
            if (_disposed
                || _state == ProjectIndexHostState.ShuttingDown
                || _reconciliationGeneration != reconciliationGeneration)
            {
                throw new ProjectIndexPublicationCanceledException();
            }

            _currentGeneration = generation;
            _state = ProjectIndexHostState.Ready;
        }
    }

    private void RestorePreviousGenerationAfterRuntimeFailure(
        long reconciliationGeneration,
        ProjectIndexGeneration baselineGeneration)
    {
        lock (_sync)
        {
            if (_disposed
                || _state == ProjectIndexHostState.ShuttingDown
                || _reconciliationGeneration != reconciliationGeneration)
            {
                return;
            }

            if (!ReferenceEquals(_currentGeneration, baselineGeneration))
            {
                throw new InvalidOperationException(
                    "runtime reconciliation changed the current generation before candidate publication.");
            }

            _state = ProjectIndexHostState.Ready;
        }
    }

    private void MarkInitialReconciliationFault(long reconciliationGeneration)
    {
        lock (_sync)
        {
            if (!_disposed
                && _state != ProjectIndexHostState.ShuttingDown
                && _reconciliationGeneration == reconciliationGeneration)
            {
                _state = ProjectIndexHostState.Faulted;
            }
        }
    }

    private static Dictionary<string, SourceFileMetadata> CloneMetadata(SourceInventory inventory)
        => inventory.Sources.ToDictionary(
            static source => source.RelativePath,
            IndexPathValidation.SourcePathComparer);

    private static ShardMutationPlan GetPlan(
        Dictionary<int, ShardMutationPlan> plans,
        int shardId)
    {
        if (!plans.TryGetValue(shardId, out ShardMutationPlan? plan))
        {
            plan = new ShardMutationPlan();
            plans.Add(shardId, plan);
        }

        return plan;
    }

    private sealed class ShardMutationPlan
    {
        public HashSet<string> Added { get; } = new(IndexPathValidation.SourcePathComparer);

        public HashSet<string> Deleted { get; } = new(IndexPathValidation.SourcePathComparer);

        public HashSet<string> PotentiallyModified { get; } = new(IndexPathValidation.SourcePathComparer);
    }

    private sealed class SourceInventory
    {
        public SourceInventory(
            IReadOnlyList<SourceFileMetadata> sources,
            int distinctShardCount)
        {
            Sources = sources ?? throw new ArgumentNullException(nameof(sources));
            DistinctShardCount = distinctShardCount;
        }

        public IReadOnlyList<SourceFileMetadata> Sources { get; }

        public int DistinctShardCount { get; }
    }
}

internal enum ProjectIndexHostState
{
    Uninitialized,
    Reconciling,
    Ready,
    Faulted,
    ShuttingDown,
}

internal sealed class ProjectIndexPublicationCanceledException : OperationCanceledException
{
    public ProjectIndexPublicationCanceledException()
        : base("project index publication was invalidated by shutdown or a newer reconciliation generation.")
    {
    }
}
