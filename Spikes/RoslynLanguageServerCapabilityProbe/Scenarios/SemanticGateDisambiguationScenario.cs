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

            context.PrimarySemanticGateDisambiguationEvidence = await AddDiagnosticChecksAsync(
                context,
                session,
                checks,
                string.Empty,
                context.PrimaryCompletionEvidence,
                context.CurrentConsumerText,
                context.CurrentConsumerCompletionPosition,
                includeProcessSurvivalCheck: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        });

    internal static async Task<SemanticGateDisambiguationEvidence> AddDiagnosticChecksAsync(
        ProbeScenarioContext context,
        ProbeSession session,
        List<ProbeCheckResult> checks,
        string checkPrefix,
        CompletionResponseEvidence? baselineCompletionEvidence,
        string? openedConsumerText,
        LspPosition? openedCompletionPosition,
        bool includeProcessSurvivalCheck,
        CancellationToken cancellationToken)
    {
        if ((openedConsumerText is null) != (openedCompletionPosition is null))
            throw new InvalidOperationException("Opened Consumer text and completion position must be supplied together.");

        string consumer = openedConsumerText ?? context.Fixture.ReadConsumer();
        string target = context.Fixture.ReadTarget();
        LspPosition naturalPosition = openedCompletionPosition ?? ProbeSourceMarker.FindUniquePositionWithin(
            consumer,
            NaturalMemberAnchor,
            NaturalMemberCaretPrefix.Length);
        CompletionRequestResult preDefinitionNaturalCompletion = await session.Client.CompletionAsync(
            context.Fixture.ConsumerPath,
            naturalPosition,
            cancellationToken).ConfigureAwait(false);

        string naturalDetails = ScenarioExecution.DescribeCompletionEvidence(preDefinitionNaturalCompletion);
        bool naturalShapeReturned = preDefinitionNaturalCompletion.Evidence.ResultKind is
            CompletionResponseResultKind.Array or CompletionResponseResultKind.CompletionList;
        bool preDefinitionIncludesProbeInstanceProperty = ScenarioExecution.ContainsLabel(
            preDefinitionNaturalCompletion.Items,
            "ProbeInstanceProperty");
        checks.Add(new ProbeCheckResult(
            checkPrefix + "NaturalMemberCompletionReturnedNonNullShape",
            naturalShapeReturned,
            naturalDetails,
            preDefinitionNaturalCompletion.DurationMs));
        checks.Add(new ProbeCheckResult(
            checkPrefix + "NaturalMemberCompletionIncludesProbeInstanceProperty",
            preDefinitionIncludesProbeInstanceProperty,
            naturalDetails));
        checks.Add(new ProbeCheckResult(
            checkPrefix + "NaturalVsBaselineCompletionShapeComparison",
            true,
            DescribeShapeComparison(baselineCompletionEvidence, preDefinitionNaturalCompletion.Evidence)));

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

        CompletionRequestResult postDefinitionNaturalCompletion = await session.Client.CompletionAsync(
            context.Fixture.ConsumerPath,
            naturalPosition,
            cancellationToken).ConfigureAwait(false);
        string postDefinitionNaturalDetails = ScenarioExecution.DescribeCompletionEvidence(postDefinitionNaturalCompletion);
        bool postDefinitionNaturalShapeReturned = postDefinitionNaturalCompletion.Evidence.ResultKind is
            CompletionResponseResultKind.Array or CompletionResponseResultKind.CompletionList;
        bool postDefinitionIncludesProbeInstanceProperty = ScenarioExecution.ContainsLabel(
            postDefinitionNaturalCompletion.Items,
            "ProbeInstanceProperty");
        checks.Add(new ProbeCheckResult(
            checkPrefix + "PostDefinitionNaturalMemberCompletionReturnedNonNullShape",
            postDefinitionNaturalShapeReturned,
            postDefinitionNaturalDetails,
            postDefinitionNaturalCompletion.DurationMs));
        checks.Add(new ProbeCheckResult(
            checkPrefix + "PostDefinitionNaturalMemberCompletionIncludesProbeInstanceProperty",
            postDefinitionIncludesProbeInstanceProperty,
            postDefinitionNaturalDetails));
        checks.Add(new ProbeCheckResult(
            checkPrefix + "PostDefinitionVsPreDefinitionNaturalCompletionShapeComparison",
            true,
            DescribePreAndPostDefinitionShapeComparison(
                preDefinitionNaturalCompletion.Evidence,
                postDefinitionNaturalCompletion.Evidence)));

        if (includeProcessSurvivalCheck)
        {
            checks.Add(new ProbeCheckResult(
                "ProcessSurvivedSemanticGateDisambiguation",
                !session.Process.HasExited));
        }

        return new SemanticGateDisambiguationEvidence(
            preDefinitionNaturalCompletion.Evidence,
            preDefinitionIncludesProbeInstanceProperty,
            definitions.Count,
            definitionMatch,
            postDefinitionNaturalCompletion.Evidence,
            postDefinitionIncludesProbeInstanceProperty);
    }

    private static string DescribeShapeComparison(
        CompletionResponseEvidence? baselineCompletionEvidence,
        CompletionResponseEvidence naturalEvidence)
    {
        string natural = ScenarioExecution.DescribeResponseShape(naturalEvidence);
        if (baselineCompletionEvidence is null)
            return $"baseline=<unavailable>; natural={natural}; differs=<unknown>";

        bool differs = baselineCompletionEvidence != naturalEvidence;
        return $"baseline={ScenarioExecution.DescribeResponseShape(baselineCompletionEvidence)}; "
            + $"natural={natural}; differs={differs.ToString().ToLowerInvariant()}";
    }

    private static string DescribePreAndPostDefinitionShapeComparison(
        CompletionResponseEvidence preDefinitionEvidence,
        CompletionResponseEvidence postDefinitionEvidence)
    {
        bool differs = preDefinitionEvidence != postDefinitionEvidence;
        return $"preDefinition={ScenarioExecution.DescribeResponseShape(preDefinitionEvidence)}; "
            + $"postDefinition={ScenarioExecution.DescribeResponseShape(postDefinitionEvidence)}; "
            + $"differs={differs.ToString().ToLowerInvariant()}";
    }

    private static bool UriEquals(string left, string right) =>
        string.Equals(Uri.UnescapeDataString(left), Uri.UnescapeDataString(right), StringComparison.OrdinalIgnoreCase);
}
