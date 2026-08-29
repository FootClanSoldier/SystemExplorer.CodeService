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
            string consumer = context.Fixture.ReadConsumer();
            LspPosition completionPosition = ProbeSourceMarker.FindUniqueCompletionPosition(consumer, "PROBE_INSTANCE_COMPLETION");

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
            double? fullCompletionMs = null;
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
                    var (fullItems, completionMs) = await session.Client.CompletionAsync(
                        context.Fixture.ConsumerPath, completionPosition, cancellationToken).ConfigureAwait(false);
                    fullCompletionMs = completionMs;
                    fullObserved = ScenarioExecution.ContainsLabel(fullItems, "ProbeUnsavedMember")
                        && !ScenarioExecution.ContainsLabel(fullItems, "ProbeDiskMember");
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
                fullFailure,
                fullCompletionMs));
            checks.Add(new ProbeCheckResult(
                "CompletionAfterFullDocumentChangeLatency",
                fullObserved,
                fullObserved ? null : "No successful post-full-change semantic result.",
                fullStopwatch.Elapsed.TotalMilliseconds));

            if (!fullSurvived)
            {
                checks.Add(new ProbeCheckResult(
                    "IncrementalDidChangeProcessSurvived",
                    false,
                    "Not attempted: Roslyn exited during full-document didChange stage."));
                checks.Add(new ProbeCheckResult(
                    "IncrementalDidChangeSemanticUpdateObserved",
                    false,
                    "Not attempted: Roslyn exited during full-document didChange stage."));
                checks.Add(new ProbeCheckResult(
                    "CompletionAfterIncrementalChangeLatency",
                    false,
                    "Not attempted: Roslyn exited during full-document didChange stage."));
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
            double? incrementalCompletionMs = null;
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
                    var (incrementalItems, completionMs) = await session.Client.CompletionAsync(
                        context.Fixture.ConsumerPath, completionPosition, cancellationToken).ConfigureAwait(false);
                    incrementalCompletionMs = completionMs;
                    incrementalObserved = ScenarioExecution.ContainsLabel(incrementalItems, "ProbeIncrementalMember")
                        && !ScenarioExecution.ContainsLabel(incrementalItems, "ProbeUnsavedMember")
                        && !ScenarioExecution.ContainsLabel(incrementalItems, "ProbeDiskMember");
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
                incrementalFailure,
                incrementalCompletionMs));
            checks.Add(new ProbeCheckResult(
                "CompletionAfterIncrementalChangeLatency",
                incrementalObserved,
                incrementalObserved ? null : "No successful post-incremental-change semantic result.",
                incrementalStopwatch.Elapsed.TotalMilliseconds));

            AddDiskAuthorityCheck(checks, context, diskText);
            ScenarioExecution.AddProtocolCoverageObservation(checks, session);
        });

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
