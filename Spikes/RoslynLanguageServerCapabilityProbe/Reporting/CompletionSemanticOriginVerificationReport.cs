using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;

internal sealed record CompletionSemanticOriginVerificationReport(
    int SchemaVersion,
    string ProbeVersion,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string SemanticOriginServerPath,
    string SemanticOriginProvenancePath,
    double FixtureRestoreDurationMs,
    ProbeScenarioResult Scenario,
    IReadOnlyList<RoslynLanguageServerProcessResult> Processes,
    bool Passed);
