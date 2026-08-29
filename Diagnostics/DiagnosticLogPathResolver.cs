namespace SystemExplorer.CodeService;

internal static class DiagnosticLogPathResolver
{
    private const string ApplicationDirectoryName = "SystemExplorer";
    private const string ServiceDirectoryName = "CodeService";
    private const string DiagnosticsDirectoryName = "Diagnostics";

    public static string ResolveDiagnosticDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                ResolveWindowsBaseDirectory(),
                ApplicationDirectoryName,
                ServiceDirectoryName,
                DiagnosticsDirectoryName);
        }

        if (OperatingSystem.IsMacOS())
        {
            string? userProfile = TryGetSpecialFolder(Environment.SpecialFolder.UserProfile);
            if (userProfile is not null)
            {
                return Path.Combine(
                    userProfile,
                    "Library",
                    "Logs",
                    ApplicationDirectoryName,
                    ServiceDirectoryName);
            }

            return Path.Combine(
                ResolveApplicationDataFallback(),
                ApplicationDirectoryName,
                ServiceDirectoryName,
                DiagnosticsDirectoryName);
        }

        string? xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgStateHome) && Path.IsPathFullyQualified(xdgStateHome))
        {
            return Path.Combine(
                xdgStateHome,
                ApplicationDirectoryName,
                ServiceDirectoryName,
                DiagnosticsDirectoryName);
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
                DiagnosticsDirectoryName);
        }

        return Path.Combine(
            ResolveApplicationDataFallback(),
            ApplicationDirectoryName,
            ServiceDirectoryName,
            DiagnosticsDirectoryName);
    }

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
