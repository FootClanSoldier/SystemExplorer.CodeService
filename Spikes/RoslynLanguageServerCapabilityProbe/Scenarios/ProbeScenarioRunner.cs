using System.Runtime.InteropServices;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal sealed class ProbeScenarioRunner
{
    private static readonly string[] RequiredFixtureScenarios =
    [
        "ExplicitSolutionOpen",
        "Completion",
        "DocumentSynchronization",
        "Navigation",
        "Diagnostics",
        "Rename",
        "Recovery",
    ];

    private readonly ProbeOptions _options;
    private readonly RoslynLanguageServerToolVerificationResult _toolVerification;
    private readonly RoslynLanguageServerLaunchSpec _launchSpec;

    public ProbeScenarioRunner(
        ProbeOptions options,
        RoslynLanguageServerToolVerificationResult toolVerification,
        RoslynLanguageServerLaunchSpec launchSpec)
    {
        _options = options;
        _toolVerification = toolVerification;
        _launchSpec = launchSpec;

        if (!string.Equals(toolVerification.ExpectedVersion, ProbeConstants.RoslynLanguageServerVersion, StringComparison.Ordinal)
            || !string.Equals(toolVerification.ActualVersion, ProbeConstants.RoslynLanguageServerVersion, StringComparison.Ordinal))
        {
            throw new ProbeServerSetupException("Capability scenarios require verified exact Roslyn Language Server private-tool provenance.");
        }
    }

    public async Task<ProbeReport> RunAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        await using ProbeFixtureWorkspace fixture = await ProbeFixtureWorkspaceBuilder.CreateAsync(
            _options.KeepArtifacts,
            cancellationToken).ConfigureAwait(false);
        ProbeFixtureRestoreResult restoreResult = await ProbeFixtureRestorer.RestoreAsync(
            fixture,
            cancellationToken).ConfigureAwait(false);
        ProbeScenarioContext context = new(_options, fixture, _launchSpec, restoreResult);
        List<ProbeScenarioResult> scenarios = [];
        ProbeWorkspaceReport realWorkspace = new(
            "RealGodot", null, null, false, false, null, null, null, null, null, "NOT RUN");

        try
        {
            ProbeScenarioResult explicitOpen = await InitializationScenario.RunExplicitSolutionOpenAsync(
                context, cancellationToken).ConfigureAwait(false);
            scenarios.Add(explicitOpen);

            if (context.FixtureSemanticRequestSucceeded)
            {
                scenarios.Add(ProbeScenarioResult.Skipped(
                    "SemanticGateDisambiguation",
                    "Primary semantic gate passed; failure disambiguation was not required."));
            }
            else if (context.PrimarySession is not null && !context.PrimarySession.Process.HasExited)
            {
                scenarios.Add(await SemanticGateDisambiguationScenario.RunAsync(
                    context, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                scenarios.Add(ProbeScenarioResult.Skipped(
                    "SemanticGateDisambiguation",
                    "Primary Roslyn generation was not alive for semantic-gate disambiguation."));
            }

            bool semanticContinuationPossible = context.PrimarySession is not null
                && !context.PrimarySession.Process.HasExited
                && context.FixtureSemanticRequestSucceeded;

            if (semanticContinuationPossible)
            {
                scenarios.Add(await CompletionScenario.RunAsync(context, cancellationToken).ConfigureAwait(false));
                ProbeScenarioResult documentSynchronization = await DocumentSynchronizationScenario.RunAsync(
                    context, cancellationToken).ConfigureAwait(false);
                scenarios.Add(documentSynchronization);

                if (context.PrimarySession is not null && !context.PrimarySession.Process.HasExited)
                {
                    scenarios.Add(await NavigationScenario.RunAsync(context, cancellationToken).ConfigureAwait(false));
                    scenarios.Add(await DiagnosticsScenario.RunAsync(context, cancellationToken).ConfigureAwait(false));
                    scenarios.Add(await RenameScenario.RunAsync(context, cancellationToken).ConfigureAwait(false));
                    scenarios.Add(await RecoveryScenario.RunAsync(context, cancellationToken).ConfigureAwait(false));

                    // Recovery may fail before it explicitly retires the primary generation.
                    // Ensure no stale-version observation can overlap an earlier Roslyn process.
                    await RetirePrimarySessionIfAnyAsync(context).ConfigureAwait(false);

                    if (documentSynchronization.Status == ProbeScenarioStatus.Pass)
                    {
                        scenarios.Add(await StaleVersionScenario.RunAsync(context, cancellationToken).ConfigureAwait(false));
                    }
                    else
                    {
                        scenarios.Add(ProbeScenarioResult.Skipped(
                            "StaleDocumentVersionObservation",
                            "Primary document synchronization did not pass; stale-version observation would not add reliable evidence."));
                    }
                }
                else
                {
                    AddSkippedAfterDeadProcess(scenarios);
                }
            }
            else
            {
                AddSkippedAfterInitializationFailure(scenarios);
            }

            // Every fixture generation is terminal before starting another comparison or real-workspace generation.
            await RetirePrimarySessionIfAnyAsync(context).ConfigureAwait(false);

            scenarios.Add(await InitializationScenario.RunAutoLoadComparisonAsync(
                context, cancellationToken).ConfigureAwait(false));

            int fixtureProcessCount = context.ProcessResults.Count;
            RoslynLanguageServerProcessResult[] fixtureProcesses = context.ProcessResults
                .Take(fixtureProcessCount)
                .ToArray();
            ProbeScenarioResult[] fixtureScenarios = scenarios.ToArray();

            ProbeWorkspaceReport fixtureWorkspace = new(
                "ControlledFixture",
                fixture.RootPath,
                fixture.SolutionPath,
                context.FixtureProjectInitializationObserved,
                context.FixtureSemanticRequestSucceeded,
                context.FixtureSemanticReadyMs,
                NoUnexpectedProcessFailure(fixtureScenarios),
                context.FixtureServerCapabilities,
                CountStderr(fixtureProcesses, "warning"),
                CountStderr(fixtureProcesses, "error"),
                _options.KeepArtifacts ? "Temporary fixture retained by --keep-artifacts." : "Temporary fixture scheduled for cleanup after probe completion.");

            (ProbeScenarioResult realResult, ProbeWorkspaceReport workspace) = await RealWorkspaceScenario.RunAsync(
                context, cancellationToken).ConfigureAwait(false);
            scenarios.Add(realResult);
            realWorkspace = workspace;

            ProbeOverallDecision decision = Classify(scenarios, _options);
            return new ProbeReport(
                ProbeConstants.ReportSchemaVersion,
                ProbeConstants.ProbeVersion,
                _toolVerification.ExpectedVersion,
                _toolVerification.ActualVersion,
                true,
                ProbeConstants.StreamJsonRpcVersion,
                _toolVerification.ServerCommandPath,
                _launchSpec.LaunchKind,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                DescribePlatform(),
                RuntimeInformation.OSDescription,
                RuntimeInformation.FrameworkDescription,
                fixtureWorkspace,
                realWorkspace,
                scenarios,
                context.ProcessResults.ToArray(),
                decision);
        }
        finally
        {
            await RetirePrimarySessionIfAnyAsync(context).ConfigureAwait(false);
        }
    }

    private static async Task RetirePrimarySessionIfAnyAsync(ProbeScenarioContext context)
    {
        if (context.PrimarySession is null)
            return;

        ProbeSession session = context.PrimarySession;
        context.PrimarySession = null;
        await session.DisposeAsync().ConfigureAwait(false);
    }

    private static string DescribePlatform()
    {
        string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "Linux"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? "macOS"
                    : "Unknown";
        return $"{os}/{RuntimeInformation.OSArchitecture}";
    }

    private static ProbeOverallDecision Classify(IReadOnlyList<ProbeScenarioResult> scenarios, ProbeOptions options)
    {
        foreach (string name in RequiredFixtureScenarios)
        {
            ProbeScenarioResult? scenario = scenarios.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
            if (scenario is null || scenario.Status != ProbeScenarioStatus.Pass)
                return ProbeOverallDecision.UnsuitableCandidate;
        }

        bool realWorkspaceSelected = options.SolutionPath is not null || options.ProjectPath is not null;
        ProbeScenarioResult? real = scenarios.FirstOrDefault(s => s.Name == "RealGodotWorkspace");
        if (!realWorkspaceSelected || real is null || real.Status == ProbeScenarioStatus.Skipped)
            return ProbeOverallDecision.SuitableCandidateForRealWorkspaceValidation;

        if (real.Status != ProbeScenarioStatus.Pass)
            return ProbeOverallDecision.UnsuitableCandidate;

        return options.FullRealSemanticValidationSelected
            ? ProbeOverallDecision.SuitableCandidate
            : ProbeOverallDecision.SuitableCandidateForRealWorkspaceValidation;
    }

    private static bool NoUnexpectedProcessFailure(IReadOnlyList<ProbeScenarioResult> scenarios)
    {
        return !scenarios
            .SelectMany(static scenario => scenario.Checks)
            .Where(static check => check.Name.Contains("ProcessSurvived", StringComparison.Ordinal)
                || check.Name.Contains("DidChangeProcessSurvived", StringComparison.Ordinal))
            .Any(static check => !check.Passed);
    }

    private static int CountStderr(IReadOnlyList<RoslynLanguageServerProcessResult> processes, string token) =>
        processes.Sum(result => result.CapturedStderr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains(token, StringComparison.OrdinalIgnoreCase)));

    private static void AddSkippedAfterInitializationFailure(List<ProbeScenarioResult> scenarios)
    {
        foreach (string name in RequiredFixtureScenarios.Skip(1))
            scenarios.Add(ProbeScenarioResult.Skipped(name, "Explicit initialization did not produce usable semantic state."));
        scenarios.Add(ProbeScenarioResult.Skipped("StaleDocumentVersionObservation", "Primary semantic state was unavailable."));
    }

    private static void AddSkippedAfterDeadProcess(List<ProbeScenarioResult> scenarios)
    {
        foreach (string name in new[] { "Navigation", "Diagnostics", "Rename", "Recovery" })
            scenarios.Add(ProbeScenarioResult.Skipped(name, "Roslyn process was no longer alive after document synchronization."));
        scenarios.Add(ProbeScenarioResult.Skipped("StaleDocumentVersionObservation", "Primary Roslyn generation died before observation setup."));
    }
}
