using System.Security.Cryptography;
using System.Text;

namespace SystemExplorer.CodeService;

internal static class IndexCachePathResolver
{
    private const string ApplicationDirectoryName = "SystemExplorer";
    private const string ServiceDirectoryName = "CodeService";
    private const string CacheDirectoryName = "Cache";
    private const string WorkspacesDirectoryName = "Workspaces";
    // Stable process-external coordination namespace. Cache format revisions do not
    // change this directory name.
    private const string ManagedWorkspacesDirectoryName = "managed";

    public static WorkspaceCacheLocation ResolveWorkspaceCache(WorkspaceIdentity workspaceIdentity)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);

        string workspaceKey = ComputeWorkspaceKey(workspaceIdentity);
        string baseDirectory = ResolveCacheBaseDirectory();
        string workspaceDirectory = Path.GetFullPath(
            Path.Combine(baseDirectory, WorkspacesDirectoryName, ManagedWorkspacesDirectoryName, workspaceKey));

        if (IsSameOrDescendantPath(workspaceDirectory, workspaceIdentity.ProjectRoot))
        {
            throw new InvalidOperationException(
                "resolved persistent cache directory must remain outside the Godot project root.");
        }

        return new WorkspaceCacheLocation(workspaceKey, workspaceDirectory);
    }

    public static string ComputeWorkspaceKey(WorkspaceIdentity workspaceIdentity)
    {
        ArgumentNullException.ThrowIfNull(workspaceIdentity);

        string canonicalIdentity;
        if (OperatingSystem.IsWindows())
        {
            canonicalIdentity = workspaceIdentity.ProjectRoot
                .Replace('\\', '/')
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .ToUpperInvariant();
        }
        else
        {
            canonicalIdentity = workspaceIdentity.ProjectRoot;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalIdentity));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolveCacheBaseDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                ResolveWindowsBaseDirectory(),
                ApplicationDirectoryName,
                ServiceDirectoryName,
                CacheDirectoryName);
        }

        if (OperatingSystem.IsMacOS())
        {
            string? userProfile = TryGetSpecialFolder(Environment.SpecialFolder.UserProfile);
            if (userProfile is not null)
            {
                return Path.Combine(
                    userProfile,
                    "Library",
                    "Caches",
                    ApplicationDirectoryName,
                    ServiceDirectoryName);
            }

            return Path.Combine(
                ResolveApplicationDataFallback(),
                ApplicationDirectoryName,
                ServiceDirectoryName,
                CacheDirectoryName);
        }

        string? xdgCacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (IsAbsolutePath(xdgCacheHome))
        {
            return Path.Combine(
                xdgCacheHome!,
                ApplicationDirectoryName,
                ServiceDirectoryName);
        }

        string? unixUserProfile = TryGetSpecialFolder(Environment.SpecialFolder.UserProfile);
        if (unixUserProfile is not null)
        {
            return Path.Combine(
                unixUserProfile,
                ".cache",
                ApplicationDirectoryName,
                ServiceDirectoryName);
        }

        return Path.Combine(
            ResolveApplicationDataFallback(),
            ApplicationDirectoryName,
            ServiceDirectoryName,
            CacheDirectoryName);
    }


    private static bool IsSameOrDescendantPath(string candidatePath, string rootPath)
    {
        string normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(normalizedCandidate, normalizedRoot, comparison))
        {
            return true;
        }

        string rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(rootPrefix, comparison);
    }

    private static bool IsAbsolutePath(string? path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static string ResolveWindowsBaseDirectory()
    {
        string? localApplicationData = TryGetSpecialFolder(Environment.SpecialFolder.LocalApplicationData);
        if (localApplicationData is not null)
        {
            return localApplicationData;
        }

        string? applicationData = TryGetSpecialFolder(Environment.SpecialFolder.ApplicationData);
        if (applicationData is not null)
        {
            return applicationData;
        }

        string? userProfile = TryGetSpecialFolder(Environment.SpecialFolder.UserProfile);
        if (userProfile is not null)
        {
            return Path.Combine(userProfile, "AppData", "Local");
        }

        throw new InvalidOperationException("could not resolve a per-user Windows cache directory.");
    }

    private static string ResolveApplicationDataFallback()
    {
        string? localApplicationData = TryGetSpecialFolder(Environment.SpecialFolder.LocalApplicationData);
        if (localApplicationData is not null)
        {
            return localApplicationData;
        }

        string? applicationData = TryGetSpecialFolder(Environment.SpecialFolder.ApplicationData);
        if (applicationData is not null)
        {
            return applicationData;
        }

        throw new InvalidOperationException("could not resolve a per-user cache directory.");
    }

    private static string? TryGetSpecialFolder(Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        return path;
    }
}

internal readonly record struct WorkspaceCacheLocation(
    string WorkspaceKey,
    string WorkspaceDirectory)
{
    public string MaintenanceAuthorityPath => Path.Combine(WorkspaceDirectory, "maintenance.lock");

    public string CurrentPointerPath => Path.Combine(WorkspaceDirectory, "current.json");

    public string ManifestsDirectory => Path.Combine(WorkspaceDirectory, "manifests");

    public string ShardsDirectory => Path.Combine(WorkspaceDirectory, "shards");
}
