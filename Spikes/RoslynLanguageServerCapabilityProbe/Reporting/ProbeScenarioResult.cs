namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;

internal enum ProbeScenarioStatus
{
    Pass,
    Fail,
    Skipped,
}

internal sealed record ProbeScenarioResult(
    string Name,
    ProbeScenarioStatus Status,
    double DurationMs,
    IReadOnlyList<ProbeCheckResult> Checks,
    string? FailureKind = null,
    string? FailureMessage = null)
{
    public static ProbeScenarioResult Skipped(string name, string reason) =>
        new(name, ProbeScenarioStatus.Skipped, 0, [], "Skipped", reason);
}
