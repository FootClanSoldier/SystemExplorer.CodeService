namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;

internal sealed record RoslynProcessIdentity(
    int ProcessId,
    DateTimeOffset? StartTimeUtc,
    long? StartTimeUtcTicks,
    string LauncherExecutablePath,
    string ServerCommandPath,
    RoslynLanguageServerLaunchKind LaunchKind,
    long ScenarioGeneration);

internal sealed record RoslynProcessMetrics(
    DateTimeOffset CapturedAtUtc,
    long? WorkingSetBytes,
    long? PrivateMemoryBytes,
    bool IsCoarse = true);

internal sealed record RoslynLanguageServerProcessResult(
    RoslynProcessIdentity Identity,
    bool HasExited,
    int? ExitCode,
    bool ForcedKill,
    bool StderrTruncated,
    string CapturedStderr,
    RoslynProcessMetrics? FinalMetrics);
