using System.Collections.ObjectModel;

namespace SystemExplorer.CodeService;

internal sealed class IndexShard
{
    public IndexShard(
        int shardId,
        string generationId,
        IReadOnlyList<IndexShardRecord> records)
    {
        if (shardId < 0 || shardId >= IndexCacheFormat.FixedShardCount)
        {
            throw new ArgumentOutOfRangeException(nameof(shardId));
        }

        if (!IndexPathValidation.IsLowerHex(generationId, IndexCacheFormat.GenerationIdHexLength))
        {
            throw new ArgumentException("generationId is invalid.", nameof(generationId));
        }

        ArgumentNullException.ThrowIfNull(records);
        IndexShardRecord[] copy = records.ToArray();
        Array.Sort(copy, static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

        HashSet<string> paths = new(IndexPathValidation.SourcePathComparer);
        foreach (IndexShardRecord record in copy)
        {
            IndexPathValidation.ValidateRelativeSourcePath(record.RelativePath);
            if (IndexShardPartitioner.GetShardId(record.RelativePath) != shardId)
            {
                throw new InvalidDataException("shard record is routed to a different deterministic shard.");
            }

            if (!IndexPathValidation.IsLowerHex(record.ContentHashSha256, IndexCacheFormat.Sha256HexLength))
            {
                throw new InvalidDataException("shard record contains an invalid SHA-256 hash.");
            }

            if (!paths.Add(record.RelativePath))
            {
                throw new InvalidDataException("shard contains duplicate source records.");
            }
        }

        ShardId = shardId;
        GenerationId = generationId;
        Records = Array.AsReadOnly(copy);
    }

    public int SchemaVersion => IndexCacheFormat.ShardSchemaVersion;

    public int CacheFormatVersion => IndexCacheFormat.CacheFormatVersion;

    public int ShardId { get; }

    public string GenerationId { get; }

    public ReadOnlyCollection<IndexShardRecord> Records { get; }
}

internal readonly record struct IndexShardRecord(
    string RelativePath,
    string ContentHashSha256);
