namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

internal sealed class ProbeFixtureSetupException(string message, Exception? innerException = null)
    : Exception(message, innerException);
