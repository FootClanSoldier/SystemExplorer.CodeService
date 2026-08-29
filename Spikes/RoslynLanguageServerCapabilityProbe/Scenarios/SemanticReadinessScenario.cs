using System.Diagnostics;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class SemanticReadinessScenario
{
    private const string ExpectedMember = "ProbeInstanceProperty";
    private const string DiskConsumerAnchor = "return target.ProbeExtension();";
    private const string EditorConsumerPrefix = "return target.";

    public static Task<ProbeScenarioResult> RunAsync(
        ProbeScenarioContext context,
        CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("SemanticReadiness", cancellationToken, async checks =>
        {
            ProbeSession session = context.PrimarySession
                ?? throw new InvalidOperationException("Primary session is not initialized.");

            context.FixtureSemanticRequestSucceeded = false;
            context.FixtureSemanticReadyMs = null;

            bool consumerSnapshotVerified = IsPrimaryConsumerSnapshotStateEstablished(context);
            bool consumerDiskUnchanged = IsConsumerDiskAuthorityIntact(context);
            bool processAliveBeforeReadiness = !session.Process.HasExited;
            bool admitted = context.FixtureProjectInitializationObserved
                && context.CurrentTargetVersion == 1
                && context.CurrentConsumerVersion == 1
                && processAliveBeforeReadiness
                && consumerSnapshotVerified
                && consumerDiskUnchanged;

            checks.Add(new ProbeCheckResult(
                "SemanticReadinessAdmission",
                admitted,
                DescribeAdmission(
                    context,
                    consumerSnapshotVerified,
                    consumerDiskUnchanged,
                    processAliveBeforeReadiness)));
            if (!admitted)
            {
                checks.Add(new ProbeCheckResult(
                    "SemanticReadinessEstablished",
                    false,
                    "source=none; semanticReadyMs=<null>"));
                return;
            }

            LspPosition completionPosition = context.CurrentConsumerCompletionPosition
                ?? throw new InvalidOperationException("Primary Consumer true-editor completion position is unavailable.");
            SemanticReadinessAttempt attempt = await SemanticReadinessOperation.ExecuteDiagnosticReadinessAsync(
                session,
                context.Fixture.ConsumerPath,
                completionPosition,
                cancellationToken).ConfigureAwait(false);

            context.FixtureServerCapabilities = session.Client.ServerCapabilities;
            checks.Add(new ProbeCheckResult(
                "SemanticReadinessDiagnosticCapabilityObserved",
                attempt.DiagnosticAvailable,
                SemanticReadinessOperation.DescribeCapability(attempt)));
            if (!attempt.DiagnosticAvailable)
            {
                checks.Add(new ProbeCheckResult(
                    "SemanticReadinessEstablished",
                    false,
                    "source=none; semanticReadyMs=<null>"));
                return;
            }

            checks.Add(new ProbeCheckResult(
                "SemanticReadinessDiagnosticPullCompleted",
                true,
                SemanticReadinessOperation.DescribeDiagnostics(attempt),
                attempt.DiagnosticDurationMs));

            CompletionRequestResult completion = attempt.Completion
                ?? throw new InvalidOperationException("Diagnostic readiness completed without a completion result.");
            context.PrimaryCompletionEvidence = completion.Evidence;

            string completionDetails = "logicalCaret=return target.|; "
                + ScenarioExecution.DescribeCompletionEvidence(completion);
            bool nonNullShape = completion.Evidence.ResultKind is
                CompletionResponseResultKind.Array or CompletionResponseResultKind.CompletionList;
            bool includesExpectedMember = ScenarioExecution.ContainsLabel(completion.Items, ExpectedMember);
            checks.Add(new ProbeCheckResult(
                "SemanticReadinessCompletionReturnedNonNullShape",
                nonNullShape,
                completionDetails,
                completion.DurationMs));
            checks.Add(new ProbeCheckResult(
                "SemanticReadinessCompletionIncludesProbeInstanceProperty",
                includesExpectedMember,
                completionDetails));

            bool processAliveAfterReadiness = !session.Process.HasExited;
            bool timestampValidAfterReadiness = context.PrimarySemanticReadinessStartTimestamp > 0;
            bool consumerSnapshotStillVerified = IsPrimaryConsumerSnapshotStateEstablished(context);
            bool consumerDiskStillUnchanged = IsConsumerDiskAuthorityIntact(context);
            bool diagnosticEstablished = attempt.DiagnosticAvailable
                && nonNullShape
                && includesExpectedMember
                && processAliveAfterReadiness
                && timestampValidAfterReadiness
                && consumerSnapshotStillVerified
                && consumerDiskStillUnchanged;
            if (diagnosticEstablished)
            {
                context.FixtureSemanticRequestSucceeded = true;
                context.FixtureSemanticReadyMs = Stopwatch.GetElapsedTime(
                    context.PrimarySemanticReadinessStartTimestamp).TotalMilliseconds;
            }

            checks.Add(new ProbeCheckResult(
                "SemanticReadinessEstablished",
                diagnosticEstablished,
                $"source=precompletion-diagnostic-pull; semanticReadyMs={FormatMilliseconds(context.FixtureSemanticReadyMs)}; "
                    + $"consumerSnapshotVerified={consumerSnapshotStillVerified.ToString().ToLowerInvariant()}; "
                    + $"consumerDiskUnchanged={consumerDiskStillUnchanged.ToString().ToLowerInvariant()}; "
                    + $"processAlive={processAliveAfterReadiness.ToString().ToLowerInvariant()}"));
        });

    private static bool IsPrimaryConsumerSnapshotStateEstablished(ProbeScenarioContext context)
    {
        if (context.CurrentConsumerVersion != 1
            || string.IsNullOrEmpty(context.CurrentConsumerText)
            || context.CurrentConsumerCompletionPosition is null
            || context.CurrentConsumerText.Contains(DiskConsumerAnchor, StringComparison.Ordinal))
        {
            return false;
        }

        int statementStart = FindUniqueEditorStatementStart(context.CurrentConsumerText);
        if (statementStart < 0)
            return false;

        int caretAbsoluteIndex = statementStart + EditorConsumerPrefix.Length;
        LspPosition expectedPosition = ProbeSourceMarker.PositionAt(
            context.CurrentConsumerText,
            caretAbsoluteIndex);
        return context.CurrentConsumerCompletionPosition == expectedPosition
            && caretAbsoluteIndex > 0
            && context.CurrentConsumerText[caretAbsoluteIndex - 1] == '.'
            && (caretAbsoluteIndex == context.CurrentConsumerText.Length
                || context.CurrentConsumerText[caretAbsoluteIndex] is '\r' or '\n');
    }

    private static bool IsConsumerDiskAuthorityIntact(ProbeScenarioContext context)
    {
        int statementStart = FindUniqueEditorStatementStart(context.CurrentConsumerText);
        if (statementStart < 0)
            return false;

        string expectedDiskConsumer = context.CurrentConsumerText[..statementStart]
            + DiskConsumerAnchor
            + context.CurrentConsumerText[(statementStart + EditorConsumerPrefix.Length)..];
        string diskConsumer = context.Fixture.ReadConsumer();
        return string.Equals(diskConsumer, expectedDiskConsumer, StringComparison.Ordinal)
            && CountOrdinalOccurrences(diskConsumer, DiskConsumerAnchor) == 1;
    }

    private static int FindUniqueEditorStatementStart(string source)
    {
        int found = -1;
        int searchStart = 0;
        while (searchStart <= source.Length - EditorConsumerPrefix.Length)
        {
            int candidate = source.IndexOf(EditorConsumerPrefix, searchStart, StringComparison.Ordinal);
            if (candidate < 0)
                break;

            int afterPrefix = candidate + EditorConsumerPrefix.Length;
            bool endsAtCaret = afterPrefix == source.Length || source[afterPrefix] is '\r' or '\n';
            if (endsAtCaret)
            {
                if (found >= 0)
                    return -1;
                found = candidate;
            }

            searchStart = candidate + EditorConsumerPrefix.Length;
        }

        return found;
    }

    private static string DescribeAdmission(
        ProbeScenarioContext context,
        bool consumerSnapshotVerified,
        bool consumerDiskUnchanged,
        bool processAlive) =>
        $"projectInitializationComplete={context.FixtureProjectInitializationObserved.ToString().ToLowerInvariant()}; "
        + $"targetVersion={context.CurrentTargetVersion}; consumerVersion={context.CurrentConsumerVersion}; "
        + $"consumerSnapshotVerified={consumerSnapshotVerified.ToString().ToLowerInvariant()}; "
        + $"consumerDiskUnchanged={consumerDiskUnchanged.ToString().ToLowerInvariant()}; "
        + $"processAlive={processAlive.ToString().ToLowerInvariant()}";

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

    private static string FormatMilliseconds(double? milliseconds) =>
        milliseconds is null ? "<null>" : milliseconds.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
