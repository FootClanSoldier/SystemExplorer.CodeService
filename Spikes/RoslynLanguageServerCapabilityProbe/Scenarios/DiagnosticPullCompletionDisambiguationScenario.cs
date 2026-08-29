using System.Diagnostics;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class DiagnosticPullCompletionDisambiguationScenario
{
    private const string NaturalMemberAnchor = "return target.ProbeExtension();";
    private const string EditorMemberPrefix = "return target.";
    private const string ExpectedMember = "ProbeInstanceProperty";
    private const int MaxDiagnosticCodes = 32;

    public static Task<ProbeScenarioResult> RunAsync(
        ProbeScenarioContext context,
        CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("DiagnosticPullCompletionDisambiguation", cancellationToken, async checks =>
        {
            TrueEditorBufferCompletionEvidence coldTrueEditorEvidence = context.TrueEditorBufferEvidence
                ?? throw new InvalidOperationException("True-editor-buffer evidence is unavailable.");
            string diskTarget = context.Fixture.ReadTarget();
            string diskConsumer = context.Fixture.ReadConsumer();
            EditorSnapshot snapshot = CreateEditorSnapshot(diskConsumer);
            string editorConsumer = snapshot.Text;

            bool targetDiskUnchangedBeforeOpen = string.Equals(
                context.Fixture.ReadTarget(),
                diskTarget,
                StringComparison.Ordinal);
            bool consumerDiskUnchangedBeforeOpen = string.Equals(
                context.Fixture.ReadConsumer(),
                diskConsumer,
                StringComparison.Ordinal);
            bool editorDiffersFromDisk = !string.Equals(editorConsumer, diskConsumer, StringComparison.Ordinal);
            bool rightHandIdentifier = snapshot.CaretAbsoluteIndex < editorConsumer.Length
                && editorConsumer[snapshot.CaretAbsoluteIndex] is not '\r' and not '\n';
            bool snapshotVerified = editorDiffersFromDisk
                && targetDiskUnchangedBeforeOpen
                && consumerDiskUnchangedBeforeOpen
                && snapshot.CaretAbsoluteIndex > 0
                && editorConsumer[snapshot.CaretAbsoluteIndex - 1] == '.'
                && !rightHandIdentifier;
            checks.Add(new ProbeCheckResult(
                "DiagnosticPullEditorBufferSnapshotVerified",
                snapshotVerified,
                $"logicalCaret=return target.|; rightHandIdentifier={rightHandIdentifier.ToString().ToLowerInvariant()}; "
                    + $"diskUnchanged={(targetDiskUnchangedBeforeOpen && consumerDiskUnchangedBeforeOpen).ToString().ToLowerInvariant()}"));
            if (!snapshotVerified)
                throw new InvalidOperationException("Diagnostic-pull editor snapshot verification failed before didOpen.");

            await using ProbeSession session = await context.StartSessionAsync(
                context.Fixture.RootPath,
                autoLoadProjects: false,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "DiagnosticPullServerStart",
                !session.Process.HasExited,
                $"pid={session.Process.Identity.ProcessId}; generation={session.Process.Identity.ScenarioGeneration}"));

            (bool readinessObserved, double elapsedMs) = await session.InitializeWorkspaceAsync(
                context.Fixture.RootPath,
                context.Fixture.SolutionPath,
                explicitOpen: true,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult("DiagnosticPullInitialize", !session.Process.HasExited));
            checks.Add(new ProbeCheckResult(
                "DiagnosticPullProjectInitializationNotificationObserved",
                readinessObserved,
                readinessObserved ? "workspace/projectInitializationComplete observed" : "notification timed out",
                elapsedMs));

            await session.Client.DidOpenAsync(
                context.Fixture.TargetPath,
                diskTarget,
                1,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "DiagnosticPullTargetDocumentDidOpen",
                !session.Process.HasExited,
                "version=1; source=disk"));

            await session.Client.DidOpenAsync(
                context.Fixture.ConsumerPath,
                editorConsumer,
                1,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "DiagnosticPullConsumerDocumentDidOpen",
                !session.Process.HasExited,
                "version=1; source=in-memory-editor-snapshot"));

            session.Client.RefreshDynamicCapabilities();
            RoslynServerCapabilities capabilities = session.Client.ServerCapabilities
                ?? throw new InvalidOperationException("Server capabilities unavailable.");
            bool dynamicDiagnosticRegistration = capabilities.HasDynamicRegistration("textDocument/diagnostic");
            bool diagnosticAvailable = capabilities.DiagnosticProvider || dynamicDiagnosticRegistration;
            checks.Add(new ProbeCheckResult(
                "DiagnosticPullCapabilityObserved",
                diagnosticAvailable,
                $"staticProvider={capabilities.DiagnosticProvider.ToString().ToLowerInvariant()}; "
                    + $"dynamicRegistration={dynamicDiagnosticRegistration.ToString().ToLowerInvariant()}"));
            if (!diagnosticAvailable)
                throw new InvalidOperationException("Diagnostic capability was not observed; diagnostic-pull hypothesis was not tested.");

            Stopwatch diagnosticStopwatch = Stopwatch.StartNew();
            IReadOnlyList<DiagnosticSummary> diagnostics = await session.Client.PullDiagnosticsAsync(
                context.Fixture.ConsumerPath,
                cancellationToken).ConfigureAwait(false);
            diagnosticStopwatch.Stop();
            checks.Add(new ProbeCheckResult(
                "DiagnosticPullRequestCompleted",
                true,
                DescribeDiagnostics(diagnostics),
                diagnosticStopwatch.Elapsed.TotalMilliseconds));

            CompletionRequestResult postDiagnosticCompletion = await session.Client.CompletionAsync(
                context.Fixture.ConsumerPath,
                snapshot.Position,
                cancellationToken).ConfigureAwait(false);
            string completionDetails = ScenarioExecution.DescribeCompletionEvidence(postDiagnosticCompletion);
            bool nonNullShape = postDiagnosticCompletion.Evidence.ResultKind is
                CompletionResponseResultKind.Array or CompletionResponseResultKind.CompletionList;
            bool includesProbeInstanceProperty = ScenarioExecution.ContainsLabel(
                postDiagnosticCompletion.Items,
                ExpectedMember);
            checks.Add(new ProbeCheckResult(
                "DiagnosticPullCrossDocumentCompletionReturnedNonNullShape",
                nonNullShape,
                completionDetails,
                postDiagnosticCompletion.DurationMs));
            checks.Add(new ProbeCheckResult(
                "DiagnosticPullCrossDocumentCompletionIncludesProbeInstanceProperty",
                includesProbeInstanceProperty,
                completionDetails));

            CompletionResponseEvidence coldTrueEditor = coldTrueEditorEvidence.CompletionEvidence;
            bool responseShapeDiffers = coldTrueEditor != postDiagnosticCompletion.Evidence;
            checks.Add(new ProbeCheckResult(
                "DiagnosticPullVsColdTrueEditorCompletionShapeComparison",
                true,
                $"coldTrueEditor={ScenarioExecution.DescribeResponseShape(coldTrueEditor)}; "
                    + $"postDiagnostic={ScenarioExecution.DescribeResponseShape(postDiagnosticCompletion.Evidence)}; "
                    + $"differs={responseShapeDiffers.ToString().ToLowerInvariant()}"));

            LspPosition definitionPosition = ProbeSourceMarker.FindUnique(editorConsumer, "PROBE_DEFINITION");
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
                "DiagnosticPullDefinitionSemanticProbeReturnedLocations",
                definitions.Count > 0,
                $"locations={definitions.Count}"));
            checks.Add(new ProbeCheckResult(
                "DiagnosticPullDefinitionSemanticProbeMatchedExpectedFixtureSymbol",
                definitionMatch,
                $"locations={definitions.Count}; expectedTargetMatched={definitionMatch.ToString().ToLowerInvariant()}"));

            string targetAfter = context.Fixture.ReadTarget();
            string consumerAfter = context.Fixture.ReadConsumer();
            bool targetDiskUnchanged = string.Equals(targetAfter, diskTarget, StringComparison.Ordinal);
            bool consumerDiskUnchanged = string.Equals(consumerAfter, diskConsumer, StringComparison.Ordinal);
            bool originalConsumerStatementPresent = CountOrdinalOccurrences(consumerAfter, NaturalMemberAnchor) == 1;
            checks.Add(new ProbeCheckResult(
                "DiagnosticPullDidOpenSnapshotDidNotWriteToDisk",
                targetDiskUnchanged && consumerDiskUnchanged && originalConsumerStatementPresent,
                $"targetDiskUnchanged={targetDiskUnchanged.ToString().ToLowerInvariant()}; "
                    + $"consumerDiskUnchanged={consumerDiskUnchanged.ToString().ToLowerInvariant()}; "
                    + $"originalConsumerStatementPresent={originalConsumerStatementPresent.ToString().ToLowerInvariant()}"));

            checks.Add(new ProbeCheckResult(
                "ProcessSurvivedDiagnosticPullCompletionDisambiguation",
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
            throw new InvalidOperationException("Diagnostic-pull caret was not immediately preceded by the member-access dot.");
        if (caretAbsoluteIndex < editorConsumer.Length
            && editorConsumer[caretAbsoluteIndex] is not '\r' and not '\n')
        {
            throw new InvalidOperationException("Diagnostic-pull caret was not followed by a line break or end-of-source.");
        }
        if (string.Equals(editorConsumer, diskConsumer, StringComparison.Ordinal))
            throw new InvalidOperationException("Diagnostic-pull editor snapshot did not differ from Consumer disk source.");

        return new EditorSnapshot(
            editorConsumer,
            caretAbsoluteIndex,
            ProbeSourceMarker.PositionAt(editorConsumer, caretAbsoluteIndex));
    }

    private static string DescribeDiagnostics(IReadOnlyList<DiagnosticSummary> diagnostics)
    {
        string[] codes = diagnostics
            .Select(static diagnostic => diagnostic.Code)
            .Where(static code => !string.IsNullOrWhiteSpace(code))
            .Select(static code => code!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static code => code, StringComparer.Ordinal)
            .Take(MaxDiagnosticCodes)
            .ToArray();
        return $"diagnostics={diagnostics.Count}; codes={(codes.Length == 0 ? "<none>" : string.Join(",", codes))}";
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
