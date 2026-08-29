using System.Diagnostics;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class RecoveryScenario
{
    private const string DiskConsumerAnchor = "return target.ProbeExtension();";
    private const string EditorConsumerPrefix = "return target.";

    public static Task<ProbeScenarioResult> RunAsync(ProbeScenarioContext context, CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("Recovery", cancellationToken, async checks =>
        {
            ProbeSession oldSession = context.PrimarySession ?? throw new InvalidOperationException("Primary session is not initialized.");
            if (string.IsNullOrEmpty(context.CurrentTargetText) || context.CurrentTargetVersion < 3)
                throw new InvalidOperationException("Document synchronization state was not established before recovery.");

            if (context.CurrentConsumerVersion != 1 || string.IsNullOrEmpty(context.CurrentConsumerText))
                throw new InvalidOperationException("Primary Consumer true-editor snapshot state was not established before recovery.");
            LspPosition completionPosition = context.CurrentConsumerCompletionPosition
                ?? throw new InvalidOperationException("Primary Consumer true-editor completion position is unavailable before recovery.");
            var (preCrashItems, _) = await oldSession.Client.CompletionAsync(
                context.Fixture.ConsumerPath, completionPosition, cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "PreCrashSemanticQuery",
                ScenarioExecution.ContainsLabel(preCrashItems, "ProbeIncrementalMember")));

            ScenarioExecution.AddProtocolCoverageObservation(checks, oldSession);
            bool preCrashProcessAlive = !oldSession.Process.HasExited;
            checks.Add(new ProbeCheckResult("PreCrashProcessAlive", preCrashProcessAlive));

            RoslynProcessIdentity oldIdentity = oldSession.Process.Identity;
            RoslynLanguageServerProcessResult oldResult = await oldSession.CrashAndRetireAsync().ConfigureAwait(false);
            context.PrimarySession = null;
            checks.Add(new ProbeCheckResult(
                "ForcedProcessExitObserved",
                oldResult.HasExited && oldResult.ForcedKill,
                $"exitCode={oldResult.ExitCode?.ToString() ?? "unknown"}; forcedKill={oldResult.ForcedKill}"));
            await oldSession.DisposeAsync().ConfigureAwait(false);

            await using ProbeSession newSession = await context.StartSessionAsync(
                context.Fixture.RootPath,
                autoLoadProjects: false,
                cancellationToken).ConfigureAwait(false);
            RoslynProcessIdentity newIdentity = newSession.Process.Identity;
            bool identityDifferent = oldIdentity.ProcessId != newIdentity.ProcessId
                || oldIdentity.StartTimeUtcTicks != newIdentity.StartTimeUtcTicks;
            checks.Add(new ProbeCheckResult(
                "NewProcessIdentityDifferent",
                identityDifferent,
                $"oldPid={oldIdentity.ProcessId}; newPid={newIdentity.ProcessId}"));

            Stopwatch reinitialize = Stopwatch.StartNew();
            (bool readinessObserved, _) = await newSession.InitializeWorkspaceAsync(
                context.Fixture.RootPath,
                context.Fixture.SolutionPath,
                explicitOpen: true,
                cancellationToken).ConfigureAwait(false);
            reinitialize.Stop();
            checks.Add(new ProbeCheckResult("Reinitialization", !newSession.Process.HasExited, null, reinitialize.Elapsed.TotalMilliseconds));
            checks.Add(new ProbeCheckResult("ProjectReload", readinessObserved));

            await newSession.Client.DidOpenAsync(
                context.Fixture.TargetPath,
                context.CurrentTargetText,
                context.CurrentTargetVersion,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult("TargetDocumentReplay", !newSession.Process.HasExited,
                $"version={context.CurrentTargetVersion}"));

            await newSession.Client.DidOpenAsync(
                context.Fixture.ConsumerPath,
                context.CurrentConsumerText,
                context.CurrentConsumerVersion,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "ConsumerDocumentReplay",
                !newSession.Process.HasExited,
                $"version={context.CurrentConsumerVersion}; source=in-memory-editor-snapshot; logicalCaret=return target.|"));
            checks.Add(new ProbeCheckResult("OpenDocumentReplay", !newSession.Process.HasExited,
                $"targetVersion={context.CurrentTargetVersion}; consumerVersion={context.CurrentConsumerVersion}"));

            SemanticReadinessAttempt readiness = await SemanticReadinessOperation.ExecuteDiagnosticReadinessAsync(
                newSession,
                context.Fixture.ConsumerPath,
                completionPosition,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult(
                "RecoverySemanticReadinessDiagnosticCapabilityObserved",
                readiness.DiagnosticAvailable,
                SemanticReadinessOperation.DescribeCapability(readiness)));

            if (readiness.DiagnosticAvailable)
            {
                checks.Add(new ProbeCheckResult(
                    "RecoverySemanticReadinessDiagnosticPullCompleted",
                    true,
                    SemanticReadinessOperation.DescribeDiagnostics(readiness),
                    readiness.DiagnosticDurationMs));

                CompletionRequestResult postRestartCompletion = readiness.Completion
                    ?? throw new InvalidOperationException("Recovery diagnostic readiness completed without a completion result.");
                bool nonNullShape = postRestartCompletion.Evidence.ResultKind is
                    CompletionResponseResultKind.Array or CompletionResponseResultKind.CompletionList;
                checks.Add(new ProbeCheckResult(
                    "RecoverySemanticReadinessCompletionReturnedNonNullShape",
                    nonNullShape,
                    ScenarioExecution.DescribeCompletionEvidence(postRestartCompletion),
                    postRestartCompletion.DurationMs));

                bool hasIncrementalMember = ScenarioExecution.ContainsLabel(
                    postRestartCompletion.Items,
                    "ProbeIncrementalMember");
                bool hasDiskMember = ScenarioExecution.ContainsLabel(
                    postRestartCompletion.Items,
                    "ProbeDiskMember");
                bool replayObserved = hasIncrementalMember && !hasDiskMember;
                checks.Add(new ProbeCheckResult(
                    "PostRestartSemanticQuery",
                    replayObserved,
                    $"ProbeIncrementalMember={hasIncrementalMember.ToString().ToLowerInvariant()}; "
                        + $"ProbeDiskMember={hasDiskMember.ToString().ToLowerInvariant()}",
                    postRestartCompletion.DurationMs));
                checks.Add(new ProbeCheckResult(
                    "CompletionAfterRoslynRestartLatency",
                    true,
                    null,
                    postRestartCompletion.DurationMs));
            }
            else
            {
                checks.Add(new ProbeCheckResult(
                    "RecoverySemanticReadinessCompletionReturnedNonNullShape",
                    false,
                    "Not attempted: diagnostic capability was unavailable."));
                checks.Add(new ProbeCheckResult(
                    "PostRestartSemanticQuery",
                    false,
                    "Not attempted: diagnostic semantic readiness was unavailable."));
                checks.Add(new ProbeCheckResult(
                    "CompletionAfterRoslynRestartLatency",
                    false,
                    "Not attempted: diagnostic semantic readiness was unavailable."));
            }

            checks.Add(new ProbeCheckResult("ReplayedUnsavedStateRemainedOffDisk",
                context.Fixture.ReadTarget().Contains("ProbeDiskMember", StringComparison.Ordinal)
                    && !context.Fixture.ReadTarget().Contains("ProbeIncrementalMember", StringComparison.Ordinal)));

            string diskConsumerAfterReplay = context.Fixture.ReadConsumer();
            string expectedDiskConsumer = ReconstructDiskConsumer(context.CurrentConsumerText);
            bool originalStatementPresent = CountOrdinalOccurrences(diskConsumerAfterReplay, DiskConsumerAnchor) == 1;
            bool matchesEditorSnapshot = string.Equals(
                diskConsumerAfterReplay,
                context.CurrentConsumerText,
                StringComparison.Ordinal);
            bool replayedConsumerStayedOffDisk = originalStatementPresent
                && !matchesEditorSnapshot
                && string.Equals(diskConsumerAfterReplay, expectedDiskConsumer, StringComparison.Ordinal);
            checks.Add(new ProbeCheckResult(
                "ReplayedConsumerSnapshotRemainedOffDisk",
                replayedConsumerStayedOffDisk,
                $"originalStatementPresent={originalStatementPresent.ToString().ToLowerInvariant()}; "
                    + $"matchesEditorSnapshot={matchesEditorSnapshot.ToString().ToLowerInvariant()}"));

            if (!newSession.Process.HasExited)
            {
                await newSession.Client.DidCloseAsync(context.Fixture.ConsumerPath, cancellationToken).ConfigureAwait(false);
                await newSession.Client.DidCloseAsync(context.Fixture.TargetPath, cancellationToken).ConfigureAwait(false);
            }
            ScenarioExecution.AddProtocolCoverageObservation(checks, newSession);
            await newSession.GracefulRetireAsync().ConfigureAwait(false);
        });

    private static string ReconstructDiskConsumer(string editorConsumer)
    {
        int statementStart = -1;
        int searchStart = 0;
        while (searchStart <= editorConsumer.Length - EditorConsumerPrefix.Length)
        {
            int candidate = editorConsumer.IndexOf(EditorConsumerPrefix, searchStart, StringComparison.Ordinal);
            if (candidate < 0)
                break;

            int afterPrefix = candidate + EditorConsumerPrefix.Length;
            if (afterPrefix == editorConsumer.Length || editorConsumer[afterPrefix] is '\r' or '\n')
            {
                if (statementStart >= 0)
                    throw new InvalidOperationException("Replayed Consumer true-editor statement occurred more than once.");
                statementStart = candidate;
            }

            searchStart = candidate + EditorConsumerPrefix.Length;
        }

        if (statementStart < 0)
            throw new InvalidOperationException("Replayed Consumer true-editor statement was unavailable.");

        return editorConsumer[..statementStart]
            + DiskConsumerAnchor
            + editorConsumer[(statementStart + EditorConsumerPrefix.Length)..];
    }

    private static int CountOrdinalOccurrences(string source, string value)
    {
        int count = 0;
        int searchStart = 0;
        while (searchStart <= source.Length - value.Length)
        {
            int found = source.IndexOf(value, searchStart, StringComparison.Ordinal);
            if (found < 0)
                break;
            count++;
            searchStart = found + value.Length;
        }
        return count;
    }
}
