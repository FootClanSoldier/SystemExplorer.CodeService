namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;

internal static class WindowsCommandShimInvocation
{
    private static readonly char[] UnsafePathCharacters = ['"', '\r', '\n', '&', '|', '<', '>', '^', '%', '!', '(', ')'];
    private static readonly char[] UnsafeArgumentCharacters = ['"', '\r', '\n', '&', '|', '<', '>', '^', '%', '!', '(', ')', ' ', '\t'];

    public static string BuildProcessArguments(string serverCommandPath, IReadOnlyList<string> arguments)
    {
        if (!Path.IsPathFullyQualified(serverCommandPath))
            throw new ProbeServerSetupException("Windows command-shim launch requires a fully-qualified server command path.");
        if (serverCommandPath.IndexOfAny(UnsafePathCharacters) >= 0)
        {
            throw new ProbeServerSetupException(
                $"Windows command-shim path contains shell-sensitive characters that this probe intentionally refuses to interpolate: {serverCommandPath}");
        }

        foreach (string argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument) || argument.IndexOfAny(UnsafeArgumentCharacters) >= 0)
            {
                throw new ProbeServerSetupException(
                    $"Windows command-shim argument cannot be represented by the probe's fail-closed quoting strategy: {argument}");
            }
        }

        string argumentText = arguments.Count == 0 ? string.Empty : " " + string.Join(" ", arguments);

        // cmd.exe owns a second command-line parsing layer. Supply the complete /d /s /c
        // invocation as one raw ProcessStartInfo.Arguments string so the nested quote pair
        // reaches the command interpreter intact. This yields:
        // /d /s /c ""C:\Path With Spaces\roslyn-language-server.cmd" --stdio ..."
        return $"/d /s /c \"\"{serverCommandPath}\"{argumentText}\"";
    }
}
