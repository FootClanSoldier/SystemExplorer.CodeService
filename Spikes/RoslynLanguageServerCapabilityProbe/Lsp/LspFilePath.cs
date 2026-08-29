namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;

internal static class LspFilePath
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static bool IsFileUriPathEqual(string uriText, string expectedPath)
    {
        if (!TryGetFilePath(uriText, out string? actualPath))
            return false;

        string normalizedExpected;
        try
        {
            normalizedExpected = NormalizePath(expectedPath);
        }
        catch
        {
            return false;
        }

        return string.Equals(actualPath, normalizedExpected, PathComparison);
    }

    public static bool TryGetFilePath(string uriText, out string? path)
    {
        path = null;
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri) || !uri.IsFile)
            return false;

        try
        {
            path = NormalizePath(uri.LocalPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
