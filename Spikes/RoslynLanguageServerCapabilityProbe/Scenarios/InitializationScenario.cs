using System.Diagnostics;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class InitializationScenario
{
    public static Task<ProbeScenarioResult> RunExplicitSolutionOpenAsync(
        ProbeScenarioContext context,
        CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("ExplicitSolutionOpen", cancellationToken, async checks =>
        {
            ProbeFixtureRestoreResult restore = context.FixtureRestore;
            bool assetsExists = File.Exists(restore.AssetsFilePath);
            checks.Add(new ProbeCheckResult(
                "FixtureRestoreVerified",
                restore.ExitCode == 0 && restore.AssetsFileLength > 0 && assetsExists,
                $"exitCode={restore.ExitCode}; assetsExists={assetsExists.ToString().ToLowerInvariant()}; "
                    + $"assetsBytes={restore.AssetsFileLength}; restoreMs={restore.DurationMs:0.###}"));

            ProbeSession session = await context.StartSessionAsync(
                context.Fixture.RootPath,
                autoLoadProjects: false,
                cancellationToken).ConfigureAwait(false);
            context.PrimarySession = session;

            checks.Add(new ProbeCheckResult("ServerStart", !session.Process.HasExited,
                $"pid={session.Process.Identity.ProcessId}; generation={session.Process.Identity.ScenarioGeneration}"));

            Stopwatch semanticReadyStopwatch = Stopwatch.StartNew();
            (bool readinessObserved, double elapsedMs) = await session.InitializeWorkspaceAsync(
                context.Fixture.RootPath,
                context.Fixture.SolutionPath,
                explicitOpen: true,
                cancellationToken).ConfigureAwait(false);

            context.FixtureProjectInitializationObserved = readinessObserved;
            context.FixtureServerCapabilities = session.Client.ServerCapabilities;
            checks.Add(new ProbeCheckResult("Initialize", !session.Process.HasExited));
            checks.Add(new ProbeCheckResult(
                "ProjectInitializationNotificationObserved",
                readinessObserved,
                readinessObserved ? "workspace/projectInitializationComplete observed" : "notification timed out",
                elapsedMs));

            string target = context.Fixture.ReadTarget();
            string consumer = context.Fixture.ReadConsumer();
            await session.Client.DidOpenAsync(
                context.Fixture.TargetPath,
                target,
                1,
                cancellationToken).ConfigureAwait(false);
            context.CurrentTargetText = target;
            context.CurrentTargetVersion = 1;
            checks.Add(new ProbeCheckResult("TargetDocumentDidOpen", !session.Process.HasExited, "version=1"));

            await session.Client.DidOpenAsync(
                context.Fixture.ConsumerPath,
                consumer,
                1,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult("ConsumerDocumentDidOpen", !session.Process.HasExited, "version=1"));

            var position = ProbeSourceMarker.FindUniqueCompletionPosition(consumer, "PROBE_INSTANCE_COMPLETION");
            CompletionRequestResult completion = await session.Client.CompletionAsync(
                context.Fixture.ConsumerPath, position, cancellationToken).ConfigureAwait(false);
            semanticReadyStopwatch.Stop();
            bool semanticSucceeded = ScenarioExecution.ContainsLabel(completion.Items, "ProbeInstanceProperty");
            context.FixtureSemanticRequestSucceeded = semanticSucceeded;
            context.FixtureSemanticReadyMs = semanticSucceeded ? semanticReadyStopwatch.Elapsed.TotalMilliseconds : null;
            context.PrimaryCompletionEvidence = completion.Evidence;
            checks.Add(new ProbeCheckResult(
                "SemanticRequestsSucceeded",
                semanticSucceeded,
                ScenarioExecution.DescribeCompletionEvidence(completion),
                completion.DurationMs));
            AddUnresolvedDependencyCheck(
                checks,
                "NoUnresolvedDependencyWarning",
                session.Client.Callbacks.Messages);
            if (!semanticSucceeded)
            {
                checks.Add(new ProbeCheckResult(
                    "ServerMessagesObserved",
                    true,
                    DescribeServerMessages(session.Client.Callbacks.Messages)));
            }
            checks.Add(new ProbeCheckResult(
                "WorkspaceConfigurationRequests",
                true,
                session.Client.Callbacks.ConfigurationRequests.Count == 0
                    ? "none observed"
                    : string.Join(" | ", session.Client.Callbacks.ConfigurationRequests)));
            checks.Add(new ProbeCheckResult(
                "DynamicRegistrationsObserved",
                true,
                session.Client.ServerCapabilities is null
                    ? "capabilities unavailable"
                    : string.Join(",", session.Client.ServerCapabilities.DynamicRegistrationMethods)));
            checks.Add(new ProbeCheckResult(
                "HandledServerRequests",
                true,
                session.Client.Callbacks.ServerRequests.Count == 0
                    ? "none observed"
                    : string.Join(",", session.Client.Callbacks.ServerRequests.Distinct(StringComparer.Ordinal))));
            checks.Add(new ProbeCheckResult("ProcessSurvivedInitialization", !session.Process.HasExited));
            ScenarioExecution.AddProtocolCoverageObservation(checks, session);
        });

    public static async Task<ProbeScenarioResult> RunAutoLoadComparisonAsync(
        ProbeScenarioContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Options.RunAutoLoadComparison)
            return ProbeScenarioResult.Skipped("AutoLoadProjects", "Disabled by --no-auto-load-comparison.");

        return await ScenarioExecution.RunAsync("AutoLoadProjects", cancellationToken, async checks =>
        {
            ProbeFixtureRestoreResult restore = context.FixtureRestore;
            checks.Add(new ProbeCheckResult(
                "AutoLoadFixtureRestoreReused",
                restore.ExitCode == 0 && restore.AssetsFileLength > 0,
                $"assetsBytes={restore.AssetsFileLength}; preRestored=true"));

            await using ProbeSession session = await context.StartSessionAsync(
                context.Fixture.RootPath,
                autoLoadProjects: true,
                cancellationToken).ConfigureAwait(false);
            (bool readinessObserved, double elapsedMs) = await session.InitializeWorkspaceAsync(
                context.Fixture.RootPath,
                context.Fixture.SolutionPath,
                explicitOpen: false,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult("ProjectInitializationNotificationObserved", readinessObserved, null, elapsedMs));

            string target = context.Fixture.ReadTarget();
            string consumer = context.Fixture.ReadConsumer();
            await session.Client.DidOpenAsync(
                context.Fixture.TargetPath,
                target,
                1,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult("AutoLoadTargetDocumentDidOpen", !session.Process.HasExited, "version=1"));

            await session.Client.DidOpenAsync(
                context.Fixture.ConsumerPath,
                consumer,
                1,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult("AutoLoadConsumerDocumentDidOpen", !session.Process.HasExited, "version=1"));

            var position = ProbeSourceMarker.FindUniqueCompletionPosition(consumer, "PROBE_INSTANCE_COMPLETION");
            CompletionRequestResult completion = await session.Client.CompletionAsync(
                context.Fixture.ConsumerPath, position, cancellationToken).ConfigureAwait(false);
            bool semanticSucceeded = ScenarioExecution.ContainsLabel(completion.Items, "ProbeInstanceProperty");
            checks.Add(new ProbeCheckResult(
                "AutoLoadSemanticRequestSucceeded",
                semanticSucceeded,
                ScenarioExecution.DescribeCompletionEvidence(completion),
                completion.DurationMs));
            checks.Add(new ProbeCheckResult(
                "AutoLoadCompletionResponseShapeComparison",
                true,
                DescribeCompletionEvidenceComparison(context.PrimaryCompletionEvidence, completion.Evidence)));
            AddUnresolvedDependencyCheck(
                checks,
                "AutoLoadNoUnresolvedDependencyWarning",
                session.Client.Callbacks.Messages);
            if (!semanticSucceeded)
            {
                checks.Add(new ProbeCheckResult(
                    "AutoLoadServerMessagesObserved",
                    true,
                    DescribeServerMessages(session.Client.Callbacks.Messages)));

                if (!session.Process.HasExited)
                {
                    await SemanticGateDisambiguationScenario.AddDiagnosticChecksAsync(
                        context,
                        session,
                        checks,
                        "AutoLoad",
                        completion.Evidence,
                        includeProcessSurvivalCheck: false,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            ScenarioExecution.AddProtocolCoverageObservation(checks, session);
            if (!session.Process.HasExited)
            {
                await session.Client.DidCloseAsync(context.Fixture.ConsumerPath, cancellationToken).ConfigureAwait(false);
                await session.Client.DidCloseAsync(context.Fixture.TargetPath, cancellationToken).ConfigureAwait(false);
            }
            await session.GracefulRetireAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static void AddUnresolvedDependencyCheck(
        List<ProbeCheckResult> checks,
        string checkName,
        IReadOnlyList<string> messages)
    {
        const string unresolvedDependencyText = "has unresolved dependencies";
        string[] matches = messages
            .Where(message => message.Contains(unresolvedDependencyText, StringComparison.OrdinalIgnoreCase))
            .TakeLast(8)
            .Select(ConciseMessage)
            .ToArray();
        checks.Add(new ProbeCheckResult(
            checkName,
            matches.Length == 0,
            matches.Length == 0 ? "none observed" : $"count={matches.Length}; recent={string.Join(" | ", matches)}"));
    }

    private static string ConciseMessage(string message)
    {
        const int maxCharacters = 512;
        string compact = string.Join(
            " ",
            message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= maxCharacters ? compact : compact[..maxCharacters] + "...";
    }

    private static string DescribeCompletionEvidenceComparison(
        CompletionResponseEvidence? explicitEvidence,
        CompletionResponseEvidence autoLoadEvidence)
    {
        if (explicitEvidence is null)
            return $"explicit=<unavailable>; autoLoad={ScenarioExecution.DescribeResponseShape(autoLoadEvidence)}; differs=<unknown>";

        bool differs = explicitEvidence != autoLoadEvidence;
        return $"explicit={ScenarioExecution.DescribeResponseShape(explicitEvidence)}; "
            + $"autoLoad={ScenarioExecution.DescribeResponseShape(autoLoadEvidence)}; differs={differs.ToString().ToLowerInvariant()}";
    }

    private static string DescribeServerMessages(IReadOnlyList<string> messages)
    {
        const int maxRecentMessages = 8;
        string[] recent = messages.TakeLast(maxRecentMessages).ToArray();
        return $"count={messages.Count}; recent={(recent.Length == 0 ? "<none>" : string.Join(" | ", recent))}";
    }

}
