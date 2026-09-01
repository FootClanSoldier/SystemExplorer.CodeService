using System.Runtime.InteropServices;

namespace SystemExplorer.CodeService;

internal enum RoslynLanguageServerRuntimeResolutionStatus
{
    Resolved,
    UnsupportedPlatform,
    Failure,
}

internal readonly record struct RoslynLanguageServerRuntimeResolutionResult(
    RoslynLanguageServerRuntimeResolutionStatus Status,
    RoslynLanguageServerRuntime? Runtime,
    string? ErrorMessage)
{
    public bool IsResolved
        => Status == RoslynLanguageServerRuntimeResolutionStatus.Resolved && Runtime is not null;

    public static RoslynLanguageServerRuntimeResolutionResult Resolved(
        RoslynLanguageServerRuntime runtime)
        => new(RoslynLanguageServerRuntimeResolutionStatus.Resolved, runtime, null);

    public static RoslynLanguageServerRuntimeResolutionResult UnsupportedPlatform()
        => new(RoslynLanguageServerRuntimeResolutionStatus.UnsupportedPlatform, null, null);

    public static RoslynLanguageServerRuntimeResolutionResult Failure(string errorMessage)
        => new(RoslynLanguageServerRuntimeResolutionStatus.Failure, null, errorMessage);
}

internal static class RoslynLanguageServerRuntimeResolver
{
    public static RoslynLanguageServerRuntimeResolutionResult Resolve(
        RoslynLanguageServerRuntime? explicitRuntime)
    {
        if (explicitRuntime is not null)
        {
            return RoslynLanguageServerRuntimeResolutionResult.Resolved(explicitRuntime);
        }

        if (!IsPackagedRuntimeSupportedPlatform())
        {
            return RoslynLanguageServerRuntimeResolutionResult.UnsupportedPlatform();
        }

        string runtimeDirectory;
        try
        {
            runtimeDirectory = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    RoslynLanguageServerConstants.PackagedWindowsX64RuntimeRelativePath));
        }
        catch (Exception exception) when (IsControlledPathException(exception))
        {
            return RoslynLanguageServerRuntimeResolutionResult.Failure(
                $"packaged private Roslyn runtime path resolution failed: {ToSingleLine(exception.Message)}");
        }

        RoslynLanguageServerRuntimeValidationResult validation =
            RoslynLanguageServerRuntime.TryValidate(
                runtimeDirectory,
                RoslynLanguageServerRuntimeSource.PackagedPrivateRuntime);

        return validation.IsSuccess
            ? RoslynLanguageServerRuntimeResolutionResult.Resolved(validation.Runtime!)
            : RoslynLanguageServerRuntimeResolutionResult.Failure(
                $"packaged private Roslyn runtime validation failed: {validation.ErrorMessage}");
    }

    public static string GetCurrentPlatformName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return "other";
    }

    private static bool IsPackagedRuntimeSupportedPlatform()
        => OperatingSystem.IsWindows()
            && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static bool IsControlledPathException(Exception exception)
        => exception is ArgumentException
            or NotSupportedException
            or PathTooLongException;

    private static string ToSingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');
}
