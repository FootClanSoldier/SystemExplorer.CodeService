using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SystemExplorer.CodeService;

internal static class IndexShardPartitioner
{
    public static int GetShardId(string relativePath)
    {
        IndexPathValidation.ValidateRelativeSourcePath(relativePath);

        string canonicalPath = OperatingSystem.IsWindows()
            ? relativePath.ToUpperInvariant()
            : relativePath;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath));
        uint value = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(0, sizeof(uint)));
        return checked((int)(value % IndexCacheFormat.FixedShardCount));
    }
}

internal static class IndexPathValidation
{
    public static StringComparer SourcePathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static void ValidateRelativeSourcePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Length > IndexCacheFormat.MaxRelativePathLength
            || relativePath.Contains('\\')
            || relativePath.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidDataException("cache source path is not a valid normalized project-relative path.");
        }

        string[] segments = relativePath.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0
                || segment.Equals(".", StringComparison.Ordinal)
                || segment.Equals("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException("cache source path contains an invalid path segment.");
            }
        }
    }

    public static void ValidateCacheFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Length > 256
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("cache metadata contains an invalid cache filename.");
        }
    }

    public static string ResolveContainedFile(string directory, string fileName)
    {
        ValidateCacheFileName(fileName);

        string normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string candidatePath = Path.GetFullPath(Path.Combine(normalizedDirectory, fileName));
        string expectedParent = Path.GetDirectoryName(candidatePath)
            ?? throw new InvalidDataException("cache file path does not have a parent directory.");

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(normalizedDirectory, expectedParent, comparison))
        {
            throw new InvalidDataException("cache file escaped its expected cache directory.");
        }

        return candidatePath;
    }

    public static bool IsLowerHex(string value, int exactLength)
    {
        if (value.Length != exactLength)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool valid = (character >= '0' && character <= '9')
                || (character >= 'a' && character <= 'f');
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }
}
