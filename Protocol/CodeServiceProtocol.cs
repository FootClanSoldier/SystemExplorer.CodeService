using System.Reflection;

namespace SystemExplorer.CodeService;

internal static class CodeServiceProtocol
{
    public const int ProtocolVersion = 1;
    public const int HandshakeSchemaVersion = 1;
    public const int WorkspaceSchemaVersion = 1;
    public const string HandshakePath = "/control/handshake";
    public const string WorkspaceInitializePath = "/workspace/initialize";
    public const string WorkspaceStatusPath = "/workspace/status";
    public const string HandshakeSuccessOutcome = "Success";
    public const string HandshakeInvalidRequestOutcome = "InvalidRequest";
    public const string HandshakeVersionMismatchOutcome = "VersionMismatch";
    public const string WorkspaceSuccessOutcome = "Success";
    public const string WorkspaceInvalidRequestOutcome = "InvalidRequest";
    public const string WorkspaceVersionMismatchOutcome = "VersionMismatch";
    public const string WorkspaceBusyOutcome = "Busy";
    public const string WorkspaceMismatchOutcome = "WorkspaceMismatch";
    public const string WorkspaceUnavailableOutcome = "Unavailable";
    public const string WorkspaceFaultedOutcome = "Faulted";
    public const string ProtocolVersionHeaderName = "X-SystemExplorer-Protocol-Version";
    public const string SessionIdHeaderName = "X-SystemExplorer-Session-Id";
    public const string RequestIdHeaderName = "X-SystemExplorer-Request-Id";

    public static CodeServiceVersionResolutionResult TryResolveServiceVersion()
    {
        try
        {
            Assembly assembly = typeof(CodeServiceProtocol).Assembly;
            AssemblyInformationalVersionAttribute? informationalVersion =
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

            string? serviceVersion = informationalVersion?.InformationalVersion;
            if (!IsUsableServiceVersion(serviceVersion))
            {
                return CodeServiceVersionResolutionResult.Failure(
                    "the built CodeService assembly does not expose a usable informational version.");
            }

            return CodeServiceVersionResolutionResult.Success(serviceVersion!);
        }
        catch (Exception exception)
        {
            return CodeServiceVersionResolutionResult.Failure(
                $"could not resolve CodeService assembly version: {ToSingleLine(exception.Message)}");
        }
    }

    private static bool IsUsableServiceVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string ToSingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');
}

internal readonly record struct CodeServiceVersionResolutionResult(
    string? ServiceVersion,
    string? ErrorMessage)
{
    public bool IsSuccess => ServiceVersion is not null;

    public static CodeServiceVersionResolutionResult Success(string serviceVersion)
        => new(serviceVersion, null);

    public static CodeServiceVersionResolutionResult Failure(string errorMessage)
        => new(null, errorMessage);
}
