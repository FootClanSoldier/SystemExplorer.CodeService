using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> SemanticOriginSummaryNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SemanticOriginLocalObserved"] = "Local",
            ["SemanticOriginParameterObserved"] = "Parameter",
            ["SemanticOriginLocalFunctionObserved"] = "LocalFunction",
            ["SemanticOriginCurrentTypeDepthObserved"] = "CurrentType depth=0",
            ["SemanticOriginBaseDepth1Observed"] = "BaseType depth=1",
            ["SemanticOriginBaseDepth2Observed"] = "BaseType depth=2",
            ["SemanticOriginOtherUserCodeObserved"] = "OtherUserCode",
            ["SemanticOriginSourceExtensionObserved"] = "SourceExtension",
            ["SemanticOriginFrameworkObserved"] = "FrameworkOrOther",
            ["SemanticOriginUnknownNonSymbolControlObserved"] = "UnknownNonSymbol",
            ["SemanticOriginMetadataWellFormed"] = "MetadataWellFormed",
            ["SemanticOriginServerSurvived"] = "ServerSurvived",
            ["SemanticOriginGracefulRetirement"] = "GracefulRetirement",
        };

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
            return options.RunMode switch
            {
                ProbeRunMode.FullCapability => await RunFullCapabilityAsync(options, cancellation.Token).ConfigureAwait(false),
                ProbeRunMode.CompletionSemanticOriginOnly => await RunCompletionSemanticOriginOnlyAsync(options, cancellation.Token).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported probe run mode: {options.RunMode}"),
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

    private static async Task<int> RunFullCapabilityAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        RoslynLanguageServerToolVerificationResult toolVerification = await RoslynLanguageServerToolVerifier.VerifyAsync(
            options.ServerCommandPath!,
            cancellationToken).ConfigureAwait(false);
        RoslynLanguageServerLaunchSpec launchSpec = RoslynLanguageServerLaunchSpec.Create(toolVerification);

        ProbeScenarioRunner runner = new(options, toolVerification, launchSpec);
        ProbeReport report = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
        string reportPath = await ProbeReportWriter.WriteAsync(report, options, CancellationToken.None).ConfigureAwait(false);
        PrintSummary(report, reportPath);

        return report.OverallDecision switch
        {
            ProbeOverallDecision.UnsuitableCandidate => ProbeConstants.CapabilityFailureExitCode,
            ProbeOverallDecision.Inconclusive => ProbeConstants.InfrastructureFailureExitCode,
            _ => ProbeConstants.SuccessExitCode,
        };
    }

    private static async Task<int> RunCompletionSemanticOriginOnlyAsync(
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        CompletionSemanticOriginVerificationRunner runner = new(options);
        CompletionSemanticOriginVerificationReport report = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
        string reportPath = await CompletionSemanticOriginVerificationReportWriter.WriteAsync(
            report,
            options,
            CancellationToken.None).ConfigureAwait(false);
        PrintCompletionSemanticOriginSummary(report, reportPath);

        return report.Scenario.Status switch
        {
            ProbeScenarioStatus.Pass => ProbeConstants.SuccessExitCode,
            ProbeScenarioStatus.Fail => ProbeConstants.CapabilityFailureExitCode,
            ProbeScenarioStatus.Skipped => ProbeConstants.InfrastructureFailureExitCode,
            _ => ProbeConstants.InfrastructureFailureExitCode,
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "Full capability mode:\n" +
            "  RoslynLanguageServerCapabilityProbe --server <absolute-path> " +
            "[--state-trace-server <absolute-path> --state-trace-provenance <absolute-path>] " +
            "[--semantic-origin-server <absolute-path> --semantic-origin-provenance <absolute-path>] [--solution <.sln|.slnx> | --project <.csproj>] " +
            "[--document <.cs> [--completion-line <n> --completion-character <n>] " +
            "[--definition-line <n> --definition-character <n> --expected-definition <.cs>]] " +
            "[--report <path>] [--keep-artifacts] [--no-auto-load-comparison] [--stale-version-experiment]\n\n" +
            "Completion semantic-origin verification mode:\n" +
            "  RoslynLanguageServerCapabilityProbe --semantic-origin-only " +
            "--semantic-origin-server <absolute-path> --semantic-origin-provenance <absolute-path> " +
            "[--report <path>] [--keep-artifacts]");
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

    private static void PrintCompletionSemanticOriginSummary(
        CompletionSemanticOriginVerificationReport report,
        string reportPath)
    {
        Console.WriteLine();
        Console.WriteLine("Completion Semantic Origin Verification");
        Console.WriteLine();

        HashSet<string> rendered = new(StringComparer.Ordinal);
        foreach (ProbeCheckResult check in report.Scenario.Checks)
        {
            if (!SemanticOriginSummaryNames.TryGetValue(check.Name, out string? displayName))
                continue;

            PrintSemanticOriginCheck(check, displayName);
            rendered.Add(check.Name);
        }

        foreach (ProbeCheckResult failedCheck in report.Scenario.Checks.Where(static check => !check.Passed))
        {
            if (rendered.Contains(failedCheck.Name))
                continue;

            PrintSemanticOriginCheck(failedCheck, failedCheck.Name);
        }

        Console.WriteLine();
        Console.WriteLine($"RESULT: {(report.Passed ? "PASS" : "FAIL")}");
        Console.WriteLine($"Report: {reportPath}");
    }

    private static void PrintSemanticOriginCheck(ProbeCheckResult check, string displayName)
    {
        Console.WriteLine($"{(check.Passed ? "PASS" : "FAIL"),-5} {displayName}");
        if (!check.Passed && !string.IsNullOrWhiteSpace(check.Details))
            Console.WriteLine($"      {check.Details}");
    }
}

internal sealed class ProbeServerSetupException(string message, Exception? innerException = null)
    : Exception(message, innerException);
