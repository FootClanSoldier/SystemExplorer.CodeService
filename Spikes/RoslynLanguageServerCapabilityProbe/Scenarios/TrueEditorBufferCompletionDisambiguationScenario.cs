using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class TrueEditorBufferCompletionDisambiguationScenario
{
    private const string NaturalMemberAnchor = "return target.ProbeExtension();";
    private const string EditorMemberPrefix = "return target.";

    public static Task<ProbeScenarioResult> RunAsync(
        ProbeScenarioContext context,
        CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("TrueEditorBufferCompletionDisambiguation", cancellationToken, async checks =>
        {
            SemanticGateDisambiguationEvidence baselineEvidence = context.PrimarySemanticGateDisambiguationEvidence
                ?? throw new InvalidOperationException("Primary semantic-gate disambiguation evidence is unavailable.");

            string diskConsumer = context.Fixture.ReadConsumer();
            EditorSnapshot snapshot = CreateEditorSnapshot(diskConsumer);
            string editorConsumer = snapshot.Text;
            bool diskUnchangedBeforeOpen = string.Equals(
                context.Fixture.ReadConsumer(),
                diskConsumer,
                StringComparison.Ordinal);
            bool editorDiffersFromDisk = !string.Equals(editorConsumer, diskConsumer, StringComparison.Ordinal);
            bool rightHandIdentifier = snapshot.CaretAbsoluteIndex < editorConsumer.Length
                && editorConsumer[snapshot.CaretAbsoluteIndex] is not '\r' and not '\n';
            bool snapshotVerified = editorDiffersFromDisk
                && diskUnchangedBeforeOpen
                && editorConsumer[snapshot.CaretAbsoluteIndex - 1] == '.'
                && !rightHandIdentifier;
            checks.Add(new ProbeCheckResult(
                "TrueEditorBufferSnapshotVerified",
                snapshotVerified,
                $"logicalCaret=return target.|; rightHandIdentifier={rightHandIdentifier.ToString().ToLowerInvariant()}; "
                    + $"diskUnchanged={diskUnchangedBeforeOpen.ToString().ToLowerInvariant()}"));
            if (!snapshotVerified)
                throw new InvalidOperationException("True-editor snapshot verification failed before didOpen.");

            await using ProbeSession session = await context.StartSessionAsync(
                context.Fixture.RootPath,
                autoLoadProjects: false,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "TrueEditorBufferServerStart",
                !session.Process.HasExited,
                $"pid={session.Process.Identity.ProcessId}; generation={session.Process.Identity.ScenarioGeneration}"));

            (bool readinessObserved, double elapsedMs) = await session.InitializeWorkspaceAsync(
                context.Fixture.RootPath,
                context.Fixture.SolutionPath,
                explicitOpen: true,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult("TrueEditorBufferInitialize", !session.Process.HasExited));
            checks.Add(new ProbeCheckResult(
                "TrueEditorBufferProjectInitializationNotificationObserved",
                readinessObserved,
                readinessObserved ? "workspace/projectInitializationComplete observed" : "notification timed out",
                elapsedMs));

            string target = context.Fixture.ReadTarget();
            await session.Client.DidOpenAsync(
                context.Fixture.TargetPath,
                target,
                1,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "TrueEditorBufferTargetDocumentDidOpen",
                !session.Process.HasExited,
                "version=1; source=disk"));

            await session.Client.DidOpenAsync(
                context.Fixture.ConsumerPath,
                editorConsumer,
                1,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "TrueEditorBufferConsumerDocumentDidOpen",
                !session.Process.HasExited,
                "version=1; source=in-memory-editor-snapshot"));

            CompletionRequestResult completion = await session.Client.CompletionAsync(
                context.Fixture.ConsumerPath,
                snapshot.Position,
                cancellationToken).ConfigureAwait(false);
            string completionDetails = ScenarioExecution.DescribeCompletionEvidence(completion);
            bool nonNullShape = completion.Evidence.ResultKind is
                CompletionResponseResultKind.Array or CompletionResponseResultKind.CompletionList;
            bool includesProbeInstanceProperty = ScenarioExecution.ContainsLabel(
                completion.Items,
                "ProbeInstanceProperty");
            checks.Add(new ProbeCheckResult(
                "TrueEditorBufferNaturalMemberCompletionReturnedNonNullShape",
                nonNullShape,
                completionDetails,
                completion.DurationMs));
            checks.Add(new ProbeCheckResult(
                "TrueEditorBufferNaturalMemberCompletionIncludesProbeInstanceProperty",
                includesProbeInstanceProperty,
                completionDetails));

            CompletionResponseEvidence baselineNatural = baselineEvidence.PreDefinitionNaturalCompletionEvidence;
            bool responseShapeDiffers = baselineNatural != completion.Evidence;
            checks.Add(new ProbeCheckResult(
                "TrueEditorBufferVsBaselineNaturalCompletionShapeComparison",
                true,
                $"baselineNatural={ScenarioExecution.DescribeResponseShape(baselineNatural)}; "
                    + $"trueEditor={ScenarioExecution.DescribeResponseShape(completion.Evidence)}; "
                    + $"differs={responseShapeDiffers.ToString().ToLowerInvariant()}"));

            LspPosition definitionPosition = ProbeSourceMarker.FindUnique(editorConsumer, "PROBE_DEFINITION");
            IReadOnlyList<LspLocationSummary> definitions = await session.Client.DefinitionAsync(
                context.Fixture.ConsumerPath,
                definitionPosition,
                cancellationToken).ConfigureAwait(false);
            LspRange expectedDefinitionRange = ProbeSourceMarker.FindUniqueTokenRange(target, "ProbeDefinitionSymbol");
            string targetUri = LspJson.FileUri(context.Fixture.TargetPath);
            bool definitionMatch = definitions.Any(location =>
                UriEquals(location.Uri, targetUri)
                && location.Range.Start.Line == expectedDefinitionRange.Start.Line);
            checks.Add(new ProbeCheckResult(
                "TrueEditorBufferDefinitionSemanticProbeReturnedLocations",
                definitions.Count > 0,
                $"locations={definitions.Count}"));
            checks.Add(new ProbeCheckResult(
                "TrueEditorBufferDefinitionSemanticProbeMatchedExpectedFixtureSymbol",
                definitionMatch,
                $"locations={definitions.Count}; expectedTargetMatched={definitionMatch.ToString().ToLowerInvariant()}"));

            string diskAfter = context.Fixture.ReadConsumer();
            bool diskUnchangedAfterRequests = string.Equals(diskAfter, diskConsumer, StringComparison.Ordinal);
            bool diskOriginalStatementPresent = CountOrdinalOccurrences(diskAfter, NaturalMemberAnchor) == 1;
            checks.Add(new ProbeCheckResult(
                "TrueEditorBufferDidOpenSnapshotDidNotWriteToDisk",
                diskUnchangedAfterRequests && diskOriginalStatementPresent,
                $"diskUnchanged={diskUnchangedAfterRequests.ToString().ToLowerInvariant()}; "
                    + $"originalStatementPresent={diskOriginalStatementPresent.ToString().ToLowerInvariant()}"));

            context.TrueEditorBufferEvidence = new TrueEditorBufferCompletionEvidence(
                completion.Evidence,
                includesProbeInstanceProperty,
                definitions.Count,
                definitionMatch,
                snapshotVerified,
                diskUnchangedAfterRequests && diskOriginalStatementPresent);

            checks.Add(new ProbeCheckResult(
                "ProcessSurvivedTrueEditorBufferCompletionDisambiguation",
                !session.Process.HasExited));
            ScenarioExecution.AddProtocolCoverageObservation(checks, session);

            if (!session.Process.HasExited)
            {
                await session.Client.DidCloseAsync(context.Fixture.ConsumerPath, cancellationToken).ConfigureAwait(false);
                await session.Client.DidCloseAsync(context.Fixture.TargetPath, cancellationToken).ConfigureAwait(false);
            }
            await session.GracefulRetireAsync().ConfigureAwait(false);
        });

    private static EditorSnapshot CreateEditorSnapshot(string diskConsumer)
    {
        int statementStart = diskConsumer.IndexOf(NaturalMemberAnchor, StringComparison.Ordinal);
        if (statementStart < 0)
            throw new InvalidOperationException("Natural member statement was not found in Consumer disk source.");
        if (diskConsumer.IndexOf(NaturalMemberAnchor, statementStart + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("Natural member statement occurred more than once in Consumer disk source.");

        string editorConsumer = diskConsumer[..statementStart]
            + EditorMemberPrefix
            + diskConsumer[(statementStart + NaturalMemberAnchor.Length)..];
        int caretAbsoluteIndex = statementStart + EditorMemberPrefix.Length;

        if (caretAbsoluteIndex <= 0 || editorConsumer[caretAbsoluteIndex - 1] != '.')
            throw new InvalidOperationException("True-editor caret was not immediately preceded by the member-access dot.");
        if (caretAbsoluteIndex < editorConsumer.Length
            && editorConsumer[caretAbsoluteIndex] is not '\r' and not '\n')
        {
            throw new InvalidOperationException("True-editor caret was not followed by a line break or end-of-source.");
        }

        return new EditorSnapshot(
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

    private static bool UriEquals(string left, string right) =>
        string.Equals(Uri.UnescapeDataString(left), Uri.UnescapeDataString(right), StringComparison.OrdinalIgnoreCase);

    private sealed record EditorSnapshot(string Text, int CaretAbsoluteIndex, LspPosition Position);
}
