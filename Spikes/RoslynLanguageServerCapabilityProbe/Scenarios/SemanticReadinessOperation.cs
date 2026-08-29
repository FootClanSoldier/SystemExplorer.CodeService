using System.Diagnostics;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal sealed record SemanticReadinessAttempt(
    bool DiagnosticAvailable,
    bool StaticDiagnosticProvider,
    bool DynamicDiagnosticRegistration,
    int DiagnosticCount,
    IReadOnlyList<string> DiagnosticCodes,
    double? DiagnosticDurationMs,
    CompletionRequestResult? Completion);

internal static class SemanticReadinessOperation
{
    private const int MaxDiagnosticCodes = 32;

    public static async Task<SemanticReadinessAttempt> ExecuteDiagnosticReadinessAsync(
        ProbeSession session,
        string consumerPath,
        LspPosition completionPosition,
        CancellationToken cancellationToken)
    {
        session.Client.RefreshDynamicCapabilities();
        RoslynServerCapabilities capabilities = session.Client.ServerCapabilities
            ?? throw new InvalidOperationException("Server capabilities unavailable.");
        bool dynamicDiagnosticRegistration = capabilities.HasDynamicRegistration("textDocument/diagnostic");
        bool diagnosticAvailable = capabilities.DiagnosticProvider || dynamicDiagnosticRegistration;
        if (!diagnosticAvailable)
        {
            return new SemanticReadinessAttempt(
                false,
                capabilities.DiagnosticProvider,
                dynamicDiagnosticRegistration,
                0,
                [],
                null,
                null);
        }

        long diagnosticStartTimestamp = Stopwatch.GetTimestamp();
        IReadOnlyList<DiagnosticSummary> diagnostics = await session.Client.PullDiagnosticsAsync(
            consumerPath,
            cancellationToken).ConfigureAwait(false);
        long diagnosticEndTimestamp = Stopwatch.GetTimestamp();
        CompletionRequestResult completion = await session.Client.CompletionAsync(
            consumerPath,
            completionPosition,
            cancellationToken).ConfigureAwait(false);

        string[] codes = diagnostics
            .Select(static diagnostic => diagnostic.Code)
            .Where(static code => !string.IsNullOrWhiteSpace(code))
            .Select(static code => code!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static code => code, StringComparer.Ordinal)
            .Take(MaxDiagnosticCodes)
            .ToArray();

        return new SemanticReadinessAttempt(
            true,
            capabilities.DiagnosticProvider,
            dynamicDiagnosticRegistration,
            diagnostics.Count,
            codes,
            Stopwatch.GetElapsedTime(diagnosticStartTimestamp, diagnosticEndTimestamp).TotalMilliseconds,
            completion);
    }

    public static string DescribeCapability(SemanticReadinessAttempt attempt) =>
        $"staticProvider={attempt.StaticDiagnosticProvider.ToString().ToLowerInvariant()}; "
        + $"dynamicRegistration={attempt.DynamicDiagnosticRegistration.ToString().ToLowerInvariant()}";

    public static string DescribeDiagnostics(SemanticReadinessAttempt attempt) =>
        $"diagnostics={attempt.DiagnosticCount}; "
        + $"codes={(attempt.DiagnosticCodes.Count == 0 ? "<none>" : string.Join(",", attempt.DiagnosticCodes))}";
}
