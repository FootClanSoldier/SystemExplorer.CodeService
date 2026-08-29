namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;

internal sealed record RoslynLanguageServerToolVerificationResult(
    string ExpectedVersion,
    string ActualVersion,
    string ToolPath,
    string ServerCommandPath,
    string CommandName);
