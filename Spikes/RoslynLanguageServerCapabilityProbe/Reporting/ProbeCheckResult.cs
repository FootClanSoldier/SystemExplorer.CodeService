namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;

internal sealed record ProbeCheckResult(
    string Name,
    bool Passed,
    string? Details = null,
    double? DurationMs = null);
