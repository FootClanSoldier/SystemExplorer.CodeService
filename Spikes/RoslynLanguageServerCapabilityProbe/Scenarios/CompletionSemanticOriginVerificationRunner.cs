using System.Text.Json;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Instrumentation;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal sealed class CompletionSemanticOriginVerificationRunner
{
    private readonly ProbeOptions _options;

    public CompletionSemanticOriginVerificationRunner(ProbeOptions options)
    {
        _options = options;
        if (options.RunMode != ProbeRunMode.CompletionSemanticOriginOnly)
            throw new ArgumentException("Semantic-origin verification runner requires CompletionSemanticOriginOnly mode.", nameof(options));
        if (!options.SemanticOriginSelected)
            throw new ArgumentException("Semantic-origin verification runner requires the semantic-origin server/provenance pair.", nameof(options));
    }

    public async Task<CompletionSemanticOriginVerificationReport> RunAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        string serverPath = _options.SemanticOriginServerPath!;
        string provenancePath = _options.SemanticOriginProvenancePath!;

        await VerifyServerProvenanceAsync(provenancePath, serverPath, cancellationToken).ConfigureAwait(false);
        RoslynLanguageServerLaunchSpec launchSpec = RoslynLanguageServerLaunchSpec.CreateInstrumentation(serverPath);

        await using ProbeFixtureWorkspace fixture = await ProbeFixtureWorkspaceBuilder.CreateAsync(
            _options.KeepArtifacts,
            cancellationToken).ConfigureAwait(false);
        ProbeFixtureRestoreResult restoreResult = await ProbeFixtureRestorer.RestoreAsync(
            fixture,
            cancellationToken).ConfigureAwait(false);
        ProbeScenarioContext context = new(_options, fixture, launchSpec, restoreResult);

        ProbeScenarioResult scenario = await CompletionSemanticOriginScenario.RunAsync(
            context,
            cancellationToken).ConfigureAwait(false);

        return new CompletionSemanticOriginVerificationReport(
            ProbeConstants.CompletionSemanticOriginVerificationReportSchemaVersion,
            ProbeConstants.ProbeVersion,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            serverPath,
            provenancePath,
            restoreResult.DurationMs,
            scenario,
            context.ProcessResults.ToArray(),
            scenario.Status == ProbeScenarioStatus.Pass);
    }

    private static async Task VerifyServerProvenanceAsync(
        string provenancePath,
        string serverPath,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await CompletionSemanticOriginProvenance.LoadAndVerifyAsync(
                provenancePath,
                serverPath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ProbeServerSetupException(
                $"Semantic-origin provenance verification failed: {exception.Message}",
                exception);
        }
    }
}
