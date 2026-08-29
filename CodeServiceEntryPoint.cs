namespace SystemExplorer.CodeService;

internal static class CodeServiceEntryPoint
{
    private const int SuccessExitCode = 0;
    private const int FatalFailureExitCode = 1;
    private const int InvalidArgumentsExitCode = 2;
    private const int OwnerValidationFailureExitCode = 3;
    private const int LocalTransportStartupFailureExitCode = 4;
    private const int SessionStartupFailureExitCode = 5;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            CodeServiceStartupOptionsParseResult parseResult = CodeServiceStartupOptions.TryParse(args);
            if (!parseResult.IsSuccess)
            {
                Console.Error.WriteLine($"SystemExplorer.CodeService: {parseResult.ErrorMessage}");
                return InvalidArgumentsExitCode;
            }

            CodeServiceHostCreationResult hostResult =
                await CodeServiceHost.TryCreateAsync(parseResult.Options!).ConfigureAwait(false);

            ReportDiagnosticStartupMetadata(hostResult);

            if (!hostResult.IsSuccess)
            {
                Console.Error.WriteLine($"SystemExplorer.CodeService: {hostResult.ErrorMessage}");

                return hostResult.FailureKind switch
                {
                    CodeServiceHostCreationFailureKind.OwnerValidationFailure
                        => OwnerValidationFailureExitCode,
                    CodeServiceHostCreationFailureKind.TransportStartupFailure
                        => LocalTransportStartupFailureExitCode,
                    CodeServiceHostCreationFailureKind.SessionStartupFailure
                        => SessionStartupFailureExitCode,
                    _ => FatalFailureExitCode,
                };
            }

            await using CodeServiceHost host = hostResult.Host!;
            await host.RunAsync().ConfigureAwait(false);
            return SuccessExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SystemExplorer.CodeService: fatal error: {exception.Message}");
            return FatalFailureExitCode;
        }
    }

    private static void ReportDiagnosticStartupMetadata(CodeServiceHostCreationResult hostResult)
    {
        if (hostResult.DiagnosticLogPath is not null)
        {
            Console.Error.WriteLine(
                $"SystemExplorer.CodeService: diagnostic log: {hostResult.DiagnosticLogPath}");
        }
        else if (hostResult.DiagnosticLoggingWarning is not null)
        {
            Console.Error.WriteLine(
                $"SystemExplorer.CodeService: warning: {hostResult.DiagnosticLoggingWarning}");
        }
    }
}
