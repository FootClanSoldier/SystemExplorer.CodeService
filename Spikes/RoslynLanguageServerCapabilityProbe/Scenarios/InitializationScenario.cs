using System.Diagnostics;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class InitializationScenario
{
    private const string PrimaryConsumerAnchor = "return target.ProbeExtension();";
    private const string PrimaryConsumerEditorPrefix = "return target.";

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

            context.PrimarySemanticReadinessStartTimestamp = Stopwatch.GetTimestamp();
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
            string diskConsumer = context.Fixture.ReadConsumer();
            PrimaryConsumerSnapshot snapshot = CreatePrimaryConsumerSnapshot(diskConsumer);
            bool diskUnchangedBeforeOpen = string.Equals(
                context.Fixture.ReadConsumer(),
                diskConsumer,
                StringComparison.Ordinal);
            bool rightHandIdentifier = snapshot.CaretAbsoluteIndex < snapshot.Text.Length
                && snapshot.Text[snapshot.CaretAbsoluteIndex] is not '\r' and not '\n';
            bool semicolonAtCaret = snapshot.CaretAbsoluteIndex < snapshot.Text.Length
                && snapshot.Text[snapshot.CaretAbsoluteIndex] == ';';
            bool snapshotVerified = !string.Equals(snapshot.Text, diskConsumer, StringComparison.Ordinal)
                && snapshot.CaretAbsoluteIndex > 0
                && snapshot.Text[snapshot.CaretAbsoluteIndex - 1] == '.'
                && !rightHandIdentifier
                && !semicolonAtCaret
                && diskUnchangedBeforeOpen
                && CountOrdinalOccurrences(diskConsumer, PrimaryConsumerAnchor) == 1;
            checks.Add(new ProbeCheckResult(
                "PrimaryTrueEditorBufferSnapshotVerified",
                snapshotVerified,
                $"logicalCaret=return target.|; rightHandIdentifier={rightHandIdentifier.ToString().ToLowerInvariant()}; "
                    + $"diskUnchanged={diskUnchangedBeforeOpen.ToString().ToLowerInvariant()}"));
            if (!snapshotVerified)
                throw new InvalidOperationException("Primary true-editor Consumer snapshot verification failed before didOpen.");

            await session.Client.DidOpenAsync(
                context.Fixture.TargetPath,
                target,
                1,
                cancellationToken).ConfigureAwait(false);
            context.CurrentTargetText = target;
            context.CurrentTargetVersion = 1;
            checks.Add(new ProbeCheckResult("TargetDocumentDidOpen", !session.Process.HasExited, "version=1; source=disk"));

            await session.Client.DidOpenAsync(
                context.Fixture.ConsumerPath,
                snapshot.Text,
                1,
                cancellationToken).ConfigureAwait(false);
            context.CurrentConsumerText = snapshot.Text;
            context.CurrentConsumerVersion = 1;
            context.CurrentConsumerCompletionPosition = snapshot.Position;
            checks.Add(new ProbeCheckResult(
                "ConsumerDocumentDidOpen",
                !session.Process.HasExited,
                "version=1; source=in-memory-editor-snapshot; logicalCaret=return target.|"));

            string diskConsumerAfterOpen = context.Fixture.ReadConsumer();
            bool diskUnchangedAfterOpen = string.Equals(
                diskConsumerAfterOpen,
                diskConsumer,
                StringComparison.Ordinal);
            bool diskOriginalStatementPresent = CountOrdinalOccurrences(
                diskConsumerAfterOpen,
                PrimaryConsumerAnchor) == 1;
            bool consumerSnapshotStayedOffDisk = diskUnchangedAfterOpen && diskOriginalStatementPresent;
            checks.Add(new ProbeCheckResult(
                "PrimaryConsumerSnapshotDidNotWriteToDisk",
                consumerSnapshotStayedOffDisk,
                $"diskUnchanged={diskUnchangedAfterOpen.ToString().ToLowerInvariant()}; "
                    + $"originalStatementPresent={diskOriginalStatementPresent.ToString().ToLowerInvariant()}"));
            checks.Add(new ProbeCheckResult(
                "PrimarySemanticReadinessDeferred",
                true,
                "firstSemanticOwner=SemanticReadiness; logicalCaret=return target.|"));
            AddUnresolvedDependencyCheck(
                checks,
                "NoUnresolvedDependencyWarning",
                session.Client.Callbacks.Messages);
            checks.Add(new ProbeCheckResult(
                "ServerMessagesObserved",
                true,
                DescribeServerMessages(session.Client.Callbacks.Messages)));
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
                "AutoLoadVsPrimaryReadinessCompletionShapeComparison",
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
                        openedConsumerText: null,
                        openedCompletionPosition: null,
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
        CompletionResponseEvidence? primaryReadinessEvidence,
        CompletionResponseEvidence autoLoadColdEvidence)
    {
        if (primaryReadinessEvidence is null)
        {
            return $"primaryReadiness=<unavailable>; autoLoadCold={ScenarioExecution.DescribeResponseShape(autoLoadColdEvidence)}; "
                + "differs=<unknown>";
        }

        bool differs = primaryReadinessEvidence != autoLoadColdEvidence;
        return $"primaryReadiness={ScenarioExecution.DescribeResponseShape(primaryReadinessEvidence)}; "
            + $"autoLoadCold={ScenarioExecution.DescribeResponseShape(autoLoadColdEvidence)}; differs={differs.ToString().ToLowerInvariant()}";
    }

    private static string DescribeServerMessages(IReadOnlyList<string> messages)
    {
        const int maxRecentMessages = 8;
        string[] recent = messages.TakeLast(maxRecentMessages).ToArray();
        return $"count={messages.Count}; recent={(recent.Length == 0 ? "<none>" : string.Join(" | ", recent))}";
    }

    private static PrimaryConsumerSnapshot CreatePrimaryConsumerSnapshot(string diskConsumer)
    {
        int statementStart = diskConsumer.IndexOf(PrimaryConsumerAnchor, StringComparison.Ordinal);
        if (statementStart < 0)
            throw new InvalidOperationException("Primary Consumer anchor was not found in disk source.");
        if (diskConsumer.IndexOf(PrimaryConsumerAnchor, statementStart + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("Primary Consumer anchor occurred more than once in disk source.");

        string editorConsumer = diskConsumer[..statementStart]
            + PrimaryConsumerEditorPrefix
            + diskConsumer[(statementStart + PrimaryConsumerAnchor.Length)..];
        int caretAbsoluteIndex = statementStart + PrimaryConsumerEditorPrefix.Length;

        if (string.Equals(editorConsumer, diskConsumer, StringComparison.Ordinal))
            throw new InvalidOperationException("Primary Consumer editor snapshot did not differ from disk source.");
        if (caretAbsoluteIndex <= 0 || editorConsumer[caretAbsoluteIndex - 1] != '.')
            throw new InvalidOperationException("Primary Consumer true-editor caret was not immediately preceded by the member-access dot.");
        if (caretAbsoluteIndex < editorConsumer.Length
            && editorConsumer[caretAbsoluteIndex] is not '\r' and not '\n')
        {
            throw new InvalidOperationException("Primary Consumer true-editor caret was not followed by a line break or end-of-source.");
        }
        if (caretAbsoluteIndex < editorConsumer.Length && editorConsumer[caretAbsoluteIndex] == ';')
            throw new InvalidOperationException("Primary Consumer true-editor caret retained a semicolon.");

        return new PrimaryConsumerSnapshot(
            editorConsumer,
            caretAbsoluteIndex,
            ProbeSourceMarker.PositionAt(editorConsumer, caretAbsoluteIndex));
    }

    private static int CountOrdinalOccurrences(string source, string value)
    {
        int count = 0;
        int searchStart = 0;
        while (searchStart <= source.Length - value.Length)
        {
            int found = source.IndexOf(value, searchStart, StringComparison.Ordinal);
            if (found < 0)
                break;
            count++;
            searchStart = found + value.Length;
        }
        return count;
    }

    private sealed record PrimaryConsumerSnapshot(string Text, int CaretAbsoluteIndex, LspPosition Position);

}
