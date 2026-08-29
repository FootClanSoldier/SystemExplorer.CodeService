using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class StaleVersionScenario
{
    public static async Task<ProbeScenarioResult> RunAsync(ProbeScenarioContext context, CancellationToken cancellationToken)
    {
        if (!context.Options.RunStaleVersionExperiment)
            return ProbeScenarioResult.Skipped("StaleDocumentVersionObservation", "Enable with --stale-version-experiment.");

        return await ScenarioExecution.RunAsync("StaleDocumentVersionObservation", cancellationToken, async checks =>
        {
            await using ProbeSession session = await context.StartSessionAsync(
                context.Fixture.RootPath,
                autoLoadProjects: false,
                cancellationToken).ConfigureAwait(false);
            (bool readiness, _) = await session.InitializeWorkspaceAsync(
                context.Fixture.RootPath,
                context.Fixture.SolutionPath,
                explicitOpen: true,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult("ExperimentProjectReady", readiness));

            string diskText = context.Fixture.ReadTarget();
            string consumer = context.Fixture.ReadConsumer();
            LspPosition completionPosition = ProbeSourceMarker.FindUniqueCompletionPosition(consumer, "PROBE_INSTANCE_COMPLETION");
            await session.Client.DidOpenAsync(context.Fixture.TargetPath, diskText, 1, cancellationToken).ConfigureAwait(false);
            await session.Client.DidOpenAsync(context.Fixture.ConsumerPath, consumer, 1, cancellationToken).ConfigureAwait(false);

            string v2 = ReplaceExactlyOnce(diskText, "ProbeDiskMember", "ProbeUnsavedMember");
            await session.Client.DidChangeFullAsync(context.Fixture.TargetPath, v2, 2, cancellationToken).ConfigureAwait(false);
            _ = await session.Client.CompletionAsync(context.Fixture.ConsumerPath, completionPosition, cancellationToken).ConfigureAwait(false);

            LspRange range = ProbeSourceMarker.FindUniqueTokenRange(v2, "ProbeUnsavedMember");
            string v3 = ReplaceExactlyOnce(v2, "ProbeUnsavedMember", "ProbeIncrementalMember");
            await session.Client.DidChangeIncrementalAsync(
                context.Fixture.TargetPath, "ProbeIncrementalMember", 3, range, "ProbeUnsavedMember".Length, cancellationToken).ConfigureAwait(false);
            _ = await session.Client.CompletionAsync(context.Fixture.ConsumerPath, completionPosition, cancellationToken).ConfigureAwait(false);

            string stale = ReplaceExactlyOnce(v3, "ProbeIncrementalMember", "ProbeStaleMember");
            await session.Client.DidChangeFullAsync(context.Fixture.TargetPath, stale, 2, cancellationToken).ConfigureAwait(false);

            if (session.Process.HasExited)
            {
                checks.Add(new ProbeCheckResult("StaleVersionServerBehavior", true, "server-exited"));
            }
            else
            {
                try
                {
                    var (items, _) = await session.Client.CompletionAsync(
                        context.Fixture.ConsumerPath, completionPosition, cancellationToken).ConfigureAwait(false);
                    string behavior = ScenarioExecution.ContainsLabel(items, "ProbeStaleMember")
                        ? "accepted-stale-version"
                        : ScenarioExecution.ContainsLabel(items, "ProbeIncrementalMember")
                            ? "ignored-or-rejected-stale-version"
                            : "semantic-result-ambiguous";
                    checks.Add(new ProbeCheckResult("StaleVersionServerBehavior", true, behavior));
                }
                catch (Exception exception)
                {
                    checks.Add(new ProbeCheckResult("StaleVersionServerBehavior", true,
                        $"request-after-stale-faulted:{exception.GetType().Name}"));
                }
            }

            checks.Add(new ProbeCheckResult("ObservationOnlyNotCodeServiceAuthority", true,
                "Phase 7 must still reject stale versions at the CodeService boundary."));
            ScenarioExecution.AddProtocolCoverageObservation(checks, session);
            if (!session.Process.HasExited)
            {
                await session.Client.DidCloseAsync(context.Fixture.ConsumerPath, cancellationToken).ConfigureAwait(false);
                await session.Client.DidCloseAsync(context.Fixture.TargetPath, cancellationToken).ConfigureAwait(false);
            }
            await session.GracefulRetireAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static string ReplaceExactlyOnce(string source, string oldValue, string newValue)
    {
        int first = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (first < 0 || source.IndexOf(oldValue, first + oldValue.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Expected exactly one occurrence of {oldValue}.");
        return source[..first] + newValue + source[(first + oldValue.Length)..];
    }
}
