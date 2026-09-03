using System.Runtime.InteropServices;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal sealed class ProbeScenarioRunner
{
    private static readonly string[] RequiredFixtureScenarios =
    [
        "ExplicitSolutionOpen",
        "SemanticReadiness",
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

            ProbeScenarioResult semanticReadiness = await SemanticReadinessScenario.RunAsync(
                context, cancellationToken).ConfigureAwait(false);
            scenarios.Add(semanticReadiness);

            if (semanticReadiness.Status == ProbeScenarioStatus.Pass
                && context.FixtureSemanticRequestSucceeded)
            {
                scenarios.Add(ProbeScenarioResult.Skipped(
                    "SemanticGateDisambiguation",
                    "Primary semantic readiness was established; failure disambiguation was not required."));
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
                    "Primary Roslyn generation was not alive after semantic-readiness failure."));
            }

            bool semanticContinuationPossible = semanticReadiness.Status == ProbeScenarioStatus.Pass
                && context.PrimarySession is not null
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
                AddSkippedAfterSemanticReadinessFailure(scenarios);
            }

            // Every fixture generation is terminal before starting another comparison or real-workspace generation.
            await RetirePrimarySessionIfAnyAsync(context).ConfigureAwait(false);

            scenarios.Add(await InitializationScenario.RunAutoLoadComparisonAsync(
                context, cancellationToken).ConfigureAwait(false));

            string? trueEditorSkipReason = GetTrueEditorBufferSkipReason(context);
            if (trueEditorSkipReason is null)
            {
                scenarios.Add(await TrueEditorBufferCompletionDisambiguationScenario.RunAsync(
                    context, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                scenarios.Add(ProbeScenarioResult.Skipped(
                    "TrueEditorBufferCompletionDisambiguation",
                    trueEditorSkipReason));
            }

            string? sameDocumentSkipReason = GetSameDocumentCompletionSkipReason(context);
            if (sameDocumentSkipReason is null)
            {
                scenarios.Add(await SameDocumentCompletionDisambiguationScenario.RunAsync(
                    context, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                scenarios.Add(ProbeScenarioResult.Skipped(
                    "SameDocumentCompletionDisambiguation",
                    sameDocumentSkipReason));
            }

            string? diagnosticPullSkipReason = GetDiagnosticPullCompletionSkipReason(context);
            if (diagnosticPullSkipReason is null)
            {
                scenarios.Add(await DiagnosticPullCompletionDisambiguationScenario.RunAsync(
                    context, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                scenarios.Add(ProbeScenarioResult.Skipped(
                    "DiagnosticPullCompletionDisambiguation",
                    diagnosticPullSkipReason));
            }

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

            // The official fixture authority snapshot above is frozen before any instrumented process exists.
            // This diagnostic-only scenario may add a custom process to the final report, but cannot influence
            // fixture process survival/stderr metrics or classifier authority.
            scenarios.Add(await RoslynStateLineageTraceScenario.RunAsync(
                context, cancellationToken).ConfigureAwait(false));
            scenarios.Add(await CompletionSemanticOriginScenario.RunAsync(
                context, cancellationToken).ConfigureAwait(false));

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

    private static string? GetTrueEditorBufferSkipReason(ProbeScenarioContext context)
    {
        if (context.FixtureSemanticRequestSucceeded)
            return "Fixture semantic readiness was established; further completion disambiguation was not required.";

        CompletionResponseEvidence? primaryCompletionEvidence = context.PrimaryCompletionEvidence;
        SemanticGateDisambiguationEvidence? evidence = context.PrimarySemanticGateDisambiguationEvidence;
        if (primaryCompletionEvidence is null || evidence is null)
            return "Primary semantic-gate disambiguation evidence was unavailable.";

        if (primaryCompletionEvidence.ResultKind != CompletionResponseResultKind.Null)
            return "Primary readiness completion did not return Null; true-editor-buffer disambiguation was not the next isolated branch.";

        if (evidence.PreDefinitionNaturalCompletionEvidence.ResultKind != CompletionResponseResultKind.Null)
            return "Pre-definition primary true-editor completion changed shape; true-editor-buffer disambiguation was not the next isolated branch.";

        if (evidence.DefinitionLocationCount <= 0 || !evidence.DefinitionMatchedExpectedFixtureSymbol)
        {
            return "Primary disambiguation did not establish the definition-pass completion-null branch required for true-editor-buffer disambiguation.";
        }

        if (evidence.PostDefinitionNaturalCompletionEvidence.ResultKind != CompletionResponseResultKind.Null)
            return "Post-definition primary true-editor completion changed shape; true-editor-buffer disambiguation was not the next isolated branch.";

        return null;
    }

    private static string? GetSameDocumentCompletionSkipReason(ProbeScenarioContext context)
    {
        if (context.FixtureSemanticRequestSucceeded)
            return "Fixture semantic readiness was established; further completion disambiguation was not required.";

        CompletionResponseEvidence? primaryCompletionEvidence = context.PrimaryCompletionEvidence;
        SemanticGateDisambiguationEvidence? primaryEvidence = context.PrimarySemanticGateDisambiguationEvidence;
        if (primaryCompletionEvidence is null || primaryEvidence is null)
            return "Primary semantic-gate disambiguation evidence was unavailable.";

        if (primaryCompletionEvidence.ResultKind != CompletionResponseResultKind.Null
            || primaryEvidence.PreDefinitionNaturalCompletionEvidence.ResultKind != CompletionResponseResultKind.Null
            || primaryEvidence.PostDefinitionNaturalCompletionEvidence.ResultKind != CompletionResponseResultKind.Null)
        {
            return "Primary true-editor completion evidence did not establish the completion-null branch required for same-document disambiguation.";
        }

        if (primaryEvidence.DefinitionLocationCount <= 0
            || !primaryEvidence.DefinitionMatchedExpectedFixtureSymbol)
        {
            return "Primary disambiguation did not establish the definition-pass completion-null branch required for same-document disambiguation.";
        }

        TrueEditorBufferCompletionEvidence? trueEditorEvidence = context.TrueEditorBufferEvidence;
        if (trueEditorEvidence is null)
            return "True-editor-buffer evidence was unavailable.";

        if (trueEditorEvidence.CompletionEvidence.ResultKind != CompletionResponseResultKind.Null)
            return "True-editor completion was non-Null; same-document completion was not the next isolated branch.";

        if (trueEditorEvidence.DefinitionLocationCount <= 0
            || !trueEditorEvidence.DefinitionMatchedExpectedFixtureSymbol)
        {
            return "True-editor generation did not establish the definition-pass completion-null branch required for same-document disambiguation.";
        }

        if (!trueEditorEvidence.SnapshotVerified || !trueEditorEvidence.DiskUnchanged)
            return "True-editor generation did not establish verified editor-buffer and disk-authority evidence required for same-document disambiguation.";

        return null;
    }

    private static string? GetDiagnosticPullCompletionSkipReason(ProbeScenarioContext context)
    {
        if (context.FixtureSemanticRequestSucceeded)
            return "Fixture semantic readiness was established; further completion disambiguation was not required.";

        CompletionResponseEvidence? primaryCompletionEvidence = context.PrimaryCompletionEvidence;
        SemanticGateDisambiguationEvidence? primaryEvidence = context.PrimarySemanticGateDisambiguationEvidence;
        if (primaryCompletionEvidence is null || primaryEvidence is null)
            return "Primary semantic-gate disambiguation evidence was unavailable.";

        if (primaryCompletionEvidence.ResultKind != CompletionResponseResultKind.Null
            || primaryEvidence.PreDefinitionNaturalCompletionEvidence.ResultKind != CompletionResponseResultKind.Null
            || primaryEvidence.PostDefinitionNaturalCompletionEvidence.ResultKind != CompletionResponseResultKind.Null)
        {
            return "Primary true-editor completion evidence did not establish the exact completion-null branch required for diagnostic-pull disambiguation.";
        }

        TrueEditorBufferCompletionEvidence? trueEditorEvidence = context.TrueEditorBufferEvidence;
        if (trueEditorEvidence is null)
            return "True-editor-buffer evidence was unavailable.";

        if (trueEditorEvidence.CompletionEvidence.ResultKind != CompletionResponseResultKind.Null)
            return "Cross-document true-editor completion was non-Null; diagnostic-pull disambiguation was not the next isolated branch.";

        bool priorComparabilityEstablished = primaryEvidence.DefinitionLocationCount > 0
            && primaryEvidence.DefinitionMatchedExpectedFixtureSymbol
            && trueEditorEvidence.SnapshotVerified
            && trueEditorEvidence.DefinitionLocationCount > 0
            && trueEditorEvidence.DefinitionMatchedExpectedFixtureSymbol
            && trueEditorEvidence.DiskUnchanged;
        if (!priorComparabilityEstablished)
        {
            return "Existing diagnostic controls did not establish the definition-pass and disk-authority branch required for diagnostic-pull disambiguation.";
        }

        SameDocumentCompletionEvidence? sameDocumentEvidence = context.SameDocumentEvidence;
        if (sameDocumentEvidence is null)
            return "Same-document completion evidence was unavailable.";

        bool sameDocumentCompletionEstablished = (sameDocumentEvidence.CompletionEvidence.ResultKind is
            CompletionResponseResultKind.Array or CompletionResponseResultKind.CompletionList)
            && sameDocumentEvidence.CompletionIncludesProbePrivateField;
        if (!sameDocumentCompletionEstablished)
        {
            return "Same-document completion did not establish the working-member-completion control required for diagnostic-pull disambiguation.";
        }

        bool sameDocumentComparabilityEstablished = sameDocumentEvidence.SnapshotVerified
            && sameDocumentEvidence.DefinitionLocationCount > 0
            && sameDocumentEvidence.DefinitionMatchedExpectedFixtureSymbol
            && sameDocumentEvidence.DiskUnchanged;
        if (!sameDocumentComparabilityEstablished)
        {
            return "Existing diagnostic controls did not establish the definition-pass and disk-authority branch required for diagnostic-pull disambiguation.";
        }

        return null;
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

    private static void AddSkippedAfterSemanticReadinessFailure(List<ProbeScenarioResult> scenarios)
    {
        foreach (string name in new[]
        {
            "Completion",
            "DocumentSynchronization",
            "Navigation",
            "Diagnostics",
            "Rename",
            "Recovery",
        })
        {
            scenarios.Add(ProbeScenarioResult.Skipped(
                name,
                "Fixture semantic readiness was not established in the primary Roslyn generation."));
        }
        scenarios.Add(ProbeScenarioResult.Skipped(
            "StaleDocumentVersionObservation",
            "Primary semantic readiness was unavailable."));
    }

    private static void AddSkippedAfterDeadProcess(List<ProbeScenarioResult> scenarios)
    {
        foreach (string name in new[] { "Navigation", "Diagnostics", "Rename", "Recovery" })
            scenarios.Add(ProbeScenarioResult.Skipped(name, "Roslyn process was no longer alive after document synchronization."));
        scenarios.Add(ProbeScenarioResult.Skipped("StaleDocumentVersionObservation", "Primary Roslyn generation died before observation setup."));
    }
}
