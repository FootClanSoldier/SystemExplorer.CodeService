using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ProbeOptionsParseResult parsed = ProbeOptions.TryParse(args);
        if (parsed.ShowHelp)
        {
            PrintUsage();
            return ProbeConstants.SuccessExitCode;
        }

        if (!parsed.IsSuccess)
        {
            Console.Error.WriteLine(parsed.ErrorMessage);
            PrintUsage();
            return ProbeConstants.InvalidArgumentsExitCode;
        }

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            ProbeOptions options = parsed.Options!;
            RoslynLanguageServerToolVerificationResult toolVerification = await RoslynLanguageServerToolVerifier.VerifyAsync(
                options.ServerCommandPath,
                cancellation.Token).ConfigureAwait(false);
            RoslynLanguageServerLaunchSpec launchSpec = RoslynLanguageServerLaunchSpec.Create(toolVerification);

            ProbeScenarioRunner runner = new(options, toolVerification, launchSpec);
            ProbeReport report = await runner.RunAsync(cancellation.Token).ConfigureAwait(false);
            string reportPath = await ProbeReportWriter.WriteAsync(report, options, CancellationToken.None).ConfigureAwait(false);
            PrintSummary(report, reportPath);

            return report.OverallDecision switch
            {
                ProbeOverallDecision.UnsuitableCandidate => ProbeConstants.CapabilityFailureExitCode,
                ProbeOverallDecision.Inconclusive => ProbeConstants.InfrastructureFailureExitCode,
                _ => ProbeConstants.SuccessExitCode,
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Probe cancelled after owned subprocess retirement.");
            return ProbeConstants.InfrastructureFailureExitCode;
        }
        catch (ProbeFixtureSetupException exception)
        {
            Console.Error.WriteLine($"Fixture setup failure: {exception.Message}");
            return ProbeConstants.InfrastructureFailureExitCode;
        }
        catch (ProbeServerSetupException exception)
        {
            Console.Error.WriteLine($"Server setup failure: {exception.Message}");
            return ProbeConstants.ServerSetupFailureExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Probe infrastructure failure: {exception}");
            return ProbeConstants.InfrastructureFailureExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "Usage: RoslynLanguageServerCapabilityProbe --server <absolute-path> " +
            "[--solution <.sln|.slnx> | --project <.csproj>] " +
            "[--document <.cs> [--completion-line <n> --completion-character <n>] " +
            "[--definition-line <n> --definition-character <n> --expected-definition <.cs>]] " +
            "[--report <path>] [--keep-artifacts] [--no-auto-load-comparison] [--stale-version-experiment]");
    }

    private static void PrintSummary(ProbeReport report, string reportPath)
    {
        Console.WriteLine();
        Console.WriteLine("Roslyn Language Server Capability Probe");
        Console.WriteLine($"Expected version: {report.RoslynLanguageServerExpectedVersion}");
        Console.WriteLine($"Actual version:   {report.RoslynLanguageServerActualVersion}");
        Console.WriteLine();
        foreach (ProbeScenarioResult scenario in report.Scenarios)
            Console.WriteLine($"{scenario.Name,-30} {scenario.Status.ToString().ToUpperInvariant()}");
        Console.WriteLine();
        Console.WriteLine($"Candidate: {report.OverallDecision}");
        Console.WriteLine($"Report: {reportPath}");
    }
}

internal sealed class ProbeServerSetupException(string message, Exception? innerException = null)
    : Exception(message, innerException);
