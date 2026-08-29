using System.Diagnostics;
using System.Security.Cryptography;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class RealWorkspaceScenario
{
    public static async Task<(ProbeScenarioResult Result, ProbeWorkspaceReport Workspace)> RunAsync(
        ProbeScenarioContext context,
        CancellationToken cancellationToken)
    {
        string? workspacePath = context.Options.SolutionPath ?? context.Options.ProjectPath;
        if (workspacePath is null)
        {
            return (
                ProbeScenarioResult.Skipped("RealGodotWorkspace", "No --solution or --project supplied."),
                new ProbeWorkspaceReport(
                    "RealGodot", null, null, false, false, null, null, null, null, null, "NOT RUN"));
        }

        string root = Path.GetDirectoryName(workspacePath)
            ?? throw new InvalidOperationException("Real workspace path has no parent directory.");
        bool readinessObserved = false;
        bool completionSucceeded = false;
        bool definitionSucceeded = false;
        bool expectedDefinitionMatched = false;
        bool sourceUnmodified = false;
        bool semanticSucceeded = false;
        double? readyMs = null;
        bool processSurvived = false;
        RoslynServerCapabilities? capabilities = null;
        RoslynLanguageServerProcessResult? retiredProcess = null;
        bool completionSelected = context.Options.CompletionSmokeSelected;
        bool definitionSelected = context.Options.DefinitionSmokeSelected;
        bool semanticSmokeSelected = completionSelected || definitionSelected;

        ProbeScenarioResult result = await ScenarioExecution.RunAsync("RealGodotWorkspace", cancellationToken, async checks =>
        {
            await using ProbeSession session = await context.StartSessionAsync(
                root,
                autoLoadProjects: false,
                cancellationToken).ConfigureAwait(false);
            (readinessObserved, double elapsedMs) = await session.InitializeWorkspaceAsync(
                root,
                workspacePath,
                explicitOpen: true,
                cancellationToken).ConfigureAwait(false);
            readyMs = elapsedMs;
            capabilities = session.Client.ServerCapabilities;
            checks.Add(new ProbeCheckResult("ProjectInitializationNotificationObserved", readinessObserved, null, elapsedMs));

            if (semanticSmokeSelected && context.Options.DocumentPath is not null)
            {
                byte[] beforeBytes = await File.ReadAllBytesAsync(context.Options.DocumentPath, cancellationToken).ConfigureAwait(false);
                byte[] beforeHash = SHA256.HashData(beforeBytes);
                string documentText = await File.ReadAllTextAsync(context.Options.DocumentPath, cancellationToken).ConfigureAwait(false);
                bool didOpen = false;
                Exception? closeFailure = null;
                Stopwatch semanticStopwatch = Stopwatch.StartNew();

                try
                {
                    await session.Client.DidOpenAsync(
                        context.Options.DocumentPath,
                        documentText,
                        1,
                        cancellationToken).ConfigureAwait(false);
                    didOpen = true;

                    if (completionSelected
                        && context.Options.CompletionLine is int completionLine
                        && context.Options.CompletionCharacter is int completionCharacter)
                    {
                        var (items, completionMs) = await session.Client.CompletionAsync(
                            context.Options.DocumentPath,
                            new LspPosition(completionLine, completionCharacter),
                            cancellationToken).ConfigureAwait(false);
                        completionSucceeded = items.Count > 0 && !session.Process.HasExited;
                        checks.Add(new ProbeCheckResult(
                            "ReadOnlyCompletionSmoke",
                            completionSucceeded,
                            $"items={items.Count}; processAlive={!session.Process.HasExited}",
                            completionMs));
                    }
                    else
                    {
                        checks.Add(new ProbeCheckResult(
                            "ReadOnlyCompletionSmokeSkipped",
                            true,
                            "Completion smoke was not selected."));
                    }

                    if (definitionSelected
                        && context.Options.DefinitionLine is int definitionLine
                        && context.Options.DefinitionCharacter is int definitionCharacter
                        && context.Options.ExpectedDefinitionPath is not null)
                    {
                        IReadOnlyList<LspLocationSummary> definitions = await session.Client.DefinitionAsync(
                            context.Options.DocumentPath,
                            new LspPosition(definitionLine, definitionCharacter),
                            cancellationToken).ConfigureAwait(false);
                        expectedDefinitionMatched = definitions.Any(location =>
                            LspFilePath.IsFileUriPathEqual(location.Uri, context.Options.ExpectedDefinitionPath));
                        definitionSucceeded = definitions.Count > 0 && expectedDefinitionMatched;
                        checks.Add(new ProbeCheckResult(
                            "ReadOnlyDefinitionSmoke",
                            definitionSucceeded,
                            $"locations={definitions.Count}; expectedTargetMatched={expectedDefinitionMatched}"));
                    }
                    else
                    {
                        checks.Add(new ProbeCheckResult(
                            "ReadOnlyDefinitionSmokeSkipped",
                            true,
                            "Definition smoke was not selected."));
                    }
                }
                finally
                {
                    if (didOpen && !session.Process.HasExited)
                    {
                        try
                        {
                            await session.Client.DidCloseAsync(
                                context.Options.DocumentPath,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            closeFailure = exception;
                        }
                    }

                    byte[] afterBytes = await File.ReadAllBytesAsync(
                        context.Options.DocumentPath,
                        CancellationToken.None).ConfigureAwait(false);
                    byte[] afterHash = SHA256.HashData(afterBytes);
                    sourceUnmodified = beforeHash.AsSpan().SequenceEqual(afterHash);
                    checks.Add(new ProbeCheckResult("RealWorkspaceSourceUnmodified", sourceUnmodified));
                }

                semanticStopwatch.Stop();
                readyMs = elapsedMs + semanticStopwatch.Elapsed.TotalMilliseconds;
                semanticSucceeded = completionSelected && definitionSelected
                    ? completionSucceeded && definitionSucceeded
                    : completionSelected
                        ? completionSucceeded
                        : definitionSelected && definitionSucceeded;

                if (closeFailure is not null)
                    throw new InvalidOperationException("Failed to close the read-only real-workspace document after semantic smoke.", closeFailure);
            }
            else
            {
                checks.Add(new ProbeCheckResult(
                    "ReadOnlySemanticSmokeSkipped",
                    true,
                    "Skipped: no completion or definition semantic selection supplied; project-load/readiness only."));
            }

            processSurvived = !session.Process.HasExited;
            checks.Add(new ProbeCheckResult("RoslynProcessSurvived", processSurvived));
            ScenarioExecution.AddProtocolCoverageObservation(checks, session);
            retiredProcess = await session.GracefulRetireAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        int? warningCount = retiredProcess is null ? null : CountLines(retiredProcess.CapturedStderr, "warning");
        int? errorCount = retiredProcess is null ? null : CountLines(retiredProcess.CapturedStderr, "error");

        return (
            result,
            new ProbeWorkspaceReport(
                "RealGodot",
                root,
                workspacePath,
                readinessObserved,
                semanticSucceeded,
                readyMs,
                processSurvived,
                capabilities,
                warningCount,
                errorCount,
                semanticSmokeSelected
                    ? "Read-only selected semantic smoke; one didOpen/didClose pair, no didChange, rename apply, or source write."
                    : "Project load/readiness only; semantic smoke was not selected.",
                completionSelected,
                completionSucceeded,
                definitionSelected,
                definitionSucceeded,
                expectedDefinitionMatched,
                sourceUnmodified));
    }

    private static int CountLines(string text, string token) => text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Count(line => line.Contains(token, StringComparison.OrdinalIgnoreCase));
}
