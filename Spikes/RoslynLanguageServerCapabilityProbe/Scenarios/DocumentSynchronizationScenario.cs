using System.Diagnostics;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class DocumentSynchronizationScenario
{
    public static Task<ProbeScenarioResult> RunAsync(ProbeScenarioContext context, CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("DocumentSynchronization", cancellationToken, async checks =>
        {
            ProbeSession session = context.PrimarySession ?? throw new InvalidOperationException("Primary session is not initialized.");
            string diskText = context.Fixture.ReadTarget();
            LspPosition completionPosition = context.CurrentConsumerCompletionPosition
                ?? throw new InvalidOperationException("Primary Consumer true-editor completion position is unavailable.");
            if (context.CurrentConsumerVersion != 1 || string.IsNullOrEmpty(context.CurrentConsumerText))
                throw new InvalidOperationException("Primary Consumer true-editor snapshot state is unavailable.");

            if (context.CurrentTargetVersion != 1)
                throw new InvalidOperationException($"Expected already-open Target version 1, but context version was {context.CurrentTargetVersion}.");
            if (!string.Equals(context.CurrentTargetText, diskText, StringComparison.Ordinal))
                throw new InvalidOperationException("Expected already-open Target text to match the exact fixture disk baseline.");

            var (baselineItems, baselineMs) = await session.Client.CompletionAsync(
                context.Fixture.ConsumerPath, completionPosition, cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "DidOpenBaselineSemanticQuery",
                ScenarioExecution.ContainsLabel(baselineItems, "ProbeDiskMember"),
                null,
                baselineMs));

            string fullChangedText = ReplaceExactlyOnce(diskText, "ProbeDiskMember", "ProbeUnsavedMember");
            context.CurrentTargetText = fullChangedText;
            context.CurrentTargetVersion = 2;
            Stopwatch fullStopwatch = Stopwatch.StartNew();
            bool fullObserved = false;
            CompletionRequestResult? fullImmediateCompletion = null;
            string? fullFailure = null;

            try
            {
                await session.Client.DidChangeFullAsync(
                    context.Fixture.TargetPath,
                    fullChangedText,
                    2,
                    cancellationToken).ConfigureAwait(false);

                if (!session.Process.HasExited)
                {
                    fullImmediateCompletion = await session.Client.CompletionAsync(
                        context.Fixture.ConsumerPath, completionPosition, cancellationToken).ConfigureAwait(false);
                    fullObserved = ScenarioExecution.ContainsLabel(fullImmediateCompletion.Items, "ProbeUnsavedMember")
                        && !ScenarioExecution.ContainsLabel(fullImmediateCompletion.Items, "ProbeDiskMember");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                fullFailure = $"{exception.GetType().Name}: {exception.Message}";
            }
            fullStopwatch.Stop();

            bool fullSurvived = !session.Process.HasExited;
            checks.Add(new ProbeCheckResult(
                "FullDocumentDidChangeProcessSurvived",
                fullSurvived,
                fullFailure));
            checks.Add(new ProbeCheckResult(
                "FullDocumentDidChangeSemanticUpdateObserved",
                fullObserved,
                DescribeImmediateMutationEvidence(
                    "ProbeUnsavedMember",
                    ["ProbeDiskMember"],
                    fullImmediateCompletion,
                    fullFailure),
                fullImmediateCompletion?.DurationMs));
            checks.Add(new ProbeCheckResult(
                "CompletionAfterFullDocumentChangeLatency",
                fullObserved,
                fullObserved ? null : "No successful post-full-change semantic result.",
                fullStopwatch.Elapsed.TotalMilliseconds));

            if (!fullObserved && fullSurvived)
            {
                await RunMutationSemanticReReadinessAsync(
                    checks,
                    "FullDocumentDidChange",
                    session,
                    context.Fixture.ConsumerPath,
                    completionPosition,
                    "ProbeUnsavedMember",
                    ["ProbeDiskMember"],
                    fullImmediateCompletion,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!fullSurvived || session.Process.HasExited)
            {
                string reason = fullSurvived
                    ? "Not attempted: Roslyn exited during full-document semantic re-readiness stage."
                    : "Not attempted: Roslyn exited during full-document didChange stage.";
                checks.Add(new ProbeCheckResult(
                    "IncrementalDidChangeProcessSurvived",
                    false,
                    reason));
                checks.Add(new ProbeCheckResult(
                    "IncrementalDidChangeSemanticUpdateObserved",
                    false,
                    reason));
                checks.Add(new ProbeCheckResult(
                    "CompletionAfterIncrementalChangeLatency",
                    false,
                    reason));
                AddDiskAuthorityCheck(checks, context, diskText);
                ScenarioExecution.AddProtocolCoverageObservation(checks, session);
                return;
            }

            LspRange range = ProbeSourceMarker.FindUniqueTokenRange(fullChangedText, "ProbeUnsavedMember");
            string incrementalText = ReplaceExactlyOnce(fullChangedText, "ProbeUnsavedMember", "ProbeIncrementalMember");
            context.CurrentTargetText = incrementalText;
            context.CurrentTargetVersion = 3;
            Stopwatch incrementalStopwatch = Stopwatch.StartNew();
            bool incrementalObserved = false;
            CompletionRequestResult? incrementalImmediateCompletion = null;
            string? incrementalFailure = null;

            try
            {
                await session.Client.DidChangeIncrementalAsync(
                    context.Fixture.TargetPath,
                    "ProbeIncrementalMember",
                    3,
                    range,
                    "ProbeUnsavedMember".Length,
                    cancellationToken).ConfigureAwait(false);

                if (!session.Process.HasExited)
                {
                    incrementalImmediateCompletion = await session.Client.CompletionAsync(
                        context.Fixture.ConsumerPath, completionPosition, cancellationToken).ConfigureAwait(false);
                    incrementalObserved = ScenarioExecution.ContainsLabel(incrementalImmediateCompletion.Items, "ProbeIncrementalMember")
                        && !ScenarioExecution.ContainsLabel(incrementalImmediateCompletion.Items, "ProbeUnsavedMember")
                        && !ScenarioExecution.ContainsLabel(incrementalImmediateCompletion.Items, "ProbeDiskMember");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                incrementalFailure = $"{exception.GetType().Name}: {exception.Message}";
            }
            incrementalStopwatch.Stop();

            bool incrementalSurvived = !session.Process.HasExited;
            checks.Add(new ProbeCheckResult(
                "IncrementalDidChangeProcessSurvived",
                incrementalSurvived,
                incrementalFailure));
            checks.Add(new ProbeCheckResult(
                "IncrementalDidChangeSemanticUpdateObserved",
                incrementalObserved,
                DescribeImmediateMutationEvidence(
                    "ProbeIncrementalMember",
                    ["ProbeUnsavedMember", "ProbeDiskMember"],
                    incrementalImmediateCompletion,
                    incrementalFailure),
                incrementalImmediateCompletion?.DurationMs));
            checks.Add(new ProbeCheckResult(
                "CompletionAfterIncrementalChangeLatency",
                incrementalObserved,
                incrementalObserved ? null : "No successful post-incremental-change semantic result.",
                incrementalStopwatch.Elapsed.TotalMilliseconds));

            if (!incrementalObserved && incrementalSurvived)
            {
                await RunMutationSemanticReReadinessAsync(
                    checks,
                    "IncrementalDidChange",
                    session,
                    context.Fixture.ConsumerPath,
                    completionPosition,
                    "ProbeIncrementalMember",
                    ["ProbeUnsavedMember", "ProbeDiskMember"],
                    incrementalImmediateCompletion,
                    cancellationToken).ConfigureAwait(false);
            }

            AddDiskAuthorityCheck(checks, context, diskText);
            ScenarioExecution.AddProtocolCoverageObservation(checks, session);
        });

    private static async Task RunMutationSemanticReReadinessAsync(
        List<ProbeCheckResult> checks,
        string checkPrefix,
        ProbeSession session,
        string consumerPath,
        LspPosition completionPosition,
        string expectedMember,
        IReadOnlyList<string> staleMembers,
        CompletionRequestResult? immediateCompletion,
        CancellationToken cancellationToken)
    {
        SemanticReadinessAttempt? attempt = null;
        string? failure = null;

        try
        {
            attempt = await SemanticReadinessOperation.ExecuteDiagnosticReadinessAsync(
                session,
                consumerPath,
                completionPosition,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failure = $"{exception.GetType().Name}: {exception.Message}";
        }

        bool diagnosticAvailable = attempt?.DiagnosticAvailable == true;
        checks.Add(new ProbeCheckResult(
            $"{checkPrefix}SemanticReReadinessDiagnosticCapabilityObserved",
            diagnosticAvailable,
            attempt is null
                ? failure
                : SemanticReadinessOperation.DescribeCapability(attempt)));

        bool diagnosticCompleted = diagnosticAvailable && attempt?.DiagnosticDurationMs is not null;
        checks.Add(new ProbeCheckResult(
            $"{checkPrefix}SemanticReReadinessDiagnosticPullCompleted",
            diagnosticCompleted,
            attempt is null
                ? failure
                : diagnosticAvailable
                    ? SemanticReadinessOperation.DescribeDiagnostics(attempt)
                    : "Not attempted: diagnostic capability unavailable.",
            attempt?.DiagnosticDurationMs));

        CompletionRequestResult? postDiagnosticCompletion = attempt?.Completion;
        bool nonNullShape = postDiagnosticCompletion is not null
            && IsNonNullCompletionShape(postDiagnosticCompletion.Evidence);
        checks.Add(new ProbeCheckResult(
            $"{checkPrefix}SemanticReReadinessCompletionReturnedNonNullShape",
            nonNullShape,
            postDiagnosticCompletion is null
                ? failure ?? "Completion unavailable after diagnostic semantic re-readiness."
                : ScenarioExecution.DescribeCompletionEvidence(postDiagnosticCompletion),
            postDiagnosticCompletion?.DurationMs));

        bool expectedPresent = postDiagnosticCompletion is not null
            && ScenarioExecution.ContainsLabel(postDiagnosticCompletion.Items, expectedMember);
        bool staleAbsent = postDiagnosticCompletion is not null
            && staleMembers.All(stale => !ScenarioExecution.ContainsLabel(postDiagnosticCompletion.Items, stale));
        checks.Add(new ProbeCheckResult(
            $"{checkPrefix}SemanticReReadinessUpdateObserved",
            expectedPresent && staleAbsent,
            DescribeUpdatedMemberEvidence(
                expectedMember,
                staleMembers,
                postDiagnosticCompletion,
                failure),
            postDiagnosticCompletion?.DurationMs));

        checks.Add(new ProbeCheckResult(
            $"{checkPrefix}ImmediateVsSemanticReReadinessCompletionShapeComparison",
            true,
            DescribeCompletionShapeComparison(immediateCompletion, postDiagnosticCompletion, failure)));
    }

    private static bool IsNonNullCompletionShape(CompletionResponseEvidence evidence) =>
        evidence.ResultKind is CompletionResponseResultKind.Array or CompletionResponseResultKind.CompletionList;

    private static string DescribeImmediateMutationEvidence(
        string expectedMember,
        IReadOnlyList<string> staleMembers,
        CompletionRequestResult? completion,
        string? failure)
    {
        bool expectedPresent = completion is not null
            && ScenarioExecution.ContainsLabel(completion.Items, expectedMember);
        List<string> parts =
        [
            $"expected={expectedMember}",
            $"expectedPresent={FormatBoolean(expectedPresent)}",
        ];
        parts.AddRange(staleMembers.Select(stale =>
            $"stale{stale}={FormatBoolean(completion is not null && ScenarioExecution.ContainsLabel(completion.Items, stale))}"));

        if (completion is not null)
            parts.Add(ScenarioExecution.DescribeCompletionEvidence(completion));
        else
            parts.Add("completion=<unavailable>");

        if (!string.IsNullOrWhiteSpace(failure))
            parts.Add($"failure={failure}");

        return string.Join("; ", parts);
    }

    private static string DescribeUpdatedMemberEvidence(
        string expectedMember,
        IReadOnlyList<string> staleMembers,
        CompletionRequestResult? completion,
        string? failure)
    {
        List<string> parts =
        [
            $"{expectedMember}={FormatBoolean(completion is not null && ScenarioExecution.ContainsLabel(completion.Items, expectedMember))}",
        ];
        parts.AddRange(staleMembers.Select(stale =>
            $"{stale}={FormatBoolean(completion is not null && ScenarioExecution.ContainsLabel(completion.Items, stale))}"));

        if (completion is not null)
            parts.Add(ScenarioExecution.DescribeCompletionEvidence(completion));
        else
            parts.Add("completion=<unavailable>");

        if (!string.IsNullOrWhiteSpace(failure))
            parts.Add($"failure={failure}");

        return string.Join("; ", parts);
    }

    private static string DescribeCompletionShapeComparison(
        CompletionRequestResult? immediateCompletion,
        CompletionRequestResult? postDiagnosticCompletion,
        string? failure)
    {
        string immediate = immediateCompletion is null
            ? "<unavailable>"
            : ScenarioExecution.DescribeResponseShape(immediateCompletion.Evidence);
        string postDiagnostic = postDiagnosticCompletion is null
            ? "<unavailable>"
            : ScenarioExecution.DescribeResponseShape(postDiagnosticCompletion.Evidence);
        string differs = immediateCompletion is null || postDiagnosticCompletion is null
            ? "<unknown>"
            : FormatBoolean(!Equals(immediateCompletion.Evidence, postDiagnosticCompletion.Evidence));
        string details = $"immediate={immediate}; postDiagnostic={postDiagnostic}; differs={differs}";
        return string.IsNullOrWhiteSpace(failure) ? details : $"{details}; failure={failure}";
    }

    private static string FormatBoolean(bool value) => value ? "true" : "false";

    private static void AddDiskAuthorityCheck(
        List<ProbeCheckResult> checks,
        ProbeScenarioContext context,
        string expectedDiskText)
    {
        string diskAfter = context.Fixture.ReadTarget();
        checks.Add(new ProbeCheckResult(
            "UnsavedChangesDidNotWriteToDisk",
            string.Equals(diskAfter, expectedDiskText, StringComparison.Ordinal)
                && diskAfter.Contains("ProbeDiskMember", StringComparison.Ordinal)
                && !diskAfter.Contains("ProbeUnsavedMember", StringComparison.Ordinal)
                && !diskAfter.Contains("ProbeIncrementalMember", StringComparison.Ordinal)));
    }

    private static string ReplaceExactlyOnce(string source, string oldValue, string newValue)
    {
        int first = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (first < 0 || source.IndexOf(oldValue, first + oldValue.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Expected exactly one occurrence of {oldValue}.");
        return source[..first] + newValue + source[(first + oldValue.Length)..];
    }
}
