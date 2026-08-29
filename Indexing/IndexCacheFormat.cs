namespace SystemExplorer.CodeService;

internal static class IndexCacheFormat
{
    public const int CacheFormatVersion = 2;
    public const int CurrentPointerSchemaVersion = 1;
    public const int ManifestSchemaVersion = 1;
    public const int ShardSchemaVersion = 1;
    public const int ShardPartitionerVersion = 1;
    public const int FixedShardCount = 64;

    public const long MaxCurrentPointerSizeBytes = 4 * 1024;
    public const long MaxManifestSizeBytes = 64L * 1024 * 1024;
    public const long MaxShardSizeBytes = 64L * 1024 * 1024;
    public const int MaxSourceEntries = 250_000;
    public const int MaxRelativePathLength = 4096;
    public const int GenerationIdByteLength = 16;
    public const int GenerationIdHexLength = GenerationIdByteLength * 2;
    public const int Sha256HexLength = 64;
}
