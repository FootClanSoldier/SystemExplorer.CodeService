using System.Diagnostics;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Process;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Reporting;
using SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Scenarios;

internal static class RecoveryScenario
{
    public static Task<ProbeScenarioResult> RunAsync(ProbeScenarioContext context, CancellationToken cancellationToken) =>
        ScenarioExecution.RunAsync("Recovery", cancellationToken, async checks =>
        {
            ProbeSession oldSession = context.PrimarySession ?? throw new InvalidOperationException("Primary session is not initialized.");
            if (string.IsNullOrEmpty(context.CurrentTargetText) || context.CurrentTargetVersion < 3)
                throw new InvalidOperationException("Document synchronization state was not established before recovery.");

            string consumer = context.Fixture.ReadConsumer();
            LspPosition completionPosition = ProbeSourceMarker.FindUniqueCompletionPosition(consumer, "PROBE_INSTANCE_COMPLETION");
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
                consumer,
                1,
                cancellationToken).ConfigureAwait(false);
            checks.Add(new ProbeCheckResult("ConsumerDocumentReplay", !newSession.Process.HasExited, "version=1"));
            checks.Add(new ProbeCheckResult("OpenDocumentReplay", !newSession.Process.HasExited,
                $"targetVersion={context.CurrentTargetVersion}; consumerVersion=1"));

            var (postRestartItems, restartCompletionMs) = await newSession.Client.CompletionAsync(
                context.Fixture.ConsumerPath, completionPosition, cancellationToken).ConfigureAwait(false);
            bool replayObserved = ScenarioExecution.ContainsLabel(postRestartItems, "ProbeIncrementalMember")
                && !ScenarioExecution.ContainsLabel(postRestartItems, "ProbeDiskMember");
            checks.Add(new ProbeCheckResult("PostRestartSemanticQuery", replayObserved, null, restartCompletionMs));
            checks.Add(new ProbeCheckResult("CompletionAfterRoslynRestartLatency", true, null, restartCompletionMs));
            checks.Add(new ProbeCheckResult("ReplayedUnsavedStateRemainedOffDisk",
                context.Fixture.ReadTarget().Contains("ProbeDiskMember", StringComparison.Ordinal)
                    && !context.Fixture.ReadTarget().Contains("ProbeIncrementalMember", StringComparison.Ordinal)));

            if (!newSession.Process.HasExited)
            {
                await newSession.Client.DidCloseAsync(context.Fixture.ConsumerPath, cancellationToken).ConfigureAwait(false);
                await newSession.Client.DidCloseAsync(context.Fixture.TargetPath, cancellationToken).ConfigureAwait(false);
            }
            ScenarioExecution.AddProtocolCoverageObservation(checks, newSession);
            await newSession.GracefulRetireAsync().ConfigureAwait(false);
        });
}
