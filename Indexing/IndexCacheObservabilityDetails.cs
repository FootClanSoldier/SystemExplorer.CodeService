namespace SystemExplorer.CodeService;

internal sealed record IndexCacheAuthorityDetails(
    string CoordinationMode,
    string? Reason);

internal sealed record IndexCacheGarbageCollectionDetails(
    IndexCacheGarbageCollectionTrigger Trigger,
    IndexCacheGarbageCollectionStatus Status,
    string CurrentGenerationId,
    int ManifestEntriesExamined,
    int ManifestsDeleted,
    int ShardEntriesExamined,
    int ShardsDeleted,
    int TempEntriesExamined,
    int TempFilesDeleted,
    int DeleteFailures,
    bool Truncated,
    double DurationMs);
