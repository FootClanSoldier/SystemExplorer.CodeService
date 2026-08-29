using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class DiagnosticsScenario
{
    public static Task<ProbeScenarioResult> RunAsync(ProbeScenarioContext context, CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("Diagnostics", cancellationToken, async checks =>
        {
            ProbeSession session = context.PrimarySession ?? throw new InvalidOperationException("Primary session is not initialized.");
            session.Client.RefreshDynamicCapabilities();
            RoslynServerCapabilities capabilities = session.Client.ServerCapabilities
                ?? throw new InvalidOperationException("Server capabilities unavailable.");
            context.FixtureServerCapabilities = capabilities;

            string baseline = context.Fixture.ReadDiagnostics();
            string uri = LspJson.FileUri(context.Fixture.DiagnosticsPath);
            await session.Client.DidOpenAsync(context.Fixture.DiagnosticsPath, baseline, 1, cancellationToken).ConfigureAwait(false);

            const string oldStatement = "int value = 1;";
            const string errorStatement = "int value = \"probe\";";
            LspRange createErrorRange = ProbeSourceMarker.FindUniqueTokenRange(baseline, oldStatement);
            string errorText = ReplaceExactlyOnce(baseline, oldStatement, errorStatement);
            await session.Client.DidChangeIncrementalAsync(
                context.Fixture.DiagnosticsPath,
                errorStatement,
                2,
                createErrorRange,
                oldStatement.Length,
                cancellationToken).ConfigureAwait(false);

            bool pull = capabilities.DiagnosticProvider || capabilities.HasDynamicRegistration("textDocument/diagnostic");
            IReadOnlyList<DiagnosticSummary> errorDiagnostics;
            long errorPublicationSequence = 0;
            if (pull)
            {
                errorDiagnostics = await session.Client.PullDiagnosticsAsync(
                    context.Fixture.DiagnosticsPath, cancellationToken).ConfigureAwait(false);
                checks.Add(new ProbeCheckResult("DiagnosticsTransport", true, "PullDiagnostics"));
                checks.Add(new ProbeCheckResult("PullDiagnostics", true, "textDocument/diagnostic"));
                checks.Add(new ProbeCheckResult("PublishDiagnostics", true, "not selected; pull diagnostics advertised/registered"));
            }
            else
            {
                errorDiagnostics = await session.Client.Callbacks.WaitForPublishedDiagnosticsAsync(
                    uri,
                    ContainsProbeTypeMismatch,
                    ProbeConstants.DiagnosticObservationTimeout,
                    cancellationToken).ConfigureAwait(false);
                bool publishObserved = ContainsProbeTypeMismatch(errorDiagnostics);
                errorPublicationSequence = session.Client.Callbacks.GetPublishedDiagnosticsSequence(uri);
                checks.Add(new ProbeCheckResult(
                    "DiagnosticsTransport",
                    publishObserved,
                    publishObserved ? "PublishDiagnostics" : "Unsupported/NotObserved"));
                checks.Add(new ProbeCheckResult("PullDiagnostics", true, "not advertised/registered"));
                checks.Add(new ProbeCheckResult("PublishDiagnostics", publishObserved,
                    publishObserved ? "textDocument/publishDiagnostics" : "Unsupported/NotObserved"));
            }

            checks.Add(new ProbeCheckResult(
                "UnsavedSemanticDiagnosticObserved",
                ContainsProbeTypeMismatch(errorDiagnostics),
                string.Join(",", errorDiagnostics.Select(d => d.Code).Where(c => c is not null).Distinct())));

            LspRange clearRange = ProbeSourceMarker.FindUniqueTokenRange(errorText, errorStatement);
            await session.Client.DidChangeIncrementalAsync(
                context.Fixture.DiagnosticsPath,
                oldStatement,
                3,
                clearRange,
                errorStatement.Length,
                cancellationToken).ConfigureAwait(false);

            IReadOnlyList<DiagnosticSummary> cleared;
            if (pull)
            {
                cleared = await session.Client.PullDiagnosticsAsync(
                    context.Fixture.DiagnosticsPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                cleared = await session.Client.Callbacks.WaitForPublishedDiagnosticsAsync(
                    uri,
                    diagnostics => !ContainsProbeTypeMismatch(diagnostics),
                    ProbeConstants.DiagnosticObservationTimeout,
                    cancellationToken,
                    afterSequence: errorPublicationSequence).ConfigureAwait(false);
            }

            checks.Add(new ProbeCheckResult("DiagnosticClearedAfterUnsavedFix", !ContainsProbeTypeMismatch(cleared)));
            checks.Add(new ProbeCheckResult("DiagnosticFileRemainedUnchangedOnDisk",
                string.Equals(context.Fixture.ReadDiagnostics(), baseline, StringComparison.Ordinal)));
            await session.Client.DidCloseAsync(context.Fixture.DiagnosticsPath, cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult("ProcessSurvivedDiagnostics", !session.Process.HasExited));
        });

    private static bool ContainsProbeTypeMismatch(IReadOnlyList<DiagnosticSummary> diagnostics) =>
        diagnostics.Any(diagnostic => string.Equals(diagnostic.Code, "CS0029", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Message.Contains("convert", StringComparison.OrdinalIgnoreCase));

    private static string ReplaceExactlyOnce(string source, string oldValue, string newValue)
    {
        int first = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (first < 0 || source.IndexOf(oldValue, first + oldValue.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"Expected exactly one occurrence of {oldValue}.");
        return source[..first] + newValue + source[(first + oldValue.Length)..];
    }
}
