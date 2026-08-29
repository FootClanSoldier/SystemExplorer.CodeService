using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class NavigationScenario
{
    public static Task<ProbeScenarioResult> RunAsync(ProbeScenarioContext context, CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("Navigation", cancellationToken, async checks =>
        {
            ProbeSession session = context.PrimarySession ?? throw new InvalidOperationException("Primary session is not initialized.");
            string consumer = context.Fixture.ReadConsumer();
            string target = context.Fixture.ReadTarget();

            LspPosition definitionPosition = ProbeSourceMarker.FindUnique(consumer, "PROBE_DEFINITION");
            IReadOnlyList<LspLocationSummary> definitions = await session.Client.DefinitionAsync(
                context.Fixture.ConsumerPath, definitionPosition, cancellationToken).ConfigureAwait(false);
            LspRange expectedDefinitionRange = ProbeSourceMarker.FindUniqueTokenRange(target, "ProbeDefinitionSymbol");
            string targetUri = LspJson.FileUri(context.Fixture.TargetPath);
            bool definitionMatch = definitions.Any(location =>
                UriEquals(location.Uri, targetUri)
                && location.Range.Start.Line == expectedDefinitionRange.Start.Line);
            checks.Add(new ProbeCheckResult("DefinitionResolvesExpectedFixtureSymbol", definitionMatch));

            LspPosition referencesPosition = ProbeSourceMarker.FindUnique(consumer, "PROBE_REFERENCES");
            IReadOnlyList<LspLocationSummary> references = await session.Client.ReferencesAsync(
                context.Fixture.ConsumerPath, referencesPosition, includeDeclaration: true, cancellationToken).ConfigureAwait(false);
            string consumerUri = LspJson.FileUri(context.Fixture.ConsumerPath);
            bool targetPresent = references.Any(location => UriEquals(location.Uri, targetUri));
            int consumerReferenceCount = references.Count(location => UriEquals(location.Uri, consumerUri));
            checks.Add(new ProbeCheckResult("ReferencesIncludeDeclarationFile", targetPresent));
            checks.Add(new ProbeCheckResult("ReferencesIncludeKnownConsumerLocations", consumerReferenceCount >= 2,
                $"consumerLocations={consumerReferenceCount}; total={references.Count}"));
            checks.Add(new ProbeCheckResult("ProcessSurvivedNavigation", !session.Process.HasExited));
        });

    private static bool UriEquals(string left, string right) =>
        string.Equals(Uri.UnescapeDataString(left), Uri.UnescapeDataString(right), StringComparison.OrdinalIgnoreCase);
}
