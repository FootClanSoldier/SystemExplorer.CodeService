using System.Security.Cryptography;
using System.Text.Json;

namespace SystemExplorer.CodeService;

internal sealed class IndexCacheStore
{
    public async Task<IndexCacheLoadResult> TryLoadCurrentAsync(
        WorkspaceCacheAccess cacheAccess,
        WorkspaceIdentity workspaceIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheAccess);
        if (!cacheAccess.CanReadPersistentCache)
        {
            return IndexCacheLoadResult.PersistenceUnavailable();
        }

        WorkspaceCacheLocation cacheLocation = cacheAccess.Location;
        try
        {
            if (!File.Exists(cacheLocation.CurrentPointerPath))
            {
                return IndexCacheLoadResult.Miss();
            }

            byte[] currentBytes = await ReadBoundedFileAsync(
                cacheLocation.CurrentPointerPath,
                IndexCacheFormat.MaxCurrentPointerSizeBytes,
                cancellationToken).ConfigureAwait(false);

            IndexCurrentPointer pointer = ParseCurrentPointer(currentBytes);
            string expectedManifestFileName = GetManifestFileName(pointer.GenerationId);
            if (!string.Equals(pointer.ManifestFile, expectedManifestFileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("current pointer manifest filename does not match its generation identity.");
            }

            if (pointer.CacheFormatVersion != IndexCacheFormat.CacheFormatVersion)
            {
                return IndexCacheLoadResult.IncompatibleFormat(pointer.CacheFormatVersion);
            }

            string manifestPath = IndexPathValidation.ResolveContainedFile(
                cacheLocation.ManifestsDirectory,
                pointer.ManifestFile);

            byte[] manifestBytes = await ReadBoundedFileAsync(
                manifestPath,
                IndexCacheFormat.MaxManifestSizeBytes,
                cancellationToken).ConfigureAwait(false);

            IndexManifest manifest = ParseManifest(
                manifestBytes,
                cacheLocation,
                workspaceIdentity,
                pointer.GenerationId);

            return IndexCacheLoadResult.Valid(manifest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return IndexCacheLoadResult.Invalid();
        }
        catch (DirectoryNotFoundException)
        {
            return IndexCacheLoadResult.Invalid();
        }
        catch (InvalidDataException)
        {
            return IndexCacheLoadResult.Invalid();
        }
        catch (JsonException)
        {
            return IndexCacheLoadResult.Invalid();
        }
        catch (Exception exception) when (IsPersistenceException(exception))
        {
            return IndexCacheLoadResult.PersistenceUnavailable();
        }
    }

    public async Task<IndexShardLoadResult> TryLoadShardAsync(
        WorkspaceCacheAccess cacheAccess,
        IndexManifest manifest,
        IndexManifestShardReference shardReference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheAccess);
        if (!cacheAccess.CanReadPersistentCache)
        {
            return IndexShardLoadResult.PersistenceUnavailable();
        }

        WorkspaceCacheLocation cacheLocation = cacheAccess.Location;
        try
        {
            string expectedPrefix = $"shard_{shardReference.ShardId:D4}_";
            if (!shardReference.FileName.StartsWith(expectedPrefix, StringComparison.Ordinal)
                || !shardReference.FileName.EndsWith(".json", StringComparison.Ordinal))
            {
                return IndexShardLoadResult.Invalid();
            }

            string shardPath = IndexPathValidation.ResolveContainedFile(
                cacheLocation.ShardsDirectory,
                shardReference.FileName);

            byte[] shardBytes = await ReadBoundedFileAsync(
                shardPath,
                IndexCacheFormat.MaxShardSizeBytes,
                cancellationToken).ConfigureAwait(false);

            IndexShard shard = ParseShard(shardBytes, shardReference.ShardId);
            if (!string.Equals(
                    shardReference.FileName,
                    GetShardFileName(shard.ShardId, shard.GenerationId),
                    StringComparison.Ordinal))
            {
                return IndexShardLoadResult.Invalid();
            }

            ValidateShardAgainstManifest(manifest, shardReference, shard);
            return IndexShardLoadResult.Valid(shard, shardPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or DirectoryNotFoundException
                or InvalidDataException
                or JsonException)
        {
            return IndexShardLoadResult.Invalid();
        }
        catch (Exception exception) when (IsPersistenceException(exception))
        {
            return IndexShardLoadResult.PersistenceUnavailable();
        }
    }

    public async Task<IndexShardLoadResult> TryLoadShardByPersistentPathAsync(
        WorkspaceCacheAccess cacheAccess,
        IndexManifest manifest,
        IndexManifestShardReference shardReference,
        string persistentPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheAccess);
        if (!cacheAccess.CanReadPersistentCache)
        {
            return IndexShardLoadResult.PersistenceUnavailable();
        }

        try
        {
            ArgumentException.ThrowIfNullOrEmpty(persistentPath);

            if (!string.Equals(
                    Path.GetFileName(persistentPath),
                    shardReference.FileName,
                    StringComparison.Ordinal))
            {
                return IndexShardLoadResult.Invalid();
            }

            string expectedPersistentPath = IndexPathValidation.ResolveContainedFile(
                cacheAccess.Location.ShardsDirectory,
                shardReference.FileName);
            StringComparison pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(
                    Path.GetFullPath(persistentPath),
                    Path.GetFullPath(expectedPersistentPath),
                    pathComparison))
            {
                return IndexShardLoadResult.Invalid();
            }

            byte[] shardBytes = await ReadBoundedFileAsync(
                persistentPath,
                IndexCacheFormat.MaxShardSizeBytes,
                cancellationToken).ConfigureAwait(false);

            IndexShard shard = ParseShard(shardBytes, shardReference.ShardId);
            if (!string.Equals(
                    shardReference.FileName,
                    GetShardFileName(shard.ShardId, shard.GenerationId),
                    StringComparison.Ordinal))
            {
                return IndexShardLoadResult.Invalid();
            }

            ValidateShardAgainstManifest(manifest, shardReference, shard);
            return IndexShardLoadResult.Valid(shard, persistentPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException
                or DirectoryNotFoundException
                or InvalidDataException
                or JsonException)
        {
            return IndexShardLoadResult.Invalid();
        }
        catch (Exception exception) when (IsPersistenceException(exception))
        {
            return IndexShardLoadResult.PersistenceUnavailable();
        }
    }

    public async Task<IndexCachePublicationResult> TryPublishAsync(
        WorkspaceCacheAccess cacheAccess,
        IndexManifest manifest,
        IReadOnlyDictionary<int, IndexShard> changedShards,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheAccess);
        if (!cacheAccess.CanWritePersistentCache)
        {
            return IndexCachePublicationResult.Unavailable();
        }

        WorkspaceCacheLocation cacheLocation = cacheAccess.Location;
        string? currentTemporaryPath = null;
        bool currentTemporaryFileOwned = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(cacheLocation.WorkspaceDirectory);
            Directory.CreateDirectory(cacheLocation.ManifestsDirectory);
            Directory.CreateDirectory(cacheLocation.ShardsDirectory);

            foreach ((int shardId, IndexShard shard) in changedShards.OrderBy(static pair => pair.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexManifestShardReference shardReference = manifest.Shards.Single(
                    reference => reference.ShardId == shardId);

                string shardPath = IndexPathValidation.ResolveContainedFile(
                    cacheLocation.ShardsDirectory,
                    shardReference.FileName);

                await WriteImmutableShardAsync(shardPath, shard, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            string manifestFileName = GetManifestFileName(manifest.GenerationId);
            string manifestPath = IndexPathValidation.ResolveContainedFile(
                cacheLocation.ManifestsDirectory,
                manifestFileName);

            await WriteImmutableManifestAsync(manifestPath, manifest, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            currentTemporaryPath = CreateUniqueTemporaryPath(
                cacheLocation.WorkspaceDirectory,
                ".current_");

            await WriteCurrentPointerTemporaryAsync(
                currentTemporaryPath,
                manifest.GenerationId,
                manifestFileName,
                cancellationToken).ConfigureAwait(false);
            currentTemporaryFileOwned = true;

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(currentTemporaryPath, cacheLocation.CurrentPointerPath, overwrite: true);
            currentTemporaryFileOwned = false;
            currentTemporaryPath = null;

            return IndexCachePublicationResult.Persisted();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException
                or ArgumentException
                or InvalidOperationException
                or System.Security.SecurityException)
        {
            return IndexCachePublicationResult.Unavailable();
        }
        finally
        {
            if (currentTemporaryFileOwned && currentTemporaryPath is not null)
            {
                TryDeleteOwnedTemporaryFile(currentTemporaryPath);
            }
        }
    }

    public string ResolveShardPath(
        WorkspaceCacheAccess cacheAccess,
        IndexManifestShardReference shardReference)
    {
        ArgumentNullException.ThrowIfNull(cacheAccess);
        if (!cacheAccess.CanReadPersistentCache)
        {
            throw new InvalidOperationException(
                "persistent shard paths require readable workspace cache access.");
        }

        return IndexPathValidation.ResolveContainedFile(
            cacheAccess.Location.ShardsDirectory,
            shardReference.FileName);
    }

    public async Task<bool> IsCurrentPointerExactAsync(
        WorkspaceCacheAccess cacheAccess,
        IndexManifest trustedCurrentManifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cacheAccess);
        ArgumentNullException.ThrowIfNull(trustedCurrentManifest);
        if (!cacheAccess.CanReadPersistentCache)
        {
            return false;
        }

        try
        {
            byte[] currentBytes = await ReadBoundedFileAsync(
                cacheAccess.Location.CurrentPointerPath,
                IndexCacheFormat.MaxCurrentPointerSizeBytes,
                cancellationToken).ConfigureAwait(false);
            IndexCurrentPointer pointer = ParseCurrentPointer(currentBytes);
            string expectedManifestFile = GetManifestFileName(trustedCurrentManifest.GenerationId);
            return pointer.CacheFormatVersion == IndexCacheFormat.CacheFormatVersion
                && string.Equals(
                    pointer.GenerationId,
                    trustedCurrentManifest.GenerationId,
                    StringComparison.Ordinal)
                && string.Equals(
                    pointer.ManifestFile,
                    expectedManifestFile,
                    StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            (exception is FileNotFoundException
                or DirectoryNotFoundException
                or InvalidDataException
                or JsonException)
            || IsPersistenceException(exception))
        {
            return false;
        }
    }

    public static string GetManifestFileName(string generationId)
        => IndexCacheFileName.GetManifestFileName(generationId);

    public static string GetShardFileName(int shardId, string generationId)
        => IndexCacheFileName.GetShardFileName(shardId, generationId);

    public static string CreateGenerationId()
    {
        Span<byte> bytes = stackalloc byte[IndexCacheFormat.GenerationIdByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        long maxSizeBytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length <= 0 || stream.Length > maxSizeBytes || stream.Length > int.MaxValue)
        {
            throw new InvalidDataException("cache file exceeds its bounded size limit.");
        }

        int length = checked((int)stream.Length);
        byte[] buffer = new byte[length];
        int totalRead = 0;

        while (totalRead < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await stream
                .ReadAsync(buffer.AsMemory(totalRead), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead != buffer.Length || stream.ReadByte() != -1)
        {
            throw new InvalidDataException("cache file changed while it was being read.");
        }

        return buffer;
    }

    private static IndexCurrentPointer ParseCurrentPointer(ReadOnlyMemory<byte> bytes)
    {
        using JsonDocument document = ParseDocument(bytes, maxDepth: 8);
        Dictionary<string, JsonElement> properties = ReadStrictObject(
            document.RootElement,
            "schemaVersion",
            "cacheFormatVersion",
            "generationId",
            "manifestFile");

        int schemaVersion = ReadInt32(properties, "schemaVersion");
        int cacheFormatVersion = ReadInt32(properties, "cacheFormatVersion");
        string generationId = ReadString(properties, "generationId");
        string manifestFile = ReadString(properties, "manifestFile");

        if (schemaVersion != IndexCacheFormat.CurrentPointerSchemaVersion
            || !IndexPathValidation.IsLowerHex(generationId, IndexCacheFormat.GenerationIdHexLength))
        {
            throw new InvalidDataException("current cache pointer uses an unsupported schema or identity.");
        }

        IndexPathValidation.ValidateCacheFileName(manifestFile);
        return new IndexCurrentPointer(cacheFormatVersion, generationId, manifestFile);
    }

    private static IndexManifest ParseManifest(
        ReadOnlyMemory<byte> bytes,
        WorkspaceCacheLocation cacheLocation,
        WorkspaceIdentity workspaceIdentity,
        string expectedGenerationId)
    {
        using JsonDocument document = ParseDocument(bytes, maxDepth: 16);
        Dictionary<string, JsonElement> properties = ReadStrictObject(
            document.RootElement,
            "schemaVersion",
            "cacheFormatVersion",
            "generationId",
            "normalizedProjectRoot",
            "workspaceKey",
            "shardPartitionerVersion",
            "shardCount",
            "sources",
            "shards");

        int schemaVersion = ReadInt32(properties, "schemaVersion");
        int cacheFormatVersion = ReadInt32(properties, "cacheFormatVersion");
        string generationId = ReadString(properties, "generationId");
        string normalizedProjectRoot = ReadString(properties, "normalizedProjectRoot");
        string workspaceKey = ReadString(properties, "workspaceKey");
        int partitionerVersion = ReadInt32(properties, "shardPartitionerVersion");
        int shardCount = ReadInt32(properties, "shardCount");

        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (schemaVersion != IndexCacheFormat.ManifestSchemaVersion
            || cacheFormatVersion != IndexCacheFormat.CacheFormatVersion
            || partitionerVersion != IndexCacheFormat.ShardPartitionerVersion
            || shardCount != IndexCacheFormat.FixedShardCount
            || !string.Equals(generationId, expectedGenerationId, StringComparison.Ordinal)
            || !string.Equals(workspaceKey, cacheLocation.WorkspaceKey, StringComparison.Ordinal)
            || !string.Equals(normalizedProjectRoot, workspaceIdentity.ProjectRoot, pathComparison)
            || !IndexPathValidation.IsLowerHex(generationId, IndexCacheFormat.GenerationIdHexLength))
        {
            throw new InvalidDataException("manifest identity or cache format does not match the established workspace.");
        }

        JsonElement sourceArray = properties["sources"];
        if (sourceArray.ValueKind != JsonValueKind.Array
            || sourceArray.GetArrayLength() > IndexCacheFormat.MaxSourceEntries)
        {
            throw new InvalidDataException("manifest sources are not a bounded array.");
        }

        List<IndexManifestSource> sources = new(sourceArray.GetArrayLength());
        HashSet<string> sourcePaths = new(IndexPathValidation.SourcePathComparer);
        int[] routedCounts = new int[IndexCacheFormat.FixedShardCount];

        foreach (JsonElement sourceElement in sourceArray.EnumerateArray())
        {
            Dictionary<string, JsonElement> sourceProperties = ReadStrictObject(
                sourceElement,
                "relativePath",
                "lastWriteTimeUtcTicks",
                "length",
                "shardId");

            string relativePath = ReadString(sourceProperties, "relativePath");
            long lastWriteTimeUtcTicks = ReadInt64(sourceProperties, "lastWriteTimeUtcTicks");
            long length = ReadInt64(sourceProperties, "length");
            int shardId = ReadInt32(sourceProperties, "shardId");

            IndexPathValidation.ValidateRelativeSourcePath(relativePath);
            if (lastWriteTimeUtcTicks < 0
                || length < 0
                || shardId < 0
                || shardId >= IndexCacheFormat.FixedShardCount
                || IndexShardPartitioner.GetShardId(relativePath) != shardId
                || !sourcePaths.Add(relativePath))
            {
                throw new InvalidDataException("manifest contains invalid or duplicate source routing metadata.");
            }

            routedCounts[shardId]++;
            sources.Add(new IndexManifestSource(relativePath, lastWriteTimeUtcTicks, length, shardId));
        }

        JsonElement shardArray = properties["shards"];
        if (shardArray.ValueKind != JsonValueKind.Array
            || shardArray.GetArrayLength() > IndexCacheFormat.FixedShardCount)
        {
            throw new InvalidDataException("manifest shard catalog is invalid.");
        }

        List<IndexManifestShardReference> shards = new(shardArray.GetArrayLength());
        HashSet<int> shardIds = new();

        foreach (JsonElement shardElement in shardArray.EnumerateArray())
        {
            Dictionary<string, JsonElement> shardProperties = ReadStrictObject(
                shardElement,
                "shardId",
                "fileName",
                "recordCount");

            int shardId = ReadInt32(shardProperties, "shardId");
            string fileName = ReadString(shardProperties, "fileName");
            int recordCount = ReadInt32(shardProperties, "recordCount");

            if (shardId < 0
                || shardId >= IndexCacheFormat.FixedShardCount
                || !shardIds.Add(shardId)
                || recordCount <= 0
                || recordCount != routedCounts[shardId]
                || !IsExpectedShardFileName(fileName, shardId))
            {
                throw new InvalidDataException("manifest contains an invalid or duplicate shard reference.");
            }

            IndexPathValidation.ValidateCacheFileName(fileName);
            shards.Add(new IndexManifestShardReference(shardId, fileName, recordCount));
        }

        for (int shardId = 0; shardId < routedCounts.Length; shardId++)
        {
            if ((routedCounts[shardId] > 0) != shardIds.Contains(shardId))
            {
                throw new InvalidDataException("manifest source routing and shard catalog are inconsistent.");
            }
        }

        return new IndexManifest(
            generationId,
            normalizedProjectRoot,
            workspaceKey,
            sources,
            shards);
    }

    private static IndexShard ParseShard(ReadOnlyMemory<byte> bytes, int expectedShardId)
    {
        using JsonDocument document = ParseDocument(bytes, maxDepth: 16);
        Dictionary<string, JsonElement> properties = ReadStrictObject(
            document.RootElement,
            "schemaVersion",
            "cacheFormatVersion",
            "shardId",
            "generationId",
            "records");

        int schemaVersion = ReadInt32(properties, "schemaVersion");
        int cacheFormatVersion = ReadInt32(properties, "cacheFormatVersion");
        int shardId = ReadInt32(properties, "shardId");
        string generationId = ReadString(properties, "generationId");

        if (schemaVersion != IndexCacheFormat.ShardSchemaVersion
            || cacheFormatVersion != IndexCacheFormat.CacheFormatVersion
            || shardId != expectedShardId
            || !IndexPathValidation.IsLowerHex(generationId, IndexCacheFormat.GenerationIdHexLength))
        {
            throw new InvalidDataException("shard schema or identity is invalid.");
        }

        JsonElement recordsArray = properties["records"];
        if (recordsArray.ValueKind != JsonValueKind.Array
            || recordsArray.GetArrayLength() > IndexCacheFormat.MaxSourceEntries)
        {
            throw new InvalidDataException("shard records are not a bounded array.");
        }

        List<IndexShardRecord> records = new(recordsArray.GetArrayLength());
        HashSet<string> paths = new(IndexPathValidation.SourcePathComparer);

        foreach (JsonElement recordElement in recordsArray.EnumerateArray())
        {
            Dictionary<string, JsonElement> recordProperties = ReadStrictObject(
                recordElement,
                "relativePath",
                "contentHashSha256");

            string relativePath = ReadString(recordProperties, "relativePath");
            string contentHash = ReadString(recordProperties, "contentHashSha256");
            IndexPathValidation.ValidateRelativeSourcePath(relativePath);

            if (IndexShardPartitioner.GetShardId(relativePath) != expectedShardId
                || !paths.Add(relativePath)
                || !IndexPathValidation.IsLowerHex(contentHash, IndexCacheFormat.Sha256HexLength))
            {
                throw new InvalidDataException("shard contains invalid, duplicate or misrouted records.");
            }

            records.Add(new IndexShardRecord(relativePath, contentHash));
        }

        return new IndexShard(shardId, generationId, records);
    }

    private static void ValidateShardAgainstManifest(
        IndexManifest manifest,
        IndexManifestShardReference shardReference,
        IndexShard shard)
    {
        if (shard.Records.Count != shardReference.RecordCount)
        {
            throw new InvalidDataException("shard record count does not match manifest routing metadata.");
        }

        HashSet<string> expectedPaths = new(IndexPathValidation.SourcePathComparer);
        foreach (IndexManifestSource source in manifest.Sources)
        {
            if (source.ShardId == shardReference.ShardId)
            {
                expectedPaths.Add(source.RelativePath);
            }
        }

        if (expectedPaths.Count != shard.Records.Count)
        {
            throw new InvalidDataException("shard source set does not match manifest routing metadata.");
        }

        foreach (IndexShardRecord record in shard.Records)
        {
            if (!expectedPaths.Remove(record.RelativePath))
            {
                throw new InvalidDataException("shard contains a source not routed to it by the manifest.");
            }
        }

        if (expectedPaths.Count != 0)
        {
            throw new InvalidDataException("shard is missing sources routed to it by the manifest.");
        }
    }

    private static async Task WriteImmutableShardAsync(
        string finalPath,
        IndexShard shard,
        CancellationToken cancellationToken)
    {
        string temporaryPath = CreateImmutableTemporaryPath(finalPath);
        bool temporaryFileOwned = false;

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                temporaryFileOwned = true;

                using (Utf8JsonWriter writer = new(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schemaVersion", IndexCacheFormat.ShardSchemaVersion);
                    writer.WriteNumber("cacheFormatVersion", IndexCacheFormat.CacheFormatVersion);
                    writer.WriteNumber("shardId", shard.ShardId);
                    writer.WriteString("generationId", shard.GenerationId);
                    writer.WritePropertyName("records");
                    writer.WriteStartArray();

                    foreach (IndexShardRecord record in shard.Records)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteStartObject();
                        writer.WriteString("relativePath", record.RelativePath);
                        writer.WriteString("contentHashSha256", record.ContentHashSha256);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.Flush();
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);

                if (stream.Length <= 0 || stream.Length > IndexCacheFormat.MaxShardSizeBytes)
                {
                    throw new InvalidOperationException("serialized shard exceeded its bounded cache size.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath, overwrite: false);
            temporaryFileOwned = false;
        }
        finally
        {
            if (temporaryFileOwned)
            {
                TryDeleteOwnedTemporaryFile(temporaryPath);
            }
        }
    }

    private static async Task WriteImmutableManifestAsync(
        string finalPath,
        IndexManifest manifest,
        CancellationToken cancellationToken)
    {
        string temporaryPath = CreateImmutableTemporaryPath(finalPath);
        bool temporaryFileOwned = false;

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                temporaryFileOwned = true;

                using (Utf8JsonWriter writer = new(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schemaVersion", IndexCacheFormat.ManifestSchemaVersion);
                    writer.WriteNumber("cacheFormatVersion", IndexCacheFormat.CacheFormatVersion);
                    writer.WriteString("generationId", manifest.GenerationId);
                    writer.WriteString("normalizedProjectRoot", manifest.NormalizedProjectRoot);
                    writer.WriteString("workspaceKey", manifest.WorkspaceKey);
                    writer.WriteNumber("shardPartitionerVersion", IndexCacheFormat.ShardPartitionerVersion);
                    writer.WriteNumber("shardCount", IndexCacheFormat.FixedShardCount);
                    writer.WritePropertyName("sources");
                    writer.WriteStartArray();

                    foreach (IndexManifestSource source in manifest.Sources)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteStartObject();
                        writer.WriteString("relativePath", source.RelativePath);
                        writer.WriteNumber("lastWriteTimeUtcTicks", source.LastWriteTimeUtcTicks);
                        writer.WriteNumber("length", source.Length);
                        writer.WriteNumber("shardId", source.ShardId);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WritePropertyName("shards");
                    writer.WriteStartArray();

                    foreach (IndexManifestShardReference shard in manifest.Shards)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteStartObject();
                        writer.WriteNumber("shardId", shard.ShardId);
                        writer.WriteString("fileName", shard.FileName);
                        writer.WriteNumber("recordCount", shard.RecordCount);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.Flush();
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);

                if (stream.Length <= 0 || stream.Length > IndexCacheFormat.MaxManifestSizeBytes)
                {
                    throw new InvalidOperationException("serialized manifest exceeded its bounded cache size.");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath, overwrite: false);
            temporaryFileOwned = false;
        }
        finally
        {
            if (temporaryFileOwned)
            {
                TryDeleteOwnedTemporaryFile(temporaryPath);
            }
        }
    }

    private static async Task WriteCurrentPointerTemporaryAsync(
        string temporaryPath,
        string generationId,
        string manifestFile,
        CancellationToken cancellationToken)
    {
        bool temporaryFileOwned = false;

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                temporaryFileOwned = true;

                using (Utf8JsonWriter writer = new(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schemaVersion", IndexCacheFormat.CurrentPointerSchemaVersion);
                    writer.WriteNumber("cacheFormatVersion", IndexCacheFormat.CacheFormatVersion);
                    writer.WriteString("generationId", generationId);
                    writer.WriteString("manifestFile", manifestFile);
                    writer.WriteEndObject();
                    writer.Flush();
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);

                if (stream.Length <= 0 || stream.Length > IndexCacheFormat.MaxCurrentPointerSizeBytes)
                {
                    throw new InvalidOperationException("serialized current pointer exceeded its bounded cache size.");
                }
            }

            temporaryFileOwned = false;
        }
        finally
        {
            if (temporaryFileOwned)
            {
                TryDeleteOwnedTemporaryFile(temporaryPath);
            }
        }
    }

    private static JsonDocument ParseDocument(ReadOnlyMemory<byte> bytes, int maxDepth)
        => JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = maxDepth,
            });

    private static Dictionary<string, JsonElement> ReadStrictObject(
        JsonElement element,
        params string[] requiredProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("cache JSON value is not the expected object.");
        }

        HashSet<string> allowed = new(requiredProperties, StringComparer.Ordinal);
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !result.TryAdd(property.Name, property.Value))
            {
                throw new InvalidDataException("cache JSON contains an unknown or duplicate property.");
            }
        }

        if (result.Count != requiredProperties.Length)
        {
            throw new InvalidDataException("cache JSON is missing a required property.");
        }

        return result;
    }

    private static string ReadString(Dictionary<string, JsonElement> properties, string name)
    {
        JsonElement value = properties[name];
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("cache JSON property is not a string.");
        }

        string? result = value.GetString();
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidDataException("cache JSON string property is empty.");
        }

        return result;
    }

    private static int ReadInt32(Dictionary<string, JsonElement> properties, string name)
    {
        if (!properties[name].TryGetInt32(out int value))
        {
            throw new InvalidDataException("cache JSON property is not a valid Int32.");
        }

        return value;
    }

    private static long ReadInt64(Dictionary<string, JsonElement> properties, string name)
    {
        if (!properties[name].TryGetInt64(out long value))
        {
            throw new InvalidDataException("cache JSON property is not a valid Int64.");
        }

        return value;
    }

    private static bool IsExpectedShardFileName(string fileName, int shardId)
        => IndexCacheFileName.IsExpectedShardFileName(fileName, shardId);

    private static string CreateImmutableTemporaryPath(string finalPath)
    {
        string? directory = Path.GetDirectoryName(finalPath);
        string finalFileNameWithoutExtension = Path.GetFileNameWithoutExtension(finalPath);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(finalFileNameWithoutExtension))
        {
            throw new ArgumentException("immutable cache final path must include a directory and filename.", nameof(finalPath));
        }

        return CreateUniqueTemporaryPath(
            directory,
            $".{finalFileNameWithoutExtension}_");
    }

    private static string CreateUniqueTemporaryPath(string directory, string prefix)
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        string suffix = Convert.ToHexString(bytes).ToLowerInvariant();
        return Path.Combine(directory, $"{prefix}{suffix}.tmp");
    }

    private static void TryDeleteOwnedTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Only this operation's exact unique temp file is eligible for best-effort cleanup.
        }
    }

    private static bool IsPersistenceException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PathTooLongException
            or ArgumentException
            or System.Security.SecurityException;

    private readonly record struct IndexCurrentPointer(
        int CacheFormatVersion,
        string GenerationId,
        string ManifestFile);
}

internal enum IndexCacheLoadStatus
{
    Miss,
    Valid,
    Invalid,
    IncompatibleFormat,
    PersistenceUnavailable,
}

internal readonly record struct IndexCacheLoadResult(
    IndexCacheLoadStatus Status,
    IndexManifest? Manifest,
    int? ObservedCacheFormatVersion)
{
    public static IndexCacheLoadResult Miss()
        => new(IndexCacheLoadStatus.Miss, null, null);

    public static IndexCacheLoadResult Valid(IndexManifest manifest)
        => new(IndexCacheLoadStatus.Valid, manifest, null);

    public static IndexCacheLoadResult Invalid()
        => new(IndexCacheLoadStatus.Invalid, null, null);

    public static IndexCacheLoadResult IncompatibleFormat(int observedCacheFormatVersion)
        => new(IndexCacheLoadStatus.IncompatibleFormat, null, observedCacheFormatVersion);

    public static IndexCacheLoadResult PersistenceUnavailable()
        => new(IndexCacheLoadStatus.PersistenceUnavailable, null, null);
}

internal enum IndexShardLoadStatus
{
    Valid,
    Invalid,
    PersistenceUnavailable,
}

internal readonly record struct IndexShardLoadResult(
    IndexShardLoadStatus Status,
    IndexShard? Shard,
    string? PersistentPath)
{
    public static IndexShardLoadResult Valid(IndexShard shard, string persistentPath)
        => new(IndexShardLoadStatus.Valid, shard, persistentPath);

    public static IndexShardLoadResult Invalid()
        => new(IndexShardLoadStatus.Invalid, null, null);

    public static IndexShardLoadResult PersistenceUnavailable()
        => new(IndexShardLoadStatus.PersistenceUnavailable, null, null);
}

internal readonly record struct IndexCachePublicationResult(bool IsPersisted)
{
    public static IndexCachePublicationResult Persisted() => new(true);

    public static IndexCachePublicationResult Unavailable() => new(false);
}
