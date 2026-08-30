namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe;

internal static class ProbeConstants
{
    public const int ReportSchemaVersion = 3;
    public const string ProbeVersion = "1.3.4";
    public const string RoslynLanguageServerVersion = "5.12.0-1.26426.8";
    public const string StreamJsonRpcVersion = "2.25.29";
    public const string RoslynSourceCommit = "3aeb96c9ecc56a5ee483558f9e648e33e7bfe756";
    public const int RoslynStateTraceVersion = 1;
    public const string RoslynStateTracePrefix = "SETRACE|";
    public const int MaxRoslynStateTraceEvents = 256;

    public const int MaxToolInventoryOutputBytes = 64 * 1024;
    public const int MaxFixtureRestoreOutputBytes = 256 * 1024;
    public const int MaxCapturedStderrBytes = 256 * 1024;
    public const int MaxCallbackEvents = 256;
    public const int MaxCompletionItems = 4096;
    public const int MaxDiagnosticItems = 1024;
    public const int MaxPublishedDiagnosticDocuments = 32;

    public static readonly TimeSpan CallbackObservationPollInterval = TimeSpan.FromMilliseconds(50);

    public static readonly TimeSpan ToolInventoryTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan FixtureRestoreTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ProjectInitializationTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DiagnosticObservationTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(10);

    public const int SuccessExitCode = 0;
    public const int CapabilityFailureExitCode = 1;
    public const int InvalidArgumentsExitCode = 2;
    public const int ServerSetupFailureExitCode = 3;
    public const int InfrastructureFailureExitCode = 4;
}
