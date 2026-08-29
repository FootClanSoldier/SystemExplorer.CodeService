namespace SystemExplorer.CodeService;

internal static class SessionRuntimePathResolver
{
    private const string ApplicationDirectoryName = "SystemExplorer";
    private const string ServiceDirectoryName = "CodeService";
    private const string SessionsDirectoryName = "Sessions";
    private const string LaunchAuthorityDirectoryName = "LaunchAuthority";
    private const string DescriptorDirectoryName = "Descriptors";

    public static string ResolveLaunchAuthorityDirectory()
        => Path.Combine(ResolveSessionsDirectory(), LaunchAuthorityDirectoryName);

    public static string ResolveDescriptorDirectory()
        => Path.Combine(ResolveSessionsDirectory(), DescriptorDirectoryName);

    public static string ResolveDescriptorPath(GodotProcessIdentity ownerIdentity)
        => Path.Combine(
            ResolveDescriptorDirectory(),
            $"owner_{ownerIdentity.ProcessId}_{ownerIdentity.StartTimeUtcTicks}.json");

    private static string ResolveSessionsDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                ResolveWindowsBaseDirectory(),
                ApplicationDirectoryName,
                ServiceDirectoryName,
                SessionsDirectoryName);
        }

        if (OperatingSystem.IsMacOS())
        {
            string? userProfile = TryGetSpecialFolder(Environment.SpecialFolder.UserProfile);
            if (userProfile is not null)
            {
                return Path.Combine(
                    userProfile,
                    "Library",
                    "Application Support",
                    ApplicationDirectoryName,
                    ServiceDirectoryName,
                    SessionsDirectoryName);
            }

            return Path.Combine(
                ResolveApplicationDataFallback(),
                ApplicationDirectoryName,
                ServiceDirectoryName,
                SessionsDirectoryName);
        }

        string? xdgRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (IsAbsolutePath(xdgRuntimeDirectory))
        {
            return Path.Combine(
                xdgRuntimeDirectory!,
                ApplicationDirectoryName,
                ServiceDirectoryName,
                SessionsDirectoryName);
        }

        string? xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (IsAbsolutePath(xdgStateHome))
        {
            return Path.Combine(
                xdgStateHome!,
                ApplicationDirectoryName,
                ServiceDirectoryName,
                SessionsDirectoryName);
        }

        string? unixUserProfile = TryGetSpecialFolder(Environment.SpecialFolder.UserProfile);
        if (unixUserProfile is not null)
        {
            return Path.Combine(
                unixUserProfile,
                ".local",
                "state",
                ApplicationDirectoryName,
                ServiceDirectoryName,
                SessionsDirectoryName);
        }

        return Path.Combine(
            ResolveApplicationDataFallback(),
            ApplicationDirectoryName,
            ServiceDirectoryName,
            SessionsDirectoryName);
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

        throw new InvalidOperationException("could not resolve a per-user Windows application-data directory.");
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

        throw new InvalidOperationException("could not resolve a per-user application-data directory.");
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
