using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class SemanticGateDisambiguationScenario
{
    private const string NaturalMemberAnchor = "return target.ProbeExtension();";
    private const string NaturalMemberCaretPrefix = "return target.";

    public static Task<ProbeScenarioResult> RunAsync(
        ProbeScenarioContext context,
        CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("SemanticGateDisambiguation", cancellationToken, async checks =>
        {
            ProbeSession session = context.PrimarySession
                ?? throw new InvalidOperationException("Primary session is not initialized.");

            await AddDiagnosticChecksAsync(
                context,
                session,
                checks,
                string.Empty,
                context.PrimaryCompletionEvidence,
                includeProcessSurvivalCheck: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        });

    internal static async Task AddDiagnosticChecksAsync(
        ProbeScenarioContext context,
        ProbeSession session,
        List<ProbeCheckResult> checks,
        string checkPrefix,
        CompletionResponseEvidence? markerEvidence,
        bool includeProcessSurvivalCheck,
        CancellationToken cancellationToken)
    {
        string consumer = context.Fixture.ReadConsumer();
        string target = context.Fixture.ReadTarget();

        LspPosition naturalPosition = ProbeSourceMarker.FindUniquePositionWithin(
            consumer,
            NaturalMemberAnchor,
            NaturalMemberCaretPrefix.Length);
        CompletionRequestResult naturalCompletion = await session.Client.CompletionAsync(
            context.Fixture.ConsumerPath,
            naturalPosition,
            cancellationToken).ConfigureAwait(false);

        string naturalDetails = ScenarioExecution.DescribeCompletionEvidence(naturalCompletion);
        bool naturalShapeReturned = naturalCompletion.Evidence.ResultKind is
            CompletionResponseResultKind.Array or CompletionResponseResultKind.CompletionList;
        checks.Add(new ProbeCheckResult(
            checkPrefix + "NaturalMemberCompletionReturnedNonNullShape",
            naturalShapeReturned,
            naturalDetails,
            naturalCompletion.DurationMs));
        checks.Add(new ProbeCheckResult(
            checkPrefix + "NaturalMemberCompletionIncludesProbeInstanceProperty",
            ScenarioExecution.ContainsLabel(naturalCompletion.Items, "ProbeInstanceProperty"),
            naturalDetails));
        checks.Add(new ProbeCheckResult(
            checkPrefix + "NaturalVsMarkerCompletionShapeComparison",
            true,
            DescribeShapeComparison(markerEvidence, naturalCompletion.Evidence)));

        LspPosition definitionPosition = ProbeSourceMarker.FindUnique(consumer, "PROBE_DEFINITION");
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
            checkPrefix + "DefinitionSemanticProbeReturnedLocations",
            definitions.Count > 0,
            $"locations={definitions.Count}"));
        checks.Add(new ProbeCheckResult(
            checkPrefix + "DefinitionSemanticProbeMatchedExpectedFixtureSymbol",
            definitionMatch,
            $"locations={definitions.Count}; expectedTargetMatched={definitionMatch.ToString().ToLowerInvariant()}"));

        if (includeProcessSurvivalCheck)
        {
            checks.Add(new ProbeCheckResult(
                "ProcessSurvivedSemanticGateDisambiguation",
                !session.Process.HasExited));
        }
    }

    private static string DescribeShapeComparison(
        CompletionResponseEvidence? markerEvidence,
        CompletionResponseEvidence naturalEvidence)
    {
        string natural = ScenarioExecution.DescribeResponseShape(naturalEvidence);
        if (markerEvidence is null)
            return $"marker=<unavailable>; natural={natural}; differs=<unknown>";

        bool differs = markerEvidence != naturalEvidence;
        return $"marker={ScenarioExecution.DescribeResponseShape(markerEvidence)}; "
            + $"natural={natural}; differs={differs.ToString().ToLowerInvariant()}";
    }

    private static bool UriEquals(string left, string right) =>
        string.Equals(Uri.UnescapeDataString(left), Uri.UnescapeDataString(right), StringComparison.OrdinalIgnoreCase);
}
