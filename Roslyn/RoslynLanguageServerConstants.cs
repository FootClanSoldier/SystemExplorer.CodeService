namespace SystemExplorer.CodeService;

internal enum RoslynDocumentDiagnosticScope
{
    AllEnabledDocumentSources,
    CompilerSemanticOnly,
}

internal static class RoslynLanguageServerConstants
{
    public const int MaxCapturedStderrBytes = 256 * 1024;
    public const int MaxDiagnosticTargetLength = 1024;
    public const string PackagedWindowsX64RuntimeRelativePath = "roslyn/win-x64";
    public const string DocumentCompilerSemantic = "DocumentCompilerSemantic";

    public static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ProjectInitializationTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DocumentSynchronizationTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SemanticReadinessTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CompletionResolveTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(10);

    public static string? GetDocumentDiagnosticIdentifier(RoslynDocumentDiagnosticScope scope)
        => scope switch
        {
            RoslynDocumentDiagnosticScope.AllEnabledDocumentSources => null,
            RoslynDocumentDiagnosticScope.CompilerSemanticOnly => DocumentCompilerSemantic,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "unknown Roslyn document diagnostic scope."),
        };
}
