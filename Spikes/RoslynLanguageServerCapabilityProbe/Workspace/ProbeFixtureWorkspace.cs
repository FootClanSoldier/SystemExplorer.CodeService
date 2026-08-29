namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

internal sealed class ProbeFixtureWorkspace : IAsyncDisposable
{
    private readonly bool _keepArtifacts;

    public ProbeFixtureWorkspace(
        string rootPath,
        string solutionPath,
        string projectPath,
        string targetPath,
        string consumerPath,
        string diagnosticsPath,
        bool keepArtifacts)
    {
        RootPath = rootPath;
        SolutionPath = solutionPath;
        ProjectPath = projectPath;
        TargetPath = targetPath;
        ConsumerPath = consumerPath;
        DiagnosticsPath = diagnosticsPath;
        _keepArtifacts = keepArtifacts;
    }

    public string RootPath { get; }
    public string SolutionPath { get; }
    public string ProjectPath { get; }
    public string TargetPath { get; }
    public string ConsumerPath { get; }
    public string DiagnosticsPath { get; }

    public string ReadTarget() => File.ReadAllText(TargetPath);
    public string ReadConsumer() => File.ReadAllText(ConsumerPath);
    public string ReadDiagnostics() => File.ReadAllText(DiagnosticsPath);

    public ValueTask DisposeAsync()
    {
        if (!_keepArtifacts && Directory.Exists(RootPath))
            Directory.Delete(RootPath, recursive: true);
        return ValueTask.CompletedTask;
    }
}
