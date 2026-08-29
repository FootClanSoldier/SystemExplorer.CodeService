namespace SystemExplorer.CodeService;

internal static class IndexCacheFileName
{
    private const string ManifestPrefix = "manifest_";
    private const string ManifestSuffix = ".json";
    private const string ShardPrefix = "shard_";
    private const string ShardSuffix = ".json";
    private const int ShardDigits = 4;
    private const int TemporaryRandomHexLength = 16;

    public static string GetManifestFileName(string generationId)
    {
        ValidateGenerationId(generationId);
        return $"{ManifestPrefix}{generationId}{ManifestSuffix}";
    }

    public static bool TryParseManifestFileName(string fileName, out string? generationId)
    {
        generationId = null;
        if (fileName.Length != ManifestPrefix.Length + IndexCacheFormat.GenerationIdHexLength + ManifestSuffix.Length
            || !fileName.StartsWith(ManifestPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(ManifestSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        string candidate = fileName.Substring(ManifestPrefix.Length, IndexCacheFormat.GenerationIdHexLength);
        if (!IndexPathValidation.IsLowerHex(candidate, IndexCacheFormat.GenerationIdHexLength))
        {
            return false;
        }

        generationId = candidate;
        return true;
    }

    public static string GetShardFileName(int shardId, string generationId)
    {
        if (shardId < 0 || shardId >= IndexCacheFormat.FixedShardCount)
        {
            throw new InvalidDataException("invalid shard filename identity.");
        }

        ValidateGenerationId(generationId);
        return $"{ShardPrefix}{shardId:D4}_{generationId}{ShardSuffix}";
    }

    public static bool TryParseShardFileName(
        string fileName,
        out int shardId,
        out string? generationId)
    {
        shardId = -1;
        generationId = null;

        int expectedLength = ShardPrefix.Length
            + ShardDigits
            + 1
            + IndexCacheFormat.GenerationIdHexLength
            + ShardSuffix.Length;
        if (fileName.Length != expectedLength
            || !fileName.StartsWith(ShardPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(ShardSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> shardDigits = fileName.AsSpan(ShardPrefix.Length, ShardDigits);
        int parsedShardId = 0;
        foreach (char digit in shardDigits)
        {
            if (digit < '0' || digit > '9')
            {
                return false;
            }

            parsedShardId = (parsedShardId * 10) + (digit - '0');
        }

        if (parsedShardId < 0 || parsedShardId >= IndexCacheFormat.FixedShardCount)
        {
            return false;
        }

        int generationStart = ShardPrefix.Length + ShardDigits + 1;
        if (fileName[ShardPrefix.Length + ShardDigits] != '_')
        {
            return false;
        }

        string candidateGeneration = fileName.Substring(
            generationStart,
            IndexCacheFormat.GenerationIdHexLength);
        if (!IndexPathValidation.IsLowerHex(
                candidateGeneration,
                IndexCacheFormat.GenerationIdHexLength))
        {
            return false;
        }

        shardId = parsedShardId;
        generationId = candidateGeneration;
        return true;
    }

    public static bool IsExpectedShardFileName(string fileName, int shardId)
        => TryParseShardFileName(fileName, out int parsedShardId, out _)
            && parsedShardId == shardId;

    public static bool IsOwnedCurrentTemporaryFileName(string fileName)
        => IsOwnedTemporaryFileName(
            fileName,
            ".current_",
            TemporaryRandomHexLength);

    public static bool IsOwnedManifestTemporaryFileName(string fileName)
    {
        const string prefix = ".manifest_";
        const string separator = "_";
        const string suffix = ".tmp";
        int expectedLength = prefix.Length
            + IndexCacheFormat.GenerationIdHexLength
            + separator.Length
            + TemporaryRandomHexLength
            + suffix.Length;
        if (fileName.Length != expectedLength
            || !fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        string generationId = fileName.Substring(prefix.Length, IndexCacheFormat.GenerationIdHexLength);
        int randomStart = prefix.Length + IndexCacheFormat.GenerationIdHexLength + separator.Length;
        return fileName[prefix.Length + IndexCacheFormat.GenerationIdHexLength] == '_'
            && IndexPathValidation.IsLowerHex(generationId, IndexCacheFormat.GenerationIdHexLength)
            && IndexPathValidation.IsLowerHex(
                fileName.Substring(randomStart, TemporaryRandomHexLength),
                TemporaryRandomHexLength);
    }

    public static bool IsOwnedShardTemporaryFileName(string fileName)
    {
        const string prefix = ".shard_";
        const string suffix = ".tmp";
        int expectedLength = prefix.Length
            + ShardDigits
            + 1
            + IndexCacheFormat.GenerationIdHexLength
            + 1
            + TemporaryRandomHexLength
            + suffix.Length;
        if (fileName.Length != expectedLength
            || !fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> shardDigits = fileName.AsSpan(prefix.Length, ShardDigits);
        int shardId = 0;
        foreach (char digit in shardDigits)
        {
            if (digit < '0' || digit > '9')
            {
                return false;
            }

            shardId = (shardId * 10) + (digit - '0');
        }

        if (shardId < 0 || shardId >= IndexCacheFormat.FixedShardCount)
        {
            return false;
        }

        int firstSeparator = prefix.Length + ShardDigits;
        int generationStart = firstSeparator + 1;
        int secondSeparator = generationStart + IndexCacheFormat.GenerationIdHexLength;
        int randomStart = secondSeparator + 1;
        return fileName[firstSeparator] == '_'
            && fileName[secondSeparator] == '_'
            && IndexPathValidation.IsLowerHex(
                fileName.Substring(generationStart, IndexCacheFormat.GenerationIdHexLength),
                IndexCacheFormat.GenerationIdHexLength)
            && IndexPathValidation.IsLowerHex(
                fileName.Substring(randomStart, TemporaryRandomHexLength),
                TemporaryRandomHexLength);
    }

    private static bool IsOwnedTemporaryFileName(
        string fileName,
        string prefix,
        int randomHexLength)
    {
        const string suffix = ".tmp";
        if (fileName.Length != prefix.Length + randomHexLength + suffix.Length
            || !fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        return IndexPathValidation.IsLowerHex(
            fileName.Substring(prefix.Length, randomHexLength),
            randomHexLength);
    }

    private static void ValidateGenerationId(string generationId)
    {
        if (!IndexPathValidation.IsLowerHex(generationId, IndexCacheFormat.GenerationIdHexLength))
        {
            throw new InvalidDataException("invalid cache generation identity.");
        }
    }
}
