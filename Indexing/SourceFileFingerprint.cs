using System.Buffers;
using System.Security.Cryptography;

namespace SystemExplorer.CodeService;

internal static class SourceFileFingerprint
{
    private const int HashBufferSize = 64 * 1024;
    private const int MaxStableReadAttempts = 3;

    public static SourceFileMetadata ReadMetadata(
        WorkspaceIdentity workspaceIdentity,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);
        IndexPathValidation.ValidateRelativeSourcePath(relativePath);

        string sourcePath = ResolveSourcePath(workspaceIdentity, relativePath);
        FileInfo fileInfo = new(sourcePath);
        fileInfo.Refresh();

        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("source file no longer exists during index preflight.", sourcePath);
        }

        return new SourceFileMetadata(
            relativePath,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc.Ticks,
            IndexShardPartitioner.GetShardId(relativePath));
    }

    public static async Task<SourceFingerprintReadResult> ComputeStableAsync(
        WorkspaceIdentity workspaceIdentity,
        string relativePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);
        IndexPathValidation.ValidateRelativeSourcePath(relativePath);

        string sourcePath = ResolveSourcePath(workspaceIdentity, relativePath);

        for (int attempt = 1; attempt <= MaxStableReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceFileStat before = CaptureStat(sourcePath);
            string hash = await ComputeHashAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SourceFileStat after = CaptureStat(sourcePath);

            if (before == after)
            {
                return new SourceFingerprintReadResult(
                    new SourceFileMetadata(
                        relativePath,
                        after.Length,
                        after.LastWriteTimeUtcTicks,
                        IndexShardPartitioner.GetShardId(relativePath)),
                    hash);
            }
        }

        throw new IOException("source file remained unstable across bounded fingerprint attempts.");
    }

    private static async Task<string> ComputeHashAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);

        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using FileStream stream = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: HashBufferSize,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await stream
                    .ReadAsync(buffer.AsMemory(0, HashBufferSize), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private static SourceFileStat CaptureStat(string sourcePath)
    {
        FileInfo fileInfo = new(sourcePath);
        fileInfo.Refresh();
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("source file no longer exists during fingerprinting.", sourcePath);
        }

        return new SourceFileStat(fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
    }

    private static string ResolveSourcePath(
        WorkspaceIdentity workspaceIdentity,
        string relativePath)
    {
        string platformRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string sourcePath = Path.GetFullPath(Path.Combine(workspaceIdentity.ProjectRoot, platformRelativePath));
        string resolvedRelativePath = WorkspaceProjectPathClassifier.NormalizeRelativePath(
            Path.GetRelativePath(workspaceIdentity.ProjectRoot, sourcePath));

        if (Path.IsPathFullyQualified(resolvedRelativePath)
            || resolvedRelativePath.Equals("..", StringComparison.Ordinal)
            || resolvedRelativePath.StartsWith("../", StringComparison.Ordinal)
            || !IndexPathValidation.SourcePathComparer.Equals(resolvedRelativePath, relativePath))
        {
            throw new IOException("source path escaped the established workspace root.");
        }

        return sourcePath;
    }

    private readonly record struct SourceFileStat(long Length, long LastWriteTimeUtcTicks);
}

internal readonly record struct SourceFileMetadata(
    string RelativePath,
    long Length,
    long LastWriteTimeUtcTicks,
    int ShardId);

internal readonly record struct SourceFingerprintReadResult(
    SourceFileMetadata Metadata,
    string ContentHashSha256);
