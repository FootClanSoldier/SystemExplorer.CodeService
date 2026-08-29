using System.Collections.ObjectModel;

namespace SystemExplorer.CodeService;

internal sealed class IndexManifest
{
    public IndexManifest(
        string generationId,
        string normalizedProjectRoot,
        string workspaceKey,
        IReadOnlyList<IndexManifestSource> sources,
        IReadOnlyList<IndexManifestShardReference> shards)
    {
        if (!IndexPathValidation.IsLowerHex(generationId, IndexCacheFormat.GenerationIdHexLength))
        {
            throw new ArgumentException("generationId is invalid.", nameof(generationId));
        }

        ArgumentException.ThrowIfNullOrEmpty(normalizedProjectRoot);
        if (!IndexPathValidation.IsLowerHex(workspaceKey, IndexCacheFormat.Sha256HexLength))
        {
            throw new ArgumentException("workspaceKey is invalid.", nameof(workspaceKey));
        }

        GenerationId = generationId;
        NormalizedProjectRoot = normalizedProjectRoot;
        WorkspaceKey = workspaceKey;
        Sources = FreezeSources(sources);
        Shards = FreezeShards(shards);
    }

    public int SchemaVersion => IndexCacheFormat.ManifestSchemaVersion;

    public int CacheFormatVersion => IndexCacheFormat.CacheFormatVersion;

    public string GenerationId { get; }

    public string NormalizedProjectRoot { get; }

    public string WorkspaceKey { get; }

    public int ShardPartitionerVersion => IndexCacheFormat.ShardPartitionerVersion;

    public int ShardCount => IndexCacheFormat.FixedShardCount;

    public ReadOnlyCollection<IndexManifestSource> Sources { get; }

    public ReadOnlyCollection<IndexManifestShardReference> Shards { get; }

    private static ReadOnlyCollection<IndexManifestSource> FreezeSources(
        IReadOnlyList<IndexManifestSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count > IndexCacheFormat.MaxSourceEntries)
        {
            throw new InvalidDataException("manifest source count exceeds the cache format limit.");
        }

        IndexManifestSource[] copy = sources.ToArray();
        Array.Sort(copy, static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return Array.AsReadOnly(copy);
    }

    private static ReadOnlyCollection<IndexManifestShardReference> FreezeShards(
        IReadOnlyList<IndexManifestShardReference> shards)
    {
        ArgumentNullException.ThrowIfNull(shards);
        IndexManifestShardReference[] copy = shards.ToArray();
        Array.Sort(copy, static (left, right) => left.ShardId.CompareTo(right.ShardId));
        return Array.AsReadOnly(copy);
    }
}

internal readonly record struct IndexManifestSource(
    string RelativePath,
    long LastWriteTimeUtcTicks,
    long Length,
    int ShardId);

internal readonly record struct IndexManifestShardReference(
    int ShardId,
    string FileName,
    int RecordCount);
