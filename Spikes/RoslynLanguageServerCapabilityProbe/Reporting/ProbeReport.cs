using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;

internal enum ProbeOverallDecision
{
    SuitableCandidate,
    SuitableCandidateForRealWorkspaceValidation,
    UnsuitableCandidate,
    Inconclusive,
}

internal sealed record ProbeWorkspaceReport(
    string Kind,
    string? RootPath,
    string? SolutionOrProjectPath,
    bool ProjectInitializationNotificationObserved,
    bool SemanticRequestsSucceeded,
    double? SemanticReadyMs,
    bool? ProcessSurvived,
    RoslynServerCapabilities? ServerCapabilities = null,
    int? StderrWarningCount = null,
    int? StderrErrorCount = null,
    string? Notes = null,
    bool CompletionSmokeSelected = false,
    bool CompletionSmokeSucceeded = false,
    bool DefinitionSmokeSelected = false,
    bool DefinitionSmokeSucceeded = false,
    bool ExpectedDefinitionMatched = false,
    bool SourceUnmodified = false);

internal sealed record ProbeReport(
    int SchemaVersion,
    string ProbeVersion,
    string RoslynLanguageServerExpectedVersion,
    string RoslynLanguageServerActualVersion,
    bool RoslynLanguageServerVersionVerified,
    string StreamJsonRpcVersion,
    string ServerCommandPath,
    RoslynLanguageServerLaunchKind ServerLaunchKind,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Platform,
    string OsDescription,
    string FrameworkDescription,
    ProbeWorkspaceReport FixtureWorkspace,
    ProbeWorkspaceReport RealWorkspace,
    IReadOnlyList<ProbeScenarioResult> Scenarios,
    IReadOnlyList<RoslynLanguageServerProcessResult> Processes,
    ProbeOverallDecision OverallDecision);
