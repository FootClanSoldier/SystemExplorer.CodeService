using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Instrumentation;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class CompletionSemanticOriginScenario
{
    private const string ScenarioName = "CompletionSemanticOrigin";

    public static async Task<ProbeScenarioResult> RunAsync(ProbeScenarioContext context, CancellationToken cancellationToken)
    {
        if (!context.Options.SemanticOriginSelected)
        {
            return ProbeScenarioResult.Skipped(
                ScenarioName,
                "No --semantic-origin-server/--semantic-origin-provenance supplied.");
        }

        return await ScenarioExecution.RunAsync(ScenarioName, cancellationToken, async checks =>
        {
            string serverPath = context.Options.SemanticOriginServerPath!;
            CompletionSemanticOriginProvenance provenance = await CompletionSemanticOriginProvenance.LoadAndVerifyAsync(
                context.Options.SemanticOriginProvenancePath!, serverPath, cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "SemanticOriginProvenanceVerified",
                true,
                $"repository={provenance.Repository}; baseCommit={provenance.BaseCommit}; distribution={provenance.BaselineDistributionId}; instrumentationVersion={provenance.InstrumentationVersion}"));

            SemanticOriginSnapshot snapshot = CreateSnapshot();
            string diskTarget = context.Fixture.ReadTarget();
            RoslynLanguageServerLaunchSpec launchSpec = RoslynLanguageServerLaunchSpec.CreateInstrumentation(serverPath);

            RoslynLanguageServerProcessResult? retired = null;
            await using ProbeSession session = await context.StartSessionAsync(
                launchSpec,
                context.Fixture.RootPath,
                autoLoadProjects: false,
                RoslynLspClientCapabilityProfile.ProductionCompletionWire,
                cancellationToken).ConfigureAwait(false);

            checks.Add(new ProbeCheckResult(
                "SemanticOriginServerStarted",
                !session.Process.HasExited,
                $"pid={session.Process.Identity.ProcessId}; generation={session.Process.Identity.ScenarioGeneration}"));

            (bool initializationObserved, double initializationMs) = await session.InitializeWorkspaceAsync(
                context.Fixture.RootPath,
                context.Fixture.SolutionPath,
                explicitOpen: true,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "SemanticOriginProjectInitializationObserved",
                initializationObserved,
                initializationObserved ? "workspace/projectInitializationComplete observed" : "notification timed out",
                initializationMs));

            await session.Client.DidOpenAsync(context.Fixture.TargetPath, diskTarget, 1, cancellationToken).ConfigureAwait(false);
            await session.Client.DidOpenAsync(context.Fixture.ConsumerPath, snapshot.Text, 1, cancellationToken).ConfigureAwait(false);

            SemanticReadinessAttempt readiness = await SemanticReadinessOperation.ExecuteDiagnosticReadinessAsync(
                session,
                context.Fixture.ConsumerPath,
                snapshot.Positions["UNQUALIFIED"],
                cancellationToken).ConfigureAwait(false);
            CompletionRequestResult unqualified = readiness.Completion
                ?? throw new InvalidOperationException("Semantic-origin readiness did not produce completion evidence.");
            checks.Add(new ProbeCheckResult(
                "SemanticOriginReadinessObserved",
                readiness.DiagnosticAvailable && (unqualified.Evidence.ResultKind is CompletionResponseResultKind.Array or CompletionResponseResultKind.CompletionList),
                $"{SemanticReadinessOperation.DescribeCapability(readiness)}; {SemanticReadinessOperation.DescribeDiagnostics(readiness)}; {Describe(unqualified)}",
                readiness.DiagnosticDurationMs));

            CompletionRequestResult other = await session.Client.CompletionAsync(
                context.Fixture.ConsumerPath, snapshot.Positions["OTHER"], cancellationToken).ConfigureAwait(false);
            CompletionRequestResult framework = await session.Client.CompletionAsync(
                context.Fixture.ConsumerPath, snapshot.Positions["FRAMEWORK"], cancellationToken).ConfigureAwait(false);
            CompletionRequestResult keyword = await session.Client.CompletionAsync(
                context.Fixture.ConsumerPath, snapshot.Positions["KEYWORD"], cancellationToken).ConfigureAwait(false);

            CompletionRequestResult[] responses = [unqualified, other, framework, keyword];
            bool wellFormed = responses.SelectMany(static response => response.Items).All(static item => !item.SemanticOriginMetadataMalformed);
            checks.Add(new ProbeCheckResult("SemanticOriginMetadataWellFormed", wellFormed, DescribeMalformed(responses)));

            AddExpected(checks, "SemanticOriginLocalObserved", unqualified, "ProbeOriginLocal", CompletionSemanticOriginKind.Local, null);
            AddExpected(checks, "SemanticOriginParameterObserved", unqualified, "ProbeOriginParameter", CompletionSemanticOriginKind.Local, null);
            AddExpected(checks, "SemanticOriginLocalFunctionObserved", unqualified, "ProbeOriginLocalFunction", CompletionSemanticOriginKind.Local, null);
            AddExpected(checks, "SemanticOriginCurrentTypeObserved", unqualified, "ProbeOriginCurrentMember", CompletionSemanticOriginKind.CurrentType, 0);
            AddExpected(checks, "SemanticOriginCurrentTypeDepthObserved", unqualified, "ProbeOriginCurrentMember", CompletionSemanticOriginKind.CurrentType, 0);
            AddExpected(checks, "SemanticOriginBaseDepth1Observed", unqualified, "ProbeOriginBase1Member", CompletionSemanticOriginKind.BaseType, 1);
            AddExpected(checks, "SemanticOriginBaseDepth2Observed", unqualified, "ProbeOriginBase2Member", CompletionSemanticOriginKind.BaseType, 2);
            AddExpected(checks, "SemanticOriginOtherUserCodeObserved", other, "ProbeOriginOtherUserMember", CompletionSemanticOriginKind.OtherUserCode, null);
            AddExpected(checks, "SemanticOriginSourceExtensionObserved", other, "ProbeOriginExtension", CompletionSemanticOriginKind.OtherUserCode, null);
            AddExpected(checks, "SemanticOriginFrameworkObserved", framework, "Length", CompletionSemanticOriginKind.FrameworkOrOther, null);

            CompletionItemSummary? keywordItem = GetUnique(keyword, "return", out bool keywordUnique);
            bool keywordUnknown = keywordUnique && keywordItem is not null
                && !keywordItem.SemanticOriginMetadataMalformed
                && (keywordItem.SemanticOrigin is null or CompletionSemanticOriginKind.Unknown)
                && keywordItem.InheritanceDepth is null;
            checks.Add(new ProbeCheckResult(
                "SemanticOriginUnknownNonSymbolControlObserved",
                keywordUnknown,
                keywordItem is null ? "label=return; item=<missing-or-duplicate>" : DescribeItem(keywordItem)));

            checks.Add(new ProbeCheckResult(
                "SemanticOriginServerSurvived",
                !session.Process.HasExited,
                $"processAliveBeforeRetirement={(!session.Process.HasExited).ToString().ToLowerInvariant()}"));
            retired = await session.GracefulRetireAsync().ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "SemanticOriginGracefulRetirement",
                retired.HasExited && !retired.ForcedKill,
                $"exitCode={retired.ExitCode?.ToString() ?? "<null>"}; forcedKill={retired.ForcedKill.ToString().ToLowerInvariant()}"));
        }).ConfigureAwait(false);
    }

    private static void AddExpected(
        List<ProbeCheckResult> checks,
        string checkName,
        CompletionRequestResult response,
        string label,
        CompletionSemanticOriginKind expectedOrigin,
        int? expectedDepth)
    {
        CompletionItemSummary? item = GetUnique(response, label, out bool unique);
        bool passed = unique && item is not null
            && !item.SemanticOriginMetadataMalformed
            && item.SemanticOrigin == expectedOrigin
            && item.InheritanceDepth == expectedDepth;
        checks.Add(new ProbeCheckResult(
            checkName,
            passed,
            item is null ? $"label={label}; item=<missing-or-duplicate>; {Describe(response)}" : DescribeItem(item)));
    }

    private static CompletionItemSummary? GetUnique(CompletionRequestResult response, string label, out bool unique)
    {
        CompletionItemSummary[] matches = response.Items.Where(item => string.Equals(item.Label, label, StringComparison.Ordinal)).Take(2).ToArray();
        unique = matches.Length == 1;
        return unique ? matches[0] : null;
    }

    private static string Describe(CompletionRequestResult result) =>
        $"shape={result.Evidence.ResultKind}; rawItems={result.Evidence.RawItemCount}; normalizedItems={result.Items.Count}";

    private static string DescribeItem(CompletionItemSummary item) =>
        $"label={item.Label}; origin={item.SemanticOrigin?.ToString() ?? "<missing>"}; depth={item.InheritanceDepth?.ToString() ?? "<none>"}; malformed={item.SemanticOriginMetadataMalformed.ToString().ToLowerInvariant()}";

    private static string DescribeMalformed(IEnumerable<CompletionRequestResult> responses)
    {
        string[] malformed = responses.SelectMany(static response => response.Items)
            .Where(static item => item.SemanticOriginMetadataMalformed)
            .Select(static item => item.Label)
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();
        return malformed.Length == 0 ? "malformed=<none>" : $"malformedLabels={string.Join(",", malformed)}";
    }

    private static SemanticOriginSnapshot CreateSnapshot()
    {
        const string source = """
using System;

class ProbeOriginGrandBase
{
    public int ProbeOriginBase2Member { get; }
}

class ProbeOriginBase : ProbeOriginGrandBase
{
    public int ProbeOriginBase1Member { get; }
}

class ProbeOriginOtherUser
{
    public int ProbeOriginOtherUserMember { get; }
}

static class ProbeOriginExtensions
{
    public static void ProbeOriginExtension(this ProbeOriginOtherUser value) { }
}

class ProbeOriginDerived : ProbeOriginBase
{
    public int ProbeOriginCurrentMember { get; }

    void Lexical(int ProbeOriginParameter)
    {
        int ProbeOriginLocal = 0;
        void ProbeOriginLocalFunction() { }
        _ = ProbeOrigin/*SE_ORIGIN_UNQUALIFIED*/;
    }

    void OtherUser(ProbeOriginOtherUser other)
    {
        _ = other.ProbeOrigin/*SE_ORIGIN_OTHER*/;
    }

    void Framework(string frameworkValue)
    {
        _ = frameworkValue.Len/*SE_ORIGIN_FRAMEWORK*/;
    }

    void Keyword()
    {
        ret/*SE_ORIGIN_KEYWORD*/
    }
}
""";

        string[] markerNames = ["UNQUALIFIED", "OTHER", "FRAMEWORK", "KEYWORD"];
        Dictionary<string, int> originalIndices = new(StringComparer.Ordinal);
        foreach (string name in markerNames)
        {
            string marker = $"/*SE_ORIGIN_{name}*/";
            int first = source.IndexOf(marker, StringComparison.Ordinal);
            if (first < 0 || source.IndexOf(marker, first + marker.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException($"Semantic-origin caret marker {name} must occur exactly once.");
            originalIndices.Add(name, first);
        }

        string clean = source;
        foreach (string name in markerNames)
            clean = clean.Replace($"/*SE_ORIGIN_{name}*/", string.Empty, StringComparison.Ordinal);

        Dictionary<string, LspPosition> positions = new(StringComparer.Ordinal);
        foreach ((string name, int originalIndex) in originalIndices)
        {
            int removedBefore = markerNames
                .Select(markerName => (Name: markerName, Marker: $"/*SE_ORIGIN_{markerName}*/", Index: originalIndices[markerName]))
                .Where(candidate => candidate.Index < originalIndex)
                .Sum(candidate => candidate.Marker.Length);
            positions.Add(name, PositionAt(clean, originalIndex - removedBefore));
        }
        return new SemanticOriginSnapshot(clean, positions);
    }

    private static LspPosition PositionAt(string source, int absoluteIndex)
    {
        int line = 0;
        int character = 0;
        for (int i = 0; i < absoluteIndex; i++)
        {
            if (source[i] == '\n') { line++; character = 0; }
            else if (source[i] != '\r') { character++; }
        }
        return new LspPosition(line, character);
    }

    private sealed record SemanticOriginSnapshot(string Text, IReadOnlyDictionary<string, LspPosition> Positions);
}
