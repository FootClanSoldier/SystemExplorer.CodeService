using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class SameDocumentCompletionDisambiguationScenario
{
    private const string SameDocumentAnchor = "_ = this./*PROBE_PRIVATE_COMPLETION*/ProbePrivateField;";
    private const string EditorMemberPrefix = "_ = this.";
    private const string ExpectedMember = "ProbePrivateField";

    public static Task<ProbeScenarioResult> RunAsync(
        ProbeScenarioContext context,
        CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("SameDocumentCompletionDisambiguation", cancellationToken, async checks =>
        {
            TrueEditorBufferCompletionEvidence crossDocumentEvidence = context.TrueEditorBufferEvidence
                ?? throw new InvalidOperationException("True-editor-buffer evidence is unavailable.");

            string diskTarget = context.Fixture.ReadTarget();
            string diskConsumer = context.Fixture.ReadConsumer();
            EditorSnapshot snapshot = CreateEditorSnapshot(diskTarget);
            string editorTarget = snapshot.Text;

            bool diskUnchangedBeforeOpen = string.Equals(
                context.Fixture.ReadTarget(),
                diskTarget,
                StringComparison.Ordinal);
            bool editorDiffersFromDisk = !string.Equals(editorTarget, diskTarget, StringComparison.Ordinal);
            bool rightHandIdentifier = snapshot.CaretAbsoluteIndex < editorTarget.Length
                && editorTarget[snapshot.CaretAbsoluteIndex] is not '\r' and not '\n';
            bool snapshotVerified = snapshot.AnchorUnique
                && editorDiffersFromDisk
                && diskUnchangedBeforeOpen
                && snapshot.CaretAbsoluteIndex > 0
                && editorTarget[snapshot.CaretAbsoluteIndex - 1] == '.'
                && !rightHandIdentifier;
            checks.Add(new ProbeCheckResult(
                "SameDocumentEditorBufferSnapshotVerified",
                snapshotVerified,
                $"logicalCaret=_ = this.|; expectedMember={ExpectedMember}; "
                    + $"rightHandIdentifier={rightHandIdentifier.ToString().ToLowerInvariant()}; "
                    + $"diskUnchanged={diskUnchangedBeforeOpen.ToString().ToLowerInvariant()}"));
            if (!snapshotVerified)
                throw new InvalidOperationException("Same-document editor snapshot verification failed before didOpen.");

            await using ProbeSession session = await context.StartSessionAsync(
                context.Fixture.RootPath,
                autoLoadProjects: false,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "SameDocumentServerStart",
                !session.Process.HasExited,
                $"pid={session.Process.Identity.ProcessId}; generation={session.Process.Identity.ScenarioGeneration}"));

            (bool readinessObserved, double elapsedMs) = await session.InitializeWorkspaceAsync(
                context.Fixture.RootPath,
                context.Fixture.SolutionPath,
                explicitOpen: true,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult("SameDocumentInitialize", !session.Process.HasExited));
            checks.Add(new ProbeCheckResult(
                "SameDocumentProjectInitializationNotificationObserved",
                readinessObserved,
                readinessObserved ? "workspace/projectInitializationComplete observed" : "notification timed out",
                elapsedMs));

            await session.Client.DidOpenAsync(
                context.Fixture.TargetPath,
                editorTarget,
                1,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "SameDocumentTargetDocumentDidOpen",
                !session.Process.HasExited,
                "version=1; source=in-memory-editor-snapshot"));

            await session.Client.DidOpenAsync(
                context.Fixture.ConsumerPath,
                diskConsumer,
                1,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "SameDocumentConsumerDocumentDidOpen",
                !session.Process.HasExited,
                "version=1; source=disk"));

            CompletionRequestResult sameDocumentCompletion = await session.Client.CompletionAsync(
                context.Fixture.TargetPath,
                snapshot.Position,
                cancellationToken).ConfigureAwait(false);
            string completionDetails = ScenarioExecution.DescribeCompletionEvidence(sameDocumentCompletion);
            bool nonNullShape = sameDocumentCompletion.Evidence.ResultKind is
                CompletionResponseResultKind.Array or CompletionResponseResultKind.CompletionList;
            bool includesProbePrivateField = ScenarioExecution.ContainsLabel(
                sameDocumentCompletion.Items,
                ExpectedMember);
            checks.Add(new ProbeCheckResult(
                "SameDocumentCompletionReturnedNonNullShape",
                nonNullShape,
                completionDetails,
                sameDocumentCompletion.DurationMs));
            checks.Add(new ProbeCheckResult(
                "SameDocumentCompletionIncludesProbePrivateField",
                includesProbePrivateField,
                completionDetails));

            CompletionResponseEvidence crossDocumentCompletion = crossDocumentEvidence.CompletionEvidence;
            bool responseShapeDiffers = crossDocumentCompletion != sameDocumentCompletion.Evidence;
            checks.Add(new ProbeCheckResult(
                "SameDocumentVsCrossDocumentTrueEditorCompletionShapeComparison",
                true,
                $"crossDocumentTrueEditor={ScenarioExecution.DescribeResponseShape(crossDocumentCompletion)}; "
                    + $"sameDocument={ScenarioExecution.DescribeResponseShape(sameDocumentCompletion.Evidence)}; "
                    + $"differs={responseShapeDiffers.ToString().ToLowerInvariant()}"));

            LspPosition definitionPosition = ProbeSourceMarker.FindUnique(diskConsumer, "PROBE_DEFINITION");
            IReadOnlyList<LspLocationSummary> definitions = await session.Client.DefinitionAsync(
                context.Fixture.ConsumerPath,
                definitionPosition,
                cancellationToken).ConfigureAwait(false);
            LspRange expectedDefinitionRange = ProbeSourceMarker.FindUniqueTokenRange(
                diskTarget,
                "ProbeDefinitionSymbol");
            string targetUri = LspJson.FileUri(context.Fixture.TargetPath);
            bool definitionMatch = definitions.Any(location =>
                UriEquals(location.Uri, targetUri)
                && location.Range.Start.Line == expectedDefinitionRange.Start.Line);
            checks.Add(new ProbeCheckResult(
                "SameDocumentDefinitionSemanticProbeReturnedLocations",
                definitions.Count > 0,
                $"locations={definitions.Count}"));
            checks.Add(new ProbeCheckResult(
                "SameDocumentDefinitionSemanticProbeMatchedExpectedFixtureSymbol",
                definitionMatch,
                $"locations={definitions.Count}; expectedTargetMatched={definitionMatch.ToString().ToLowerInvariant()}"));

            string targetAfter = context.Fixture.ReadTarget();
            string consumerAfter = context.Fixture.ReadConsumer();
            bool targetDiskUnchanged = string.Equals(targetAfter, diskTarget, StringComparison.Ordinal);
            bool consumerDiskUnchanged = string.Equals(consumerAfter, diskConsumer, StringComparison.Ordinal);
            bool originalTargetStatementPresent = CountOrdinalOccurrences(targetAfter, SameDocumentAnchor) == 1;
            checks.Add(new ProbeCheckResult(
                "SameDocumentDidOpenSnapshotsDidNotWriteToDisk",
                targetDiskUnchanged && consumerDiskUnchanged && originalTargetStatementPresent,
                $"targetDiskUnchanged={targetDiskUnchanged.ToString().ToLowerInvariant()}; "
                    + $"consumerDiskUnchanged={consumerDiskUnchanged.ToString().ToLowerInvariant()}; "
                    + $"originalTargetStatementPresent={originalTargetStatementPresent.ToString().ToLowerInvariant()}"));

            checks.Add(new ProbeCheckResult(
                "ProcessSurvivedSameDocumentCompletionDisambiguation",
                !session.Process.HasExited));
            ScenarioExecution.AddProtocolCoverageObservation(checks, session);

            if (!session.Process.HasExited)
            {
                await session.Client.DidCloseAsync(context.Fixture.ConsumerPath, cancellationToken).ConfigureAwait(false);
                await session.Client.DidCloseAsync(context.Fixture.TargetPath, cancellationToken).ConfigureAwait(false);
            }
            await session.GracefulRetireAsync().ConfigureAwait(false);
        });

    private static EditorSnapshot CreateEditorSnapshot(string diskTarget)
    {
        int statementStart = diskTarget.IndexOf(SameDocumentAnchor, StringComparison.Ordinal);
        if (statementStart < 0)
            throw new InvalidOperationException("Same-document completion anchor was not found in Target disk source.");
        if (diskTarget.IndexOf(SameDocumentAnchor, statementStart + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("Same-document completion anchor occurred more than once in Target disk source.");

        string editorTarget = diskTarget[..statementStart]
            + EditorMemberPrefix
            + diskTarget[(statementStart + SameDocumentAnchor.Length)..];
        int caretAbsoluteIndex = statementStart + EditorMemberPrefix.Length;

        if (caretAbsoluteIndex <= 0 || editorTarget[caretAbsoluteIndex - 1] != '.')
            throw new InvalidOperationException("Same-document caret was not immediately preceded by the member-access dot.");
        if (caretAbsoluteIndex < editorTarget.Length
            && editorTarget[caretAbsoluteIndex] is not '\r' and not '\n')
        {
            throw new InvalidOperationException("Same-document caret was not followed by a line break or end-of-source.");
        }
        if (string.Equals(editorTarget, diskTarget, StringComparison.Ordinal))
            throw new InvalidOperationException("Same-document editor snapshot did not differ from Target disk source.");

        return new EditorSnapshot(
            editorTarget,
            caretAbsoluteIndex,
            ProbeSourceMarker.PositionAt(editorTarget, caretAbsoluteIndex),
            AnchorUnique: true);
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

    private sealed record EditorSnapshot(
        string Text,
        int CaretAbsoluteIndex,
        LspPosition Position,
        bool AnchorUnique);
}
