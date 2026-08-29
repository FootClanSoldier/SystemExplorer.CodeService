using System.Diagnostics;
using System.Runtime.ExceptionServices;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal sealed class ProbeScenarioContext
{
    private long _generation;

    public ProbeScenarioContext(
        ProbeOptions options,
        ProbeFixtureWorkspace fixture,
        RoslynLanguageServerLaunchSpec launchSpec,
        ProbeFixtureRestoreResult fixtureRestore)
    {
        Options = options;
        Fixture = fixture;
        LaunchSpec = launchSpec;
        FixtureRestore = fixtureRestore;
    }

    public ProbeOptions Options { get; }
    public ProbeFixtureWorkspace Fixture { get; }
    public RoslynLanguageServerLaunchSpec LaunchSpec { get; }
    public ProbeFixtureRestoreResult FixtureRestore { get; }
    public List<RoslynLanguageServerProcessResult> ProcessResults { get; } = [];
    public ProbeSession? PrimarySession { get; set; }
    public string CurrentTargetText { get; set; } = string.Empty;
    public int CurrentTargetVersion { get; set; }
    public bool FixtureSemanticRequestSucceeded { get; set; }
    public bool FixtureProjectInitializationObserved { get; set; }
    public double? FixtureSemanticReadyMs { get; set; }
    public RoslynServerCapabilities? FixtureServerCapabilities { get; set; }
    public CompletionResponseEvidence? PrimaryCompletionEvidence { get; set; }
    public SemanticGateDisambiguationEvidence? PrimarySemanticGateDisambiguationEvidence { get; set; }
    public TrueEditorBufferCompletionEvidence? TrueEditorBufferEvidence { get; set; }

    public async Task<ProbeSession> StartSessionAsync(
        string workingDirectory,
        bool autoLoadProjects,
        CancellationToken cancellationToken)
    {
        long generation = Interlocked.Increment(ref _generation);
        RoslynLanguageServerProcess process = await RoslynLanguageServerProcess.StartAsync(
            LaunchSpec,
            workingDirectory,
            generation,
            autoLoadProjects,
            cancellationToken).ConfigureAwait(false);
        return new ProbeSession(process, ProcessResults);
    }
}

internal sealed record SemanticGateDisambiguationEvidence(
    CompletionResponseEvidence PreDefinitionNaturalCompletionEvidence,
    bool PreDefinitionNaturalCompletionIncludesProbeInstanceProperty,
    int DefinitionLocationCount,
    bool DefinitionMatchedExpectedFixtureSymbol,
    CompletionResponseEvidence PostDefinitionNaturalCompletionEvidence,
    bool PostDefinitionNaturalCompletionIncludesProbeInstanceProperty);

internal sealed record TrueEditorBufferCompletionEvidence(
    CompletionResponseEvidence CompletionEvidence,
    bool CompletionIncludesProbeInstanceProperty,
    int DefinitionLocationCount,
    bool DefinitionMatchedExpectedFixtureSymbol,
    bool SnapshotVerified,
    bool DiskUnchanged);

internal sealed class ProbeSession : IAsyncDisposable
{
    private readonly List<RoslynLanguageServerProcessResult> _processResults;
    private int _retired;

    public ProbeSession(RoslynLanguageServerProcess process, List<RoslynLanguageServerProcessResult> processResults)
    {
        Process = process;
        _processResults = processResults;
        Client = new RoslynLspClient(process);
    }

    public RoslynLanguageServerProcess Process { get; }
    public RoslynLspClient Client { get; }

    public async Task<(bool ReadinessObserved, double ElapsedMs)> InitializeWorkspaceAsync(
        string rootPath,
        string solutionOrProjectPath,
        bool explicitOpen,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        await Client.InitializeAsync(rootPath, cancellationToken).ConfigureAwait(false);
        if (explicitOpen)
            await Client.OpenWorkspaceAsync(solutionOrProjectPath, cancellationToken).ConfigureAwait(false);
        bool observed = await Client.Callbacks.WaitForProjectInitializationAsync(
            ProbeConstants.ProjectInitializationTimeout,
            cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        Client.RefreshDynamicCapabilities();
        return (observed, stopwatch.Elapsed.TotalMilliseconds);
    }

    public async Task<RoslynLanguageServerProcessResult> GracefulRetireAsync()
    {
        if (Volatile.Read(ref _retired) != 0)
            return GetLastResult();

        Exception? clientDisposeFailure = null;
        try
        {
            if (!Process.HasExited)
                await Client.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Normal retirement still owns the process even if LSP shutdown cannot complete.
        }

        try
        {
            await Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            clientDisposeFailure = exception;
        }

        RoslynLanguageServerProcessResult result = await Process.RetireAsync(
            requestForcedKill: false,
            CancellationToken.None).ConfigureAwait(false);
        RecordResult(result);

        if (clientDisposeFailure is not null)
            ExceptionDispatchInfo.Capture(clientDisposeFailure).Throw();

        return result;
    }

    public async Task<RoslynLanguageServerProcessResult> CrashAndRetireAsync()
    {
        if (Volatile.Read(ref _retired) != 0)
            return GetLastResult();

        Exception? failure = null;
        bool processAliveAtCrashBoundary = !Process.HasExited;

        // Intentional failure injection: kill the still-connected Roslyn generation first.
        if (processAliveAtCrashBoundary)
        {
            try
            {
                await Process.ForceKillAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        // Only after the process is terminal (or the kill attempt itself faulted) may transport retire.
        try
        {
            await Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        RoslynLanguageServerProcessResult result = await Process.RetireAsync(
            requestForcedKill: !Process.HasExited,
            CancellationToken.None).ConfigureAwait(false);
        RecordResult(result);

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Volatile.Read(ref _retired) == 0)
                await GracefulRetireAsync().ConfigureAwait(false);
        }
        finally
        {
            await Process.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RecordResult(RoslynLanguageServerProcessResult result)
    {
        _processResults.Add(result);
        Volatile.Write(ref _retired, 1);
    }

    private RoslynLanguageServerProcessResult GetLastResult()
    {
        RoslynLanguageServerProcessResult? result = _processResults.LastOrDefault(
            candidate => candidate.Identity.ScenarioGeneration == Process.Identity.ScenarioGeneration);
        return result ?? throw new InvalidOperationException("Session was marked retired before its process result was recorded.");
    }
}

internal static class ScenarioExecution
{
    public static async Task<ProbeScenarioResult> RunAsync(
        string name,
        CancellationToken cancellationToken,
        Func<List<ProbeCheckResult>, Task> body)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<ProbeCheckResult> checks = [];
        try
        {
            await body(checks).ConfigureAwait(false);
            stopwatch.Stop();
            bool passed = checks.All(static check => check.Passed);
            return new ProbeScenarioResult(
                name,
                passed ? ProbeScenarioStatus.Pass : ProbeScenarioStatus.Fail,
                stopwatch.Elapsed.TotalMilliseconds,
                checks,
                passed ? null : "CheckFailed",
                passed ? null : "One or more required checks failed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProbeServerSetupException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            checks.Add(new ProbeCheckResult("UnhandledScenarioException", false, exception.GetType().Name));
            return new ProbeScenarioResult(
                name,
                ProbeScenarioStatus.Fail,
                stopwatch.Elapsed.TotalMilliseconds,
                checks,
                exception.GetType().Name,
                exception.Message);
        }
    }

    public static bool ContainsLabel(IReadOnlyList<CompletionItemSummary> items, string label) =>
        items.Any(item => string.Equals(item.Label, label, StringComparison.Ordinal));

    public static string DescribeCompletionEvidence(CompletionRequestResult result)
    {
        const int maxProbeLabels = 32;
        string[] probeLabels = result.Items
            .Select(static item => item.Label)
            .Where(static label => label.StartsWith("Probe", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static label => label, StringComparer.Ordinal)
            .Take(maxProbeLabels)
            .ToArray();

        return $"resultKind={result.Evidence.ResultKind}; rawItems={result.Evidence.RawItemCount}; "
            + $"normalizedItems={result.Items.Count}; isIncomplete={FormatNullableBoolean(result.Evidence.IsIncomplete)}; "
            + $"probeLabels={(probeLabels.Length == 0 ? "<none>" : string.Join(",", probeLabels))}";
    }

    public static string DescribeResponseShape(CompletionResponseEvidence evidence) =>
        $"{evidence.ResultKind}/rawItems={evidence.RawItemCount}/isIncomplete={FormatNullableBoolean(evidence.IsIncomplete)}";

    private static string FormatNullableBoolean(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => "<null>",
    };

    public static void AddProtocolCoverageObservation(List<ProbeCheckResult> checks, ProbeSession session)
    {
        IReadOnlyList<string> unsupported = session.Client.Callbacks.UnsupportedServerRequests;
        checks.Add(new ProbeCheckResult(
            "UnsupportedServerRequests",
            true,
            unsupported.Count == 0
                ? "none observed"
                : $"count={unsupported.Count}; {string.Join(" | ", unsupported)}"));
    }
}
