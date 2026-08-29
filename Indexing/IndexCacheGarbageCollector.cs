using System.Diagnostics;
using System.Security;

namespace SystemExplorer.CodeService;

internal sealed class IndexCacheGarbageCollector
{
    internal const int MaxMaintenanceEntriesPerDirectory = 4096;
    internal const int MaxMaintenanceDeletesPerPass = 2048;

    private readonly IndexCacheStore _cacheStore;

    public IndexCacheGarbageCollector(IndexCacheStore cacheStore)
    {
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
    }

    public async Task<IndexCacheGarbageCollectionResult> TryCollectAsync(
        WorkspaceCacheAccess cacheAccess,
        IndexManifest trustedCurrentManifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheAccess);
        ArgumentNullException.ThrowIfNull(trustedCurrentManifest);

        long started = Stopwatch.GetTimestamp();
        MaintenanceCounters counters = new();

        if (!cacheAccess.CanCollectGarbage || cacheAccess.Authority is null)
        {
            return counters.CreateResult(
                IndexCacheGarbageCollectionStatus.SkippedNoAuthority,
                started);
        }

        WorkspaceCacheLocation location = cacheAccess.Location;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryBuildLiveSets(
                    location,
                    trustedCurrentManifest,
                    out string? liveManifestFile,
                    out HashSet<string>? liveShardFiles))
            {
                return counters.CreateResult(
                    IndexCacheGarbageCollectionStatus.SkippedCurrentMismatch,
                    started);
            }

            bool currentMatches = await _cacheStore.IsCurrentPointerExactAsync(
                cacheAccess,
                trustedCurrentManifest,
                cancellationToken).ConfigureAwait(false);
            if (!currentMatches)
            {
                return counters.CreateResult(
                    IndexCacheGarbageCollectionStatus.SkippedCurrentMismatch,
                    started);
            }

            if (!IsMaintenanceDirectorySafe(location.WorkspaceDirectory)
                || !IsMaintenanceDirectorySafe(location.ManifestsDirectory)
                || !IsMaintenanceDirectorySafe(location.ShardsDirectory))
            {
                return counters.CreateResult(
                    IndexCacheGarbageCollectionStatus.SkippedPersistenceUnavailable,
                    started);
            }

            string liveManifestPath = IndexPathValidation.ResolveContainedFile(
                location.ManifestsDirectory,
                liveManifestFile!);
            if (!File.Exists(liveManifestPath))
            {
                return counters.CreateResult(
                    IndexCacheGarbageCollectionStatus.SkippedCurrentMismatch,
                    started);
            }

            foreach (string liveShardFile in liveShardFiles!)
            {
                string liveShardPath = IndexPathValidation.ResolveContainedFile(
                    location.ShardsDirectory,
                    liveShardFile);
                if (!File.Exists(liveShardPath))
                {
                    return counters.CreateResult(
                        IndexCacheGarbageCollectionStatus.SkippedCurrentMismatch,
                        started);
                }
            }

            if (!SweepManifestDirectory(
                    location,
                    liveManifestFile!,
                    counters,
                    cancellationToken))
            {
                return counters.CreateResult(
                    IndexCacheGarbageCollectionStatus.Completed,
                    started);
            }

            if (!SweepShardDirectory(
                    location,
                    liveShardFiles!,
                    counters,
                    cancellationToken))
            {
                return counters.CreateResult(
                    IndexCacheGarbageCollectionStatus.Completed,
                    started);
            }

            SweepWorkspaceTemporaryFiles(location, counters, cancellationToken);
            return counters.CreateResult(
                IndexCacheGarbageCollectionStatus.Completed,
                started);
        }
        catch (OperationCanceledException)
        {
            return counters.CreateResult(
                IndexCacheGarbageCollectionStatus.Canceled,
                started);
        }
        catch (Exception exception) when (IsMaintenanceFailure(exception))
        {
            counters.DeleteFailures = SaturatingIncrement(counters.DeleteFailures);
            counters.Truncated = true;
            return counters.CreateResult(
                IndexCacheGarbageCollectionStatus.Completed,
                started);
        }
    }

    private static bool TryBuildLiveSets(
        WorkspaceCacheLocation location,
        IndexManifest trustedCurrentManifest,
        out string? liveManifestFile,
        out HashSet<string>? liveShardFiles)
    {
        liveManifestFile = null;
        liveShardFiles = null;

        try
        {
            if (!string.Equals(
                    trustedCurrentManifest.WorkspaceKey,
                    location.WorkspaceKey,
                    StringComparison.Ordinal)
                || !IndexPathValidation.IsLowerHex(
                    trustedCurrentManifest.GenerationId,
                    IndexCacheFormat.GenerationIdHexLength))
            {
                return false;
            }

            if (trustedCurrentManifest.Shards.Count > IndexCacheFormat.FixedShardCount)
            {
                return false;
            }

            liveManifestFile = IndexCacheFileName.GetManifestFileName(
                trustedCurrentManifest.GenerationId);
            HashSet<string> shards = new(StringComparer.Ordinal);
            HashSet<int> shardIds = new();
            foreach (IndexManifestShardReference reference in trustedCurrentManifest.Shards)
            {
                if (!IndexCacheFileName.TryParseShardFileName(
                        reference.FileName,
                        out int shardId,
                        out string? shardGenerationId)
                    || shardGenerationId is null
                    || shardId != reference.ShardId
                    || !shardIds.Add(shardId)
                    || !string.Equals(
                        reference.FileName,
                        IndexCacheFileName.GetShardFileName(shardId, shardGenerationId),
                        StringComparison.Ordinal)
                    || !shards.Add(reference.FileName))
                {
                    return false;
                }
            }

            liveShardFiles = shards;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static bool SweepManifestDirectory(
        WorkspaceCacheLocation location,
        string liveManifestFile,
        MaintenanceCounters counters,
        CancellationToken cancellationToken)
    {
        foreach (string enumeratedPath in Directory.EnumerateFiles(
                     location.ManifestsDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (counters.ManifestEntriesExamined >= MaxMaintenanceEntriesPerDirectory)
            {
                counters.Truncated = true;
                return false;
            }

            counters.ManifestEntriesExamined = SaturatingIncrement(
                counters.ManifestEntriesExamined);
            string fileName = Path.GetFileName(enumeratedPath);

            if (IndexCacheFileName.TryParseManifestFileName(fileName, out _))
            {
                if (string.Equals(fileName, liveManifestFile, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryDeleteOwnedFile(
                        location.ManifestsDirectory,
                        fileName,
                        counters,
                        isManifest: true,
                        isShard: false,
                        isTemporary: false,
                        stopOnDeleteFailure: true))
                {
                    return false;
                }

                continue;
            }

            if (IndexCacheFileName.IsOwnedManifestTemporaryFileName(fileName))
            {
                counters.TempEntriesExamined = SaturatingIncrement(counters.TempEntriesExamined);
                if (!TryDeleteOwnedFile(
                        location.ManifestsDirectory,
                        fileName,
                        counters,
                        isManifest: false,
                        isShard: false,
                        isTemporary: true))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool SweepShardDirectory(
        WorkspaceCacheLocation location,
        HashSet<string> liveShardFiles,
        MaintenanceCounters counters,
        CancellationToken cancellationToken)
    {
        foreach (string enumeratedPath in Directory.EnumerateFiles(
                     location.ShardsDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (counters.ShardEntriesExamined >= MaxMaintenanceEntriesPerDirectory)
            {
                counters.Truncated = true;
                return false;
            }

            counters.ShardEntriesExamined = SaturatingIncrement(counters.ShardEntriesExamined);
            string fileName = Path.GetFileName(enumeratedPath);

            if (IndexCacheFileName.TryParseShardFileName(fileName, out _, out _))
            {
                if (liveShardFiles.Contains(fileName))
                {
                    continue;
                }

                if (!TryDeleteOwnedFile(
                        location.ShardsDirectory,
                        fileName,
                        counters,
                        isManifest: false,
                        isShard: true,
                        isTemporary: false))
                {
                    return false;
                }

                continue;
            }

            if (IndexCacheFileName.IsOwnedShardTemporaryFileName(fileName))
            {
                counters.TempEntriesExamined = SaturatingIncrement(counters.TempEntriesExamined);
                if (!TryDeleteOwnedFile(
                        location.ShardsDirectory,
                        fileName,
                        counters,
                        isManifest: false,
                        isShard: false,
                        isTemporary: true))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void SweepWorkspaceTemporaryFiles(
        WorkspaceCacheLocation location,
        MaintenanceCounters counters,
        CancellationToken cancellationToken)
    {
        int entriesExamined = 0;
        foreach (string enumeratedPath in Directory.EnumerateFiles(
                     location.WorkspaceDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entriesExamined >= MaxMaintenanceEntriesPerDirectory)
            {
                counters.Truncated = true;
                return;
            }

            entriesExamined = SaturatingIncrement(entriesExamined);
            string fileName = Path.GetFileName(enumeratedPath);
            if (!IndexCacheFileName.IsOwnedCurrentTemporaryFileName(fileName))
            {
                continue;
            }

            counters.TempEntriesExamined = SaturatingIncrement(counters.TempEntriesExamined);
            if (!TryDeleteOwnedFile(
                    location.WorkspaceDirectory,
                    fileName,
                    counters,
                    isManifest: false,
                    isShard: false,
                    isTemporary: true))
            {
                return;
            }
        }
    }

    private static bool TryDeleteOwnedFile(
        string directory,
        string fileName,
        MaintenanceCounters counters,
        bool isManifest,
        bool isShard,
        bool isTemporary,
        bool stopOnDeleteFailure = false)
    {
        if (counters.TotalDeleted >= MaxMaintenanceDeletesPerPass)
        {
            counters.Truncated = true;
            return false;
        }

        string containedPath;
        try
        {
            containedPath = IndexPathValidation.ResolveContainedFile(directory, fileName);
        }
        catch (Exception exception) when (IsMaintenanceFailure(exception))
        {
            counters.DeleteFailures = SaturatingIncrement(counters.DeleteFailures);
            return !stopOnDeleteFailure;
        }

        try
        {
            File.Delete(containedPath);
            counters.TotalDeleted = SaturatingIncrement(counters.TotalDeleted);
            if (isManifest)
            {
                counters.ManifestsDeleted = SaturatingIncrement(counters.ManifestsDeleted);
            }
            else if (isShard)
            {
                counters.ShardsDeleted = SaturatingIncrement(counters.ShardsDeleted);
            }
            else if (isTemporary)
            {
                counters.TempFilesDeleted = SaturatingIncrement(counters.TempFilesDeleted);
            }
        }
        catch (Exception exception) when (IsMaintenanceFailure(exception))
        {
            counters.DeleteFailures = SaturatingIncrement(counters.DeleteFailures);
            return !stopOnDeleteFailure;
        }

        return true;
    }

    private static bool IsMaintenanceDirectorySafe(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            FileAttributes attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            return new DirectoryInfo(directory).LinkTarget is null;
        }
        catch (Exception exception) when (IsMaintenanceFailure(exception))
        {
            return false;
        }
    }

    private static bool IsMaintenanceFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException
            or PathTooLongException
            or ArgumentException;

    private static int SaturatingIncrement(int value)
        => value == int.MaxValue ? int.MaxValue : value + 1;

    private sealed class MaintenanceCounters
    {
        public int ManifestEntriesExamined { get; set; }
        public int ManifestsDeleted { get; set; }
        public int ShardEntriesExamined { get; set; }
        public int ShardsDeleted { get; set; }
        public int TempEntriesExamined { get; set; }
        public int TempFilesDeleted { get; set; }
        public int DeleteFailures { get; set; }
        public int TotalDeleted { get; set; }
        public bool Truncated { get; set; }

        public IndexCacheGarbageCollectionResult CreateResult(
            IndexCacheGarbageCollectionStatus status,
            long started)
            => new(
                status,
                ManifestEntriesExamined,
                ManifestsDeleted,
                ShardEntriesExamined,
                ShardsDeleted,
                TempEntriesExamined,
                TempFilesDeleted,
                DeleteFailures,
                Truncated,
                Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds);
    }
}

internal enum IndexCacheGarbageCollectionStatus
{
    Completed,
    SkippedNoAuthority,
    SkippedCurrentMismatch,
    SkippedPersistenceUnavailable,
    Canceled,
}

internal enum IndexCacheGarbageCollectionTrigger
{
    WarmInitialization,
    PostPublication,
}

internal sealed record IndexCacheGarbageCollectionResult(
    IndexCacheGarbageCollectionStatus Status,
    int ManifestEntriesExamined,
    int ManifestsDeleted,
    int ShardEntriesExamined,
    int ShardsDeleted,
    int TempEntriesExamined,
    int TempFilesDeleted,
    int DeleteFailures,
    bool Truncated,
    double DurationMs);
