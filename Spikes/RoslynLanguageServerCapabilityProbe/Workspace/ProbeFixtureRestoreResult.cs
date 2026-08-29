namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

internal sealed record ProbeFixtureRestoreResult(
    string AssetsFilePath,
    long AssetsFileLength,
    double DurationMs,
    int ExitCode);
